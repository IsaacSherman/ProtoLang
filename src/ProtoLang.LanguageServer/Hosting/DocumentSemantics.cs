using System.Collections.Concurrent;
using ProtoLang.Binding;
using ProtoLang.Semantics;
using ProtoLang.LanguageServer.Workspace;

namespace ProtoLang.LanguageServer.Hosting;

/// <summary>One buffer compiled under one settled configuration, and what can be asked of it.</summary>
/// <remarks>
/// <para>
/// The compilation and everything that comes with it -- the settings it ran under, the loader it
/// ended up using, and the model that answers questions about positions -- kept together, because a
/// caller holding the result alone cannot ask where protoc's own errors should be resolved against,
/// and a caller holding the settings alone cannot say whether anything was compiled at all.
/// </para>
/// <para>
/// <see cref="Result"/> is null in exactly the two cases that stop a document before it compiles: a
/// protoc that was named and could not be prepared, and a configuration file that was found and
/// refused. Both are the caller's to report; this type only says which happened.
/// </para>
/// </remarks>
public sealed record DocumentCompilation(
    OpenDocument Document,
    WorkspaceConfiguration Configuration,
    DocumentConfiguration Settings,
    DescriptorLoader? Loader,
    DescriptorLoadException? LoaderFailure,
    CompilationResult? Result)
{
    /// <summary>What answers "what is at this position" and "what is in scope here", or null when
    /// nothing was compiled.</summary>
    /// <remarks>
    /// Built here rather than by each caller, and eagerly because building it is a constructor: the
    /// work it fronts -- the position search, the reference index -- is deferred until something asks.
    /// </remarks>
    public SemanticModel? Semantics { get; } = Result is null ? null : SemanticModel.For(Result);

}

/// <summary>
/// Compiles the buffer a request was read against, and remembers the answer for as long as that
/// buffer is the one the editor is showing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a request compiles at all.</b> Import path completion never needed to: what schemas a
/// directory holds is a question for the file system. Everything else an editor asks -- what may
/// follow a dot, what names are in scope, which type a hover is over -- is a question only the binder
/// can answer, and the binder answers it about one exact text.
/// </para>
/// <para>
/// <b>Why not the scheduled compile.</b> <see cref="CompileScheduler"/> debounces, so what it last
/// produced describes the buffer as it stood before the keystroke that asked the question. Answering
/// from it would measure a caret offset against text the client has already replaced -- and the
/// keystroke that triggers completion is itself the edit that invalidates it, so the reuse would
/// almost never be correct and would be wrong silently when it was not.
/// </para>
/// <para>
/// <b>What a hit means.</b> An entry answers only when it was built for this very
/// <see cref="OpenDocument"/> instance and under a configuration of the same generation -- the pair
/// <see cref="CompletionProvider"/> already checks before it will publish an answer. So a hit is by
/// construction an answer that cannot then be refused as stale, rather than one that has to be
/// re-validated after it comes back. Identity rather than a version number because a document closed
/// and reopened starts again at version one, and the store hands out a fresh instance for every edit
/// and every open.
/// </para>
/// <para>
/// <b>Those two are what the editor moved, and they are not everything that can move.</b> A
/// compilation is a function of three things and the editor owns only one of them: the buffer, the
/// configuration the buffer resolves to, and the schemas that configuration reaches. The other two
/// live in files, and a file changes without any keystroke -- a branch switched underneath the
/// session, an imported <c>.proto</c> edited in another window, a <c>protolang.config.xml</c>
/// repaired after it was refused. So the pair above is checked and then two more questions are asked,
/// each of them put to the thing that already owns the answer:
/// <see cref="DocumentConfiguration.CompilesTheSameWayAs"/> for the settings, and
/// <see cref="SchemaClosure.IsCurrent"/> -- the very check <see cref="DescriptorCache"/> makes before
/// it will answer from an entry -- for the schemas. Neither is restated here, because a second
/// statement of either is one that eventually disagrees with the compiler.
/// </para>
/// <para>
/// <b>What that costs on a hit is one configuration resolution and one hash per schema.</b> The
/// resolution is a directory walk and an XML parse; the hashing is a handful of small files. Both are
/// far below the lex, parse and bind a miss costs, and the alternative is not cheaper -- it is
/// answering a completion with fields the schema no longer has, which is the failure that gets an
/// editor integration switched off.
/// </para>
/// <para>
/// <b>One entry per document, and no eviction policy.</b> What is held is the lex, parse and bind of
/// one exact buffer, worthless the moment that buffer moves; and every question is asked about the
/// document the store is currently holding, so a previous buffer's entry can never be asked for
/// again. Keeping it would be keeping an answer nothing can request. The expensive half is already
/// cached where it belongs -- <see cref="DescriptorCache"/> holds the descriptors, which is why a
/// compile whose schemas have not changed never reaches protoc -- so what a miss costs is a lex, a
/// parse and a bind. The bound is the number of open documents, the same bound and the same argument
/// as <see cref="CompileScheduler"/>'s queue and <see cref="CompletionProvider.Outstanding"/>.
/// </para>
/// <para>
/// <b>Two questions about one buffer may both compile.</b> Nothing is held while a compile runs, so
/// a hover and a completion arriving together can each build one and the last to finish wins. That
/// costs one lex, parse and bind, because both of them wait on the same descriptor load inside the
/// cache. Serializing them behind a lock would make every reader wait on a compile it may not have
/// needed, to save work that is only ever duplicated when two questions arrive within milliseconds of
/// each other. <see cref="OpenDocument.Lines"/> settles the identical question the identical way.
/// </para>
/// </remarks>
public sealed class DocumentSemantics
{
    private readonly LoaderPool _loaders;
    private readonly ConcurrentDictionary<string, DocumentCompilation> _entries = new();

    /// <summary>Held across publishing an entry and withdrawing one, so the two cannot interleave.</summary>
    private readonly object _publication = new();

    private int _compilations;
    private int _withdrawals;

    public DocumentSemantics(LoaderPool loaders)
        => _loaders = loaders ?? throw new ArgumentNullException(nameof(loaders));

    /// <summary>How the buffer is compiled, so a test can watch or delay the one step that is slow.</summary>
    /// <remarks>
    /// The seam <see cref="CompletionProvider.Enumerate"/> is for the file-system walk. A schema
    /// question leaves the process the same way -- through protoc, on a cold descriptor cache -- and
    /// a test that has to disturb a buffer while it is being compiled has nowhere else to stand.
    /// </remarks>
    public Func<Compilation, CancellationToken, CompilationResult> Compile { get; set; }
        = static (compilation, cancellationToken) => compilation.Compile(cancellationToken);

    /// <summary>Compilations that actually ran, as opposed to being answered from an entry.</summary>
    /// <remarks>
    /// Published for the reason <see cref="CompileScheduler.Compilations"/> is: without it, every
    /// claim that a buffer is compiled once however many questions are asked of it is an argument
    /// rather than a measurement, and a cache that silently stopped answering would look exactly like
    /// one that was working.
    /// </remarks>
    public int Compilations => Volatile.Read(ref _compilations);

    /// <summary>Documents an answer is currently held for.</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// The compilation of this exact buffer under the configuration now in force, compiling it if
    /// what is held is about some other text.
    /// </summary>
    /// <remarks>
    /// The configuration is passed in rather than read from the sync, so the compile runs under the
    /// same generation the caller captured and will later check its answer against. Read here, a
    /// configuration that changed between the caller's capture and this call would produce a
    /// compilation the caller then discards as stale although nothing was wrong with it.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> or
    /// <paramref name="configuration"/> is null.</exception>
    public DocumentCompilation For(
        OpenDocument document, WorkspaceConfiguration configuration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(configuration);

        // Once, here, rather than on the miss alone: it is both half of what makes an entry answer
        // and the first thing building one needs, and resolving it twice would be paying for the
        // walk twice in exchange for two chances to disagree about what it said.
        var settings = configuration.Resolve(document.Uri);

        if (_entries.TryGetValue(document.Uri.Key, out var held) && Answers(held, document, settings))
        {
            return held;
        }

        // Read before the compile rather than after it, because what this is watching for is a
        // withdrawal that lands while the compile runs.
        var withdrawals = Volatile.Read(ref _withdrawals);
        var built = Build(document, configuration, settings, cancellationToken);

        Publish(document.Uri, built, withdrawals);

        return built;
    }

    /// <summary>Discards what is held for a document, because it is no longer open.</summary>
    /// <remarks>
    /// The obligation spec 26.1 states and <see cref="CompileScheduler.ForgetAsync"/> and
    /// <see cref="CompletionProvider.Forget"/> already discharge. Without it an entry for a closed
    /// buffer is held until the server exits -- a whole syntax tree and IR module for a document
    /// nothing can ask about, since every question comes through the store.
    /// </remarks>
    public void Forget(DocumentUri document)
    {
        ArgumentNullException.ThrowIfNull(document);

        lock (_publication)
        {
            _withdrawals++;
            _entries.TryRemove(document.Key, out _);
        }
    }

    /// <summary>Keeps a compilation, unless a document was withdrawn while it was being produced.</summary>
    /// <remarks>
    /// <para>
    /// A compile takes long enough for a close to land inside it, and it did: a document closed while
    /// its own compilation was in flight was forgotten and then put straight back by the assignment
    /// that finished afterwards, leaving the whole syntax tree and IR module of a buffer the editor
    /// had shut. Removing an entry and publishing one are therefore one decision under one lock, and
    /// the counter is what a publisher compares itself against -- a remove that has already happened
    /// is seen as a higher count, and one that has not cannot slip in between the comparison and the
    /// assignment.
    /// </para>
    /// <para>
    /// <b>One counter for every document rather than one apiece, and the coarseness is the point.</b>
    /// What it costs is that closing one document declines to cache a compilation of another that was
    /// in flight at that instant -- the caller still gets its answer, and the next question rebuilds.
    /// A close is a person's deliberate action and a compile is milliseconds, so that is a cache miss
    /// somewhere between rarely and never, bought with a single field. Per-document stamps would buy
    /// nothing back and would outlive the entries they guard, since a stamp is only safe to drop once
    /// nothing is building against it.
    /// </para>
    /// <para>
    /// Assigned rather than added conditionally, because the entry it replaces is about a buffer the
    /// store no longer holds and nothing will ask for again. A race between two compiles of the same
    /// buffer leaves whichever finished last, and they are equal.
    /// </para>
    /// </remarks>
    private void Publish(DocumentUri document, DocumentCompilation built, int withdrawals)
    {
        lock (_publication)
        {
            if (_withdrawals == withdrawals)
            {
                _entries[document.Key] = built;
            }
        }
    }

    /// <summary>Whether what is held is about this buffer, and still describes what is on disk.</summary>
    /// <remarks>
    /// The generation is compared as well as the settings it produced, although the settings are the
    /// stronger question. It is the caller's contract rather than this one's: a host refuses an answer
    /// whose generation is not the current one, so an entry stamped with an older generation would be
    /// handed back only to be thrown away.
    /// </remarks>
    private static bool Answers(
        DocumentCompilation held, OpenDocument document, DocumentConfiguration settings)
        => ReferenceEquals(held.Document, document)
            && held.Settings.Generation == settings.Generation
            && held.Settings.CompilesTheSameWayAs(settings)
            && SchemasAreUnchanged(held);

    /// <summary>Whether the schemas this compilation rests on still stand as it read them.</summary>
    /// <remarks>
    /// <para>
    /// The check <see cref="DescriptorCache"/> makes on its own entries, asked one level up so that a
    /// document cache cannot answer from a compilation whose descriptors the loader would already
    /// have refused -- and asked against the same roots, which is the part that has to be said out
    /// loud. A compilation publishes the caller's include paths and not the implicit ones the loader
    /// puts behind them, while the closure it is compared against holds whatever those resolved: one
    /// import of <c>google/protobuf/timestamp.proto</c> is enough for the re-description to find no
    /// file where a file was recorded, and then every entry is stale and nothing is ever reused.
    /// <see cref="DescriptorRequest.RootsFor"/> is where that order lives.
    /// </para>
    /// <para>
    /// <b>A load that failed is not reused at all, which is the layer below's policy rather than a
    /// new one.</b> A missing import or a malformed <c>.proto</c> leaves no bundle and therefore no
    /// closure, and there is no honest way to describe what such a compilation depended on: protoc
    /// blames a use rather than a declaration, so the file the author will edit to fix it is
    /// routinely one protoc never named and nothing here can name either. Reconstructing the
    /// dependency graph means reading <c>import</c> declarations out of schema text -- which is this
    /// process holding a second opinion about protobuf's grammar, and a wrong one, since protoc
    /// accepts spellings a scan will miss and any bound on the walk turns a missed dependency into a
    /// permanent refusal. Spec 21.1 already settles the question one layer down: a load that failed
    /// is not cached at all. Following it here costs a protoc run per question while the workspace is
    /// broken, and buys the guarantee that fixing a schema is seen however the fix was made.
    /// </para>
    /// <para>
    /// It costs very little else, because such a compilation is nearly empty: no descriptors means no
    /// module, no types and no scope, so what is being declined is the reuse of an answer that had
    /// almost nothing in it.
    /// </para>
    /// </remarks>
    private static bool SchemasAreUnchanged(DocumentCompilation held)
        => held.Result is not { } result
            || (result.Schema is { } schema
                && SchemaClosure.IsCurrent(schema.Closure, RootsOf(result, held.Loader)));

    /// <summary>Every root a schema name resolved against for this compilation, in priority order.</summary>
    /// <remarks>
    /// Recomputed at each question rather than stored beside the description, because both halves are
    /// fixed properties of a compilation that has already run: the include paths it was given and the
    /// implicit ones belonging to the loader that ran it. Neither can drift between the answer being
    /// built and the answer being checked, and a stored copy would be a third statement of a list that
    /// already has one home.
    /// </remarks>
    private static IReadOnlyList<string> RootsOf(CompilationResult result, DescriptorLoader? loader)
        => DescriptorRequest.RootsFor(result.SearchPaths, loader?.ImplicitIncludePaths ?? []);

    private DocumentCompilation Build(
        OpenDocument document,
        WorkspaceConfiguration configuration,
        DocumentConfiguration settings,
        CancellationToken cancellationToken)
    {
        _loaders.TryGet(settings.ProtocPath, out var loader, out var failure);

        // The two ways a document is stopped before it compiles, in the order the settings settle
        // them: a protoc that was named and cannot be built into a loader, and a configuration file
        // that was found and refused. Which of the two happened is read back off this record rather
        // than decided again, so the diagnostic each one owns is reported in one place.
        if (failure is not null && settings.ProtocPath is not null)
        {
            return new DocumentCompilation(document, configuration, settings, loader, failure, null);
        }

        if (!settings.TryCreateCompilationOptions(loader, out var options))
        {
            return new DocumentCompilation(document, configuration, settings, loader, failure, null);
        }

        var compilation = new Compilation(document.ToSource(settings.Folder?.Path), options!);

        Interlocked.Increment(ref _compilations);

        var result = Compile(compilation, cancellationToken);

        // The loader the compilation settled on rather than the one the pool handed over. They are
        // usually the same object and are not always: a compilation with no protoc named builds its
        // own, and the roots protoc's messages are resolved against have to come from the one that
        // actually ran.
        return new DocumentCompilation(
            document, configuration, settings, compilation.Loader ?? loader, failure, result);
    }
}
