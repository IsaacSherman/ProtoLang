using System.Globalization;
using System.Text;
using ProtoLang.LanguageServer.Protocol.Lsp;
using ProtoLang.LanguageServer.Workspace;

namespace ProtoLang.LanguageServer.Hosting;

/// <summary>
/// Everything one compilation has to say, filed under the document each thing belongs to.
/// </summary>
/// <remarks>
/// More than one document, because a ProtoLang compilation can be wrong about a file that is not the
/// one being compiled: protoc's complaints belong in the <c>.proto</c> it named. Building the whole
/// answer before any of it is published is what lets the router work out which files stopped having
/// problems as well as which started.
/// </remarks>
public sealed class DiagnosticContribution
{
    private readonly Dictionary<string, Entry> _byUri = new(StringComparer.Ordinal);

    /// <summary>One document's share of the answer.</summary>
    public sealed record Entry(DocumentUri Uri, List<Diagnostic> Diagnostics);

    public IReadOnlyCollection<Entry> Entries => _byUri.Values;

    public void Add(DocumentUri uri, Diagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(diagnostic);

        Claim(uri).Diagnostics.Add(diagnostic);
    }

    /// <summary>
    /// Names a document as covered by this answer even if nothing is wrong with it.
    /// </summary>
    /// <remarks>
    /// How a file stops being wrong. LSP has no way to withdraw one diagnostic, so a document whose
    /// last error was just fixed has to be published with an empty list -- and a compilation that
    /// simply omitted it would leave the old squiggles on the screen forever.
    /// </remarks>
    public Entry Claim(DocumentUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        if (!_byUri.TryGetValue(uri.Key, out var entry))
        {
            entry = new Entry(uri, []);
            _byUri[uri.Key] = entry;
        }

        return entry;
    }
}

/// <summary>
/// Decides what each document's published diagnostics are, given what every open document has to say
/// about it.
/// </summary>
/// <remarks>
/// <para>
/// The complication is that diagnostics are published <em>per file</em> and produced <em>per
/// compilation</em>, and the two do not line up once a <c>.proto</c> can be blamed. Two ProtoLang
/// buffers importing the same broken schema both report it; closing one of them must not clear it,
/// because the other one is still open and still broken. So what is published for a file is the union
/// of what every owner currently says about it, and an owner's contribution is replaced wholesale
/// each time it recompiles.
/// </para>
/// <para>
/// Identical diagnostics from two owners are collapsed. Without that, opening a second file that
/// imports the same schema doubles every squiggle in it, which reads as two distinct problems.
/// </para>
/// <para>
/// A file whose merged set has not changed is not re-sent. An editor that is told the same thing
/// repeatedly redraws, and a compile runs on every settled keystroke.
/// </para>
/// </remarks>
public sealed class DiagnosticRouter(
    Func<PublishDiagnosticsParams, Task> publish,
    Func<DocumentUri, int?> versionOf)
{
    private readonly Lock _gate = new();

    private readonly Dictionary<string, Dictionary<string, DiagnosticContribution.Entry>> _byOwner =
        new(StringComparer.Ordinal);

    private readonly Dictionary<string, string> _lastPublished = new(StringComparer.Ordinal);

    /// <summary>Replaces everything <paramref name="owner"/> had to say.</summary>
    public Task PublishAsync(DocumentUri owner, DiagnosticContribution contribution)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(contribution);

        var replacement = contribution.Entries.ToDictionary(entry => entry.Uri.Key, StringComparer.Ordinal);

        return SendAsync(Settle(owner.Key, replacement));
    }

    /// <summary>Withdraws everything <paramref name="owner"/> had to say, because it has closed.</summary>
    public Task ClearAsync(DocumentUri owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        return SendAsync(Settle(owner.Key, []));
    }

    /// <summary>
    /// Swaps in one owner's answer and works out which files that changed the published set for.
    /// </summary>
    /// <remarks>
    /// Under the lock, and it does no I/O: it decides what to send and returns it. Sending inside the
    /// lock would mean holding it across a write to a stream a slow client may not be reading.
    /// </remarks>
    private List<PublishDiagnosticsParams> Settle(
        string owner,
        Dictionary<string, DiagnosticContribution.Entry> replacement)
    {
        var messages = new List<PublishDiagnosticsParams>();

        lock (_gate)
        {
            _byOwner.TryGetValue(owner, out var previous);

            if (replacement.Count == 0)
            {
                _byOwner.Remove(owner);
            }
            else
            {
                _byOwner[owner] = replacement;
            }

            // Everything this owner touches now, plus everything it used to touch: the second half is
            // what clears a file the owner has stopped having an opinion about.
            var affected = new Dictionary<string, DocumentUri>(StringComparer.Ordinal);
            foreach (var entry in replacement.Values)
            {
                affected[entry.Uri.Key] = entry.Uri;
            }

            foreach (var entry in previous?.Values ?? Enumerable.Empty<DiagnosticContribution.Entry>())
            {
                affected[entry.Uri.Key] = entry.Uri;
            }

            foreach (var (key, uri) in affected)
            {
                var merged = Merge(key);
                var signature = SignatureOf(merged);

                if (_lastPublished.TryGetValue(key, out var sent) && string.Equals(sent, signature, StringComparison.Ordinal))
                {
                    continue;
                }

                if (merged.Count == 0)
                {
                    _lastPublished.Remove(key);
                }
                else
                {
                    _lastPublished[key] = signature;
                }

                messages.Add(new PublishDiagnosticsParams
                {
                    Uri = uri.Text,
                    Version = versionOf(uri),
                    Diagnostics = merged,
                });
            }
        }

        return messages;
    }

    /// <summary>What every owner currently says about one file, said once each.</summary>
    private List<Diagnostic> Merge(string uri)
    {
        var merged = new List<Diagnostic>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var owner in _byOwner.Values)
        {
            if (!owner.TryGetValue(uri, out var entry))
            {
                continue;
            }

            foreach (var diagnostic in entry.Diagnostics)
            {
                if (seen.Add(SignatureOf(diagnostic)))
                {
                    merged.Add(diagnostic);
                }
            }
        }

        return merged;
    }

    private async Task SendAsync(List<PublishDiagnosticsParams> messages)
    {
        foreach (var message in messages)
        {
            await publish(message).ConfigureAwait(false);
        }
    }

    /// <remarks>
    /// Value identity for a diagnostic, which record equality does not give: the related-information
    /// list and the data object are compared by reference, so two diagnostics saying exactly the same
    /// thing about the same range are unequal. What makes two of them the same to a reader is what is
    /// written here.
    /// </remarks>
    private static string SignatureOf(Diagnostic diagnostic)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{diagnostic.Range.Start.Line}:{diagnostic.Range.Start.Character}-{diagnostic.Range.End.Line}:{diagnostic.Range.End.Character}|{(int)diagnostic.Severity}|{diagnostic.Code}|{diagnostic.Source}|{diagnostic.Message}");

    /// <summary>A character no diagnostic text contains, so joined signatures cannot collide.</summary>
    private const char Separator = '\u001f';

    private static string SignatureOf(List<Diagnostic> diagnostics)
    {
        var signature = new StringBuilder();

        foreach (var diagnostic in diagnostics)
        {
            signature.Append(SignatureOf(diagnostic)).Append(Separator);
        }

        return signature.ToString();
    }
}
