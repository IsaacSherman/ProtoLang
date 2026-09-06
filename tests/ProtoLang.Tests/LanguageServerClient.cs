using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ProtoLang.LanguageServer.Hosting;
using ProtoLang.LanguageServer.Protocol;
using ProtoLang.LanguageServer.Protocol.Lsp;

namespace ProtoLang.Tests;

/// <summary>
/// A client that speaks the base protocol at a real server over a real pair of streams.
/// </summary>
/// <remarks>
/// <para>
/// The point of driving the server this way rather than calling its handlers is that the framing, the
/// JSON, the lifecycle gate, the dispatch queue and the response correlation are all under test as
/// well -- and every one of them is a place a server can be wrong in a way no unit test of a handler
/// would notice. What runs here is what <c>protolang-server</c> runs; only the streams differ.
/// </para>
/// <para>
/// It answers <c>workspace/configuration</c> itself, because a server that asks a question and is
/// never answered waits forever, and a test that hangs teaches nothing.
/// </para>
/// </remarks>
public sealed class LanguageServerClient : IAsyncDisposable
{
    /// <summary>How long any wait for a message is given before the test fails instead of hanging.</summary>
    /// <remarks>
    /// Generous, because a compilation may start protoc on a cold cache. It is a failure bound rather
    /// than a timing assertion: nothing here asserts that anything happened <em>quickly</em>.
    /// </remarks>
    public static TimeSpan Patience => TimeSpan.FromSeconds(30);

    private readonly ChannelStream _toServer = new();
    private readonly ChannelStream _fromServer = new();
    private readonly MessageReader _reader;
    private readonly Channel<IncomingMessage> _inbox = Channel.CreateUnbounded<IncomingMessage>();
    private readonly List<IncomingMessage> _seen = [];
    private readonly Task _serving;
    private readonly Task _receiving;
    private readonly Func<ConfigurationParams, object?> _configuration;

    private readonly Lock _writing = new();

    /// <summary>The server's own log, kept so that a timeout can say what it was doing.</summary>
    private readonly StringWriter _log = new();

    private readonly TextWriter _transcript;

    private Exception? _fatal;
    private int _nextId;

    private LanguageServerClient(TimeSpan debounce, Func<ConfigurationParams, object?> configuration)
    {
        _configuration = configuration;
        _transcript = TextWriter.Synchronized(_log);
        _reader = new MessageReader(_fromServer);

        Host = new LanguageServerHost(_toServer, _fromServer, new ServerLog { Mirror = _transcript }, debounce);

        _serving = Host.RunAsync();
        _receiving = ReceiveAsync();
    }

    /// <summary>The server under test, for the few facts that are not on the wire.</summary>
    public LanguageServerHost Host { get; }

    /// <summary>Starts a server and completes the opening handshake.</summary>
    /// <param name="settings">
    /// What the client answers <c>workspace/configuration</c> with, given the scopes it was asked
    /// about. Null answers every scope with an empty object, which is a workspace that states nothing.
    /// </param>
    public static async Task<LanguageServerClient> StartAsync(
        TimeSpan? debounce = null,
        ClientCapabilities? capabilities = null,
        IEnumerable<string>? folders = null,
        Func<ConfigurationParams, object?>? settings = null)
    {
        var client = Create(debounce, settings);

        await client.InitializeAsync(capabilities ?? FullCapabilities, folders).ConfigureAwait(false);

        return client;
    }

    /// <summary>A connected server that has not been initialized, for the tests about that.</summary>
    public static LanguageServerClient Create(
        TimeSpan? debounce = null,
        Func<ConfigurationParams, object?>? settings = null)
        => new(
            debounce ?? TimeSpan.FromMilliseconds(10),
            settings ?? (parameters => parameters.Items.Select(_ => new Dictionary<string, object?>()).ToList()));

    /// <summary>A client that declares everything this server looks for.</summary>
    public static ClientCapabilities FullCapabilities => new()
    {
        Workspace = new WorkspaceClientCapabilities { Configuration = true, WorkspaceFolders = true },
        TextDocument = new TextDocumentClientCapabilities
        {
            PublishDiagnostics = new PublishDiagnosticsClientCapabilities { RelatedInformation = true },
            SemanticTokens = new SemanticTokensClientCapabilities(),
        },
        General = new GeneralClientCapabilities { PositionEncodings = ["utf-16"] },
    };

    // ------------------------------------------------------- the opening exchange

    public async Task<InitializeResult> InitializeAsync(ClientCapabilities capabilities, IEnumerable<string>? folders)
    {
        var result = await RequestAsync(
                Methods.Initialize,
                new InitializeParams
                {
                    Capabilities = capabilities,
                    WorkspaceFolders =
                    [
                        .. folders?.Select(path => new WorkspaceFolder
                        {
                            Uri = new Uri(path).AbsoluteUri,
                            Name = Path.GetFileName(path),
                        }) ?? [],
                    ],
                })
            .ConfigureAwait(false);

        Notify(Methods.Initialized, new Dictionary<string, object?>());

        return result.Deserialize<InitializeResult>(LspJson.Options)!;
    }

    // ------------------------------------------------------- sending

    /// <summary>Asks the server something and waits for its answer.</summary>
    /// <exception cref="JsonRpcException">The server answered with an error.</exception>
    public async Task<JsonElement> RequestAsync(string method, object? parameters)
    {
        var response = await AskAsync(method, parameters).ConfigureAwait(false);

        return response.Error is { } error ? throw new JsonRpcException(error) : response.Result;
    }

    /// <summary>Asks the server something that is expected to be refused.</summary>
    public async Task<ResponseError> RefusalAsync(string method, object? parameters)
    {
        var response = await AskAsync(method, parameters).ConfigureAwait(false);

        return response.Error
            ?? throw new InvalidOperationException($"'{method}' was answered rather than refused.");
    }

    public void Notify(string method, object? parameters)
        => Send(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = parameters,
        });

    /// <summary>Sends bytes that may not be a message at all.</summary>
    public void SendRaw(string body) => Frame(Encoding.UTF8.GetBytes(body));

    // ------------------------------------------------------- waiting

    /// <summary>The next message satisfying <paramref name="wanted"/>, from anywhere in the stream.</summary>
    /// <remarks>
    /// Messages already read are searched first, so a test may ask about two things in either order
    /// without one of them having consumed the other.
    /// </remarks>
    public async Task<IncomingMessage> WaitForAsync(Func<IncomingMessage, bool> wanted, string describe)
    {
        if (_seen.FirstOrDefault(wanted) is { } already)
        {
            _seen.Remove(already);
            return already;
        }

        using var patience = new CancellationTokenSource(Patience);

        try
        {
            while (await _inbox.Reader.WaitToReadAsync(patience.Token).ConfigureAwait(false))
            {
                while (_inbox.Reader.TryRead(out var message))
                {
                    if (wanted(message))
                    {
                        return message;
                    }

                    _seen.Add(message);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }

        throw new TimeoutException(
            $"The server never sent {describe}. State {Host.State}, {Host.Compilations} compilations"
                + (_fatal is null ? "." : $"; this client stopped reading because of {_fatal}.")
                + Environment.NewLine + "The server said:" + Environment.NewLine + _log);
    }

    /// <summary>The next notification of one method.</summary>
    public async Task<T> NotificationAsync<T>(string method)
    {
        var message = await WaitForAsync(
                candidate => candidate.IsNotification && candidate.Method == method,
                $"a '{method}' notification")
            .ConfigureAwait(false);

        return LspJson.Read<T>(message.Params)!;
    }

    /// <summary>The next diagnostics published for one document.</summary>
    public async Task<PublishDiagnosticsParams> DiagnosticsAsync(string uri)
    {
        var message = await WaitForAsync(
                candidate => candidate.IsNotification
                    && candidate.Method == Methods.PublishDiagnostics
                    && LspJson.Read<PublishDiagnosticsParams>(candidate.Params)?.Uri == uri,
                $"diagnostics for '{uri}'")
            .ConfigureAwait(false);

        return LspJson.Read<PublishDiagnosticsParams>(message.Params)!;
    }

    /// <summary>Diagnostics for one document, ignoring any that do not yet satisfy a condition.</summary>
    /// <remarks>
    /// A document is published every time its compilation settles, and a test usually cares about one
    /// particular state of it -- after an edit has taken effect, after an error has been fixed. Waiting
    /// for the condition rather than for the next message is what keeps such a test from racing an
    /// earlier, entirely correct publication.
    /// </remarks>
    public async Task<PublishDiagnosticsParams> DiagnosticsAsync(string uri, Func<PublishDiagnosticsParams, bool> wanted)
    {
        while (true)
        {
            var published = await DiagnosticsAsync(uri).ConfigureAwait(false);

            if (wanted(published))
            {
                return published;
            }
        }
    }

    /// <summary>Whether nothing further is published about one document for a while.</summary>
    /// <remarks>
    /// Looks only at messages that arrive from now on, which is the question being asked: an earlier
    /// publication that a test has already read is not the server speaking again. Used for the two
    /// properties that are about something <em>not</em> happening -- a superseded compilation putting
    /// old errors back, and a schema being cleared while another document still reports it.
    /// </remarks>
    public async Task<bool> StaysSilentAboutAsync(string uri, TimeSpan quiet)
    {
        using var patience = new CancellationTokenSource(quiet);

        try
        {
            while (await _inbox.Reader.WaitToReadAsync(patience.Token).ConfigureAwait(false))
            {
                while (_inbox.Reader.TryRead(out var message))
                {
                    if (message.IsNotification
                        && message.Method == Methods.PublishDiagnostics
                        && LspJson.Read<PublishDiagnosticsParams>(message.Params)?.Uri == uri)
                    {
                        return false;
                    }

                    _seen.Add(message);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }

        return true;
    }

    public async ValueTask DisposeAsync()
    {
        Notify(Methods.Exit, null);

        // A server that has not stopped within a few seconds of being told to is a defect, and
        // waiting the full patience for it would hide that behind a slow test rather than a failing
        // one.
        await Task.WhenAny(_serving, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);

        _toServer.Complete();
        _fromServer.Complete();

        await Task.WhenAny(_receiving, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);

        Host.Dispose();
    }

    // ------------------------------------------------------- the plumbing

    private async Task<IncomingMessage> AskAsync(string method, object? parameters)
    {
        var id = Interlocked.Increment(ref _nextId);

        Send(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters,
        });

        return await WaitForAsync(
                message => message.IsResponse && message.Id?.Number == id,
                $"an answer to '{method}'")
            .ConfigureAwait(false);
    }

    private void Send(Dictionary<string, object?> message)
        => Frame(JsonSerializer.SerializeToUtf8Bytes(message, LspJson.Options));

    /// <summary>Writes one whole framed message, and never half of one.</summary>
    /// <remarks>
    /// Both halves in one write, under a lock, and neither half of that is decoration. A test sends
    /// from its own thread while this client answers <c>workspace/configuration</c> from its receive
    /// loop; writing a header and then a body lets those two interleave into a header describing
    /// somebody else's body. The server then reads a "body" that begins with <c>Content-Length</c>,
    /// refuses it, and every message after it is off by one -- which surfaces a long way away as a
    /// request that is simply never answered. <c>MessageWriter</c> holds a gate for exactly this
    /// reason, and a client needs one just as much.
    /// </remarks>
    private void Frame(byte[] payload)
    {
        var header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");
        var message = new byte[header.Length + payload.Length];

        header.CopyTo(message, 0);
        payload.CopyTo(message, header.Length);

        lock (_writing)
        {
            _toServer.Write(message, 0, message.Length);
        }
    }

    /// <remarks>
    /// Server-to-client requests are answered here rather than queued, so that a handler awaiting an
    /// answer gets one. Everything else goes to the inbox for a test to find.
    /// </remarks>
    private async Task ReceiveAsync()
    {
        while (true)
        {
            byte[]? body;

            try
            {
                body = await _reader.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or MessageFramingException)
            {
                break;
            }

            if (body is null)
            {
                break;
            }

            IncomingMessage? message;

            try
            {
                message = JsonSerializer.Deserialize<IncomingMessage>(body, LspJson.Options);
            }
            catch (Exception ex)
            {
                // Recorded rather than swallowed. A receive loop that dies quietly turns every
                // subsequent wait into a thirty-second timeout with nothing to say about why.
                _fatal = ex;
                break;
            }

            if (message is null)
            {
                continue;
            }

            if (message.IsRequest)
            {
                Answer(message);
                continue;
            }

            _inbox.Writer.TryWrite(message);
        }

        _inbox.Writer.TryComplete();
    }

    private void Answer(IncomingMessage request)
    {
        var result = request.Method == Methods.Configuration
            ? _configuration(LspJson.Read<ConfigurationParams>(request.Params) ?? new ConfigurationParams())
            : null;

        Send(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = request.Id?.Number,
            ["result"] = result,
        });
    }

    /// <summary>A one-way in-memory stream, so a test needs no pipes and no process.</summary>
    /// <remarks>
    /// Writes are never blocked and reads wait for one, which is the shape both ends of a language
    /// server connection assume. Completing it is how a test says the far end has gone away.
    /// </remarks>
    private sealed class ChannelStream : Stream
    {
        private readonly Channel<byte[]> _blocks = Channel.CreateUnbounded<byte[]>();

        private ReadOnlyMemory<byte> _remainder;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public void Complete() => _blocks.Writer.TryComplete();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_remainder.IsEmpty)
            {
                try
                {
                    if (!await _blocks.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        return 0;
                    }
                }
                catch (ChannelClosedException)
                {
                    return 0;
                }

                if (_blocks.Reader.TryRead(out var block))
                {
                    _remainder = block;
                }
            }

            var take = Math.Min(buffer.Length, _remainder.Length);
            _remainder[..take].CopyTo(buffer);
            _remainder = _remainder[take..];

            return take;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _blocks.Writer.TryWrite(buffer.ToArray());

            return ValueTask.CompletedTask;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Write(byte[] buffer, int offset, int count)
            => _blocks.Writer.TryWrite(buffer.AsSpan(offset, count).ToArray());

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
