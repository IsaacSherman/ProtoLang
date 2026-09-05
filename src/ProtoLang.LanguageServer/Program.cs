using ProtoLang.LanguageServer.Hosting;
using ProtoLang.LanguageServer.Protocol;

namespace ProtoLang.LanguageServer;

/// <summary>
/// The <c>protolang-server</c> executable: LSP over standard input and output.
/// </summary>
/// <remarks>
/// Thin on purpose. Everything it does beyond opening the two streams is done again by any test that
/// drives the server, so the smaller it is the less of the server is reachable only by starting a
/// process. <see cref="RunAsync"/> is the shared entry point for exactly that reason.
/// </remarks>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var input = Console.OpenStandardInput();
        var output = Console.OpenStandardOutput();

        // Standard output is the protocol from here on. A single stray line on it -- a Console.Write
        // anywhere in the compiler, a library writing a banner -- puts bytes between two messages that
        // no client can resynchronize from, and the session dies with a parse error nobody can trace
        // back. Pointing Console.Out at standard error makes that unwritable rather than merely
        // discouraged.
        Console.SetOut(Console.Error);

        var log = new ServerLog { Level = LevelFrom(args) };

        return await RunAsync(input, output, log, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Serves one client over one pair of streams, and reports the process's exit code.</summary>
    public static async Task<int> RunAsync(
        Stream input,
        Stream output,
        ServerLog log,
        CancellationToken cancellationToken)
    {
        using var host = new LanguageServerHost(input, output, log);

        await host.RunAsync(cancellationToken).ConfigureAwait(false);

        return host.ExitCode;
    }

    /// <remarks>
    /// A starting level for the lines written before <c>initialize</c> has said what the client wants.
    /// After that, <c>$/setTrace</c> and the client's own trace setting take over.
    /// </remarks>
    private static LogLevel LevelFrom(string[] args)
    {
        const string Option = "--log-level=";

        foreach (var argument in args ?? [])
        {
            if (argument.StartsWith(Option, StringComparison.OrdinalIgnoreCase)
                && Enum.TryParse<LogLevel>(argument[Option.Length..], ignoreCase: true, out var level))
            {
                return level;
            }
        }

        return LogLevel.Info;
    }
}
