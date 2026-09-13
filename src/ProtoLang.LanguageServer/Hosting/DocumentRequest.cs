using ProtoLang.LanguageServer.Protocol.Lsp;
using ProtoLang.LanguageServer.Workspace;

namespace ProtoLang.LanguageServer.Hosting;

/// <summary>
/// One request, pinned to the buffer and the configuration it was asked about.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is settled while messages are still being read in order, and none of it can move
/// afterwards: <see cref="OpenDocument"/> and <see cref="WorkspaceConfiguration"/> are both
/// immutable, so holding the objects holds the question. What comes later -- listing directories,
/// compiling -- may take as long as it takes without changing what the answer is about, and can be
/// checked against these two before it is sent.
/// </para>
/// <para>
/// <b>Objects rather than a version and a generation.</b> A version number is unique only within one
/// open session: close a document and reopen it and the client starts again at one, so a request
/// read at version one compares equal to a buffer that may hold something else entirely. The store
/// hands out a fresh <see cref="OpenDocument"/> for every edit and every open, so "is this still the
/// object I read?" answers editing, closing and reopening in one question.
/// </para>
/// <para>
/// A base class rather than one shape per request kind, because <see cref="DeferredAnswers"/> is
/// what supersedes, bounds and re-checks all of them and it needs exactly these three. What each
/// kind adds -- a caret offset, a completion context -- it adds below.
/// </para>
/// </remarks>
public abstract class DocumentRequest
{
    protected DocumentRequest(
        DocumentUri uri, OpenDocument document, WorkspaceConfiguration configuration)
    {
        Uri = uri ?? throw new ArgumentNullException(nameof(uri));
        Document = document ?? throw new ArgumentNullException(nameof(document));
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    public DocumentUri Uri { get; }

    /// <summary>The buffer this is about, as an object rather than as a version.</summary>
    public OpenDocument Document { get; }

    /// <summary>The settings this is about, as an object rather than as a generation.</summary>
    public WorkspaceConfiguration Configuration { get; }
}

/// <summary>A request about one caret.</summary>
/// <param name="offset">
/// Where the caret is, in the buffer's own coordinates. Converted from the client's line and
/// character while the document is still being held, so that the offset and the text it indexes
/// into are the same instant.
/// </param>
/// <remarks>
/// Hover and go-to-definition are the same question asked of one position and answered two ways, so
/// they are one shape. Completion is not among them: it settles which <em>context</em> the caret is
/// in before it yields, and that decision is the thing it may not defer.
/// </remarks>
public sealed class PositionRequest(
    DocumentUri uri, OpenDocument document, WorkspaceConfiguration configuration, int offset)
    : DocumentRequest(uri, document, configuration)
{
    /// <inheritdoc cref="PositionRequest(DocumentUri, OpenDocument, WorkspaceConfiguration, int)" path="/param[@name='offset']"/>
    public int Offset { get; } = offset;

    /// <summary>
    /// Which buffer a request is about and where in it -- settled while messages are still being
    /// read in order -- or null when the document is not open.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This must not be deferred.</b> Everything after it is allowed to take as long as compiling
    /// does, and none of it is allowed to decide <em>which text</em> the answer is about: a
    /// <c>didChange</c> sitting behind this request in the queue would otherwise be applied first,
    /// and the offset would be measured against a buffer the client had not sent when it asked. The
    /// configuration is captured here for the same reason and by the same means -- it is immutable
    /// and stamped with a generation, so holding the object holds the answer.
    /// </para>
    /// <para>
    /// On this type rather than on each provider, because it is that rule and every position request
    /// obeys the same one. A second copy of it is the copy that eventually reads the buffer a
    /// keystroke later.
    /// </para>
    /// <para>
    /// Null when the document is not open -- closed before the request was read, or never opened.
    /// Nothing rather than an error: the client has done nothing wrong, and there is genuinely
    /// nothing to say about a buffer this server does not hold.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static PositionRequest? Read(
        DocumentStore documents, ConfigurationSync configuration, TextDocumentPositionParams message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return Read(documents, configuration, message.TextDocument, message.Position);
    }

    /// <inheritdoc cref="Read(DocumentStore, ConfigurationSync, TextDocumentPositionParams)"/>
    /// <remarks>
    /// The two pieces rather than the shape that usually carries them, for the requests whose params
    /// carry something else as well -- a reference request states whether it wants the declaration.
    /// Building a throwaway <see cref="TextDocumentPositionParams"/> to hand to the overload above
    /// would be assembling a message nobody sent in order to take it apart again.
    /// </remarks>
    public static PositionRequest? Read(
        DocumentStore documents,
        ConfigurationSync configuration,
        TextDocumentIdentifier document,
        Position position)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(position);

        if (!DocumentUri.TryParse(document.Uri, out var uri)
            || documents.Find(uri) is not { } open)
        {
            return null;
        }

        return new PositionRequest(
            uri!,
            open,
            configuration.Current,
            EditorPositions.OffsetOf(open.Lines, position));
    }
}
