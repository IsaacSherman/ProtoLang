using System.Text;
using Google.Protobuf.Reflection;
using ProtoLang.Binding;
using ProtoLang.Config;
using ProtoLang.Diagnostics;
using ProtoLang.Ir;
using ProtoLang.Semantics;
using ProtoLang.Symbols;
using ProtoLang.Types;
using ProtoLang.LanguageServer.Protocol.Lsp;
using SymbolKind = ProtoLang.Symbols.SymbolKind;

namespace ProtoLang.LanguageServer.Hosting;

/// <summary>
/// What the editor shows when the pointer rests on something: what it is, what type it has, what
/// the schema said about it, and what the language is doing here that the source does not show.
/// </summary>
/// <remarks>
/// <para>
/// <b>A name first, an expression second.</b> Where the caret is on a name that resolved, the card
/// is about the symbol -- which is what the reader is pointing at, and the only thing that has
/// documentation and a declaration. Where it is not, the card is about the innermost expression
/// covering the caret: an operator, a literal, the parentheses of a call. Nothing else answers, so
/// whitespace, punctuation and a name that did not resolve produce no card at all rather than an
/// empty one, and a client shows nothing rather than a box saying nothing.
/// </para>
/// <para>
/// <b>Types come from whatever already knows them, never from a second reading.</b> A local, a
/// parameter and a loop binding are in <see cref="IrModule.Scope"/> with their types, which is the
/// binder's own record and answers for a declaration and a use alike. A method is in
/// <see cref="IrModule.Methods"/>, and <see cref="IrMethodSignature.DisplayName"/> is already the
/// one spelling of a signature in this repository. A message or an enum is in
/// <see cref="SchemaTypes"/>. Only a field and an enum constant are read off the IR node under the
/// caret, because their descriptors live on the node and nowhere a <see cref="SymbolId"/> reaches
/// -- and that is three shapes rather than a rule, so a construct added later that names a field
/// falls back to a card with no type line rather than to a wrong one.
/// </para>
/// <para>
/// <b>The policy is stated exactly where the binder stamped it.</b> Integer overflow, a zero
/// divisor, a numeric conversion: each is a choice <c>protolang.config.xml</c> made (spec 10.4) that
/// the source text does not show, and the generated file's header already says so for a reader of
/// the output. A reader of the input has no such line, and hover is where it goes. It is read off
/// the <see cref="ArithmeticBehavior"/> the IR node carries rather than off
/// <see cref="ProjectConfig"/>, because the annotation is what the backends will actually emit and a
/// second derivation of it is a second answer that can differ from the first.
/// </para>
/// <para>
/// <b>And nowhere else.</b> A card that recited the project's policy on every hover would be a card
/// nobody reads by the third one. The rule is mechanical: an operation whose IR node carries a
/// behavior says what that behavior is, a singular message field says that reading it needs a guard
/// (spec 13.1), and everything else says nothing about configuration.
/// </para>
/// <para>
/// Markdown, because a signature has to be set apart from the prose around it and a <c>.proto</c>
/// comment is prose the author wrote. The signature goes in a fenced <c>protolang</c> block so that
/// a client colours it with the same grammar it colours the buffer with.
/// </para>
/// </remarks>
internal static class HoverCard
{
    /// <summary>What to say about <paramref name="offset"/>, or null when there is nothing.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="compiled"/> is null.</exception>
    public static Hover? For(DocumentCompilation compiled, int offset)
    {
        ArgumentNullException.ThrowIfNull(compiled);

        if (compiled.Semantics is not { } model || compiled.Result is not { } result)
        {
            return null;
        }

        // The symbol arm first, and the expression arm as the fallback rather than the alternative.
        // Every arm below can produce nothing -- a schema element from another load, a field whose
        // descriptor is on no node this knows -- and the type of the expression standing there is
        // still worth saying. Silence is for a caret that is on nothing.
        var card = DeclaredSymbol.At(compiled, offset) is { } symbol
            ? Card(AboutSymbol(model, result, symbol), symbol.Span)
            : null;

        return card ?? AboutExpression(model, offset);
    }

    // ------------------------------------------------------- what the caret is on

    /// <remarks>
    /// Every arm produces the same three things in the same order -- a signature, a sentence saying
    /// what it is, and whatever the schema author wrote -- so that a reader learns the shape of a
    /// card once rather than per kind.
    /// </remarks>
    private static IEnumerable<string> AboutSymbol(
        SemanticModel model, CompilationResult result, DeclaredSymbol symbol)
        => symbol.Kind switch
        {
            SymbolKind.Local or SymbolKind.Parameter or SymbolKind.LoopBinding
                => AboutBinding(result, symbol),
            SymbolKind.Method => AboutMethod(result, symbol),
            SymbolKind.Field => AboutField(model, symbol),
            SymbolKind.EnumValue => AboutEnumValue(model, symbol),
            _ => AboutType(result, symbol),
        };

    /// <summary>A local, a parameter, or the name a <c>for</c> loop binds each element to.</summary>
    /// <remarks>
    /// One arm for three kinds because the card is the same for all three but for the sentence: each
    /// is a name with a declared type, visible over a range, declared in this buffer. What separates
    /// them is how the reader should think about where the value comes from, which is exactly one
    /// sentence long.
    /// </remarks>
    private static IEnumerable<string> AboutBinding(CompilationResult result, DeclaredSymbol symbol)
    {
        if (symbol.Site is not { } site || TypeOf(result, symbol) is not { } type)
        {
            yield break;
        }

        yield return Signature($"{site.Name.Text}: {type.DisplayName}");

        yield return symbol.Kind switch
        {
            SymbolKind.Local => "Local variable.",
            SymbolKind.Parameter => "Parameter.",
            _ => "Loop binding, taking each element of a repeated field in turn.",
        };
    }

    private static IEnumerable<string> AboutMethod(CompilationResult result, DeclaredSymbol symbol)
    {
        if (SignatureOf(result, symbol) is not { } signature)
        {
            yield break;
        }

        yield return Signature(signature.DisplayName);
        yield return $"Method on `{signature.Receiver.FullName}`.";
    }

    private static IEnumerable<string> AboutField(SemanticModel model, DeclaredSymbol symbol)
    {
        if (FieldAt(model, symbol) is not { } field)
        {
            yield break;
        }

        var type = TypeFactory.FromField(field);

        yield return Signature($"{field.Name}: {type.DisplayName}");
        yield return $"Field of `{field.ContainingType.FullName}`{DeclaredIn(symbol)}.";

        foreach (var paragraph in Documentation(symbol))
        {
            yield return paragraph;
        }

        // Spec 13.1: an unset singular message field has no value to read, so the language requires
        // the presence test to have been established before the read. It is the one rule here that
        // is invisible in the source and not attached to an operator -- it belongs to the field's
        // own declaration in a .proto the reader may never have opened.
        if (type is MessageType)
        {
            yield return "Reading this field requires an established presence test: an unset "
                + "singular message field has no value to read (spec 13.1).";
        }
    }

    private static IEnumerable<string> AboutEnumValue(SemanticModel model, DeclaredSymbol symbol)
    {
        if (EnumValueAt(model, symbol) is not { } value)
        {
            yield break;
        }

        yield return Signature($"{value.Name}: {value.EnumDescriptor.FullName}");
        yield return $"Enum value{DeclaredIn(symbol)}.";

        foreach (var paragraph in Documentation(symbol))
        {
            yield return paragraph;
        }
    }

    /// <summary>A message or an enum, named in a type position or after <c>extend</c>.</summary>
    /// <remarks>
    /// The full name rather than what was written, which is the one place this card deliberately
    /// does not echo the buffer: a simple name is what an author writes and the full name is what it
    /// resolved to, and the second is the thing a reader hovered to find out.
    /// </remarks>
    private static IEnumerable<string> AboutType(CompilationResult result, DeclaredSymbol symbol)
    {
        if (result.Types.Find(symbol.Id) is not { } named)
        {
            yield break;
        }

        yield return Signature(named.FullName);
        yield return $"{(named.IsMessage ? "Message" : "Enum")}{DeclaredIn(symbol)}.";

        foreach (var paragraph in Documentation(symbol))
        {
            yield return paragraph;
        }
    }

    /// <summary>What the innermost expression covering the caret is, where no name is.</summary>
    /// <remarks>
    /// <para>
    /// The requirement that any expression shows its resolved type, discharged for every position
    /// that is not a name: an operator, a literal, a cast's keyword, the parentheses of a call. A
    /// name is answered above and more fully, because a name has a declaration and a comment and an
    /// expression has neither.
    /// </para>
    /// <para>
    /// An error-typed expression produces no card. <c>&lt;error&gt;</c> is what the binder writes
    /// where it could not work out what something is, and showing it to a reader is the compiler
    /// saying "I do not know" in a box that covers the code they are trying to read.
    /// </para>
    /// </remarks>
    private static Hover? AboutExpression(SemanticModel model, int offset)
    {
        if (model.IrAt(offset)?.Enclosing<IrExpression>() is not { } expression
            || expression.Type is ErrorType)
        {
            return null;
        }

        return Card([Signature(expression.Type.DisplayName), .. Policy(expression)], expression.Span);
    }

    // ------------------------------------------------------- what the language is doing here

    /// <summary>What the project's policy makes this operation do, where it makes it do anything.</summary>
    /// <remarks>
    /// <para>
    /// Read off the node rather than off the configuration, so that what is described is what will
    /// be emitted. <see cref="NumericPolicy"/> is the one place the configuration becomes a
    /// behavior, and it has already run by the time anything here is asked.
    /// </para>
    /// <para>
    /// <b>Where it governs nothing, nothing is said.</b> Every arithmetic node carries an
    /// <see cref="ArithmeticBehavior"/> and only an integer one is governed by it, which is what
    /// <see cref="IrUnary.OverflowingType"/> answers -- asked rather than re-derived, because the
    /// two backends already ask it and a fourth opinion is how a reader came to be told that
    /// <c>double</c> arithmetic wraps in two's complement. A floating-point operation is left to its
    /// type line: IEEE-754 is not a choice this project made, and a card that implied it was would
    /// be inventing a policy to have something to say.
    /// </para>
    /// </remarks>
    private static IEnumerable<string> Policy(IrExpression expression)
    {
        switch (expression)
        {
            // Integer by construction -- float division is an IrBinary -- so the annotation always
            // governs, and the zero divisor 10.2.1 requires an answer for always applies.
            case IrIntegerDivision division:
                yield return $"Integer division. {Overflow(division.Behavior)} {OnZero(division)}";
                break;

            case IrBinary { OverflowingType: not null } binary:
                yield return Overflow(binary.Behavior);
                break;

            case IrUnary { OverflowingType: not null } negation:
                yield return Overflow(negation.Behavior);
                break;

            case IrConversion conversion:
                yield return Conversion(conversion);
                break;
        }
    }

    /// <remarks>
    /// Spelt out here rather than shared with <see cref="ProjectConfig.DescribeForHeader"/>, which
    /// says the same three things in a different register: that one is a preamble naming the policy
    /// a whole generated file was produced under, this one is a sentence about the operation the
    /// reader is pointing at. One string serving both would be a header that reads like a footnote
    /// or a footnote that reads like a header.
    /// </remarks>
    private static string Overflow(ArithmeticBehavior behavior) => behavior switch
    {
        ArithmeticBehavior.Wrap => "Overflow wraps, two's complement (spec 10.1).",
        ArithmeticBehavior.Check => "Overflow terminates the program, exit code 70 (spec 10.1).",
        ArithmeticBehavior.Saturate => "Overflow clamps to the type's bounds (spec 10.1).",
        _ => throw new ArgumentOutOfRangeException(
            nameof(behavior), behavior, "Unhandled overflow behavior."),
    };

    private static string OnZero(IrIntegerDivision division) => division.ZeroBehavior switch
    {
        ZeroDivisorBehavior.Unreachable => "The divisor is a non-zero literal, so no zero check is "
            + "emitted (spec 10.2.1).",
        ZeroDivisorBehavior.Fallback => "A zero divisor yields the declared `on_zero` value "
            + "(spec 10.2.1).",
        ZeroDivisorBehavior.Fail => "A zero divisor terminates the program, exit code 70 "
            + "(spec 10.2.1).",
        _ => throw new ArgumentOutOfRangeException(
            nameof(division), division.ZeroBehavior, "Unhandled zero-divisor behavior."),
    };

    /// <summary>What this conversion does with a value the target cannot hold.</summary>
    /// <remarks>
    /// <para>
    /// <b>One sentence per row of spec 10.3's table, because the rows disagree.</b> An integer
    /// target takes the low bits, a floating-point target rounds, and only a floating-point source
    /// reaching an integer truncates and clamps and maps NaN to zero. Saying the last of those about
    /// all of them -- which this did -- tells a reader that <c>ratio as double</c> discards the
    /// fraction and flattens a NaN, and both are false.
    /// </para>
    /// <para>
    /// <see cref="IrConversion.Kind"/> is the discriminator, which is the one the backends switch on
    /// as well: the four families need different treatment in each target and this is a fifth reader
    /// of the same classification rather than a second opinion about it. The one distinction it does
    /// not draw is between the two directions across floating point, which the table does -- widening
    /// is exact and narrowing rounds -- so the target's width settles that.
    /// </para>
    /// </remarks>
    private static string Conversion(IrConversion conversion) => conversion.Behavior switch
    {
        ConversionBehavior.WrapOrSaturate => WrapOrSaturate(conversion),
        _ => throw new ArgumentOutOfRangeException(
            nameof(conversion), conversion.Behavior, "Unhandled conversion behavior."),
    };

    /// <inheritdoc cref="Conversion"/>
    private static string WrapOrSaturate(IrConversion conversion) => conversion.Kind switch
    {
        ConversionKind.Identity =>
            "A conversion to the type the value already has. It states nothing new (spec 10.3).",
        ConversionKind.IntegerToInteger =>
            "Takes the low bits: the value reduced modulo 2^N, where N is the target's width "
                + "(spec 10.3).",
        ConversionKind.IntegerToFloat => "Rounds to nearest, ties to even (spec 10.3).",
        ConversionKind.FloatToFloat => conversion.TargetType.Kind is ScalarKind.Double
            ? "Widening to double, which is exact (spec 10.3)."
            : "Rounds to nearest, ties to even; a magnitude too large for float becomes an infinity "
                + "(spec 10.3).",
        ConversionKind.FloatToInteger =>
            "Truncates toward zero; a value outside the target's range clamps to that bound, and NaN "
                + "becomes zero (spec 10.3).",
        _ => throw new ArgumentOutOfRangeException(
            nameof(conversion), conversion.Kind, "Unhandled conversion kind."),
    };

    // ------------------------------------------------------- asking what already knows

    /// <summary>The declared type of a local, a parameter or a loop binding.</summary>
    /// <remarks>
    /// From the binder's own scope record, which is what #49 published, and so is the same answer
    /// for the declaration and for every use of it. Reconstructing it from the IR node under the
    /// caret would work for a use and fail on a declaration, where there is no reference node at
    /// all.
    /// </remarks>
    private static PlType? TypeOf(CompilationResult result, DeclaredSymbol symbol)
        => result.Module?.Scope.FirstOrDefault(entry => entry.Declaration.Id == symbol.Id)?.Type;

    private static IrMethodSignature? SignatureOf(CompilationResult result, DeclaredSymbol symbol)
        => result.Module?.Methods
            .FirstOrDefault(method => method.Signature.Id == symbol.Id)?.Signature;

    /// <summary>The field descriptor the IR node under this name carries.</summary>
    /// <remarks>
    /// The three shapes a field name can be written in and be resolved: read through a receiver or
    /// bare, tested by <c>has</c>, and set in a test fixture. Asked at the start of the name the
    /// binder recorded rather than at the caret, so that a caret on a dot or in the whitespace of a
    /// member access cannot reach a different field than the one this card is about.
    /// </remarks>
    private static FieldDescriptor? FieldAt(SemanticModel model, DeclaredSymbol symbol)
        => model.IrAt(symbol.Span.Start.Offset) is not { } at
            ? null
            : at.Enclosing<IrFieldAccess>()?.Field
                ?? at.Enclosing<IrFieldPresence>()?.Field
                ?? at.Enclosing<IrTestFieldValue>()?.Field;

    /// <inheritdoc cref="FieldAt"/>
    private static EnumValueDescriptor? EnumValueAt(SemanticModel model, DeclaredSymbol symbol)
        => model.IrAt(symbol.Span.Start.Offset)?.Enclosing<IrEnumValue>()?.Value;

    // ------------------------------------------------------- rendering

    /// <summary>Which <c>.proto</c> declared this, or nothing when no schema here holds it.</summary>
    /// <remarks>
    /// A clause rather than a sentence, because it is only ever half of one. Absent for a well-known
    /// type protoc resolved from descriptors compiled into itself, which is present, real, and
    /// nowhere on this machine -- a sentence claiming otherwise would send a reader looking.
    /// </remarks>
    private static string DeclaredIn(DeclaredSymbol symbol)
        => symbol.Schema is { } declaration ? $", declared in `{declaration.SchemaName}`" : string.Empty;

    /// <summary>What the schema author wrote about this, cleaned, one paragraph at a time.</summary>
    /// <remarks>
    /// <para>
    /// The comment is the point of hovering a schema member. A field's type is usually obvious from
    /// the name beside it and the comment above it never is, and it is written in a file the reader
    /// of a ProtoLang buffer may never have opened.
    /// </para>
    /// <para>
    /// The leading comment first, then a trailing one, and detached paragraphs last: the first two
    /// document the declaration and the third is prose that sits above it with a blank line between,
    /// which usually belongs to the section rather than to the element. Escaped, because a comment
    /// is text and this card is Markdown -- an underscore in a field name is not emphasis.
    /// </para>
    /// </remarks>
    private static IEnumerable<string> Documentation(DeclaredSymbol symbol)
    {
        if (symbol.Schema?.Documentation is not { IsEmpty: false } comments)
        {
            yield break;
        }

        if (comments.Leading is { } leading)
        {
            yield return AsMarkdown(leading);
        }

        if (comments.Trailing is { } trailing)
        {
            yield return AsMarkdown(trailing);
        }

        foreach (var paragraph in comments.Detached)
        {
            yield return AsMarkdown(paragraph);
        }
    }

    /// <summary>The signature line, set apart so a client colours it as ProtoLang.</summary>
    private static string Signature(string text) => $"```protolang\n{text}\n```";

    /// <summary>
    /// The blocks as one card, or null when nothing was said and so no card should appear.
    /// </summary>
    /// <remarks>
    /// Every arm above can produce nothing -- a symbol whose type the model does not hold, a schema
    /// element from another load -- and an empty card is worse than none: a client shows an empty
    /// box over the code, and the reader learns that the server is running rather than what the name
    /// means.
    /// </remarks>
    private static Hover? Card(IEnumerable<string> blocks, SourceSpan span)
    {
        var written = string.Join("\n\n", blocks.Where(block => block.Length > 0));

        return written.Length == 0
            ? null
            : new Hover(new MarkupContent(MarkupKind.Markdown, written), EditorPositions.RangeOf(span));
    }

    /// <summary>Comment text, with what Markdown would have claimed put back.</summary>
    /// <remarks>
    /// Only the characters that start a construct at the point they appear, which is what keeps a
    /// comment readable: a backslash before every punctuation mark would make prose look like it had
    /// been escaped, which it would have been. Line breaks inside a paragraph are hard-wrapped by
    /// the schema author and Markdown would join them, so each is made to hold.
    /// </remarks>
    private static string AsMarkdown(string comment)
    {
        var escaped = new StringBuilder(comment.Length);

        foreach (var character in comment)
        {
            if (character is '\\' or '`' or '*' or '_' or '<' or '>' or '[' or ']' or '#')
            {
                escaped.Append('\\');
            }

            escaped.Append(character == '\n' ? "  \n" : character);
        }

        return escaped.ToString();
    }
}
