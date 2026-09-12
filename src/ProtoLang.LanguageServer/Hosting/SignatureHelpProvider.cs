using ProtoLang.Ir;
using ProtoLang.LanguageServer.Protocol.Lsp;
using ProtoLang.LanguageServer.Workspace;
using ProtoLang.Symbols;
using SymbolKind = ProtoLang.Symbols.SymbolKind;

namespace ProtoLang.LanguageServer.Hosting;

/// <summary>
/// The signature of the call being typed, with the argument the caret is supplying marked.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two halves from two places, because neither can answer the other's question.</b> Where the
/// caret is -- which call, which argument slot -- comes from the token stream through
/// <see cref="CallSubject"/>, because the tree records no comma positions and has recovered from the
/// half-written call by the time anything can be asked of it. What the call <em>names</em> comes from
/// the binder, because a lexical guess about which method <c>scaled</c> refers to is a second name
/// resolution and would disagree with go-to-definition the first time a schema grew a method of that
/// name.
/// </para>
/// <para>
/// <b>The binder still records the method even when the call is wrong, which is what makes this
/// possible at all.</b> A call being typed almost never has the right number of arguments -- a
/// trailing comma alone manufactures a phantom one -- so it fails the arity check and the node it
/// produces carries no target. But the reference to the method name was recorded before that check
/// ran, so the identity survives where the node does not, and
/// <see cref="IrModule.SignatureOf"/> turns it back into the signature.
/// </para>
/// <para>
/// <b>Nothing, cleanly, wherever a step fails.</b> A name that is not a method, a misspelt one, a
/// caret outside any parentheses: each produces no panel rather than a plausible one. There is no
/// overloading in this language (spec 16.1), so where there is an answer there is exactly one, and
/// none of the candidate ranking a signature help usually does applies.
/// </para>
/// <para>
/// Read in order and answered out of it, bounded and refused when stale, on the same terms as
/// <see cref="HoverProvider"/>.
/// </para>
/// </remarks>
public sealed class SignatureHelpProvider
{
    /// <summary>What opens the panel, and what moves it along once it is open.</summary>
    /// <remarks>
    /// The comma is in both lists on purpose: a client consults the first when no panel is showing
    /// and the second when one is, and a comma is the one character that does both jobs. Nothing
    /// dismisses the panel explicitly -- a close parenthesis leaves the caret inside no call, this
    /// answers nothing, and the client takes that as the dismissal.
    /// </remarks>
    public static IReadOnlyList<string> TriggerCharacters { get; } = ["(", ","];

    /// <inheritdoc cref="TriggerCharacters"/>
    public static IReadOnlyList<string> RetriggerCharacters { get; } = [","];

    /// <inheritdoc cref="LabelOffsets"/>
    private volatile bool _labelOffsets;

    private readonly DocumentStore _documents;
    private readonly ConfigurationSync _configuration;
    private readonly DocumentSemantics _semantics;
    private readonly DeferredAnswers _deferred;

    public SignatureHelpProvider(
        DocumentStore documents,
        ConfigurationSync configuration,
        LoaderPool loaders,
        int concurrency = DeferredAnswers.DefaultConcurrency,
        DocumentSemantics? semantics = null)
    {
        ArgumentNullException.ThrowIfNull(loaders);

        _documents = documents ?? throw new ArgumentNullException(nameof(documents));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _semantics = semantics ?? new DocumentSemantics(loaders);
        _deferred = new DeferredAnswers("signature help", documents, configuration, concurrency);
    }

    /// <summary>Whether the client accepts a parameter label as a pair of offsets.</summary>
    /// <remarks>
    /// Set at <c>initialize</c>, on the worker that reads the wire, and read on whichever thread ends
    /// up answering -- so the field behind it is volatile, for the reason
    /// <see cref="DefinitionProvider.LinkSupport"/>'s is. The two label forms are not interchangeable:
    /// a client that never asked for offsets and is sent an array finds no string where it expects
    /// one, and the parameter it was meant to point at is the one thing the panel then fails to say.
    /// </remarks>
    public bool LabelOffsets
    {
        get => _labelOffsets;
        set => _labelOffsets = value;
    }

    /// <inheritdoc cref="DeferredAnswers.Outstanding"/>
    public int Outstanding => _deferred.Outstanding;

    /// <inheritdoc cref="DeferredAnswers.InFlight"/>
    public int InFlight => _deferred.InFlight;

    /// <inheritdoc cref="DeferredAnswers.PeakInFlight"/>
    public int PeakInFlight => _deferred.PeakInFlight;

    /// <inheritdoc cref="PositionRequest.Read(DocumentStore, ConfigurationSync, TextDocumentPositionParams)"/>
    public PositionRequest? Read(TextDocumentPositionParams message)
        => PositionRequest.Read(_documents, _configuration, message);

    /// <summary>
    /// The signature of the call the caret is inside, or null when it is inside none that resolved.
    /// </summary>
    /// <inheritdoc cref="DeferredAnswers.AnswerAsync" path="/exception"/>
    public Task<SignatureHelp?> AnswerAsync(PositionRequest asked, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asked);

        return _deferred.AnswerAsync(asked, token => Answer(asked, token), cancellationToken);
    }

    /// <inheritdoc cref="DeferredAnswers.Forget"/>
    public void Forget(DocumentUri document) => _deferred.Forget(document);

    private SignatureHelp? Answer(PositionRequest asked, CancellationToken cancellationToken)
    {
        if (!CallSubject.TryFind(asked.Document.Text, asked.Offset, out var call) || call is null)
        {
            return null;
        }

        var compiled = _semantics.For(asked.Document, asked.Configuration, cancellationToken);

        // A declaration is not a call, and lexically the two are the same shape: `fn scaled(` has an
        // identifier before an open parenthesis exactly as `scaled(` does. What tells them apart is
        // what the binder recorded -- the name in a declaration is where the method was introduced,
        // and describing a method to the author who is still writing its parameter list would be
        // showing them what they have typed so far as though it were settled.
        if (compiled.Semantics?.ReferenceAt(call.Callee.Start.Offset) is not { } reference
            || reference.Symbol.Kind is not SymbolKind.Method
            || reference.Kind is ReferenceKind.Declaration
            || compiled.Result?.Module?.SignatureOf(reference.Symbol) is not { } signature)
        {
            return null;
        }

        return new SignatureHelp
        {
            Signatures = [Describe(signature, LabelOffsets)],
            ActiveParameter = Highlighted(call.ActiveParameter, signature.Parameters.Count),
        };
    }

    /// <summary>Which parameter to point at when more arguments were written than exist.</summary>
    /// <remarks>
    /// <para>
    /// <b>Clamped, because out of range does not mean "none" on the wire.</b> LSP 3.17 says an
    /// <c>activeParameter</c> outside the signature's parameters falls back to zero -- so sending the
    /// honest index for a third argument to a two-parameter method makes the client highlight the
    /// <em>first</em> one, which is the most misleading answer available: it points at the argument
    /// furthest from the mistake. Pointing at the last parameter is not true either, but it is
    /// adjacent to what is being typed, and it is what a reader of the panel can make sense of.
    /// </para>
    /// <para>
    /// A method with no parameters clamps to zero, which the same rule renders as nothing, because
    /// there is nothing to point at.
    /// </para>
    /// </remarks>
    private static int Highlighted(int supplied, int parameters)
        => parameters == 0 ? 0 : Math.Min(supplied, parameters - 1);

    /// <remarks>
    /// <para>
    /// The label is the one spelling of a signature this repository has, which a hover already shows,
    /// and the parameter ranges come from beside it rather than from searching it -- see
    /// <see cref="IrMethodSignature.ParameterLabels"/> for why a search is wrong rather than merely
    /// slower.
    /// </para>
    /// <para>
    /// Both forms are cut from those same ranges, so a client that took the substring is told exactly
    /// what a client that took the offsets is told. What it loses is the one case the ranges exist
    /// for: two parameters spelled alike are two identical substrings, and finding the first is the
    /// client's own rule. Sending it an array instead would not fix that and would lose the
    /// highlight altogether.
    /// </para>
    /// </remarks>
    private static SignatureInformation Describe(IrMethodSignature signature, bool offsets)
    {
        var label = signature.DisplayName;

        return new SignatureInformation
        {
            Label = label,
            Parameters =
            [
                .. signature.ParameterLabels.Select(parameter => offsets
                    ? new ParameterInformation { Offsets = [parameter.Start, parameter.End] }
                    : new ParameterInformation { Written = label[parameter.Start..parameter.End] }),
            ],
        };
    }
}
