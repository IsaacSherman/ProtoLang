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
/// <b>A test nests under the <c>extend</c> whose receiver it names, by spelling.</b> Resolving the
/// two to one message is the binder's job and the binder is not here, so <c>extend
/// protolang.tests.Outer</c> and <c>test Outer.f</c> are two names that a reader can see are the
/// same and this cannot. The cost of being wrong is one test listed at the top level instead of
/// nested, which is visible, harmless, and honest; the cost of compiling to avoid it is the outline
/// disappearing whenever the schema does.
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

        var tests = unit.Tests.ToList();
        var outline = new List<DocumentSymbol>();

        foreach (var extend in unit.Extends)
        {
            var receiver = extend.MessageName.Text;
            var nested = tests
                .Where(test => Targets(test, receiver))
                .Select(Test)
                .ToList();

            tests.RemoveAll(test => Targets(test, receiver));

            outline.Add(Entry(
                Named(extend.MessageName, "extend"),
                detail: null,
                SymbolKind.Class,
                extend.Span,
                extend.MessageName.Span,
                [.. extend.Methods.Select(Method), .. nested]));
        }

        // Whatever named no extend block in this file: a test of a message extended elsewhere, or
        // one whose receiver is still being typed. Listed rather than dropped, because a declaration
        // missing from an outline reads as a declaration that is not there.
        outline.AddRange(tests.Select(Test));

        // Source order across the whole file. The two lists are separate on the compilation unit and
        // an author interleaves them, so the order they arrive in is not the order they were written
        // in -- and an outline that disagrees with the file it describes is one nobody trusts twice.
        return [.. outline.OrderBy(symbol => symbol.Range.Start.Line).ThenBy(symbol => symbol.Range.Start.Character)];
    }

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
