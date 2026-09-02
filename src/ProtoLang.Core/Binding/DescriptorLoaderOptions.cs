namespace ProtoLang.Binding;

/// <summary>Everything a <see cref="DescriptorLoader"/> needs that is not the path to protoc.</summary>
/// <remarks>
/// Init-only members rather than constructor parameters, for the reason
/// <see cref="CompilationOptions"/> states out loud: this is where knobs land, and each one added to
/// a signature is another round of call sites to update. The existing one-argument constructor keeps
/// working and keeps meaning what it always did -- no cache, and a budget rather than an unbounded
/// wait.
/// </remarks>
public sealed record DescriptorLoaderOptions
{
    /// <summary>
    /// How long protoc may run before it is stopped and the load reported as a failure.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Generous, because it bounds a cold run over a large closure on a machine that has not read
    /// those files before, and a budget that fires on slow-but-working input turns a delay into an
    /// error. It is a backstop, not a latency target: #57 pins the numbers a language server is held
    /// to, and #54 owns supervision properly -- cancellation, cleanup of abandoned runs, and bounded
    /// concurrency.
    /// </para>
    /// <para>
    /// There is deliberately no way to say "wait forever". A compiler an editor calls on every
    /// keystroke cannot have a mode in which one bad invocation stops it answering, and offering the
    /// option would mean the failure exists in the field even if nothing in this repository selects
    /// it.
    /// </para>
    /// </remarks>
    public static TimeSpan DefaultTimeout => TimeSpan.FromSeconds(30);

    /// <summary>
    /// Where loads are kept, or null to run protoc every time.
    /// </summary>
    /// <remarks>
    /// Null by default, so a loader built the way the CLI builds one behaves exactly as it always
    /// has. Caching is worth nothing to a process that compiles once and exits, and it is worth a
    /// great deal to one that compiles on every keystroke; the caller knows which it is.
    /// </remarks>
    public DescriptorCache? Cache { get; init; }

    /// <inheritdoc cref="DefaultTimeout"/>
    public TimeSpan Timeout { get; init; } = DefaultTimeout;
}
