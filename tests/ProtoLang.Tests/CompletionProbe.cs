using ProtoLang.Binding;
using ProtoLang.Diagnostics;
using ProtoLang.LanguageServer.Hosting;
using ProtoLang.Ir;
using ProtoLang.Semantics;
using ProtoLang.Symbols;
using ProtoLang.Syntax;
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
    /// <c>PL0041</c>), a call that cannot resolve (<c>PL0042</c> to <c>PL0044</c>), a test target
    /// that names no method of its receiver or no receiver at all (<c>PL0057</c>, <c>PL0058</c>), an
    /// unknown enum value (<c>PL0076</c>), and a fixture field or argument that names nothing
    /// (<c>PL0059</c>, <c>PL0068</c>). Codes for contexts not yet built are here from the start,
    /// because the cost of listing one early is nothing and the cost of forgetting one is a sweep
    /// that passes over exactly the defect it exists to find.
    /// </para>
    /// <para>
    /// The two target codes were the case that proved that sentence. They were missing while nothing
    /// answered in a test header, so a receiver offered there could have stranded the method after it
    /// -- <c>test Outer.levels_match</c>, where the message is real and the method is not -- and the
    /// sweep would have applied it, recompiled, seen <c>PL0058</c>, and not recognized the code.
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
        "PL0042", "PL0043", "PL0044", "PL0057", "PL0058", "PL0059", "PL0068", "PL0074", "PL0076",
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

    /// <summary>Every offset where a name is being written, and every one where a statement could start.</summary>
    /// <remarks>
    /// <para>
    /// Both, because they are different questions with different answers: a name half-typed is an
    /// expression position, and the blank space after a semicolon is a statement position, and a
    /// keyword legal in one is illegal in the other. Sweeping only the first would leave the whole
    /// statement-keyword set unchecked.
    /// </para>
    /// <para>
    /// <b>A caret inside a written keyword is left out, and that is a statement about the sweep
    /// rather than about completion.</b> An item accepted there replaces the keyword, and a keyword
    /// is load-bearing in a way a name is not: replacing the <c>if</c> of an <c>else if</c> strands
    /// the block that followed it, and parser recovery then swallows declarations further down the
    /// file -- so the recompile reports that a method is unknown when what actually happened is that
    /// its declaration was eaten. <em>Every</em> item fails such a caret, correct ones included, so it
    /// measures the fixture rather than the items. Completion still answers there, and a client may
    /// still offer it; what is not claimed is that the result compiles.
    /// </para>
    /// <para>
    /// <b>A caret before a dot is left out only when the dot reaches into a value.</b> There, the
    /// name is one link of a chain and replacing a link says nothing about the links that follow:
    /// whether <c>seconds</c> is a field of whatever the receiver has just been changed to is the
    /// author's next edit rather than this item's fault. Inside a qualified name it is the opposite,
    /// because there are no links -- <c>protolang.tests.Outer</c> is one name, an item there replaces
    /// the whole of it, and nothing survives to be broken. Excluding those was the sweep declining to
    /// look at precisely the region where two defects were then found by hand, so the exclusion is
    /// now asked of the parser rather than of the character after the word: a dot is a qualifier when
    /// the parser put it inside a type reference, an <c>extend</c> receiver or a test target, and a
    /// member access otherwise.
    /// </para>
    /// </remarks>
    public static IEnumerable<int> AtEveryNameAndStatementStart(string text)
    {
        var keywords = KeywordSpans(text);
        var qualified = QualifiedNamesIn(text);

        for (var offset = 0; offset < text.Length; offset++)
        {
            var previous = offset == 0 ? '\0' : text[offset - 1];

            if (char.IsLetter(text[offset]) && !char.IsLetterOrDigit(previous) && previous != '_')
            {
                // One character in, so the caret sits inside a name being typed rather than before it.
                var caret = Math.Min(offset + 1, text.Length);
                var after = EndOfWord(text, offset);
                var reaches = after < text.Length
                    && text[after] == '.'
                    && !qualified.Any(name => Covers(name, offset));

                if (!keywords.Contains(offset) && !reaches)
                {
                    yield return caret;
                }
            }

            if (previous is ';' or '{' or '}')
            {
                yield return offset;
            }
        }
    }

    private static bool Covers(SourceSpan span, int offset)
        => offset >= span.Start.Offset && offset <= span.End.Offset;

    /// <summary>Every span the parser decided was one qualified name rather than a chain of members.</summary>
    /// <remarks>
    /// The three places the grammar joins dotted identifiers into a single name: a type reference,
    /// which is the four positions a type may be written in; the receiver of an <c>extend</c>; and a
    /// test's target. Everything else with a dot in it is a member access on a value, and the parser
    /// is the one that already made that distinction -- taking it from here rather than guessing at
    /// the token stream is what keeps this sweep from holding a second opinion about the grammar it
    /// is meant to be probing.
    /// </remarks>
    private static IReadOnlyList<SourceSpan> QualifiedNamesIn(string text)
    {
        var diagnostics = new DiagnosticBag();
        var tokens = new Lexer(text, SourceIdentity.UnsavedName, diagnostics).Tokenize();
        var unit = new Parser(tokens, SourceIdentity.UnsavedName, diagnostics).ParseCompilationUnit();

        var names = new List<SourceSpan>();
        var pending = new Stack<SyntaxNode>();

        pending.Push(unit);

        while (pending.Count > 0)
        {
            var node = pending.Pop();

            switch (node)
            {
                case TypeReference reference:
                    names.Add(reference.Name.Span);
                    break;

                case ExtendDeclaration extend:
                    names.Add(extend.MessageName.Span);
                    break;

                case TestTarget target:
                    names.Add(SourceSpan.Union(target.Receiver.Span, target.Method.Span));
                    break;
            }

            foreach (var child in SyntaxWalk.ChildrenOf(node))
            {
                pending.Push(child);
            }
        }

        return names;
    }

    /// <summary>One past the last character of the word beginning at <paramref name="start"/>.</summary>
    private static int EndOfWord(string text, int start)
    {
        var end = start;

        while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_'))
        {
            end++;
        }

        return end;
    }

    /// <summary>Where every keyword in the text begins, taken from the lexer rather than guessed.</summary>
    private static HashSet<int> KeywordSpans(string text)
        => [.. new Lexer(text, SourceIdentity.UnsavedName, new DiagnosticBag())
            .Tokenize()
            .Where(token => token.Kind.IsKeyword())
            .Select(token => token.Span.Start.Offset)];

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
        var written = Written(text, path, loader);

        foreach (var caret in carets)
        {
            var asked = provider.Read(Ask(uri, lines, caret));
            var here = new List<AppliedItem>();

            if (asked is not null)
            {
                foreach (var item in (await provider.AnswerAsync(asked, CancellationToken.None)).Items)
                {
                    here.Add(Accept(text, lines, caret, item, path, loader));
                }
            }

            RequireTheWrittenNameIsOffered(written, text, caret, here);
            applied.AddRange(here);
        }

        return applied;
    }

    /// <summary>The buffer as it stands, so the sweep can ask what the names in it already mean.</summary>
    private static SemanticModel Written(string text, string path, DescriptorLoader loader)
        => SemanticModel.For(
            new Compilation(
                new SourceDocument(SourceIdentity.FromPath(path), text),
                new CompilationOptions { Loader = loader }).Compile(CancellationToken.None));

    /// <summary>
    /// A name that already resolves must be among the things offered where it is written.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The other half of the promise, and the half nothing else here can check.</b> Every other
    /// assertion in this file is of the form "what was offered binds", and an empty list satisfies
    /// all of them perfectly -- so the whole class of defect where completion falls silent, or drops
    /// the one item that was right, passes a sweep without a mark. That is not hypothetical: a filter
    /// added to make a qualified-name edit sound left two carets offering nothing at all, and every
    /// soundness check stayed green through it.
    /// </para>
    /// <para>
    /// The claim is narrow enough to be true everywhere. It is made only where the compiler says the
    /// caret is on a name that resolved to something, so a half-typed name, an unresolved one, a
    /// literal and an operator are all outside it. And what is demanded is only that <em>some</em>
    /// item reproduces the buffer exactly: completion is not being told what to rank first, or what
    /// else to include, only that the answer the author has already written is somewhere in it.
    /// Reaccepting a name is what a client does constantly -- the list is opened, filtered by typing
    /// the name that is already there, and dismissed -- so an item that cannot do it is a list that
    /// cannot be used without changing the code.
    /// </para>
    /// <para>
    /// A declaration is not a use and is excluded: the caret on the <c>f</c> of <c>fn f()</c> is
    /// naming something new, and there is nothing for completion to have offered.
    /// </para>
    /// <para>
    /// <b>Two positions are known not to keep it, and they are held to something else rather than
    /// waved past.</b> A test's target and the qualified name of an enum in front of one of its
    /// constants are both places completion has no context for at all, so they answer nothing --
    /// which is a gap in the feature and not a place where this rule stops applying. Excusing them
    /// outright would make the exclusion permanent by being invisible; instead they are required to
    /// go on answering <em>nothing</em>. The day either grows a context, it offers something, this
    /// assertion fails, and whoever built it is told to delete the line that was waiting for them.
    /// </para>
    /// </remarks>
    private static void RequireTheWrittenNameIsOffered(
        SemanticModel written, string text, int caret, IReadOnlyList<AppliedItem> here)
    {
        // On the name, rather than merely inside it. A caret in the gap between two segments of a
        // qualified name -- after the dot of 'protolang.' and before the line holding 'tests.Outer'
        // -- is covered by that name's reference and is on none of its words, so reproducing the
        // buffer there would mean accepting an item that inserts nothing, which is not a thing a
        // completion list contains. The whole name still resolves; there is simply no text at the
        // caret for an item to have replaced.
        if (!OnAWord(text, caret))
        {
            return;
        }

        if (written.ReferenceAt(caret) is not { Kind: ReferenceKind.Read or ReferenceKind.Write } name)
        {
            return;
        }

        if (here.Any(attempt => attempt.Applied == text))
        {
            return;
        }

        Assert.True(
            here.Count == 0 && HasNoContextYet(written, caret),
            $"the name at offset {caret} resolves to {name.Symbol.Key}, so completion must offer "
                + $"something that reproduces it; it offered {here.Count} item(s): "
                + string.Join(", ", here.Take(8).Select(attempt => attempt.Item.Label)));
    }

    /// <summary>Whether a word character sits on either side of the caret.</summary>
    /// <remarks>
    /// The same rule <c>SchemaSubject.WordAt</c> applies, which is what decides the range an item
    /// would replace: adjacency on either side, because a caret has as much claim to the name it has
    /// just finished typing as to the one it is standing inside.
    /// </remarks>
    private static bool OnAWord(string text, int caret)
        => (caret < text.Length && IsWordCharacter(text[caret]))
            || (caret > 0 && IsWordCharacter(text[caret - 1]));

    private static bool IsWordCharacter(char character)
        => char.IsLetterOrDigit(character) || character == '_';

    /// <summary>Whether the caret is in one of the positions completion does not yet answer in.</summary>
    /// <inheritdoc cref="RequireTheWrittenNameIsOffered" path="/remarks/para[3]"/>
    private static bool HasNoContextYet(SemanticModel written, int caret)
    {
        var at = written.SyntaxAt(caret);

        // A target written without a dot is a method and no receiver, so what is missing at
        // 'test f' is a name and a dot together and no single name completes it. Both halves of a
        // target that has a receiver are answered.
        if (at?.Enclosing<TestTarget>() is { Receiver.IsMissing: true })
        {
            return true;
        }

        // The name in front of an enum constant is a type written in expression position, so the
        // parser built a chain of member accesses rather than one qualified name -- and every dot in
        // it reaches the constant's own enum, whose members are its constants rather than the next
        // segment of its name.
        return written.IrAt(caret)?.Enclosing<IrEnumValue>() is not null
            && at?.Ancestors.OfType<MemberAccessExpression>().LastOrDefault() is { } constant
            && Covers(constant.Receiver.Span, caret);
    }

    /// <summary>Applies one item exactly as a client would, and compiles what it produced.</summary>
    /// <remarks>
    /// The range is checked before it is used, because applying it is not the same as honouring it.
    /// This method takes any range at all and splices it, so a sweep that only ever applied edits
    /// would go on passing over a range no client would honour. LSP says two things about a
    /// completion's range -- it contains the position the request was made at, and it begins and ends
    /// on one line -- and a client is entitled to discard or misapply an item that breaks either.
    /// Both are asserted of every item at every caret, which is the whole point of having a sweep.
    /// </remarks>
    private static AppliedItem Accept(
        string text, LineMap lines, int caret, CompletionItem item, string path, DescriptorLoader loader)
    {
        Assert.NotNull(item.TextEdit);

        var start = Offset(lines, item.TextEdit!.Range.Start);
        var end = Offset(lines, item.TextEdit.Range.End);

        Assert.True(
            start <= caret && caret <= end,
            $"'{item.Label}' offered at offset {caret} replaces [{start},{end}], which does not "
                + "contain the position the request was made at");

        Assert.True(
            item.TextEdit.Range.Start.Line == item.TextEdit.Range.End.Line,
            $"'{item.Label}' offered at offset {caret} edits lines {item.TextEdit.Range.Start.Line} "
                + $"through {item.TextEdit.Range.End.Line}; a completion range must be on one line");
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
