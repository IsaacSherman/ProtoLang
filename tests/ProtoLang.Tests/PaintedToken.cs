using ProtoLang.Diagnostics;
using ProtoLang.LanguageServer.Hosting;
using ProtoLang.LanguageServer.Protocol.Lsp;

namespace ProtoLang.Tests;

/// <summary>One classified token, back in absolute coordinates and named categories.</summary>
/// <remarks>
/// <para>
/// Undoing the delta encoding is the only way to assert anything about it, and it is written out here
/// rather than compared against a hand-computed array of integers, because an expected array says
/// nothing to a reader and has to be recomputed by hand every time a fixture is touched.
/// </para>
/// <para>
/// One decoder rather than one per suite. Two would agree until the day one of them was taught about
/// modifiers and the other was not, and the suite reading the stale one would go on passing while
/// describing something the server had stopped sending.
/// </para>
/// </remarks>
internal readonly record struct PaintedToken(
    int Line, int Character, int Length, string Type, int Modifiers)
{
    public static List<PaintedToken> Decode(IReadOnlyList<int> data)
    {
        var painted = new List<PaintedToken>();

        var line = 0;
        var character = 0;

        for (var index = 0; index + 4 < data.Count; index += SemanticTokens.IntegersPerToken)
        {
            line += data[index];
            character = data[index] == 0 ? character + data[index + 1] : data[index + 1];

            painted.Add(new PaintedToken(
                line,
                character,
                data[index + 2],
                SemanticTokenLegend.TokenTypes[data[index + 3]],
                data[index + 4]));
        }

        return painted;
    }

    /// <summary>Whether this token carries <paramref name="modifier"/>, by its legend name.</summary>
    /// <remarks>
    /// A name the legend does not carry throws rather than answering false. A misspelled modifier
    /// would otherwise make every <c>Assert.False(token.Has(...))</c> in the suite pass for the wrong
    /// reason, which is the failure a test helper is least able to notice about itself.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The legend declares no such modifier.</exception>
    public bool Has(string modifier) => (Modifiers & (1 << BitOf(modifier))) != 0;

    private static int BitOf(string modifier)
    {
        for (var bit = 0; bit < SemanticTokenLegend.TokenModifiers.Count; bit++)
        {
            if (string.Equals(SemanticTokenLegend.TokenModifiers[bit], modifier, StringComparison.Ordinal))
            {
                return bit;
            }
        }

        throw new ArgumentOutOfRangeException(
            nameof(modifier), modifier, "The legend does not carry that modifier.");
    }

    /// <summary>The text this token covers, which is how an assertion names one.</summary>
    public string TextIn(string source)
    {
        var lines = new LineMap(source);

        return source.Substring(lines.OffsetOf(Line + 1, Character + 1), Length);
    }
}
