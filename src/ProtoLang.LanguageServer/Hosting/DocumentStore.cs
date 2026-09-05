using System.Collections.Concurrent;
using ProtoLang.Diagnostics;
using ProtoLang.LanguageServer.Protocol.Lsp;
using ProtoLang.LanguageServer.Workspace;

namespace ProtoLang.LanguageServer.Hosting;

/// <summary>A buffer the editor has open, at one version.</summary>
/// <remarks>
/// <para>
/// The buffer is the source of truth and the file on disk is not consulted while it is open. Between
/// saves the disk copy is stale, and before the first save there is no disk copy at all -- which is
/// why <see cref="SourceIdentity"/> already has a shape for text with no path, and why this hands the
/// compiler a <see cref="SourceDocument"/> rather than a file name.
/// </para>
/// <para>
/// Immutable. An edit produces a new one, so a compile that started three keystrokes ago still holds
/// the exact text it was reading, and the version it holds is the version it can be judged stale
/// against.
/// </para>
/// </remarks>
public sealed class OpenDocument
{
    private LineMap? _lines;

    public OpenDocument(DocumentUri uri, string languageId, int version, string text)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(text);

        Uri = uri;
        LanguageId = languageId ?? string.Empty;
        Version = version;
        Text = text;
    }

    public DocumentUri Uri { get; }

    /// <summary>What the client calls this language. Not consulted; kept for the log.</summary>
    public string LanguageId { get; }

    public int Version { get; }

    public string Text { get; }

    /// <summary>Where the lines of <see cref="Text"/> begin.</summary>
    /// <remarks>
    /// Built on demand and kept, because an edit asks for it and so does anything converting an
    /// editor position. Not synchronized: two threads racing to build it produce equal maps and one of
    /// them is discarded, which costs a scan and cannot be observed.
    /// </remarks>
    public LineMap Lines => _lines ??= new LineMap(Text);

    /// <summary>The same document with new text, at a new version.</summary>
    public OpenDocument With(int version, string text) => new(Uri, LanguageId, version, text);

    /// <summary>What the compiler is handed.</summary>
    /// <param name="directory">
    /// Where a buffer with no path of its own should be considered to live -- its workspace folder --
    /// so that an unsaved file inside an open project gets the project's policy and proto directory
    /// rather than being treated as if it came from nowhere. Ignored for a document that has a path.
    /// </param>
    public SourceDocument ToSource(string? directory)
        => new(
            Uri.Path is { } path ? SourceIdentity.FromPath(path) : SourceIdentity.Unsaved(Uri.Text, directory),
            Text);
}

/// <summary>Every document the editor has open, and the edits that move them along.</summary>
public sealed class DocumentStore
{
    private readonly ConcurrentDictionary<string, OpenDocument> _documents = new(StringComparer.Ordinal);

    /// <summary>Every open document, as a snapshot that will not change while it is walked.</summary>
    public IReadOnlyList<OpenDocument> All => [.. _documents.Values];

    public OpenDocument Open(DocumentUri uri, string languageId, int version, string text)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var document = new OpenDocument(uri, languageId, version, text);
        _documents[uri.Key] = document;

        return document;
    }

    public OpenDocument? Find(DocumentUri uri) => uri is null ? null : _documents.GetValueOrDefault(uri.Key);

    /// <summary>Forgets a document, and says what it was.</summary>
    public OpenDocument? Close(DocumentUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        return _documents.TryRemove(uri.Key, out var closed) ? closed : null;
    }

    /// <summary>
    /// Applies one notification's worth of changes, or null when the document is not open.
    /// </summary>
    /// <remarks>
    /// In order, each against the result of the one before it, because that is what the client meant:
    /// the second change's range was computed against the text the first change produced. Applying
    /// them in any other order, or all against the original text, silently corrupts the buffer -- and
    /// the corruption shows up later as diagnostics on the wrong lines, a long way from here.
    /// </remarks>
    public OpenDocument? Apply(DocumentUri uri, int version, IReadOnlyList<TextDocumentContentChangeEvent> changes)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(changes);

        if (Find(uri) is not { } document)
        {
            return null;
        }

        var text = document.Text;
        foreach (var change in changes)
        {
            text = Apply(text, change);
        }

        var updated = document.With(version, text);
        _documents[uri.Key] = updated;

        return updated;
    }

    /// <remarks>
    /// A change with no range replaces everything, which a client may send even to a server that
    /// asked for incremental updates. Offsets come from <see cref="LineMap"/>, which clamps rather
    /// than throwing -- a range describing text the server does not have is something a client can
    /// legitimately produce when messages cross, and it must not take a request down.
    /// </remarks>
    private static string Apply(string text, TextDocumentContentChangeEvent change)
    {
        if (change.Range is not { } range)
        {
            return change.Text;
        }

        var lines = new LineMap(text);
        var start = lines.OffsetOf(range.Start.Line + 1, range.Start.Character + 1);
        var end = lines.OffsetOf(range.End.Line + 1, range.End.Character + 1);

        if (end < start)
        {
            (start, end) = (end, start);
        }

        return string.Concat(text.AsSpan(0, start), change.Text.AsSpan(), text.AsSpan(end));
    }
}
