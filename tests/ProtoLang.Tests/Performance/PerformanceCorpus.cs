using ProtoLang.Semantics;
using ProtoLang.Symbols;

namespace ProtoLang.Tests.Performance;

/// <summary>
/// The two files every performance number in <c>docs/performance.md</c> is taken against, and the
/// few questions the measurements and their guards both need to ask of them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two files and not a curve.</b> #57 asks for a representative size, stated rather than assumed,
/// plus a larger case so the numbers say something at scale. A curve over generated sizes would
/// answer a different and harder question -- how cost grows -- and a growth rate is not something a
/// budget can be checked against or a regression noticed in. So: one file somebody wrote, one file
/// large enough to be uncomfortable, both fixed.
/// </para>
/// <para>
/// <b>The normal case is a real file on purpose.</b> <c>examples/simpleScript.protolang</c> is
/// maintained for its own reasons and will go on being edited by people who are not thinking about
/// this, which is exactly what keeps it representative. A fixture written for measurement drifts
/// towards whatever the measurement finds convenient.
/// </para>
/// </remarks>
internal static class PerformanceCorpus
{
    public const string Normal = "normal";
    public const string Stress = "stress";

    /// <summary>Where the named half of the corpus lives.</summary>
    public static string PathOf(string which) => which switch
    {
        Normal => TestPaths.SimpleScript,
        Stress => StressCorpus.Path,
        _ => throw new ArgumentOutOfRangeException(nameof(which), which, "not a corpus file"),
    };

    /// <summary>The named half of the corpus, as text.</summary>
    public static string TextOf(string which) => which switch
    {
        Normal => File.ReadAllText(TestPaths.SimpleScript),
        Stress => StressCorpus.Text,
        _ => throw new ArgumentOutOfRangeException(nameof(which), which, "not a corpus file"),
    };

    /// <summary>How many lines it holds, which is the size #57 asks to be stated.</summary>
    public static int Lines(string which) => TextOf(which).Count(character => character == '\n');

    /// <summary>
    /// The named half compiled from disk, against the schemas the examples import.
    /// </summary>
    /// <remarks>
    /// Both files import <c>invoice.proto</c>, which is the examples' schema rather than the test
    /// fixtures' -- deliberately, so that the corpus depends on something the repository ships and
    /// not on a schema written to make measurement easy.
    /// </remarks>
    public static CompilationResult Compile(string which)
        => Compilation.Compile(PathOf(which), [TestPaths.ExampleProtoDirectory]);

    /// <summary>How many places name the method called <paramref name="method"/>.</summary>
    /// <remarks>
    /// Asked through the reference index rather than counted in the text, because the index is what
    /// occurrence highlighting walks and the text is not. The declaration is found by its own
    /// spelling and then never mentioned again: everything after that is a <see cref="SymbolId"/>,
    /// so a second method of the same name elsewhere could not be folded in by accident.
    /// </remarks>
    public static int ReferencesTo(string which, string method)
    {
        var text = TextOf(which);
        var model = SemanticModel.For(Compile(which));
        var declaration = text.IndexOf($"fn {method}(", StringComparison.Ordinal);

        if (declaration < 0 || model.ReferenceAt(declaration + "fn ".Length) is not { } reference)
        {
            return 0;
        }

        return model.ReferencesTo(reference.Symbol).Count;
    }

    /// <summary>How many names resolve at <paramref name="offset"/>.</summary>
    public static int NamesInScopeAt(string which, int offset)
        => SemanticModel.For(Compile(which)).ScopeAt(offset)?.Names.Count ?? 0;
}
