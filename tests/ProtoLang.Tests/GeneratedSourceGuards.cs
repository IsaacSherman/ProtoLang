using ProtoLang.Backend;
using Xunit;

namespace ProtoLang.Tests;

/// <summary>
/// Vacuous-pass guards shared by the two backend smoke suites.
/// </summary>
/// <remarks>
/// Both suites make their point by compiling and executing compiler output for
/// <see cref="TestPaths.SimpleScript"/>, so their coverage is exactly whatever that one example
/// happens to use. These assertions pin the constructs it is expected to exercise: if the example
/// is ever trimmed, or a backend quietly stops emitting one of them, the smoke tests fail instead
/// of passing over a smaller language than they claim to cover.
/// </remarks>
internal static class GeneratedSourceGuards
{
    /// <summary>
    /// Asserts that the emitted sources use every control-flow construct the language has.
    /// </summary>
    /// <param name="backend">Backend name, used only in the failure message.</param>
    /// <param name="iterationFragment">
    /// How this backend spells iteration over a repeated field: C# emits <c>foreach (</c> and C++
    /// emits a range <c>for (</c>. Everything else is spelled identically by both.
    /// </param>
    public static void AssertExercisesControlFlow(
        string backend,
        string iterationFragment,
        IEnumerable<GeneratedFile> files)
    {
        var emitted = string.Join("\n", files.Select(file => file.Contents));
        var lines = emitted.Split('\n').Select(line => line.Trim()).ToArray();

        Require("if", emitted.Contains("if (", StringComparison.Ordinal));
        Require("else if", emitted.Contains("else if (", StringComparison.Ordinal));

        // A bare 'else' occupies its own line, so match the line rather than the word: searching
        // the whole text for "else" would be satisfied by the 'else if' above.
        Require("else", lines.Contains("else"));

        Require("while", emitted.Contains("while (", StringComparison.Ordinal));
        Require("while true", emitted.Contains("while (true)", StringComparison.Ordinal));
        Require("break", lines.Contains("break;"));
        Require("continue", lines.Contains("continue;"));
        Require("for ... in", emitted.Contains(iterationFragment, StringComparison.Ordinal));

        void Require(string construct, bool appears) => Assert.True(
            appears,
            $"The {backend} smoke test compiles generated code that never uses '{construct}', so it "
            + $"proves nothing about it. Either the backend stopped emitting '{construct}', or "
            + $"{Path.GetFileName(TestPaths.SimpleScript)} stopped using it.");
    }
}
