namespace ProtoLang.LanguageServer.Protocol.Lsp;

/// <summary>What the editor shows while a call's arguments are being typed.</summary>
/// <remarks>
/// <para>
/// A list, an index into it, and an index into that signature's parameters. ProtoLang has no
/// overloading (spec 16.1), so the list is always one long and <see cref="ActiveSignature"/> is
/// always zero -- which is the whole reason this feature is small enough to be worth having: none of
/// the ranking a language with overloads has to do applies.
/// </para>
/// <para>
/// The shape is still the list, because it is what the protocol asks for and a server that sent a
/// bare signature would be inventing a dialect.
/// </para>
/// </remarks>
public sealed record SignatureHelp
{
    public IReadOnlyList<SignatureInformation> Signatures { get; init; } = [];

    public int ActiveSignature { get; init; }

    /// <summary>Which parameter the caret is currently supplying.</summary>
    /// <remarks>
    /// Carried here rather than on the signature because that is where every client reads it from.
    /// It may point past the last parameter, which is what typing one argument too many looks like;
    /// a client renders that by highlighting nothing, which is the honest picture.
    /// </remarks>
    public int ActiveParameter { get; init; }
}

/// <summary>One signature, as a line of text and the parameters inside that line.</summary>
/// <remarks>
/// <see cref="Label"/> is <c>IrMethodSignature.DisplayName</c> -- the one spelling of a signature in
/// this repository, which a hover already shows. A second rendering here would be the same fact in
/// two voices, and the reader would be the one reconciling them.
/// </remarks>
public sealed record SignatureInformation
{
    public string Label { get; init; } = string.Empty;

    public IReadOnlyList<ParameterInformation> Parameters { get; init; } = [];
}

/// <summary>Which part of the signature line one parameter occupies.</summary>
/// <remarks>
/// <para>
/// <b>A range, never a substring.</b> LSP allows either, and the substring form is the tempting one
/// -- send <c>"factor: int64"</c> and let the client find it. It is wrong here: a client is told to
/// highlight the <em>first</em> match, and ProtoLang permits a method to declare the same parameter
/// name twice. The file where that happens is exactly the file whose signature a reader is squinting
/// at, and highlighting the wrong one of two identical parameters is the sort of small lie that
/// makes a reader stop trusting the panel.
/// </para>
/// <para>
/// Two integers, start and end, counted in UTF-16 code units into <see cref="SignatureInformation.Label"/>
/// -- the same units the rest of this protocol counts columns in.
/// </para>
/// </remarks>
public sealed record ParameterInformation
{
    public IReadOnlyList<int> Label { get; init; } = [];
}

/// <summary>When a client should ask for signature help, and when it should ask again.</summary>
/// <remarks>
/// An options object rather than a bare <c>true</c>, because the trigger characters live in it and
/// there is nowhere else to put them -- the same reason <see cref="CompletionOptions"/> is one. The
/// comma appears in both lists deliberately: the first opens the panel on the first argument, the
/// second moves it along on every argument after that, and a client consults a different list for
/// each.
/// </remarks>
public sealed record SignatureHelpOptions
{
    public IReadOnlyList<string> TriggerCharacters { get; init; } = [];

    public IReadOnlyList<string> RetriggerCharacters { get; init; } = [];
}
