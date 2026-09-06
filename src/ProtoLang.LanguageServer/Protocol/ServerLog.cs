namespace ProtoLang.LanguageServer.Protocol;

/// <summary>
/// How important a log line is. The numbers are LSP's <c>MessageType</c>, which is what goes on the
/// wire.
/// </summary>
public enum LogLevel
{
    Error = 1,
    Warning = 2,
    Info = 3,
    Trace = 4,
}

/// <summary>
/// Where the server says what it is doing, at a level the user can raise when reporting a problem.
/// </summary>
/// <remarks>
/// <para>
/// Two destinations, and both earn their place. <see cref="Sink"/> is wired to
/// <c>window/logMessage</c> once there is a connection, which is what puts the text in a channel the
/// user can open without leaving the editor. <see cref="Mirror"/> is standard error, which is where
/// anything that goes wrong <em>before</em> there is a connection has to go, and where a client that
/// captures the server's stderr will find it after a crash.
/// </para>
/// <para>
/// Standard output is not a destination and must never become one. It carries the protocol, and a
/// single stray line on it desynchronizes the framing for the rest of the session. <c>Program</c>
/// points <see cref="Console.Out"/> at standard error for that reason.
/// </para>
/// </remarks>
public sealed class ServerLog
{
    /// <summary>The least important level that is written. Raised by <c>$/setTrace</c>.</summary>
    public LogLevel Level { get; set; } = LogLevel.Info;

    /// <summary>Publishes to the client. Null until there is a connection to publish over.</summary>
    public Action<LogLevel, string>? Sink { get; set; }

    /// <summary>Where lines are mirrored regardless of the client. Never standard output.</summary>
    public TextWriter Mirror { get; init; } = Console.Error;

    public void Error(string message, Exception? exception = null) => Write(LogLevel.Error, message, exception);

    public void Warning(string message, Exception? exception = null) => Write(LogLevel.Warning, message, exception);

    public void Info(string message) => Write(LogLevel.Info, message);

    public void Trace(string message) => Write(LogLevel.Trace, message);

    /// <remarks>
    /// The mirror is written even when the level filters the client out, because the mirror is the
    /// record kept for a defect report and the level is a preference about what to show in an editor.
    /// Nothing here throws: a log that can fail a request is worse than no log.
    /// </remarks>
    public void Write(LogLevel level, string message, Exception? exception = null)
    {
        var text = exception is null ? message : $"{message}{Environment.NewLine}{exception}";

        try
        {
            Mirror.WriteLine($"[{level.ToString().ToLowerInvariant()}] {text}");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }

        if (level > Level)
        {
            return;
        }

        try
        {
            Sink?.Invoke(level, text);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }
}
