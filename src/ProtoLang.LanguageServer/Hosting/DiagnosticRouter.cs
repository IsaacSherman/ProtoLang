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

    /// <summary>What has been settled and not yet written, in the order it was settled.</summary>
    /// <remarks>
    /// <para>
    /// Settling and writing were one step, and the order they happened in was whatever order the
    /// threads doing them woke up in. That is how a client ends up holding the older of two answers:
    /// a compile settles, is descheduled before it writes, a close settles and writes its empty list,
    /// and then the compile writes the diagnostics the close had just withdrawn. Nothing corrects it
    /// afterwards, because <see cref="_lastPublished"/> already records the newer state and so the
    /// next comparison finds nothing to say.
    /// </para>
    /// <para>
    /// The obvious repair -- hold the lock across the write -- trades that bug for a worse one. A
    /// close would then wait on a write that a slow client has stalled, and closing a document is
    /// handled on the one worker that reads every other notification, so the whole server would stop
    /// with it. Queueing separates the two: the order is fixed when a message is settled, and the
    /// thread that settled it is free to leave.
    /// </para>
    /// </remarks>
    private readonly Queue<PublishDiagnosticsParams> _outbox = new();

    /// <summary>The tail of the chain that drains <see cref="_outbox"/>, one message at a time.</summary>
    private Task _pumping = Task.CompletedTask;

    private readonly Dictionary<string, Dictionary<string, DiagnosticContribution.Entry>> _byOwner =
        new(StringComparer.Ordinal);

    private readonly Dictionary<string, string> _lastPublished = new(StringComparer.Ordinal);

    /// <summary>Replaces everything <paramref name="owner"/> had to say.</summary>
    /// <param name="stale">
    /// Asked, under the lock, whether the answer still describes anything. Its job is to order this
    /// publication against a <see cref="ClearAsync"/> that may be happening at the same moment.
    /// </param>
    /// <returns>Whether the answer was published, as opposed to refused for being stale.</returns>
    /// <remarks>
    /// The predicate has to be evaluated here rather than by the caller beforehand, and the reason is
    /// a race that costs a user real squiggles. A compile checks that its document is still open,
    /// finds that it is, and is then descheduled; the close arrives, withdraws the document's
    /// diagnostics, and removes it from the store; the compile resumes and publishes. The owner is
    /// gone from everything that would later clear it, so the editor shows errors on a closed file
    /// until the session ends. This lock is the only thing that orders a publication against a
    /// withdrawal, so the question has to be asked while it is held.
    /// </remarks>
    public async Task<bool> PublishAsync(DocumentUri owner, DiagnosticContribution contribution, Func<bool>? stale = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(contribution);

        var replacement = contribution.Entries.ToDictionary(entry => entry.Uri.Key, StringComparer.Ordinal);

        Task pumping;

        lock (_gate)
        {
            var messages = Settle(owner.Key, replacement, stale);

            if (messages is null)
            {
                return false;
            }

            pumping = Enqueue(messages);
        }

        // Waited for, unlike a clear: this runs on a compile worker, and a compile worker blocking
        // while the client catches up is the backpressure that stops work piling up faster than it can
        // be reported. #54 owns the bound that makes that a queue rather than a stall.
        await pumping.ConfigureAwait(false);

        return true;
    }

    /// <summary>Withdraws everything <paramref name="owner"/> had to say, because it has closed.</summary>
    /// <remarks>
    /// Returns once the withdrawal is settled and queued, without waiting for it to be written. A
    /// close is handled on the worker that reads every other notification, so a close that waited on a
    /// stalled client would stop the server from reading anything at all. The ordering that matters is
    /// already fixed: whatever was settled before this is written before it, and whatever comes after
    /// is written after.
    /// </remarks>
    public Task ClearAsync(DocumentUri owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        lock (_gate)
        {
            Enqueue(Settle(owner.Key, [], stale: null)!);
        }

        return Task.CompletedTask;
    }

    /// <summary>Adds messages to the outbox and makes sure something is draining it.</summary>
    /// <remarks>
    /// Called with <see cref="_gate"/> held, so the order messages enter the queue is the order they
    /// were settled in. The drain is chained onto the previous one rather than started beside it,
    /// which is what makes the writes serial; it is started on the thread pool rather than inline so
    /// that it cannot begin while the caller still holds the lock it needs.
    /// </remarks>
    private Task Enqueue(List<PublishDiagnosticsParams> messages)
    {
        foreach (var message in messages)
        {
            _outbox.Enqueue(message);
        }

        _pumping = _pumping.ContinueWith(_ => DrainAsync(), TaskScheduler.Default).Unwrap();

        return _pumping;
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            PublishDiagnosticsParams message;

            lock (_gate)
            {
                if (_outbox.Count == 0)
                {
                    return;
                }

                message = _outbox.Dequeue();
            }

            await publish(message).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Swaps in one owner's answer and works out which files that changed the published set for.
    /// </summary>
    /// <remarks>
    /// Called with <see cref="_gate"/> already held, and it does no I/O of its own: it decides what to
    /// send and hands it back to a caller that is still holding the gate while it writes.
    /// </remarks>
    /// <returns>What to send, or null when the answer was refused as stale.</returns>
    private List<PublishDiagnosticsParams>? Settle(
        string owner,
        Dictionary<string, DiagnosticContribution.Entry> replacement,
        Func<bool>? stale)
    {
        var messages = new List<PublishDiagnosticsParams>();

        if (stale?.Invoke() == true)
        {
            return null;
        }

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
