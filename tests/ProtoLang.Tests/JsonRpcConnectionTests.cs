using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ProtoLang.LanguageServer.Protocol;
using ProtoLang.LanguageServer.Protocol.Lsp;
using Xunit;

namespace ProtoLang.Tests;

public class JsonRpcConnectionTests
{
    [Fact]
    public async Task CancelRequestReachesARequestHandlerThatIsAlreadyRunning()
    {
        await using var streams = new ConnectionHarness();

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new JsonRpcConnection(streams.ToServer, streams.FromServer, new ServerLog { Mirror = TextWriter.Null });
        var serving = connection.RunAsync(TestContext.Current.CancellationToken);

        connection.OnRequest(
            "test/slow",
            async (_, cancellationToken) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return null;
            });

        try
        {
            await streams.ClientWriter.WriteAsync(
                    new
                    {
                        jsonrpc = "2.0",
                        id = 1,
                        method = "test/slow",
                    },
                    TestContext.Current.CancellationToken);

            await started.Task.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

            await streams.ClientWriter.WriteAsync(
                    new
                    {
                        jsonrpc = "2.0",
                        method = Methods.CancelRequest,
                        @params = new { id = 1 },
                    },
                    TestContext.Current.CancellationToken);

            var response = await ReadAsync(streams.ClientReader)
                .WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

            Assert.Equal(ErrorCodes.RequestCancelled, response.Error?.Code);
        }
        finally
        {
            connection.Stop();
            streams.Complete();
            await Task.WhenAny(serving, Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
            connection.Dispose();
        }
    }

    /// <summary>
    /// Ending a conversation cancels the requests that are still running, and stops cleanly.
    /// </summary>
    /// <remarks>
    /// The assumption the shutdown path rests on, made explicit. Requests still running are not swept
    /// at shutdown, on the grounds that every one of them holds a cancellation source linked to the
    /// connection's own -- so cancelling that one cancels them all, and a second pass over them would
    /// only race the requests retiring themselves. That reasoning is only as good as the link: a
    /// change that gave a request an unlinked source would leave running handlers waiting forever on
    /// a connection that had already gone, and nothing else in the suite would notice.
    /// </remarks>
    [Fact]
    public async Task EndingAConnectionCancelsARequestThatIsStillRunning()
    {
        await using var streams = new ConnectionHarness();

        using var connection = new JsonRpcConnection(
            streams.ToServer,
            streams.FromServer,
            new ServerLog { Mirror = TextWriter.Null });

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        connection.OnRequest(
            "test/slow",
            async (_, cancellationToken) =>
            {
                started.SetResult();

                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    cancelled.SetResult();
                    throw;
                }

                return null;
            });

        var serving = connection.RunAsync(TestContext.Current.CancellationToken);

        await streams.ClientWriter.WriteAsync(
                new
                {
                    jsonrpc = "2.0",
                    id = 1,
                    method = "test/slow",
                },
                TestContext.Current.CancellationToken);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        streams.Complete();

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await serving.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Ending a conversation stops it cleanly, even though the requests it is ending are retiring
    /// themselves at the same moment.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A stress test, and it says so rather than pretending otherwise. <c>Stop</c> releases every
    /// in-flight handler before shutdown finishes -- they all wait on tokens linked to the
    /// connection's own -- so the dispatcher retires and disposes cancellation sources on one thread
    /// while shutdown walks the same collections on another. The window is microseconds wide and
    /// cannot be opened on command.
    /// </para>
    /// <para>
    /// So it is widened honestly rather than waited for. The pending client requests are the lever:
    /// shutdown cancels those first, and five thousand of them take long enough that the dispatcher
    /// has retired real work before the second collection is reached. Without that the shutdown path
    /// finishes in microseconds and wins every time -- this test passed against the defect until the
    /// requests were added, which is exactly the sort of test worth not shipping.
    /// </para>
    /// <para>
    /// Twenty shutdowns, and twenty is not a number arrived at by arithmetic. Whether a run collides
    /// at all turns out to be settled before the first shutdown -- by how many threads the pool has
    /// warm, most likely -- so runs are bimodal and more iterations buy almost nothing: against the
    /// defect, twelve caught it five times in six, fifty also five in six, and twenty nine times in
    /// ten. Twenty is the cheapest of those. A green from this test is evidence and not a proof,
    /// which is why the guarantee is asserted separately, by the test above.
    /// </para>
    /// <para>
    /// The cost of the defect is why it earns a two-second test. Shutdown runs inside a
    /// <c>finally</c>: an <see cref="ObjectDisposedException"/> there skips the await on the
    /// dispatcher, leaves the rest of the outstanding work uncancelled, and faults the task whose
    /// caller is entitled to a clean stop -- so a shutdown that succeeded is reported as a crash.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task EndingAConnectionWhileItsRequestsRetireThemselvesNeverFaultsIt()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await using var streams = new ConnectionHarness();

            using var connection = new JsonRpcConnection(
                streams.ToServer,
                streams.FromServer,
                new ServerLog { Mirror = TextWriter.Null });

            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            connection.OnRequest(
                "test/slow",
                async (_, cancellationToken) =>
                {
                    started.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return null;
                });

            var serving = connection.RunAsync(TestContext.Current.CancellationToken);

            // One of these runs and the rest queue behind it, but every one of them has a cancellation
            // source registered the moment it is read -- so the sweep has a hundred entries to walk.
            for (var id = 1; id <= 100; id++)
            {
                await streams.ClientWriter.WriteAsync(
                        new
                        {
                            jsonrpc = "2.0",
                            id,
                            method = "test/slow",
                        },
                        TestContext.Current.CancellationToken);
            }

            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            // Not decoration: see the remarks. These are what give the dispatcher time to retire a
            // request while shutdown is still working through what is outstanding.
            var asking = new List<Task>();
            for (var ask = 0; ask < 5000; ask++)
            {
                asking.Add(connection.RequestAsync<object>("test/ask", null, CancellationToken.None));
            }

            streams.Complete();

            // The assertion is that this returns rather than throws.
            await serving.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            foreach (var ask in asking)
            {
                try
                {
                    await ask;
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
    }

    private static async Task<IncomingMessage> ReadAsync(MessageReader reader)
    {
        var body = await reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(body);

        return JsonSerializer.Deserialize<IncomingMessage>(body, LspJson.Options)
            ?? throw new InvalidOperationException("The response body did not contain a JSON-RPC message.");
    }

    private sealed class ConnectionHarness : IAsyncDisposable
    {
        public ChannelStream ToServer { get; } = new();

        public ChannelStream FromServer { get; } = new();

        public MessageWriter ClientWriter { get; }

        public MessageReader ClientReader { get; }

        public ConnectionHarness()
        {
            ClientWriter = new MessageWriter(ToServer);
            ClientReader = new MessageReader(FromServer);
        }

        public void Complete()
        {
            ToServer.Complete();
            FromServer.Complete();
        }

        public ValueTask DisposeAsync()
        {
            Complete();
            return ValueTask.CompletedTask;
        }
    }

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

        public override void Write(byte[] buffer, int offset, int count)
            => _blocks.Writer.TryWrite(buffer.AsSpan(offset, count).ToArray());

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _blocks.Writer.TryWrite(buffer.ToArray());
            return ValueTask.CompletedTask;
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
