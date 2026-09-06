using ProtoLang.LanguageServer.Protocol.Lsp;
using ProtoLang.Syntax;

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
/// <b>Identifiers are all one category today, deliberately.</b> Telling a local from a parameter from
/// a field needs the position lookup in #38 and the declaration data in #39 applied to a bound model,
/// and this server classifies from the token stream alone -- which is what lets it answer for a file
/// that does not parse, exactly when a user is staring at the screen. A classification that is right
/// sometimes is worse than one that is consistently coarse, because the wrong colour reads as a fact
/// about the code.
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

    /// <summary>The modifiers, in the order their bits refer to.</summary>
    /// <remarks>
    /// None are emitted yet. They are declared for the same reason the unused types are: a modifier
    /// added later shifts every bit above it.
    /// </remarks>
    public static IReadOnlyList<string> TokenModifiers { get; } =
    [
        "declaration", "definition", "readonly", "static", "deprecated", "abstract", "async",
        "modification", "documentation", "defaultLibrary",
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
