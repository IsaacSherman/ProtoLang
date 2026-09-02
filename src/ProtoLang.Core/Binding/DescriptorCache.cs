namespace ProtoLang.Binding;

/// <summary>What a cache has done since it was created.</summary>
/// <remarks>
/// Published because the acceptance criterion for caching is "this compilation did not invoke
/// protoc", and a property nobody can observe is a property nobody can test. #58 reports these in the
/// language server's status command, where the question is the same one asked from a support request
/// instead of from a test.
/// </remarks>
/// <param name="Hits">Loads answered from an entry that was still valid.</param>
/// <param name="Misses">Loads that had to run protoc.</param>
/// <param name="Invalidations">Entries found and then dropped because their files had changed.</param>
/// <param name="Evictions">Entries dropped to stay within capacity.</param>
public readonly record struct DescriptorCacheStatistics(int Hits, int Misses, int Invalidations, int Evictions);

/// <summary>
/// Keeps descriptor loads, so that a second compilation over unchanged schemas does not shell out to
/// protoc again.
/// </summary>
/// <remarks>
/// <para>
/// Caller-owned and never process-global. A test, the CLI and a language server each hold their own,
/// so a test starts from empty by constructing one and no test can be reached by another's entries.
/// Handed to a <see cref="DescriptorLoader"/> through <see cref="DescriptorLoaderOptions"/>; a loader
/// without one behaves exactly as the loader always has.
/// </para>
/// <para>
/// A hit is not merely a matching request. The request covers the arguments -- which protoc, which
/// roots in which order, which files -- and the bundle's <see cref="DescriptorBundle.Closure"/>
/// covers the files themselves, which cannot be part of a request because until protoc has run nobody
/// knows what the closure contains. So a lookup matches the request, then re-checks the closure, and
/// treats a changed closure as a miss. That is what makes a change to a transitively imported schema
/// invalidate an entry that never named it.
/// </para>
/// <para>
/// Single-flight, through a <see cref="Lazy{T}"/> inserted under the lock and forced outside it. Two
/// compilations racing to populate one entry is the normal case in an editor -- a keystroke arrives
/// while the previous one is still loading -- and running protoc twice for it would waste exactly the
/// work this type exists to avoid. Holding the lock across the load instead would serialize every
/// unrelated load behind it.
/// </para>
/// </remarks>
public sealed class DescriptorCache
{
    /// <remarks>
    /// Enough for the schemas of a handful of files open at once. #57 pins the real number against
    /// measured latency and memory; a descriptor set carrying source info is not small, and the point
    /// of a bound is that a session lasting all day does not grow without limit.
    /// </remarks>
    public const int DefaultCapacity = 16;

    /// <remarks>
    /// A found entry that fails its closure check is dropped and the load retried, which normally
    /// settles on the second pass: the retry finds nothing, runs protoc, and returns without
    /// re-checking, because a closure described from the files protoc has just read is current by
    /// construction. More passes than that mean another thread keeps inserting entries that are stale
    /// by the time this one looks, which is a file being rewritten continuously. Bounding the attempts
    /// and falling back to an uncached load keeps that case slow rather than unbounded.
    /// </remarks>
    private const int MaxAttempts = 2;

    private readonly object _gate = new();
    private readonly Dictionary<DescriptorRequest, LinkedListNode<Entry>> _entries = [];

    /// <summary>Least recently used at the front, so eviction takes <c>First</c>.</summary>
    private readonly LinkedList<Entry> _order = new();

    private int _hits;
    private int _misses;
    private int _invalidations;
    private int _evictions;

    public DescriptorCache(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        Capacity = capacity;
    }

    /// <summary>The most entries this cache will hold.</summary>
    public int Capacity { get; }

    /// <summary>How many entries it holds now.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <inheritdoc cref="DescriptorCacheStatistics"/>
    public DescriptorCacheStatistics Statistics => new(
        Volatile.Read(ref _hits),
        Volatile.Read(ref _misses),
        Volatile.Read(ref _invalidations),
        Volatile.Read(ref _evictions));

    /// <summary>
    /// The bundle for <paramref name="request"/>, from the cache when one is there and still valid, and
    /// from <paramref name="load"/> otherwise.
    /// </summary>
    /// <remarks>
    /// A load that throws leaves nothing behind. Caching a failure would mean that fixing the broken
    /// <c>.proto</c> and compiling again produced the same error, from an entry, without protoc ever
    /// being asked to look at the corrected file -- which is the single most confusing thing a cache
    /// can do to someone who has just fixed their mistake.
    /// </remarks>
    public DescriptorBundle GetOrLoad(DescriptorRequest request, Func<DescriptorBundle> load)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(load);

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var node = Rent(request, load, out var wasPresent);

            DescriptorBundle bundle;
            try
            {
                bundle = node.Value.Bundle.Value;
            }
            catch
            {
                Drop(node);
                throw;
            }

            if (!wasPresent)
            {
                Interlocked.Increment(ref _misses);
                return bundle;
            }

            if (SchemaClosure.IsCurrent(bundle.Closure, request.SearchRoots))
            {
                Interlocked.Increment(ref _hits);
                return bundle;
            }

            Interlocked.Increment(ref _invalidations);
            Drop(node);
        }

        Interlocked.Increment(ref _misses);
        return load();
    }

    private LinkedListNode<Entry> Rent(DescriptorRequest request, Func<DescriptorBundle> load, out bool wasPresent)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(request, out var existing))
            {
                _order.Remove(existing);
                _order.AddLast(existing);
                wasPresent = true;
                return existing;
            }

            var created = _order.AddLast(
                new Entry(request, new Lazy<DescriptorBundle>(load, LazyThreadSafetyMode.ExecutionAndPublication)));

            _entries[request] = created;
            Evict();

            wasPresent = false;
            return created;
        }
    }

    /// <remarks>
    /// The node identifies the entry, not the request: by the time a stale or failed load is dropped,
    /// another thread may already have replaced that request with a fresh entry, and removing by
    /// request alone would throw away the good one -- leaving the next lookup to redo work that had
    /// just been done correctly.
    /// </remarks>
    private void Drop(LinkedListNode<Entry> node)
    {
        lock (_gate)
        {
            if (node.List is null)
            {
                return;
            }

            if (_entries.TryGetValue(node.Value.Request, out var current) && ReferenceEquals(current, node))
            {
                _entries.Remove(node.Value.Request);
            }

            _order.Remove(node);
        }
    }

    /// <summary>Trims to <see cref="Capacity"/>, oldest first, skipping loads still in flight.</summary>
    /// <remarks>
    /// <para>
    /// An entry whose load has not finished is not a candidate. Evicting one would undo the
    /// single-flight guarantee at exactly the moment it matters: the next caller for that same
    /// request finds nothing, and starts a second protoc over the schemas the first is still
    /// compiling. That would make "two compilations racing populate one entry once" conditional on
    /// no unrelated load arriving in between -- which, in an editor holding a small cache and several
    /// open files, is not a rare accident but the normal traffic.
    /// </para>
    /// <para>
    /// The consequence is that a cache with every entry in flight sits briefly over capacity rather
    /// than evicting work it is waiting on. That is the right way round: the bound exists to stop a
    /// day-long session growing without limit, and it is restored by the next insertion after those
    /// loads land. Nothing stays in flight indefinitely, because protoc runs under a budget and a
    /// load that throws is dropped.
    /// </para>
    /// </remarks>
    private void Evict()
    {
        var node = _order.First;

        while (_entries.Count > Capacity && node is not null)
        {
            var next = node.Next;

            if (node.Value.Bundle.IsValueCreated)
            {
                _entries.Remove(node.Value.Request);
                _order.Remove(node);
                Interlocked.Increment(ref _evictions);
            }

            node = next;
        }
    }

    private sealed record Entry(DescriptorRequest Request, Lazy<DescriptorBundle> Bundle);
}
