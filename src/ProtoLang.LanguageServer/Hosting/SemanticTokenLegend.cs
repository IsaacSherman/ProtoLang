using ProtoLang.LanguageServer.Protocol.Lsp;
using ProtoLang.Symbols;
using ProtoLang.Syntax;
using SymbolKind = ProtoLang.Symbols.SymbolKind;

namespace ProtoLang.LanguageServer.Hosting;

/// <summary>
/// The categories this server classifies source into, and the order a client indexes them by.
/// </summary>
/// <remarks>
/// <para>
/// <b>The whole standard set is declared, and only part of it is used.</b> That asymmetry is the
/// point. The legend is negotiated once, at <c>initialize</c>, and a client builds its colour table
/// from the indices it was given; adding a category later renumbers everything after it, which means
/// renegotiating capabilities and repainting every open file. #50 refines identifiers into
/// parameters, properties, methods and enum members, and it must be able to do that by emitting
/// different numbers rather than by changing what the numbers mean.
/// </para>
/// <para>
/// <b>An identifier is classified twice, and the second answer is the binder's.</b> The lexer says
/// <c>variable</c>, because from a token stream that is the whole truth. #50 then replaces that with
/// the category of the symbol the binder resolved the name to, wherever it resolved one, which is
/// what <see cref="IndexOf(SymbolKind)"/> and <see cref="ModifiersOf"/> are for. A name that did not
/// resolve keeps the lexical answer, so a file that does not parse is still coloured -- exactly when
/// a user is staring at the screen. That is the whole of the degradation rule: refinement adds, and
/// never takes colour away.
/// </para>
/// <para>
/// <b>Structural punctuation is not classified at all.</b> Braces, parentheses, semicolons, commas,
/// colons and the member dot get no token, so whatever the client's own grammar does with them
/// survives. There is nothing to convey by colouring them: nobody learns anything about a program
/// from the colour of its semicolons.
/// </para>
/// </remarks>
public static class SemanticTokenLegend
{
    public const string Namespace = "namespace";
    public const string Type = "type";
    public const string Class = "class";
    public const string Enum = "enum";
    public const string Interface = "interface";
    public const string Struct = "struct";
    public const string TypeParameter = "typeParameter";
    public const string Parameter = "parameter";
    public const string Variable = "variable";
    public const string Property = "property";
    public const string EnumMember = "enumMember";
    public const string Event = "event";
    public const string Function = "function";
    public const string Method = "method";
    public const string Macro = "macro";
    public const string Keyword = "keyword";
    public const string Modifier = "modifier";
    public const string Comment = "comment";
    public const string String = "string";
    public const string Number = "number";
    public const string Regexp = "regexp";
    public const string Operator = "operator";
    public const string Decorator = "decorator";

    /// <summary>The categories, in the order their indices refer to.</summary>
    public static IReadOnlyList<string> TokenTypes { get; } =
    [
        Namespace, Type, Class, Enum, Interface, Struct, TypeParameter, Parameter, Variable, Property,
        EnumMember, Event, Function, Method, Macro, Keyword, Modifier, Comment, String, Number, Regexp,
        Operator, Decorator,
    ];

    public const string Declaration = "declaration";
    public const string Definition = "definition";
    public const string ReadOnly = "readonly";
    public const string Static = "static";
    public const string Deprecated = "deprecated";
    public const string Abstract = "abstract";
    public const string Async = "async";
    public const string Modification = "modification";
    public const string Documentation = "documentation";
    public const string DefaultLibrary = "defaultLibrary";

    /// <summary>The modifiers, in the order their bits refer to.</summary>
    /// <remarks>
    /// Three of the ten are emitted; the rest are declared for the same reason the unused types are,
    /// since a modifier added later shifts every bit above it. <see cref="Definition"/> is one of the
    /// seven on purpose: in ProtoLang a name is declared and defined in the same breath, so emitting
    /// both bits for one event would be telling a client twice about one thing.
    /// </remarks>
    public static IReadOnlyList<string> TokenModifiers { get; } =
    [
        Declaration, Definition, ReadOnly, Static, Deprecated, Abstract, Async, Modification,
        Documentation, DefaultLibrary,
    ];

    /// <summary>The legend as it goes on the wire.</summary>
    public static SemanticTokensLegend Wire { get; } = new()
    {
        TokenTypes = TokenTypes,
        TokenModifiers = TokenModifiers,
    };

    /// <summary>The index of the category comments are published under.</summary>
    public static int CommentIndex { get; } = IndexOf(Comment);

    private static readonly int KeywordIndex = IndexOf(Keyword);
    private static readonly int VariableIndex = IndexOf(Variable);
    private static readonly int StringIndex = IndexOf(String);
    private static readonly int NumberIndex = IndexOf(Number);
    private static readonly int OperatorIndex = IndexOf(Operator);

    private static readonly int ParameterIndex = IndexOf(Parameter);
    private static readonly int PropertyIndex = IndexOf(Property);
    private static readonly int EnumMemberIndex = IndexOf(EnumMember);
    private static readonly int MethodIndex = IndexOf(Method);
    private static readonly int ClassIndex = IndexOf(Class);
    private static readonly int EnumIndex = IndexOf(Enum);

    private static readonly int DeclarationBit = BitOf(Declaration);
    private static readonly int ReadOnlyBit = BitOf(ReadOnly);
    private static readonly int ModificationBit = BitOf(Modification);

    /// <summary>Which category a token belongs to, or null when it is not classified.</summary>
    /// <remarks>
    /// Keywords are asked of <see cref="TokenKindExtensions.IsKeyword"/> rather than listed again
    /// here, so a keyword added to the language colours without anyone remembering this file.
    /// </remarks>
    public static int? IndexOf(TokenKind kind)
    {
        if (kind.IsKeyword())
        {
            return KeywordIndex;
        }

        return kind switch
        {
            TokenKind.Identifier => VariableIndex,
            TokenKind.StringLiteral => StringIndex,
            TokenKind.IntegerLiteral or TokenKind.FloatLiteral => NumberIndex,

            TokenKind.Arrow or TokenKind.Plus or TokenKind.Minus or TokenKind.Star or TokenKind.Slash
                or TokenKind.Percent or TokenKind.Equals or TokenKind.EqualsEquals or TokenKind.BangEquals
                or TokenKind.Bang or TokenKind.Less or TokenKind.LessEquals or TokenKind.Greater
                or TokenKind.GreaterEquals or TokenKind.AmpersandAmpersand or TokenKind.PipePipe
                => OperatorIndex,

            // Structural punctuation, end of file, and the character the lexer could not make sense
            // of. A token the client should colour by its own grammar, or not at all.
            _ => null,
        };
    }

    /// <summary>Which category a resolved name belongs to.</summary>
    /// <remarks>
    /// <para>
    /// The binder's answer, translated. <see cref="SymbolKind"/> is finer than the compiler needs
    /// precisely so that this translation exists, and its own remarks name a semantic highlighter as
    /// the reason -- so the mapping is a reading of that enum rather than a second opinion about what
    /// a name means.
    /// </para>
    /// <para>
    /// <b>A local and a loop binding are both <c>variable</c>, and the modifier is what tells them
    /// apart.</b> LSP has no category for the name a <c>for</c> binds, and inventing one is not
    /// available -- the published set is fixed (spec 6.5). What a loop binding does have is that it
    /// cannot be assigned, and neither can a parameter or a field, so <see cref="ModifiersOf"/>
    /// marks all three <c>readonly</c>; a local is then the only <c>variable</c> without that bit.
    /// </para>
    /// <para>
    /// An unrecognized kind falls back to the lexical answer rather than throwing. A kind added to
    /// the compiler is a kind this file has not been taught yet, and colouring it as an identifier is
    /// what it looked like before anyone asked.
    /// </para>
    /// </remarks>
    public static int IndexOf(SymbolKind kind)
        => kind switch
        {
            SymbolKind.Parameter => ParameterIndex,
            SymbolKind.Field => PropertyIndex,
            SymbolKind.EnumValue => EnumMemberIndex,
            SymbolKind.Method => MethodIndex,
            SymbolKind.MessageType => ClassIndex,
            SymbolKind.EnumType => EnumIndex,
            _ => VariableIndex,
        };

    /// <summary>What else is true of a name written here: declared, assigned, or unassignable.</summary>
    /// <remarks>
    /// <para>
    /// Both halves come from what the binder recorded rather than from a second look at the tree.
    /// <see cref="ReferenceKind"/> already separates the place a name was introduced from the places
    /// it was used, and a use that assigns from one that reads, which is exactly the pair LSP's
    /// <c>declaration</c> and <c>modification</c> describe.
    /// </para>
    /// <para>
    /// <c>readonly</c> is a fact about the language, not a decoration: spec 18 makes a local the only
    /// thing a method may assign, so a parameter, a loop binding, a field and an enum constant all
    /// genuinely are read-only. A method and a type are left out of it -- neither is a place a value
    /// could be stored, and a bit that is true of everything conveys nothing.
    /// </para>
    /// <para>
    /// An assignment the language refuses is still marked. <c>line.quantity = 2</c> is
    /// <c>PL0034</c>, and the binder still records the write, because what the author wrote is what
    /// an editor is describing.
    /// </para>
    /// </remarks>
    public static int ModifiersOf(SymbolKind kind, ReferenceKind reference)
    {
        var modifiers = reference switch
        {
            ReferenceKind.Declaration => DeclarationBit,
            ReferenceKind.Write => ModificationBit,
            _ => 0,
        };

        return CannotBeAssigned(kind) ? modifiers | ReadOnlyBit : modifiers;
    }

    private static bool CannotBeAssigned(SymbolKind kind)
        => kind is SymbolKind.Parameter or SymbolKind.LoopBinding
            or SymbolKind.Field or SymbolKind.EnumValue;

    private static int BitOf(string modifier)
    {
        for (var index = 0; index < TokenModifiers.Count; index++)
        {
            if (string.Equals(TokenModifiers[index], modifier, StringComparison.Ordinal))
            {
                return 1 << index;
            }
        }

        throw new ArgumentOutOfRangeException(
            nameof(modifier), modifier, "The legend does not carry that modifier.");
    }

    private static int IndexOf(string type)
    {
        for (var index = 0; index < TokenTypes.Count; index++)
        {
            if (string.Equals(TokenTypes[index], type, StringComparison.Ordinal))
            {
                return index;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(type), type, "The legend does not carry that category.");
    }
}
