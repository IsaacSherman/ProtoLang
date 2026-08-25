using System.Diagnostics;

namespace ProtoLang.Tests.Harness;

internal sealed record ProcessResult(int ExitCode, string Output)
{
    /// <summary>Exit code used when the process could not be started or had to be killed.</summary>
    public const int NotRun = -1;
}

/// <summary>
/// Runs the external tools the backend suites depend on: protoc, dotnet, and a C++ compiler.
/// </summary>
/// <remarks>
/// Shared rather than duplicated per suite because two details here are easy to get wrong and were
/// previously written twice: both output pipes must be drained concurrently or a chatty child
/// deadlocks once either fills, and a nested build must not inherit the outer test run's MSBuild
/// variables.
/// </remarks>
internal static class ProcessRunner
{
    /// <summary>How long a nested tool invocation may run before it is treated as hung.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    public static ProcessStartInfo Create(string fileName, string? workingDirectory = null)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };

        if (workingDirectory is not null)
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        return startInfo;
    }

    /// <summary>
    /// Removes the MSBuild and test-platform variables the outer test run exports. A nested
    /// <c>dotnet build</c> or <c>dotnet test</c> that inherits them resolves the wrong SDK.
    /// </summary>
    public static void ScrubMsBuildEnvironment(ProcessStartInfo startInfo)
    {
        startInfo.Environment.Remove("MSBuildSDKsPath");
        startInfo.Environment.Remove("MSBuildExtensionsPath");
        startInfo.Environment.Remove("MSBUILD_EXE_PATH");
        startInfo.Environment.Remove("VSTEST_HOST_DEBUG");
        startInfo.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
    }

    public static ProcessResult Run(ProcessStartInfo startInfo, TimeSpan? timeout = null)
    {
        var limit = timeout ?? DefaultTimeout;

        Process? started;
        try
        {
            started = Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new ProcessResult(ProcessResult.NotRun, ex.Message);
        }

        using var process = started ?? throw new InvalidOperationException("Process.Start returned null.");

        // Drain both pipes concurrently; reading them in sequence deadlocks once either fills.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit((int)limit.TotalMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }

            return new ProcessResult(
                ProcessResult.NotRun,
                $"'{startInfo.FileName}' timed out after {limit.TotalMinutes:0.#} minute(s).");
        }

        var output = string.Join(
            Environment.NewLine,
            stdout.GetAwaiter().GetResult(),
            stderr.GetAwaiter().GetResult());

        return new ProcessResult(process.ExitCode, output);
    }

    /// <summary>
    /// Writes and runs a batch script. MSVC is reached this way because <c>VsDevCmd.bat</c> sets up
    /// the compiler environment for the rest of the script, and because batching the compile, link,
    /// and run steps into one script pays that setup cost once.
    /// </summary>
    public static ProcessResult RunCmdScript(
        string scriptPath,
        IEnumerable<string> lines,
        string workingDirectory,
        TimeSpan? timeout = null)
    {
        File.WriteAllLines(scriptPath, lines);

        var startInfo = Create(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", workingDirectory);
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add(scriptPath);

        return Run(startInfo, timeout);
    }

    public static string QuoteForCmd(string path)
        => "\"" + path.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary>Quotes a response-file argument, which needs quoting only when it contains a space.</summary>
    public static string QuoteRspArgument(string argument)
        => argument.Contains(' ', StringComparison.Ordinal) ? "\"" + argument + "\"" : argument;

    public static string? FindOnPath(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), executable);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry is not worth failing over.
            }
        }

        return null;
    }
}
