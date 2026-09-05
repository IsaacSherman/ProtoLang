namespace ProtoLang.Tests.Harness;

/// <summary>
/// Executables that stand in for protoc when what is being tested is the supervision rather than the
/// compilation.
/// </summary>
/// <remarks>
/// <para>
/// Written on the spot rather than checked in as binaries, so the suite asks nothing of the machine
/// it did not already ask, and because what each has to do differs by platform in ways a binary could
/// not hide.
/// </para>
/// <para>
/// The two shells see protoc's first argument differently, and neither is wrong: cmd counts an equals
/// sign as an argument delimiter, so <c>--descriptor_set_out=path</c> arrives as <c>%1</c> and
/// <c>%2</c>, while a POSIX shell hands the whole thing over as <c>$1</c> and leaves the taking apart
/// to the script.
/// </para>
/// </remarks>
public static class StandInProtoc
{
    /// <summary>A protoc that sleeps instead of compiling, whatever it is handed.</summary>
    /// <remarks>
    /// A minute is both long enough that no wait measured in milliseconds can outlast it and short
    /// enough to be a leash: a kill that somehow failed strands the process on one test rather than
    /// on the machine. The sleep happens in a child of the script, so a kill has the process tree to
    /// walk that a real protoc running a plugin would give it.
    /// </remarks>
    public static string Sleeping()
        => Write(
            windows:
            [
                "@echo off",

                // ping rather than timeout.exe, which refuses to run at all when input is redirected
                // -- and what the test host hands this process for stdin is not ours to choose.
                "ping -n 60 127.0.0.1 > nul",
            ],
            posix:
            [
                "#!/bin/sh",

                // Not exec: the shell stays, so the sleep is a child and the kill has a tree here too.
                "sleep 60",
            ]);

    /// <summary>
    /// A protoc that refuses the schema and leaves a descriptor set behind that cannot be deleted.
    /// </summary>
    /// <remarks>
    /// A real file that a delete really cannot remove, which is the only fixture with any teeth: the
    /// cleanup this exercises was guarded by <c>File.Exists</c>, so anything that is not a file -- a
    /// directory left where the descriptor set should be, say -- is skipped rather than attempted,
    /// and a test built on one passes whether the defect is present or not.
    /// <para>
    /// What makes a delete fail differs by platform. Windows refuses to remove a file marked
    /// read-only; a POSIX unlink does not consult the file at all, only the directory holding it, so
    /// there it is the directory that loses its write permission. <see cref="Unlock"/> is the other
    /// half of each.
    /// </para>
    /// </remarks>
    public static string Obstructive()
        => Write(
            windows:
            [
                "@echo off",
                "echo refused> %2",
                "attrib +r %2",
                "echo protoc: refused 1>&2",
                "exit /b 1",
            ],
            posix:
            [
                "#!/bin/sh",
                "out=\"${1#--descriptor_set_out=}\"",
                "echo refused > \"$out\"",
                "chmod 500 \"$(dirname \"$out\")\"",
                "echo 'protoc: refused' >&2",
                "exit 1",
            ]);

    /// <summary>Undoes what <see cref="Obstructive"/> did, so the next delete can succeed.</summary>
    public static void Unlock(string temporaryDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var file in Directory.GetFiles(temporaryDirectory))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            return;
        }

        File.SetUnixFileMode(
            temporaryDirectory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static string Write(string[] windows, string[] posix)
    {
        var directory = TestPaths.CreateTempDirectory();

        if (OperatingSystem.IsWindows())
        {
            var batch = Path.Combine(directory, "protoc.cmd");
            File.WriteAllLines(batch, windows);

            return batch;
        }

        var script = Path.Combine(directory, "protoc");
        File.WriteAllLines(script, posix);
        File.SetUnixFileMode(
            script,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        return script;
    }
}
