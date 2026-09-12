using ProtoLang.LanguageServer.Protocol.Lsp;

namespace ProtoLang.LanguageServer.Hosting;

/// <summary>
/// The part of the legend this client said it can paint, and what to publish instead for the part it
/// cannot.
/// </summary>
/// <remarks>
/// <para>
/// <b>The legend on the wire does not narrow; only what is emitted into it does.</b> Spec 6.5 fixed
/// the published category set precisely so it is negotiated once and never renumbered, and
/// <see cref="SemanticTokenLegend.Wire"/> still carries the whole standard set to every client. What
/// this decides is a different question: having worked out that a name is an <c>enumMember</c>, is
/// there any point telling a client that never claimed to know what an <c>enumMember</c> is? A client
/// that meets a category it has no rule for renders the token with no colour at all, which is worse
/// than the <c>variable</c> it has been painting since #42.
/// </para>
/// <para>
/// <b>A client that stated no list is taken to support the standard set.</b> LSP requires
/// <c>tokenTypes</c> of a client that asks for semantic tokens at all, so an absent list means this
/// server is talking to something that did not fill the capability in rather than something that
/// supports nothing -- and reading it as "supports nothing" would silently switch the feature off for
/// every such client, including this repository's own test client. An <em>empty</em> list is read
/// literally, because a client that sent one said something.
/// </para>
/// <para>
/// <b>The lexical answer is never withheld.</b> #42 published <c>variable</c>, <c>keyword</c>,
/// <c>string</c>, <c>number</c>, <c>comment</c> and <c>operator</c> to every client unconditionally,
/// and spec 6.5 says classification never fails. This issue adds colour and may not take any away, so
/// a category the client did not declare degrades to what it was already being sent and no further.
/// </para>
/// </remarks>
public sealed class ClientLegend
{
    private readonly bool[] _categories;
    private readonly int _modifiers;

    private ClientLegend(bool[] categories, int modifiers)
    {
        _categories = categories;
        _modifiers = modifiers;
    }

    /// <summary>A client that can paint everything the legend declares.</summary>
    /// <remarks>
    /// What the lexical path uses, and what a client that stated no lists is treated as. Shared
    /// rather than rebuilt because it holds nothing about any one client.
    /// </remarks>
    public static ClientLegend Everything { get; } = new(
        [.. SemanticTokenLegend.TokenTypes.Select(_ => true)],
        AllOf(SemanticTokenLegend.TokenModifiers));

    /// <summary>What <paramref name="capabilities"/> said this client can paint.</summary>
    public static ClientLegend Of(SemanticTokensClientCapabilities? capabilities)
    {
        if (capabilities is null)
        {
            return Everything;
        }

        bool[] categories = capabilities.TokenTypes is { } types
            ? [.. SemanticTokenLegend.TokenTypes.Select(type => types.Contains(type, StringComparer.Ordinal))]
            : Everything._categories;

        var modifiers = capabilities.TokenModifiers is { } declared
            ? BitsOf(declared)
            : Everything._modifiers;

        return new ClientLegend(categories, modifiers);
    }

    /// <summary>
    /// The category to publish for a name this server refined: <paramref name="refined"/> where the
    /// client can paint it, and <paramref name="lexical"/> where it cannot.
    /// </summary>
    public int Category(int refined, int lexical)
        => _categories[refined] ? refined : lexical;

    /// <summary>The bits of <paramref name="modifiers"/> this client declared, and no others.</summary>
    public int Modifiers(int modifiers) => modifiers & _modifiers;

    private static int BitsOf(IReadOnlyList<string> declared)
    {
        var bits = 0;

        for (var index = 0; index < SemanticTokenLegend.TokenModifiers.Count; index++)
        {
            if (declared.Contains(SemanticTokenLegend.TokenModifiers[index], StringComparer.Ordinal))
            {
                bits |= 1 << index;
            }
        }

        return bits;
    }

    private static int AllOf(IReadOnlyList<string> modifiers) => (1 << modifiers.Count) - 1;
}
