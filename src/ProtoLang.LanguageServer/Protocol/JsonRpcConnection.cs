using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace ProtoLang.LanguageServer.Protocol;

/// <summary>The other end answered a request with an error.</summary>
public sealed class JsonRpcException(ResponseError error)
    : Exception($"{error.Message} ({error.Code})")
{
    public ResponseError Error { get; } = error;
}

/// <summary>
/// One JSON-RPC conversation over a pair of streams: framing, routing, correlation, and cancellation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reading and handling are separate.</b> The read loop parses a message, completes a response, or
/// cancels a running request, and then hands anything else to a queue that one worker drains. That
/// separation is not tidiness, it is the only arrangement in which a handler may ask the client a
/// question: <c>workspace/configuration</c> is a request the <em>server</em> sends, and its answer
/// arrives as an ordinary message. A handler awaiting that answer on the thread that reads messages
/// would be waiting for itself.
/// </para>
/// <para>
/// <b>One worker, in order.</b> LSP guarantees that notifications are seen in the order they were
/// sent, and a document that was changed and then closed must be handled in that order or the server
/// resurrects a closed buffer. A single consumer awaiting each handler in turn gives that for free.
/// Nothing slow runs there: a compile is scheduled rather than performed, and the handlers that do
/// work -- semantic tokens, which lexes -- do not touch protoc or the disk.
/// </para>
/// <para>
/// <b>Nothing a peer sends ends the session.</b> A handler that throws becomes an error response, an
/// unparseable body becomes a parse error and the reader steps over exactly the bytes the header
/// promised, and an unknown method is refused. The one exception is framing that cannot be
/// resynchronized, which ends the connection with an explanation -- see
/// <see cref="MessageFramingException"/>.
/// </para>
/// </remarks>
public sealed class JsonRpcConnection : IDisposable
{
    private readonly MessageReader _reader;
    private readonly MessageWriter _writer;
    private readonly ServerLog _log;

    private readonly Dictionary<string, Func<JsonElement?, CancellationToken, Task<object?>>> _requests
        = new(StringComparer.Ordinal);

    private readonly Dictionary<string, Func<JsonElement?, CancellationToken, Task>> _notifications
        = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, TaskCompletionSource<IncomingMessage>> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new(StringComparer.Ordinal);

    private readonly Channel<IncomingMessage> _inbox =
        Channel.CreateUnbounded<IncomingMessage>(new UnboundedChannelOptions { SingleReader = true });

    private readonly CancellationTokenSource _stopping = new();

    private long _nextId;
    private volatile bool _writable = true;

    public JsonRpcConnection(Stream input, Stream output, ServerLog log)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        _reader = new MessageReader(input);
        _writer = new MessageWriter(output);
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>Registers what answers <paramref name="method"/>.</summary>
    public void OnRequest(string method, Func<JsonElement?, CancellationToken, Task<object?>> handler)
        => _requests[method] = handler;

    /// <summary>Registers what acts on <paramref name="method"/>, which expects no answer.</summary>
    public void OnNotification(string method, Func<JsonElement?, CancellationToken, Task> handler)
        => _notifications[method] = handler;

    /// <summary>Reads until the stream ends, the peer stops, or <paramref name="cancellationToken"/> fires.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(Stop);

        var dispatching = Task.Run(DispatchAsync, CancellationToken.None);

        try
        {
            await ReadAsync().ConfigureAwait(false);
        }
        finally
        {
            // Order matters, and getting it wrong wedges the process rather than ending it. A handler
            // may be awaiting an answer from the client -- workspace/configuration is one -- and the
            // client is exactly what has just gone away. Cancelling first is what lets that handler
            // finish; awaiting the dispatcher first would wait for it forever, and the cancellation
            // that would have released it is the line that never runs.
            Stop();
            CancelEverythingOutstanding();

            await dispatching.ConfigureAwait(false);
        }
    }

    /// <summary>Ends the conversation: no more reading, and the queue drains and stops.</summary>
    public void Stop()
    {
        if (!_stopping.IsCancellationRequested)
        {
            _stopping.Cancel();
        }

        _inbox.Writer.TryComplete();
    }

    /// <summary>Tells the client something, expecting nothing back.</summary>
    public Task NotifyAsync(string method, object? parameters)
        => SendAsync(new OutgoingNotification(method) { Params = parameters });

    /// <summary>Asks the client something, and waits for its answer.</summary>
    /// <exception cref="JsonRpcException">The client answered with an error.</exception>
    /// <remarks>
    /// Safe to await from a handler; see the type's remarks for why that is a design property rather
    /// than a coincidence.
    /// </remarks>
    public async Task<TResult?> RequestAsync<TResult>(
        string method,
        object? parameters,
        CancellationToken cancellationToken)
    {
        var id = RequestId.Of(Interlocked.Increment(ref _nextId));
        var completion = new TaskCompletionSource<IncomingMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        _pending[id.ToString()] = completion;

        try
        {
            await SendAsync(new OutgoingRequest(id, method) { Params = parameters }).ConfigureAwait(false);

            using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));

            var response = await completion.Task.ConfigureAwait(false);

            return response.Error is { } error ? throw new JsonRpcException(error) : LspJson.Read<TResult>(response.Result);
        }
        finally
        {
            _pending.TryRemove(id.ToString(), out _);
        }
    }

    public void Dispose()
    {
        _stopping.Dispose();
    }

    // ------------------------------------------------------- reading

    private async Task ReadAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            byte[]? body;

            try
            {
                body = await _reader.ReadAsync(_stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (MessageFramingException ex)
            {
                _log.Error("The message stream cannot be read any further, so the connection is ending.", ex);
                return;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                _log.Info("The client closed the connection.");
                return;
            }

            if (body is null)
            {
                return;
            }

            await ReceiveAsync(body).ConfigureAwait(false);
        }
    }

    /// <remarks>
    /// A body that will not parse is answered and stepped over rather than ending anything: the header
    /// already said how long it was, so the stream is still synchronized and the next message is
    /// exactly where it should be.
    /// </remarks>
    private async Task ReceiveAsync(byte[] body)
    {
        IncomingMessage? message;

        try
        {
            message = JsonSerializer.Deserialize<IncomingMessage>(body, LspJson.Options);
        }
        catch (JsonException ex)
        {
            // With the text, because a parse failure without sight of what was parsed is a log line
            // that says only that something went wrong. Truncated: a malformed message can be as long
            // as the sender likes, and the first line of it is where the fault always is.
            _log.Warning($"A message could not be parsed and was refused: {Preview(body)}", ex);
            await SendAsync(ResponseMessage.Failure(null, ErrorCodes.ParseError, ex.Message)).ConfigureAwait(false);
            return;
        }

        if (message is null)
        {
            await SendAsync(ResponseMessage.Failure(null, ErrorCodes.ParseError, "The message was empty."))
                .ConfigureAwait(false);
            return;
        }

        if (message.IsResponse)
        {
            Complete(message);
            return;
        }

        if (string.Equals(message.Method, Lsp.Methods.CancelRequest, StringComparison.Ordinal))
        {
            Cancel(message.Params);
            return;
        }

        if (message.Method is null)
        {
            await SendAsync(
                    ResponseMessage.Failure(
                        message.Id,
                        ErrorCodes.InvalidRequest,
                        "A message must carry a method, an id and a result, or an id and an error."))
                .ConfigureAwait(false);
            return;
        }

        if (message.IsRequest)
        {
            // Registered here rather than where the handler runs, so that a client which cancels a
            // request still sitting in the queue is obeyed instead of waited out.
            _running[message.Id!.Value.ToString()] =
                CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        }

        _inbox.Writer.TryWrite(message);
    }

    /// <summary>As much of a message as is worth putting in a log line.</summary>
    private static string Preview(byte[] body)
    {
        const int Most = 200;

        var text = System.Text.Encoding.UTF8.GetString(body, 0, Math.Min(body.Length, Most));

        return body.Length <= Most ? $"{body.Length} bytes, '{text}'" : $"{body.Length} bytes, '{text}'...";
    }

    private void Complete(IncomingMessage message)
    {
        if (_pending.TryRemove(message.Id!.Value.ToString(), out var completion))
        {
            completion.TrySetResult(message);
            return;
        }

        _log.Trace($"An answer arrived for request {message.Id}, which nothing was waiting for.");
    }

    private void Cancel(JsonElement? parameters)
    {
        if (LspJson.Read<Lsp.CancelParams>(parameters) is not { Id: { } id })
        {
            return;
        }

        if (_running.TryGetValue(id.ToString(), out var cancellation))
        {
            CancelQuietly(cancellation);
        }
    }

    /// <summary>Cancels a source that another thread may already have finished with.</summary>
    /// <remarks>
    /// A running request's source stays reachable until the request answers, so a cancel and the
    /// retirement of the very request it names can overlap. Both callers run somewhere an exception
    /// would cost more than the cancel is worth: one is the read loop, where it would end the
    /// conversation over a request that had already been answered, and the other is the shutdown path,
    /// where it would leave the rest of the outstanding work uncancelled.
    /// </remarks>
    private static void CancelQuietly(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    // ------------------------------------------------------- handling

    private async Task DispatchAsync()
    {
        while (await _inbox.Reader.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false))
        {
            while (_inbox.Reader.TryRead(out var message))
            {
                if (message.IsRequest)
                {
                    await AnswerAsync(message).ConfigureAwait(false);
                }
                else
                {
                    await ActAsync(message).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task AnswerAsync(IncomingMessage message)
    {
        var id = message.Id!.Value;
        var key = id.ToString();

        // Looked up, not taken. The entry has to stay reachable for as long as the handler runs, or
        // $/cancelRequest can only reach a request that is still queued -- which is the half of
        // cancellation that does not matter. A request worth cancelling is one that has started.
        _running.TryGetValue(key, out var cancellation);

        var token = cancellation?.Token ?? _stopping.Token;

        if (!_requests.TryGetValue(message.Method!, out var handler))
        {
            Retire(key, cancellation);

            await SendAsync(
                    ResponseMessage.Failure(
                        id,
                        ErrorCodes.MethodNotFound,
                        $"This server does not answer '{message.Method}'."))
                .ConfigureAwait(false);
            return;
        }

        try
        {
            token.ThrowIfCancellationRequested();

            var result = await handler(message.Params, token).ConfigureAwait(false);
            await SendAsync(ResponseMessage.Success(id, result)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await SendAsync(
                    ResponseMessage.Failure(id, ErrorCodes.RequestCancelled, $"'{message.Method}' was cancelled."))
                .ConfigureAwait(false);
        }
        catch (JsonRpcException ex)
        {
            // A handler that already knows which error this is -- a request that arrived before
            // initialize, or after shutdown -- says so itself. Flattening those to an internal error
            // would leave a client unable to tell "too early" from "the server is broken".
            await SendAsync(ResponseMessage.Failure(id, ex.Error)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // One request degrades; the session does not. A server that dies on a malformed file is
            // worse than no server, because the editor stops answering and the user cannot tell why.
            _log.Error($"'{message.Method}' failed.", ex);
            await SendAsync(ResponseMessage.Failure(id, ErrorCodes.InternalError, ex.Message)).ConfigureAwait(false);
        }
        finally
        {
            Retire(key, cancellation);
        }
    }

    /// <summary>Forgets a finished request, and releases what its cancellation source was holding.</summary>
    /// <remarks>
    /// Disposed rather than dropped, because each source is linked to <see cref="_stopping"/> and a
    /// registration on that token would otherwise accumulate for the life of the connection -- one per
    /// request, over a working day. A <c>$/cancelRequest</c> that read the entry a moment before this
    /// runs may cancel a disposed source; <see cref="Cancel"/> expects that and says why.
    /// </remarks>
    private void Retire(string key, CancellationTokenSource? cancellation)
    {
        _running.TryRemove(key, out _);
        cancellation?.Dispose();
    }

    private async Task ActAsync(IncomingMessage message)
    {
        if (!_notifications.TryGetValue(message.Method!, out var handler))
        {
            // Unknown notifications are dropped rather than refused: the protocol has no way to
            // answer one, and a client is entitled to send notifications a server never registered.
            _log.Trace($"Ignoring the notification '{message.Method}', which this server does not handle.");
            return;
        }

        try
        {
            await handler(message.Params, _stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.Error($"'{message.Method}' failed.", ex);
        }
    }

    // ------------------------------------------------------- writing

    /// <remarks>
    /// <see cref="_writable"/> is cleared before the failure is logged, and that order is the whole
    /// point of it. The log's own sink publishes through this method, so a send that fails and then
    /// logs would send again, fail again, and log again -- for as long as the stack held out. Once the
    /// connection is broken nothing more goes down it, and the log line reaches the mirror instead.
    /// </remarks>
    private async Task SendAsync(object message)
    {
        if (!_writable)
        {
            return;
        }

        try
        {
            await _writer.WriteAsync(message, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            _writable = false;

            // The client is gone. Saying so once is useful; failing the work that was trying to
            // report to it is not.
            _log.Info("A message could not be sent because the connection is closed.");
            Stop();
        }
    }

    /// <remarks>
    /// <para>
    /// Anything still waiting on the client is told the conversation is over. Without this, a handler
    /// blocked on <c>workspace/configuration</c> when the client exits waits forever, and the process
    /// never leaves. These are the ones that need saying: a pending request's completion is cancelled
    /// through the caller's own token, and a caller is entitled to pass one that never cancels.
    /// </para>
    /// <para>
    /// The requests still <em>running</em> are deliberately not swept, and it took a crash to notice
    /// why they should not be. Every source in <c>_running</c> is linked to <see cref="_stopping"/>,
    /// which <see cref="Stop"/> cancelled on the line above, so each of them is already cancelled by
    /// the time this runs and a second cancel achieves nothing. What it does achieve is touching a
    /// source that the request retiring itself on another thread is disposing at that moment -- and
    /// the resulting <see cref="ObjectDisposedException"/> escapes a <c>finally</c>, so the rest of
    /// the outstanding work goes uncancelled, the dispatcher is never awaited, and a clean shutdown
    /// is reported to its caller as a crash. A redundant loop is a poor thing to pay that for.
    /// </para>
    /// </remarks>
    private void CancelEverythingOutstanding()
    {
        foreach (var completion in _pending.Values)
        {
            completion.TrySetCanceled();
        }
    }
}
