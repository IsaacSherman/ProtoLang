using ProtoLang.Ir;
using Xunit;

namespace ProtoLang.Tests.Conformance;

/// <summary>One conformance vector: a ProtoLang source file whose <c>test</c> blocks are the vectors.</summary>
internal sealed record ConformanceVector(string Name, string SourcePath);

/// <summary>
/// Discovers the conformance corpus under <c>tests/conformance/</c>.
/// </summary>
/// <remarks>
/// Spec 25.2 left the vector format open and sketched a YAML file. This repository answers that
/// question with the ProtoLang <c>test</c> declaration of spec 25.3 instead: it is already parsed,
/// bound, and type-checked against protobuf descriptors, so a vector whose expectation has the
/// wrong type is a compile error rather than a runtime surprise, and each backend already knows how
/// to lower one into a runnable test.
/// </remarks>
internal static class ConformanceVectors
{
    public const string SchemaFileName = "conformance.proto";

    public static string RootDirectory { get; } =
        Path.Combine(TestPaths.RepositoryRoot, "tests", "conformance");

    public static string ProtoDirectory { get; } = Path.Combine(RootDirectory, "protos");

    public static string VectorDirectory { get; } = Path.Combine(RootDirectory, "vectors");

    /// <summary>Every vector, in a stable order so failures reproduce.</summary>
    public static IReadOnlyList<ConformanceVector> All { get; } = Discover();

    /// <summary>Vector names, for a theory that runs one case per vector.</summary>
    public static TheoryData<string> Names
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var vector in All)
            {
                data.Add(vector.Name);
            }

            return data;
        }
    }

    public static ConformanceVector ByName(string name)
        => All.Single(vector => string.Equals(vector.Name, name, StringComparison.Ordinal));

    public static CompilationResult Compile(ConformanceVector vector)
        => Compilation.Compile(vector.SourcePath, [ProtoDirectory]);

    /// <summary>
    /// The backend-independent name of every test the corpus declares. Both backends report these
    /// back, which is what turns "each backend passed" into "both backends ran the same vectors".
    /// </summary>
    public static IReadOnlyList<string> DeclaredIdentities(IEnumerable<IrTest> tests)
        => tests.Select(test => test.Identity).ToList();

    private static IReadOnlyList<ConformanceVector> Discover()
        => Directory.Exists(VectorDirectory)
            ? Directory.GetFiles(VectorDirectory, "*.protolang")
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(path => new ConformanceVector(Path.GetFileNameWithoutExtension(path), path))
                .ToList()
            : [];
}
