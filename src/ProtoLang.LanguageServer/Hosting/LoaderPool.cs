using System.Collections.Concurrent;
using ProtoLang.Binding;
using ProtoLang.LanguageServer.Protocol;

namespace ProtoLang.LanguageServer.Hosting;

/// <summary>
/// The descriptor loaders this server compiles through, and the one cache they all share.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a pool at all.</b> <c>CompilationOptions</c> has no protoc of its own: which executable runs
/// can only reach a compilation through its loader, and a loader is also where the descriptor cache
/// lives. A server that passed no loader would get a fresh, uncached one built inside every
/// compilation -- which is #48 undone for the common case, since the common case is a user who never
/// set <c>protolang.protocPath</c> at all. So a loader is always supplied, and it always carries
/// <see cref="Cache"/>.
/// </para>
/// <para>
/// <b>Successes are kept and failures are not.</b> Building a loader locates protoc and then stats
/// the directories beside it looking for the well-known schemas, which is work worth doing once. A
/// failure is a different thing: it means protoc is not installed yet, and remembering that would mean
/// installing protoc had no effect until the editor was restarted. Retrying costs a <c>PATH</c> and
/// package-cache probe on each compile of a workspace that cannot compile anyway, and buys a server
/// that heals by itself.
/// </para>
/// </remarks>
public sealed class LoaderPool(ServerLog log)
{
    /// <summary>The key a loader with no stated protoc is filed under.</summary>
    private const string Located = "<located>";

    private readonly ConcurrentDictionary<string, DescriptorLoader> _loaders = new(StringComparer.Ordinal);

    private int _reportedMissing;

    /// <summary>Told once, the first time no protoc can be found at all.</summary>
    /// <remarks>
    /// Once, because the retry policy above means this is discovered again on every compile, and a
    /// notification per keystroke is not a notification. The message is
    /// <c>ProtocLocator</c>'s, which names everywhere it looked -- the thing a user actually needs in
    /// order to fix it.
    /// </remarks>
    public Action<string>? OnProtocMissing { get; init; }

    /// <summary>
    /// One cache for the whole server, so two documents importing one schema load it once.
    /// </summary>
    /// <remarks>
    /// Shared across loaders as well as across documents. Its keys already name the protoc that ran,
    /// so entries built under one executable are simply never matched by another -- there is nothing
    /// to partition by hand.
    /// </remarks>
    public DescriptorCache Cache { get; } = new();

    /// <summary>
    /// The loader for <paramref name="protocPath"/>, or the reason there cannot be one.
    /// </summary>
    /// <param name="protocPath">
    /// The protoc a setting or the environment named, or null to let <c>ProtocLocator</c> find one.
    /// </param>
    public bool TryGet(string? protocPath, out DescriptorLoader? loader, out DescriptorLoadException? failure)
    {
        failure = null;

        var key = protocPath is null ? Located : PathIdentity.KeyFor(protocPath);

        if (_loaders.TryGetValue(key, out loader))
        {
            return true;
        }

        try
        {
            var options = new DescriptorLoaderOptions { Cache = Cache };

            loader = protocPath is null
                ? DescriptorLoader.CreateDefault(options)
                : new DescriptorLoader(protocPath, options);

            log.Info($"Compiling schemas with '{loader.ProtocPath}'.");
        }
        catch (DescriptorLoadException ex)
        {
            loader = null;
            failure = ex;

            if (Interlocked.Exchange(ref _reportedMissing, 1) == 0)
            {
                log.Warning(ex.Message);
                OnProtocMissing?.Invoke(ex.Message);
            }
            else
            {
                log.Trace(ex.Message);
            }

            return false;
        }

        // Whichever instance wins the race is the one everybody uses, so two documents opened at once
        // cannot end up with a cache each.
        loader = _loaders.GetOrAdd(key, loader);

        return true;
    }
}
