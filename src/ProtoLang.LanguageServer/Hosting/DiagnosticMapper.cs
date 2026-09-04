using ProtoLang.Diagnostics;
using ProtoLang.LanguageServer.Protocol.Lsp;
using Diagnostic = ProtoLang.LanguageServer.Protocol.Lsp.Diagnostic;
using DiagnosticSeverity = ProtoLang.Diagnostics.DiagnosticSeverity;
using Range = ProtoLang.LanguageServer.Protocol.Lsp.Range;

namespace ProtoLang.LanguageServer.Hosting;

/// <summary>What a client hands back when it asks for a code action on a diagnostic.</summary>
/// <remarks>
/// The title and the help text, structurally, whatever was done to render them for a person. #61
/// turns help into quick fixes and should read the string the compiler wrote rather than recover it
/// from prose that a rendering decision may have reflowed or prefixed.
/// </remarks>
public sealed record DiagnosticData(string Title, string? Help);

/// <summary>
/// Turns a compiler diagnostic into an editor one, without losing any part of it.
/// </summary>
/// <param name="relatedInformationSupported">
/// Whether the client declared <c>publishDiagnostics.relatedInformation</c>, which decides how help
/// text is rendered.
/// </param>
/// <remarks>
/// <para>
/// <b>Severity is mapped, not invented.</b> Spec 26 gives the compiler two levels and LSP has four.
/// Promoting something to <c>Information</c> or <c>Hint</c> here would be this server asserting a
/// distinction the language does not draw; if those are wanted they are a change to the compiler's own
/// severity set, and one worth arguing about in its own right.
/// </para>
/// <para>
/// <b>The title is carried in the message, because LSP has nowhere else to put it.</b> Spec 26's
/// template puts <c>PL0001: no proto imports</c> on one line and the message on the next; a client
/// renders the code itself, in a column of its own, so prefixing the title reconstructs the same
/// reading. It earns its place: <c>PL0003</c>'s message is whatever protoc wrote, and without
/// "protobuf schema could not be loaded" in front of it a reader has to work out for themselves
/// which part of the compiler is speaking.
/// </para>
/// <para>
/// <b>Help text is not appended to the message where that can be avoided.</b> Several ProtoLang
/// diagnostics put the actionable instruction in <c>Help</c> -- <c>PL0078</c> tells the user exactly
/// how to guard a message field -- and running it into the message makes it one long sentence a reader
/// skims past. A related-information entry renders it as its own line the client shows beside the
/// diagnostic. Where the client did not declare support for that, appending is the only remaining way
/// to keep it visible, and losing it is not an option; it goes into <see cref="Diagnostic.Data"/>
/// either way.
/// </para>
/// </remarks>
public sealed class DiagnosticMapper(bool relatedInformationSupported)
{
    /// <summary>What this server calls itself in the diagnostics it produces.</summary>
    public const string Source = "protolang";

    /// <summary>The range a diagnostic with no location is published at.</summary>
    /// <remarks>
    /// The very start of the document. An unusable include path, a setting being ignored, a
    /// configuration file that was refused -- none of them is anywhere in the source, and all of them
    /// have to be seen. The message already names where the value really came from
    /// (<c>&lt;workspace settings&gt;</c>, <c>protolang.config.xml</c>), so the range being a
    /// placeholder does not make the diagnostic ambiguous.
    /// </remarks>
    public static Range WholeDocumentStart { get; } = new(new Position(0, 0), new Position(0, 0));

    /// <param name="at">
    /// Where to draw it, when the caller knows better than the span does. Used for a diagnostic whose
    /// position is a position in some other file: publishing it at that position against this document
    /// would be a squiggle on an unrelated line, or past the end of the text.
    /// </param>
    public Diagnostic Map(ProtoLang.Diagnostics.Diagnostic diagnostic, string uri, Range? at = null)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);

        var range = at ?? RangeOf(diagnostic.Span);
        var help = diagnostic.Help;
        var message = $"{diagnostic.Title}: {diagnostic.Message}";

        return new Diagnostic
        {
            Range = range,
            Severity = diagnostic.Severity == DiagnosticSeverity.Error
                ? Protocol.Lsp.DiagnosticSeverity.Error
                : Protocol.Lsp.DiagnosticSeverity.Warning,
            Code = diagnostic.Code,
            Source = Source,
            Message = help is not null && !relatedInformationSupported ? $"{message}\nhelp: {help}" : message,
            RelatedInformation = help is not null && relatedInformationSupported
                ? [new DiagnosticRelatedInformation(new Location(uri, range), help)]
                : null,
            Data = new DiagnosticData(diagnostic.Title, help),
        };
    }

    /// <summary>The editor range a compiler span names.</summary>
    /// <remarks>
    /// Both coordinate systems are 0-based in LSP and 1-based in the compiler, so the conversion is a
    /// subtraction -- except for a span that is nowhere, which must never go through it. Line 0 minus
    /// one is line -1, which is not a position any client can be given. Everything else is clamped at
    /// zero as well, because a span this server did not produce is a span this server does not get to
    /// assume about.
    /// </remarks>
    public static Range RangeOf(SourceSpan span)
        => span.IsNone
            ? WholeDocumentStart
            : new Range(PositionOf(span.Start), PositionOf(span.End));

    /// <inheritdoc cref="RangeOf"/>
    private static Position PositionOf(SourcePosition position)
        => new(Math.Max(position.Line - 1, 0), Math.Max(position.Column - 1, 0));
}
