namespace ProtoLang.Tests;

internal static class TestPaths
{
    /// <summary>Walks up from the test binaries to the directory holding the solution file.</summary>
    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    public static string ExamplesDirectory => Path.Combine(RepositoryRoot, "examples");

    public static string ExampleProtoDirectory => Path.Combine(ExamplesDirectory, "protos");

    public static string SimpleScript => Path.Combine(ExamplesDirectory, "simpleScript.protolang");

    /// <summary>Test-only schemas covering shapes the examples do not, such as nested enums.</summary>
    public static string FixtureProtoDirectory
        => Path.Combine(RepositoryRoot, "tests", "ProtoLang.Tests", "protos");

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ProtoLang.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate the repository root above '{AppContext.BaseDirectory}'.");
    }

    /// <summary>
    /// Writes <paramref name="source"/> to a temporary .protolang file that imports the example
    /// invoice schema, so binder tests can exercise real descriptors.
    /// </summary>
    public static string WriteTempScript(string source)
    {
        var directory = Path.Combine(Path.GetTempPath(), "protolang-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, "test.protolang");
        File.WriteAllText(path, source);
        return path;
    }
}
