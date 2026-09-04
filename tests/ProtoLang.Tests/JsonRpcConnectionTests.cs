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
