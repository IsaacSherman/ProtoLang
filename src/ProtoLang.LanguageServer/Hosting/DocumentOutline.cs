using ProtoLang.Diagnostics;
using ProtoLang.Syntax;
using ProtoLang.LanguageServer.Protocol.Lsp;

namespace ProtoLang.LanguageServer.Hosting;

/// <summary>
/// The shape of a file: its <c>extend</c> blocks, and the methods and tests inside them.
/// </summary>
/// <remarks>
/// <para>
/// One implementation for three surfaces -- the outline view, the breadcrumb bar, and go-to-symbol
/// -- which is what makes something that looks cosmetic worth its place.
/// </para>
/// <para>
/// <b>Parsed and not compiled, which is the whole design.</b> An outline has to survive a file that
/// does not parse: one that vanishes while you type is worse than one that is briefly out of date,
/// and the moments it would vanish are exactly the moments somebody is navigating a file they are
/// halfway through editing. The parser recovers and produces the declarations it found; the binder
/// would add resolved types and would, on a schema that cannot be loaded, add nothing at all and
/// take the outline with it. So this asks the parser and stops there -- which also means it never
/// waits on protoc, never leaves this process, and is answered on the worker that read it, in the
/// same instant, with no version to go stale between the reading and the answer.
/// </para>
/// <para>
/// <b>A signature is therefore what was written, not what it resolved to.</b>
/// <see cref="Ir.IrMethodSignature.DisplayName"/> is the one spelling of a <em>bound</em> signature
/// and is not what is wanted here: it cannot exist for a method whose parameter type is a name the
/// schema does not declare, and such a method still belongs in the outline. The two are different
/// facts about the same declaration rather than two renderings of one.
/// </para>
/// <para>
/// <b>A test nests under the <c>extend</c> whose receiver it names, by spelling and when it is
/// written beside it.</b> Resolving the two to one message is the binder's job and the binder is
/// not here, so <c>extend protolang.tests.Outer</c> and <c>test Outer.f</c> are two names that a
/// reader can see are the same and this cannot. The cost of being wrong is one test listed at the
/// top level instead of nested, which is visible, harmless, and honest; the cost of compiling to
/// avoid it is the outline disappearing whenever the schema does.
/// </para>
/// <para>
/// <b>Beside, because a tree of ranges is read by containment.</b> A test is a top-level
/// declaration written outside the block it tests, and LSP's outline is not merely a picture: a
/// client works out which symbol the caret is in by descending into whichever range holds it, so a
/// child written outside its parent is a child the breadcrumb bar and the outline's follow-cursor
/// can never reach. What makes the nesting true rather than decorative is the block's range
/// covering what hangs under it -- and that is only honest while the two are written together.
/// <see cref="TestsBeside"/> is where the rule lives and why it stops where it does.
/// </para>
/// </remarks>
public static class DocumentOutline
{
    /// <summary>The outline of <paramref name="text"/>, as far as it parses.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    public static IReadOnlyList<DocumentSymbol> Of(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        // The diagnostics are dropped on purpose. Whatever is wrong with this buffer has already
        // been published by the compile the scheduler ran, and an outline reporting it a second
        // time would double every squiggle in the file.
        var diagnostics = new DiagnosticBag();
        var tokens = new Lexer(text, SourceIdentity.UnsavedName, diagnostics).Tokenize();
        var unit = new Parser(tokens, SourceIdentity.UnsavedName, diagnostics).ParseCompilationUnit();

        var declarations = InSourceOrder(unit);
        var outline = new List<DocumentSymbol>();

        for (var index = 0; index < declarations.Count; index++)
        {
            switch (declarations[index])
            {
                case ExtendDeclaration extend:
                    var beside = TestsBeside(declarations, index, extend.MessageName.Text);

                    outline.Add(Block(extend, beside));
                    index += beside.Count;
                    break;

                // Whatever was not taken by the block above it: a test of a message extended
                // elsewhere, one written away from its block, or one whose receiver is still being
                // typed. Listed rather than dropped, because a declaration missing from an outline
                // reads as a declaration that is not there.
                case TestDeclaration test:
                    outline.Add(Test(test));
                    break;
            }
        }

        return outline;
    }

    /// <summary>The declarations an outline shows, in the order they were written.</summary>
    /// <remarks>
    /// <para>
    /// <see cref="CompilationUnit"/> keeps extends and tests in two lists and an author interleaves
    /// them, so neither list on its own is the file's order -- and an outline that disagrees with
    /// the file it describes is one nobody trusts twice. The order decides more than what a reader
    /// sees here: it is also what says which tests are written beside which block.
    /// </para>
    /// <para>
    /// Imports are left out rather than filtered afterwards, which is what makes this list the whole
    /// answer to "what does the outline walk": an outline lists what a file declares, and an import
    /// declares nothing. Anything added to the unit later is out of the outline until it is added
    /// here, which is one edit in one place rather than a case that silently does nothing.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<SyntaxNode> InSourceOrder(CompilationUnit unit)
        => [.. unit.Extends
            .Cast<SyntaxNode>()
            .Concat(unit.Tests)
            .OrderBy(declaration => declaration.Span.Start.Offset)];

    /// <summary>An <c>extend</c> block, holding its methods and whatever nests under it.</summary>
    /// <remarks>
    /// The range covers the tests as well as the block, which is what makes them reachable by a
    /// client that descends a tree by containment rather than merely drawing it. Its children are in
    /// source order without being sorted, because the methods are inside the block and the tests
    /// immediately follow it.
    /// </remarks>
    private static DocumentSymbol Block(ExtendDeclaration extend, IReadOnlyList<TestDeclaration> beside)
        => Entry(
            Named(extend.MessageName, "extend"),
            detail: null,
            SymbolKind.Class,
            Covering(extend.Span, beside),
            extend.MessageName.Span,
            [.. extend.Methods.Select(Method), .. beside.Select(Test)]);

    /// <summary>The run of tests written straight after a block, all of them naming it.</summary>
    /// <remarks>
    /// <para>
    /// <b>The run stops at the first declaration that is not one of them, and that is the point.</b>
    /// Nesting a test means widening the block's range to cover it, because a child outside its
    /// parent is one no client can descend to. Widening across something else would swallow it:
    /// two top-level entries would overlap, and a caret inside the second would be reported as
    /// being inside the first -- a wrong answer, where declining to nest is only a less useful
    /// one.
    /// </para>
    /// <para>
    /// Forward only. A test is written after the thing it tests, and looking backwards as well
    /// would buy the unusual layout at the price of a block whose range starts before its own
    /// keyword. A test written away from its block is listed on its own, which is the answer a test
    /// whose receiver names no block at all already gets.
    /// </para>
    /// </remarks>
    private static List<TestDeclaration> TestsBeside(
        IReadOnlyList<SyntaxNode> declarations, int block, string receiver)
    {
        List<TestDeclaration> beside = [];

        for (var index = block + 1; index < declarations.Count; index++)
        {
            if (declarations[index] is not TestDeclaration test || !Targets(test, receiver))
            {
                break;
            }

            beside.Add(test);
        }

        return beside;
    }

    /// <summary>The block and everything nested under it, as one range.</summary>
    /// <remarks>
    /// The last of them is enough: the run is contiguous and in source order, so nothing nested
    /// reaches past it.
    /// </remarks>
    private static SourceSpan Covering(SourceSpan block, IReadOnlyList<TestDeclaration> beside)
        => beside.Count == 0 ? block : SourceSpan.Union(block, beside[^1].Span);

    /// <summary>
    /// The same outline for a client that cannot show a tree, with the nesting kept as a name.
    /// </summary>
    /// <inheritdoc cref="SymbolInformation" path="/remarks"/>
    /// <exception cref="ArgumentNullException"><paramref name="outline"/> or <paramref name="uri"/> is null.</exception>
    public static IReadOnlyList<SymbolInformation> Flattened(
        IReadOnlyList<DocumentSymbol> outline, string uri)
    {
        ArgumentNullException.ThrowIfNull(outline);
        ArgumentNullException.ThrowIfNull(uri);

        return [.. Flatten(outline, uri, container: null)];
    }

    private static IEnumerable<SymbolInformation> Flatten(
        IReadOnlyList<DocumentSymbol> outline, string uri, string? container)
    {
        foreach (var symbol in outline)
        {
            yield return new SymbolInformation
            {
                Name = symbol.Name,
                Kind = symbol.Kind,
                Location = new Location(uri, symbol.Range),
                ContainerName = container,
            };

            foreach (var child in Flatten(symbol.Children ?? [], uri, symbol.Name))
            {
                yield return child;
            }
        }
    }

    // ------------------------------------------------------- one declaration at a time

    private static DocumentSymbol Method(MethodDeclaration method)
        => Entry(
            Named(method.Name, "fn"),
            Signature(method),
            SymbolKind.Method,
            method.Span,
            method.Name.Span,
            children: null);

    /// <summary>
    /// A test, named by what it says it is and annotated with what it is a test of.
    /// </summary>
    /// <remarks>
    /// The quoted description is the name, because it is what tells two tests of one method apart
    /// and the method's own name is already the heading they sit under. The target goes in the
    /// detail, and the selection range is the target as well: it is the only part of a test that has
    /// a range of its own -- the description is a string literal whose span the tree does not keep
    /// -- and landing on what a test tests is the right place to land anyway.
    /// </remarks>
    private static DocumentSymbol Test(TestDeclaration test)
    {
        var target = $"{test.Target.Receiver.Text}.{test.Target.Method.Text}";

        return Entry(
            test.Name.Length > 0 ? test.Name : Named(test.Target.Method, "test"),
            target.Length > 1 ? target : null,
            SymbolKind.Function,
            test.Span,
            test.Target.Span,
            children: null);
    }

    /// <summary>The method's parameter list and return type, as the author wrote them.</summary>
    /// <remarks>
    /// The return type is written even where the author left the arrow off, because what a call
    /// produces is what decides whether it may be used as a value -- the same argument
    /// <see cref="Ir.IrMethodSignature.DisplayName"/> makes for always writing <c>void</c>.
    /// </remarks>
    private static string Signature(MethodDeclaration method)
    {
        var parameters = method.Parameters.Select(
            parameter => $"{parameter.Name.Text}: {parameter.Type.Name.Text}");

        var written = $"({string.Join(", ", parameters)}) -> {method.ReturnType?.Name.Text ?? "void"}";

        return method.IsVirtual ? $"virtual {written}" : written;
    }

    // ------------------------------------------------------- the shape of an entry

    /// <summary>
    /// One entry, with its selection range guaranteed to lie inside the range it selects from.
    /// </summary>
    /// <remarks>
    /// Widened here for the reason <see cref="Symbols.DeclarationSite"/> widens its own: LSP
    /// requires the containment and a client handed the other way round has no defined behavior.
    /// The widening is a no-op for every name that was actually written, and the case it is not is
    /// ordinary -- a declaration whose name is still being typed has an empty name range that the
    /// parser anchored just outside what it had managed to parse.
    /// </remarks>
    private static DocumentSymbol Entry(
        string name,
        string? detail,
        SymbolKind kind,
        SourceSpan extent,
        SourceSpan selection,
        IReadOnlyList<DocumentSymbol>? children)
    {
        var range = SourceSpan.Union(extent, selection);

        return new DocumentSymbol
        {
            Name = name,
            Detail = detail,
            Kind = kind,
            Range = EditorPositions.RangeOf(range),
            SelectionRange = EditorPositions.RangeOf(selection.IsNone ? range : selection),
            Children = children is { Count: > 0 } ? children : null,
        };
    }

    /// <summary>What to call a declaration whose name has not been written yet.</summary>
    /// <remarks>
    /// The keyword that introduced it. A buffer being typed into is full of these, and an entry with
    /// an empty name is one a client renders as a blank row -- present in the tree and unreadable,
    /// which is the one outcome worse than being absent from it.
    /// </remarks>
    private static string Named(SyntaxName name, string keyword)
        => name.Text.Length > 0 ? name.Text : keyword;

    private static bool Targets(TestDeclaration test, string receiver)
        => receiver.Length > 0
            && string.Equals(test.Target.Receiver.Text, receiver, StringComparison.Ordinal);
}
