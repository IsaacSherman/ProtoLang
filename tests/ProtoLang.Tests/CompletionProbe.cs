using ProtoLang.Binding;
using ProtoLang.Diagnostics;
using ProtoLang.LanguageServer.Hosting;
using ProtoLang.LanguageServer.Protocol;
using ProtoLang.LanguageServer.Protocol.Lsp;
using ProtoLang.LanguageServer.Workspace;
using Xunit;
using Diagnostic = ProtoLang.Diagnostics.Diagnostic;

namespace ProtoLang.Tests;

/// <summary>One offered item, accepted, and what the compiler then made of the result.</summary>
internal sealed record AppliedItem(
    int Caret,
    CompletionItem Item,
    string Applied,
    int InsertedStart,
    int InsertedEnd,
    CompilationResult Result)
{
    /// <summary>Diagnostics about the text this item inserted, rather than about the rest of the file.</summary>
    /// <remarks>
    /// Intersection with the inserted range rather than a set difference against the diagnostics the
    /// buffer already had, because an edit shifts every span after it: the same diagnostic about the
    /// same mistake is at a different offset before and after, and comparing sets would report every
    /// one of them as new.
    /// </remarks>
    public IEnumerable<Diagnostic> About
        => Result.Diagnostics.Where(diagnostic
            => diagnostic.Span.Start.Offset <= InsertedEnd && diagnostic.Span.End.Offset >= InsertedStart);
}

/// <summary>
/// Accepts every item completion offers and asks the compiler what happened, which is the only way
/// the rule the feature rests on can be checked at all.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists rather than tests that read lists.</b> "Nothing is offered that would not bind"
/// is a claim about every item at every position, and reading a list can only ever check the items
/// somebody thought to look at. Applying each one and recompiling checks the claim itself, and keeps
/// checking it as the language grows: a construct added later that breaks completion fails here
/// rather than being found by a user.
/// </para>
/// <para>
/// <b>One recompile per item, and why it is still cheap.</b> The scope query's probe splices every
/// offered name into one buffer and compiles once, which works because those probes are independent
/// statements. A completion item is an edit that <em>replaces a range</em>, and two items at one
/// caret conflict, so they cannot share a buffer. What makes the sweep affordable instead is that
/// every recompile goes through one loader and therefore one descriptor cache, so protoc runs once
/// for the schema set however many items are applied -- and the applied text is compiled from memory,
/// so nothing touches the disk either.
/// </para>
/// <para>
/// <b>What counts as a failure.</b> Not "the buffer has no errors": half the corpus does not parse,
/// which is the point of sweeping it. Only a diagnostic that overlaps the text the item inserted, and
/// only one of <see cref="DidNotBind"/> -- the codes that say a name did not resolve to anything. A
/// type error is a different thing and is allowed: offering a string field where an int64 is wanted
/// is a name that bound perfectly well.
/// </para>
/// </remarks>
internal static class CompletionProbe
{
    /// <summary>The codes that mean a name did not bind, which is what an offered item must never cause.</summary>
    /// <remarks>
    /// <para>
    /// Every way the binder can say "what you wrote here names nothing": an unknown or ambiguous
    /// message (<c>PL0020</c>, <c>PL0021</c>) or type (<c>PL0025</c>, <c>PL0074</c>), an unknown name
    /// (<c>PL0037</c>), a member that is not there or cannot be reached (<c>PL0038</c> to
    /// <c>PL0041</c>), a call that cannot resolve (<c>PL0042</c> to <c>PL0044</c>), an unknown enum
    /// value (<c>PL0076</c>), and a fixture field or argument that names nothing (<c>PL0059</c>,
    /// <c>PL0068</c>). Codes for contexts not yet built are here from the start, because the cost of
    /// listing one early is nothing and the cost of forgetting one is a sweep that passes over
    /// exactly the defect it exists to find.
    /// </para>
    /// <para>
    /// <c>PL0078</c> is deliberately absent. A message field with no established presence is a name
    /// that resolved and then drew a diagnostic about its <em>value</em>, and withholding it would
    /// hide the field from the author who has to write the guard -- the same judgement
    /// <c>ScopeSearch.ReachableFields</c> already makes.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyList<string> DidNotBind =
    [
        "PL0020", "PL0021", "PL0025", "PL0037", "PL0038", "PL0039", "PL0040", "PL0041",
        "PL0042", "PL0043", "PL0044", "PL0059", "PL0068", "PL0074", "PL0076",
    ];

    /// <summary>Every offset at which a client would ask because a dot was just typed.</summary>
    public static IEnumerable<int> AfterEveryDot(string text)
    {
        for (var offset = 0; offset < text.Length; offset++)
        {
            if (text[offset] == '.')
            {
                yield return offset + 1;
            }
        }
    }

    /// <summary>Every offset where a name begins, and every one where a statement could.</summary>
    /// <remarks>
    /// Both, because they are different questions with different answers: a name half-typed is an
    /// expression position, and the blank space after a semicolon is a statement position, and a
    /// keyword legal in one is illegal in the other. Sweeping only the first would leave the whole
    /// statement-keyword set unchecked.
    /// </remarks>
    public static IEnumerable<int> AtEveryNameAndStatementStart(string text)
    {
        for (var offset = 0; offset < text.Length; offset++)
        {
            var previous = offset == 0 ? '\0' : text[offset - 1];

            if (char.IsLetter(text[offset]) && !char.IsLetterOrDigit(previous) && previous != '_')
            {
                // One character in, so the caret sits inside a name being typed rather than before it.
                yield return Math.Min(offset + 1, text.Length);
            }

            if (previous is ';' or '{' or '}')
            {
                yield return offset;
            }
        }
    }

    /// <summary>Offers at every caret, accepts every item, and recompiles each result.</summary>
    public static async Task<IReadOnlyList<AppliedItem>> SweepAsync(
        CompletionProvider provider,
        DocumentUri uri,
        string text,
        IEnumerable<int> carets,
        string path,
        DescriptorLoader loader)
    {
        var lines = new LineMap(text);
        var applied = new List<AppliedItem>();

        foreach (var caret in carets)
        {
            var asked = provider.Read(Ask(uri, lines, caret));

            if (asked is null)
            {
                continue;
            }

            foreach (var item in (await provider.AnswerAsync(asked, CancellationToken.None)).Items)
            {
                applied.Add(Accept(text, lines, caret, item, path, loader));
            }
        }

        return applied;
    }

    /// <summary>Applies one item exactly as a client would, and compiles what it produced.</summary>
    private static AppliedItem Accept(
        string text, LineMap lines, int caret, CompletionItem item, string path, DescriptorLoader loader)
    {
        Assert.NotNull(item.TextEdit);

        var start = Offset(lines, item.TextEdit!.Range.Start);
        var end = Offset(lines, item.TextEdit.Range.End);
        var inserted = item.TextEdit.NewText;
        var applied = string.Concat(text.AsSpan(0, start), inserted, text.AsSpan(end));

        // Compiled from memory rather than from a file, so a sweep of thousands of items writes
        // nothing: the identity carries the path only so the schemas beside it are the include root.
        var source = new SourceDocument(SourceIdentity.FromPath(path), applied);
        var compilation = new Compilation(source, new CompilationOptions { Loader = loader });

        return new AppliedItem(
            caret, item, applied, start, start + inserted.Length, compilation.Compile(CancellationToken.None));
    }

    private static int Offset(LineMap lines, Position position)
        => lines.OffsetOf(position.Line + 1, position.Character + 1);

    private static CompletionParams Ask(DocumentUri uri, LineMap lines, int offset)
    {
        var position = lines.PositionOf(offset);

        return new CompletionParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = uri.ToString() },
            Position = new Position(position.Line - 1, position.Column - 1),
        };
    }
}
