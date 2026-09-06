using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ProtoLang.LanguageServer.Protocol;

/// <summary>
/// The framing is broken beyond recovery, so this connection cannot be resynchronized.
/// </summary>
/// <remarks>
/// Distinct from a body that will not parse, which is recoverable: the header said how long it was,
/// so the reader can step over exactly that many bytes and carry on. A header that does not say how
/// long the body is leaves nowhere to resume from -- every subsequent byte would be read as though it
/// were a header. Ending the connection is the only honest answer, and it is not the same thing as
/// crashing: the server says why, in the log, before it stops.
/// </remarks>
public sealed class MessageFramingException(string message) : Exception(message);

/// <summary>
/// Reads the LSP base protocol: <c>Content-Length</c> headers, a blank line, and that many bytes.
/// </summary>
/// <remarks>
/// Its own buffer rather than a <see cref="StreamReader"/>, because a reader over the header would
/// read ahead into the body and there would be no way to give the bytes back. Buffering here keeps
/// the header scan a byte at a time -- which is what a header scan is -- without a system call per
/// byte.
/// </remarks>
public sealed class MessageReader(Stream stream)
{
    /// <summary>
    /// How much header the reader will take before deciding the stream is not the base protocol.
    /// </summary>
    /// <remarks>
    /// A stream that is not LSP at all -- a shell that piped the wrong thing in, a client writing
    /// diagnostics to its own stdout -- otherwise reads forever without ever finding a blank line.
    /// </remarks>
    private const int HeaderLimit = 16 * 1024;

    private const string ContentLength = "content-length";

    private readonly byte[] _buffer = new byte[8192];

    private int _start;
    private int _end;

    /// <summary>Reads one message body, or null at end of stream.</summary>
    /// <exception cref="MessageFramingException">The headers cannot be understood.</exception>
    public async Task<byte[]?> ReadAsync(CancellationToken cancellationToken)
    {
        var length = -1;
        var headerBytes = 0;

        while (true)
        {
            var line = await ReadHeaderLineAsync(cancellationToken).ConfigureAwait(false);

            if (line is null)
            {
                // End of stream. Mid-header is the client having gone away, which is ordinary at
                // shutdown and is reported the same way as a clean end.
                return null;
            }

            if (line.Length == 0)
            {
                break;
            }

            headerBytes += line.Length;
            if (headerBytes > HeaderLimit)
            {
                throw new MessageFramingException(
                    $"No blank line ended the headers within {HeaderLimit} bytes, so this stream is "
                        + "not the LSP base protocol.");
            }

            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator < 0)
            {
                throw new MessageFramingException($"'{line}' is not a header.");
            }

            if (line[..separator].Trim().Equals(ContentLength, StringComparison.OrdinalIgnoreCase)
                && !int.TryParse(
                    line[(separator + 1)..].Trim(),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out length))
            {
                throw new MessageFramingException($"'{line}' does not state a length.");
            }
        }

        if (length < 0)
        {
            throw new MessageFramingException("A message arrived with no Content-Length header.");
        }

        return await ReadExactlyAsync(length, cancellationToken).ConfigureAwait(false);
    }

    /// <returns>The line without its ending, or null at end of stream.</returns>
    /// <remarks>
    /// A bare newline is accepted as well as <c>\r\n</c>. The specification requires both characters,
    /// but a hand-written test client or a middlebox that rewrites line endings is a likelier
    /// explanation than a message that was truly meant to be rejected.
    /// </remarks>
    private async Task<string?> ReadHeaderLineAsync(CancellationToken cancellationToken)
    {
        var line = new StringBuilder();

        while (true)
        {
            var next = await ReadByteAsync(cancellationToken).ConfigureAwait(false);

            if (next < 0)
            {
                // Whatever was already on the line goes with it. End of stream part-way through a
                // header is the client having exited, which is what happens at shutdown; reporting it
                // as a malformed header would put a spurious error in the log every time.
                return null;
            }

            if (next == '\n')
            {
                if (line.Length > 0 && line[^1] == '\r')
                {
                    line.Length--;
                }

                return line.ToString();
            }

            line.Append((char)next);
        }
    }

    private async Task<int> ReadByteAsync(CancellationToken cancellationToken)
    {
        if (_start == _end && !await FillAsync(cancellationToken).ConfigureAwait(false))
        {
            return -1;
        }

        return _buffer[_start++];
    }

    private async Task<byte[]?> ReadExactlyAsync(int length, CancellationToken cancellationToken)
    {
        var body = new byte[length];
        var filled = 0;

        while (filled < length)
        {
            if (_start == _end && !await FillAsync(cancellationToken).ConfigureAwait(false))
            {
                // A truncated body is the client having died mid-write. There is no message to
                // deliver and nothing after it to read.
                return null;
            }

            var take = Math.Min(_end - _start, length - filled);
            Array.Copy(_buffer, _start, body, filled, take);
            _start += take;
            filled += take;
        }

        return body;
    }

    private async Task<bool> FillAsync(CancellationToken cancellationToken)
    {
        _start = 0;
        _end = await stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);

        return _end > 0;
    }
}

/// <summary>Writes the LSP base protocol, one whole message at a time.</summary>
/// <remarks>
/// The gate is the point of the type. Diagnostics are published from compile workers, logs are written
/// from wherever something went wrong, and responses are written from the dispatch loop; two of those
/// interleaving inside one message produces a header describing a body that is not there, and the
/// client cannot resynchronize from that any more than the reader could. Serialization happens outside
/// the gate, so the lock covers the write and nothing else.
/// </remarks>
public sealed class MessageWriter(Stream stream)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task WriteAsync(object message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var payload = JsonSerializer.SerializeToUtf8Bytes(message, message.GetType(), LspJson.Options);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
