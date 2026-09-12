using System.Collections.Concurrent;
using ProtoLang.LanguageServer.Protocol;
using ProtoLang.LanguageServer.Workspace;

namespace ProtoLang.LanguageServer.Hosting;

/// <summary>
/// What one kind of request owes for being answered off the worker that reads the wire.
/// </summary>
/// <remarks>
/// <para>
/// A handler that leaves this process -- one that opens a directory, or waits on a tool -- must not
/// be answered in order, because behind it sit every <c>didChange</c>, every <c>didClose</c>, and
/// the <c>$/cancelRequest</c> that would have shortened it. <see cref="JsonRpcConnection.OnRequest"/>
/// says so and says what such a handler owes in return. This is that debt, paid once: completion,
/// hover, go-to-definition, classification, find-references, occurrence highlighting and signature
/// help all leave the process through <see cref="DocumentSemantics"/>, and seven copies of the rules
/// below would be seven things to keep in agreement. The count is the argument -- it was three when
/// this was written and has not needed a second copy since.
/// </para>
/// <para>
/// <b>Supersede.</b> A newer request for a document cancels the outstanding one for that document,
/// exactly as a keystroke supersedes a scheduled compile, so a client that asks on every character
/// cannot accumulate work. The client's own withdrawal is linked to the same token, so either reason
/// stops the same walk.
/// </para>
/// <para>
/// <b>Bound.</b> Across documents a semaphore bounds how many run at once, because a per-walk budget
/// bounds one walk and says nothing about how many there are -- and a slow include root turns that
/// into threads and open handles piling up behind a user who is simply typing.
/// </para>
/// <para>
/// <b>Give up what nobody is waiting for, before it takes a slot.</b> Waiting is where a busy
/// request spends most of its life, and most of the reasons a request ends arrive while it waits. So
/// the wait sits inside the same cleanup as the work: whichever way it ends the entry is retired,
/// and the slot is given back only if it was ever taken. And freshness is checked when a request
/// reaches the front of the queue as well as when it finishes, because an edit is the one reason for
/// abandonment that carries no cancellation to notice.
/// </para>
/// <para>
/// <b>Answer about what was read, or refuse.</b> Spec 26.1 requires it of every kind of answer and
/// not only of diagnostics: a hover or a completion computed against text the user has already
/// replaced describes something nobody is looking at. A request that is owed a reply is refused with
/// <c>ContentModified</c> rather than answered.
/// </para>
/// <para>
/// <b>One of these per request kind, rather than one per server.</b> Superseding is per document
/// <em>and</em> per kind: a hover while a completion is outstanding is a different question about
/// the same buffer and must not cancel it, and a go-to-definition is a deliberate click that a
/// passing mouse must never take back. The counters follow the same seam, which is what lets #58
/// report and a soak test watch each surface on its own rather than watching one number that three
/// features move.
/// </para>
/// </remarks>
public sealed class DeferredAnswers
{
    /// <summary>How many of one kind of request may be leaving the process at once.</summary>
    /// <remarks>
    /// The same figure and the same reasoning as <see cref="CompileScheduler.DefaultConcurrency"/>:
    /// ten open documents must not mean ten simultaneous walks of an include root. #57 pins it, and
    /// <see cref="PeakInFlight"/> is what shows whether whatever it is pinned to is honoured.
    /// </remarks>
    public const int DefaultConcurrency = 4;

    private readonly DocumentStore _documents;
    private readonly ConfigurationSync _configuration;
    private readonly SemaphoreSlim _concurrency;

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _outstanding =
        new(StringComparer.Ordinal);

    private int _inFlight;
    private int _peakInFlight;

    /// <param name="what">
    /// What this answers, named for the refusals: a client reading "was edited while this hover was
    /// being answered" can tell which of its outstanding requests was refused and why.
    /// </param>
    public DeferredAnswers(
        string what,
        DocumentStore documents,
        ConfigurationSync configuration,
        int concurrency = DefaultConcurrency)
    {
        What = what ?? throw new ArgumentNullException(nameof(what));
        _documents = documents ?? throw new ArgumentNullException(nameof(documents));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _concurrency = new SemaphoreSlim(concurrency, concurrency);
    }

    /// <inheritdoc cref="DeferredAnswers(string, DocumentStore, ConfigurationSync, int)" path="/param[@name='what']"/>
    public string What { get; }

    /// <summary>Documents with a request of this kind still outstanding.</summary>
    /// <remarks>
    /// At most one per document, because a newer request for a document replaces the entry the last
    /// one left. The bound is therefore the number of open documents however fast anybody types --
    /// the same bound, for the same reason, as <see cref="CompileScheduler.Pending"/>.
    /// </remarks>
    public int Outstanding => _outstanding.Count;

    /// <summary>Answers that are past the gate and have not yet returned.</summary>
    public int InFlight => Volatile.Read(ref _inFlight);

    /// <summary>The most answers that have ever been past the gate at one moment.</summary>
    /// <inheritdoc cref="CompileScheduler.PeakInFlight" path="/remarks"/>
    public int PeakInFlight => Volatile.Read(ref _peakInFlight);

    /// <summary>Produces <paramref name="answer"/>, off this worker, for a buffer that is still there.</summary>
    /// <exception cref="JsonRpcException">
    /// The buffer or the configuration moved while this was being answered.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// The client withdrew the request, or a newer one of the same kind for the same document
    /// superseded it. Either way nobody is waiting on the answer, and finishing is work spent on
    /// nothing.
    /// </exception>
    public async Task<T> AnswerAsync<T>(
        DocumentRequest asked, Func<CancellationToken, T> answer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asked);
        ArgumentNullException.ThrowIfNull(answer);

        var work = Supersede(asked.Uri, cancellationToken);
        var token = work.Token;
        var acquired = false;

        try
        {
            // Inside the cleanup, because waiting is where a request spends most of its life and
            // cancelling one is the ordinary case: withdrawn by the client, superseded by the next
            // keystroke, or abandoned because the document closed. Left outside, every one of those
            // leaves its entry behind, and an entry nothing will ever retire makes this document look
            // permanently busy to anything that counts what is outstanding.
            await _concurrency.WaitAsync(token).ConfigureAwait(false);
            acquired = true;

            // Task.Run rather than trusting the await above to have yielded. A semaphore with a slot
            // free completes synchronously, and the continuation would then run the work on whichever
            // thread called this -- which is the one reading the wire, and the whole reason a
            // concurrent handler is two halves.
            return await Task.Run(() => Produce(asked, answer, token), token).ConfigureAwait(false);
        }
        finally
        {
            // Only what was taken is given back. Releasing unconditionally would hand the pool a slot
            // for a wait that never succeeded, and the limit would climb by one for every request the
            // client withdrew.
            if (acquired)
            {
                _concurrency.Release();
            }

            Retire(asked.Uri, work);
        }
    }

    /// <summary>Abandons a document's outstanding request, because it is no longer open.</summary>
    /// <remarks>
    /// The same obligation <see cref="CompileScheduler.ForgetAsync"/> already discharges for
    /// compiles, and spec 26.1 states it once for both: what is outstanding for a document is
    /// abandoned when the document closes. Without it a request queued behind a slow one still takes
    /// its turn, works on a buffer the editor has shut, and is refused at the end -- having spent one
    /// of the few slots a live request was waiting for. Refusing it later is correct and too late.
    /// </remarks>
    public void Forget(DocumentUri document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (_outstanding.TryRemove(document.Key, out var work))
        {
            Cancel(work);
        }
    }

    /// <summary>Refuses an answer about a document, or a configuration, that has moved on.</summary>
    /// <inheritdoc cref="DocumentRequest" path="/remarks/para[2]"/>
    public void Require(DocumentRequest asked)
    {
        ArgumentNullException.ThrowIfNull(asked);

        if (!ReferenceEquals(_documents.Find(asked.Uri), asked.Document))
        {
            throw Refuse(
                $"'{asked.Uri}' was edited, closed or reopened while this {What} was being "
                    + "answered, so the answer describes text that is no longer there.");
        }

        // The same question the compile scheduler asks, and for the same reason: an include path
        // removed while this ran would otherwise be answered against roots that no longer apply.
        if (_configuration.Current.Generation != asked.Configuration.Generation)
        {
            throw Refuse(
                $"The configuration for '{asked.Uri}' changed while this {What} was being "
                    + "answered, so what it read is not what now applies.");
        }
    }

    private T Produce<T>(DocumentRequest asked, Func<CancellationToken, T> answer, CancellationToken token)
    {
        // Before the work as well as after it. A request that queued behind a slow one may have been
        // waiting for a while, and a buffer that moved in the meantime has already decided the
        // answer: working to produce something that will be refused spends the slot a live request is
        // waiting for. Cancellation covers the cases that have a token -- withdrawal, supersession, a
        // close -- and an edit is the one that does not.
        Require(asked);

        Enter();

        try
        {
            var produced = answer(token);

            Require(asked);

            return produced;
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    private static JsonRpcException Refuse(string reason)
        => new(new ResponseError(ErrorCodes.ContentModified, reason + " Ask again."));

    /// <summary>
    /// Takes this document's outstanding slot, cancelling whoever had it, and links the client's own
    /// withdrawal to it.
    /// </summary>
    /// <inheritdoc cref="CompileScheduler.Supersede" path="/remarks"/>
    private CancellationTokenSource Supersede(DocumentUri document, CancellationToken cancellationToken)
    {
        var work = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _outstanding.AddOrUpdate(
            document.Key,
            work,
            (_, previous) =>
            {
                Cancel(previous);
                return work;
            });

        return work;
    }

    /// <remarks>
    /// Both halves of "remove it only if it is still mine" in one operation, because a newer request
    /// lands between a look and a remove and this would then retire that one instead -- leaving work
    /// nothing holds a handle to, which no later keystroke can supersede.
    /// </remarks>
    private void Retire(DocumentUri document, CancellationTokenSource work)
        => _outstanding.TryRemove(KeyValuePair.Create(document.Key, work));

    private static void Cancel(CancellationTokenSource work)
    {
        try
        {
            work.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <inheritdoc cref="CompileScheduler.Enter" path="/remarks"/>
    private void Enter()
    {
        var current = Interlocked.Increment(ref _inFlight);

        var peak = Volatile.Read(ref _peakInFlight);
        while (current > peak)
        {
            var seen = Interlocked.CompareExchange(ref _peakInFlight, current, peak);
            if (seen == peak)
            {
                return;
            }

            peak = seen;
        }
    }
}
