using ProtoLang.LanguageServer.Protocol.Lsp;

namespace ProtoLang.LanguageServer.Hosting;

/// <summary>
/// What changed between the classification a client holds and the one it would be sent now.
/// </summary>
/// <remarks>
/// <para>
/// <b>One edit, from the common prefix and the common suffix.</b> That is not a shortcut around a
/// real diff -- it is what the encoding makes true. The integers are already relative: a token's line
/// is a delta from the token before it, so an edit on one line changes the numbers of the tokens on
/// that line and the line delta of the first token after it, and leaves every number after that
/// identical. Typing therefore produces exactly one contiguous run of changed integers, and finding
/// it is finding where the two arrays stop agreeing at each end.
/// </para>
/// <para>
/// <b>Both ends are rounded back to a token boundary.</b> Two tokens can share four of their five
/// integers by coincidence -- same length, same category, same modifiers, one column apart -- and an
/// edit that started or ended inside a token would hand the client a stream that decodes into
/// nonsense from that point on. Rounding costs at most eight surplus integers and removes the whole
/// class of failure.
/// </para>
/// <para>
/// No edits at all is the honest answer for two identical answers, and the common case for a
/// keystroke that changed no token: LSP takes an empty edit list as "nothing changed".
/// </para>
/// </remarks>
public static class SemanticTokenDiff
{
    /// <summary>The edits that turn <paramref name="previous"/> into <paramref name="next"/>.</summary>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public static IReadOnlyList<SemanticTokensEdit> Between(
        IReadOnlyList<int> previous, IReadOnlyList<int> next)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(next);

        var shortest = Math.Min(previous.Count, next.Count);
        var prefix = OnATokenBoundary(AgreeingFromTheStart(previous, next, shortest));
        var suffix = OnATokenBoundary(AgreeingFromTheEnd(previous, next, shortest - prefix));

        var deleted = previous.Count - prefix - suffix;
        var inserted = next.Count - prefix - suffix;

        if (deleted == 0 && inserted == 0)
        {
            return [];
        }

        return
        [
            new SemanticTokensEdit
            {
                Start = prefix,
                DeleteCount = deleted,
                Data = inserted == 0 ? null : [.. next.Skip(prefix).Take(inserted)],
            },
        ];
    }

    private static int AgreeingFromTheStart(
        IReadOnlyList<int> previous, IReadOnlyList<int> next, int room)
    {
        var agreed = 0;

        while (agreed < room && previous[agreed] == next[agreed])
        {
            agreed++;
        }

        return agreed;
    }

    /// <param name="room">
    /// How much is left after the common prefix, so the two runs cannot overlap and claim one integer
    /// twice.
    /// </param>
    private static int AgreeingFromTheEnd(
        IReadOnlyList<int> previous, IReadOnlyList<int> next, int room)
    {
        var agreed = 0;

        while (agreed < room
            && previous[previous.Count - 1 - agreed] == next[next.Count - 1 - agreed])
        {
            agreed++;
        }

        return agreed;
    }

    /// <remarks>
    /// Rounded down, always: a shorter run of agreement is only a larger edit, while a longer one
    /// would be a claim of agreement that is not there.
    /// </remarks>
    private static int OnATokenBoundary(int integers)
        => integers - (integers % SemanticTokens.IntegersPerToken);
}
