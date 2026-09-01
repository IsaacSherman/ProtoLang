using Google.Protobuf.Reflection;
using ProtoLang.Binding;
using ProtoLang.Config;
using ProtoLang.Diagnostics;
using ProtoLang.Ir;
using ProtoLang.Syntax;

namespace ProtoLang;

/// <param name="Module">
/// The typed IR, covering as much of the source as could be bound. Non-null whenever binding ran at
/// all, which now includes sources that failed to parse -- the point of doing so is that a buffer
/// mid-edit still has types for the parts that are finished. Null only when the compilation stopped
/// before the binder: a configuration file that could not be read, an unusable include path, or a
/// protobuf schema that could not be found or loaded. Never treat this as a whole program: ask
/// <see cref="CompilationResult.EmittableModule"/> for the one that may be written from.
/// </param>
/// <param name="SyntaxTree">
/// The syntax tree, present whenever the source was parsed at all, error-recovered and complete
/// enough to walk. Null on the same three stops that leave <paramref name="Module"/> null.
/// </param>
/// <param name="Descriptors">
/// Every protobuf file backing this compilation, including transitively imported ones, in
/// dependency order. protoc is asked for <c>--include_imports</c>, so this is the whole closure and
/// not only the schemas the ProtoLang source named. Empty when binding did not get that far.
/// </param>
/// <param name="Config">
/// The policy this compilation ran under, whether discovered, supplied, or defaulted. Callers that
/// report what was generated need it, because the same source produces different code under a
/// different policy and a build log that does not say which one is not reproducible.
/// </param>
/// <param name="SearchPaths">
/// The directories imports were resolved against, in the order they were searched. Carried out of
/// the compilation so that a caller which has to say where a schema came from uses the list the
/// compiler actually used, rather than recomputing it from inputs it hopes were the same ones. A
/// buffer with no path has no source-directory fallback to recompute from, so for those callers
/// this is not a convenience but the only correct answer. Empty when the compilation stopped before
/// they were settled.
/// </param>
/// <param name="Imports">
/// Every <c>import proto</c> declaration and what became of it, in the order they were written.
/// Empty when the compilation stopped before the imports were looked at. See
/// <see cref="ImportResolution"/> for why this is an object rather than a count of failures.
/// </param>
public sealed record CompilationResult(
    IrModule? Module,
    CompilationUnit? SyntaxTree,
    IReadOnlyList<FileDescriptor> Descriptors,
    DiagnosticBag Diagnostics,
    ProjectConfig Config,
    IReadOnlyList<string> SearchPaths,
    IReadOnlyList<ImportResolution> Imports)
{
    /// <summary>Whether this compilation produced a whole program.</summary>
    /// <remarks>
    /// One question with one job: <b>may artifacts be written from this?</b> It is not a question
    /// about whether the compiler got anything done, and nothing inside the pipeline may use it to
    /// decide whether to keep going -- stopping at the first error is how the second one stays
    /// hidden until the first is fixed. Prefer <see cref="EmittableModule"/>, which states the rule
    /// rather than leaving each caller to remember it.
    /// </remarks>
    public bool Success => Module is not null && !Diagnostics.HasErrors;

    /// <summary>
    /// The module an emitter may write from, or null when this compilation must produce nothing.
    /// </summary>
    /// <remarks>
    /// The whole of what <see cref="Success"/> governs, said in the type instead of in a comment
    /// every caller has to have read. <see cref="Module"/> is the partial one -- present whenever
    /// binding ran at all, which includes files that did not parse, because that is precisely what
    /// an editor came for. Emission is the one thing that must never see it, and a null check here
    /// cannot be forgotten the way an ordering convention can.
    /// </remarks>
    public IrModule? EmittableModule => Success ? Module : null;
}

/// <summary>Everything a compilation needs that is not source text.</summary>
/// <remarks>
/// Init-only members rather than a constructor parameter list, because this is where new knobs land
/// -- a descriptor cache, an include path the caller settled some other way -- and each one added to
/// a method signature is another round of call sites to update and another positional argument to
/// transpose. Binding through parse errors was expected to land here and did not: it is what the
/// pipeline does now, for everyone, because a second mode is a second thing to keep correct and the
/// tolerant one is the one that must never crash.
/// </remarks>
public sealed record CompilationOptions
{
    /// <summary>
    /// Directories searched for the .proto files named in <c>import proto</c> declarations. Each
    /// source's own directory is searched after these; see <see cref="Compilation.SearchPaths"/>.
    /// </summary>
    public IReadOnlyList<string> IncludePaths { get; init; } = [];

    /// <summary>Descriptor loader. Null means a protoc-backed one is built on demand.</summary>
    public DescriptorLoader? Loader { get; init; }

    /// <summary>
    /// The project's language policy (spec 10.4). Null means <c>protolang.config.xml</c> is
    /// discovered from the sources' directory, and <see cref="ProjectConfig.Default"/> applies when
    /// nothing -- or nowhere -- is found.
    /// </summary>
    public ProjectConfig? Config { get; init; }
}

/// <summary>
/// Drives the pipeline described in spec 22.1: source, lexer/parser, descriptor binding, name
/// resolution, type checking, typed IR. Backends run separately, over the IR this produces.
/// </summary>
/// <remarks>
/// An object rather than a bare function, for two reasons. It holds a set of sources, so growing to
/// several files is a change to how the set is filled rather than to every signature that reaches
/// the pipeline. And it outlives a single run: an editor recompiles the same buffer many times a
/// minute, and the text, the settled search paths, and later a descriptor cache are all things
/// worth keeping between those runs rather than rebuilding on each.
/// </remarks>
public sealed class Compilation
{
    /// <summary>Creates a compilation over one source document.</summary>
    public Compilation(SourceDocument source, CompilationOptions options)
        : this([source], options)
    {
    }

    /// <remarks>
    /// Private for now. The object holds a set because a set is what it is going to be, and every
    /// derived answer below already reads the whole set. What is missing is a binder that can bind a
    /// second unit, and a public door onto a capability that is not written yet is worse than no
    /// door: it promises something the compiler would then have to refuse at run time.
    /// </remarks>
    private readonly IReadOnlyList<UnusableIncludePath> _unusableIncludePaths;

    private Compilation(IReadOnlyList<SourceDocument> sources, CompilationOptions options)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);

        if (sources.Count == 0)
        {
            throw new ArgumentException("A compilation needs at least one source.", nameof(sources));
        }

        Sources = [.. sources];
        Options = options;
        SearchPaths = BuildSearchPaths(
            Sources.Select(source => source.Identity),
            options.IncludePaths,
            out _unusableIncludePaths);
    }

    /// <summary>An include path the caller named that the file system could not make sense of.</summary>
    /// <remarks>
    /// Collected rather than thrown. Normalizing an include path is not something a compilation may
    /// fail at before it has said anything about the source it was given: a buffer full of syntax
    /// errors and a mistyped include directory are two independent problems, and the editor needs
    /// the first one reported whatever the state of the second.
    /// </remarks>
    private sealed record UnusableIncludePath(string Path, string Reason);

    public IReadOnlyList<SourceDocument> Sources { get; }

    public CompilationOptions Options { get; }

    /// <summary>
    /// The directories an <c>import proto</c> path is resolved against, in order: the caller's
    /// include paths, then the directory each source belongs to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Published because callers that need to say where a schema came from -- test project
    /// scaffolding has to tell a build system the proto root -- must resolve imports the same way
    /// the compiler did. Reimplementing the fallback rule outside this file is how the two drift
    /// apart.
    /// </para>
    /// <para>
    /// Deliberately excludes the well-known schemas protoc resolves on its own, even though imports
    /// are checked against those too. The question this answers is where the user's schemas live,
    /// and scaffolding turns the answer into proto roots in a build file -- which must never come
    /// out pointing into a NuGet cache.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> SearchPaths { get; }

    /// <summary>
    /// The directory whose <c>protolang.config.xml</c> settles policy: the first source that has a
    /// directory at all, and null when none of them does.
    /// </summary>
    /// <remarks>
    /// With one source this is that source's directory, which is what it has always been. With
    /// several it is a placeholder for a decision multi-file compilation has to make properly --
    /// whether policy should come from a project root instead. First-with-a-directory is picked here
    /// because it is the only rule that also handles the mixed case an editor produces: one saved
    /// file and one buffer that has never been written.
    /// </remarks>
    private string? ConfigDirectory
        => Sources
            .Select(source => source.Identity.Directory)
            .FirstOrDefault(directory => directory is not null);

    /// <summary>Compiles the sources to typed IR.</summary>
    public CompilationResult Compile() => Compile(new DiagnosticBag());

    /// <summary>
    /// Compiles a single ProtoLang file to typed IR, reading it from disk.
    /// </summary>
    /// <param name="sourcePath">Path to the .protolang file.</param>
    /// <param name="includePaths">
    /// Directories searched for the .proto files named in <c>import proto</c> declarations. The
    /// directory containing the source file is always searched as a fallback.
    /// </param>
    /// <param name="loader">Descriptor loader; defaults to a protoc-backed one.</param>
    /// <param name="config">
    /// The project's language policy (spec 10.4). When null, <c>protolang.config.xml</c> is
    /// searched for in the source's directory and every directory above it; when nothing is found,
    /// <see cref="ProjectConfig.Default"/> applies.
    /// </param>
    public static CompilationResult Compile(
        string sourcePath,
        IReadOnlyList<string> includePaths,
        DescriptorLoader? loader = null,
        ProjectConfig? config = null)
    {
        var identity = SourceIdentity.FromPath(sourcePath);
        var diagnostics = new DiagnosticBag();

        // Policy is settled before the source is read, exactly as it always has been. A project
        // whose protolang.config.xml is broken is told so even when the file it names cannot be
        // read; reading first would replace that diagnostic with an IOException. It is the one
        // ordering this route cannot inherit from the object, because the object is handed text that
        // has already been read.
        var settled = config ?? ResolveConfig(identity.Directory, diagnostics);
        if (settled is null)
        {
            // Search paths are left empty rather than computed: getting here means the compilation
            // never started, and building them could itself throw on a malformed include path,
            // replacing the config diagnostic this route exists to deliver.
            return new CompilationResult(null, null, [], diagnostics, ProjectConfig.Default, [], []);
        }

        return new Compilation(
                SourceDocument.ReadFrom(identity),
                new CompilationOptions
                {
                    IncludePaths = includePaths,
                    Loader = loader,
                    Config = settled,
                })
            .Compile(diagnostics);
    }

    /// <summary>
    /// Compiles source text the caller already holds, without reading it from disk.
    /// </summary>
    /// <remarks>
    /// The convenience form of <see cref="Compilation(SourceDocument, CompilationOptions)"/>,
    /// mirroring the path-based overload argument for argument so that a caller moving from one to
    /// the other changes only what it passes first. Callers that recompile the same buffer should
    /// hold the object instead.
    /// </remarks>
    public static CompilationResult Compile(
        SourceDocument source,
        IReadOnlyList<string> includePaths,
        DescriptorLoader? loader = null,
        ProjectConfig? config = null)
        => new Compilation(
                source,
                new CompilationOptions
                {
                    IncludePaths = includePaths,
                    Loader = loader,
                    Config = config,
                })
            .Compile();

    /// <summary>
    /// Settles the policy a compilation runs under: the nearest <c>protolang.config.xml</c> at or
    /// above <paramref name="startDirectory"/>, or <see cref="ProjectConfig.Default"/> when there is
    /// none -- or when there is no directory to search at all, which is what a buffer that has never
    /// been saved has.
    /// </summary>
    /// <returns>
    /// Null when a configuration file was found and could not be read; the reason is in
    /// <paramref name="diagnostics"/>. A caller must stop rather than fall back to the defaults,
    /// because a project that states a policy and is then silently ignored is worse off than one
    /// that states nothing.
    /// </returns>
    public static ProjectConfig? ResolveConfig(string? startDirectory, DiagnosticBag diagnostics)
    {
        if (string.IsNullOrEmpty(startDirectory))
        {
            return ProjectConfig.Default;
        }

        var discovered = ProjectConfig.Discover(startDirectory);
        return discovered is null ? ProjectConfig.Default : ProjectConfig.Load(discovered, diagnostics);
    }

    /// <inheritdoc cref="SearchPaths"/>
    /// <remarks>
    /// An include path that cannot be normalized is skipped, the same as it is skipped in a
    /// compilation -- which will have reported <c>PL0082</c> against it already. Throwing here would
    /// only turn a diagnosed problem into a crash in the caller that came along afterwards to ask
    /// where the schemas were.
    /// </remarks>
    public static IReadOnlyList<string> GetSearchPaths(string sourcePath, IReadOnlyList<string> includePaths)
        => BuildSearchPaths([SourceIdentity.FromPath(sourcePath)], includePaths, out _);

    private CompilationResult Compile(DiagnosticBag diagnostics)
    {
        var config = Options.Config ?? ResolveConfig(ConfigDirectory, diagnostics);
        if (config is null)
        {
            // A project that states a policy and is then silently ignored is worse off than one that
            // states nothing, so a bad config file stops the compilation.
            return new CompilationResult(null, null, [], diagnostics, ProjectConfig.Default, SearchPaths, []);
        }

        var source = Sources[0];
        var file = source.Identity.Name;

        var tokens = new Lexer(source.Text, file, diagnostics).Tokenize();
        var unit = new Parser(tokens, file, diagnostics).ParseCompilationUnit();

        // Parse errors used to stop here. They no longer do: the parser recovers, the binder does
        // not throw on what recovery leaves behind, and a buffer being typed into is broken most of
        // the time an editor is asked anything about it. The most valuable question an editor asks
        // -- what may follow this dot -- is one only the binder can answer, and refusing to run it
        // on a file with a syntax error is refusing to answer it at all.

        // Where the path-based pipeline used to build the search paths: after the source has had its
        // say, so a broken buffer reports what is wrong with it rather than what is wrong with the
        // include arguments. Reported once and then stopped, because letting the import loop run on
        // a truncated search path produces a second diagnostic blaming the import for the first
        // one's cause.
        if (_unusableIncludePaths.Count > 0)
        {
            foreach (var (path, reason) in _unusableIncludePaths)
            {
                diagnostics.Error(
                    "PL0082",
                    "include path could not be used",
                    $"'{path}' could not be resolved to a directory: {reason}",
                    SourceSpan.None,
                    "The path is malformed, not merely missing -- an include directory that does not "
                        + "exist is searched and skipped. Correct the path or drop the entry.");
            }

            return new CompilationResult(null, unit, [], diagnostics, config, SearchPaths, []);
        }

        if (unit.Imports.Count == 0)
        {
            diagnostics.Error(
                "PL0001",
                "no proto imports",
                "A ProtoLang file must import at least one protobuf schema.",
                unit.Span,
                "Add an 'import proto \"your.proto\";' declaration (spec 5.2).");
            return new CompilationResult(null, unit, [], diagnostics, config, SearchPaths, []);
        }

        // Resolved before the imports are checked, because the loader knows about include
        // directories the caller never named: protoc's own bundled well-known schemas. An
        // 'import proto "google/protobuf/timestamp.proto"' resolves for protoc but exists nowhere
        // under the user's proto roots, so checking it against those alone would reject it.
        var loader = Options.Loader;
        try
        {
            loader ??= DescriptorLoader.CreateDefault();
        }
        catch (DescriptorLoadException ex)
        {
            diagnostics.Error(
                "PL0003",
                "protobuf schema could not be loaded",
                ex.Message,
                unit.Imports[0].Span);
            return new CompilationResult(null, unit, [], diagnostics, config, SearchPaths, []);
        }

        var resolvePaths = new List<string>(SearchPaths);
        resolvePaths.AddRange(loader.ImplicitIncludePaths);

        var imports = unit.Imports.Select(import => Resolve(import, resolvePaths)).ToList();

        foreach (var import in imports)
        {
            // An import naming no path at all is passed over in silence. It names no file, the
            // parser has already reported the missing token, and describing the empty string as a
            // schema that could not be found is one mistake told twice.
            if (import.Outcome is ImportOutcome.NotFound)
            {
                diagnostics.Error(
                    "PL0002",
                    "proto file not found",
                    $"Could not find '{import.Path}' in any include directory.",
                    import.Span,
                    ImportSearchHelp(import.SearchedPaths));
            }
        }

        // The question is whether the schemas are all here, not whether anything at all has gone
        // wrong. Asking the bag instead would stop a buffer whose imports are perfectly good and
        // whose only problem is the half-typed line the editor is asking about.
        if (!imports.TrueForAll(import => import.IsResolved))
        {
            return new CompilationResult(null, unit, [], diagnostics, config, SearchPaths, imports);
        }

        // The path the author wrote, not the one it resolved to: protoc is given the same relative
        // path and the same roots to find it under, so that what it reports matches what was asked
        // for.
        var protoFiles = imports.ConvertAll(import => import.Path);

        IReadOnlyList<FileDescriptor> descriptors;
        try
        {
            descriptors = loader.Load(protoFiles, SearchPaths);
        }
        catch (DescriptorLoadException ex)
        {
            diagnostics.Error(
                "PL0003",
                "protobuf schema could not be loaded",
                ex.Message,
                unit.Imports.Count > 0 ? unit.Imports[0].Span : unit.Span);

            // The imports, not an empty list: every one of them resolved -- the gate above refuses
            // to reach protoc otherwise -- and what failed is protoc's reading of schemas this
            // compilation had already found. An empty list here would say the imports were never
            // looked at, and would throw away the file-to-declaration mapping that is exactly what
            // an editor wants to report against and what a cache wants to key on.
            return new CompilationResult(null, unit, [], diagnostics, config, SearchPaths, imports);
        }

        var module = new Binder(descriptors, diagnostics, new NumericPolicy(config), config, source.Identity)
            .Bind(unit);

        // Carried out whether or not anything went wrong, because a module built from a broken tree
        // is exactly what an editor came for and is no use to anyone else. Nothing can mistake it
        // for a finished compilation: Success wants an empty diagnostic bag as well as a module, so
        // every existing caller -- the CLI and every backend -- still sees the same false it always
        // did and never reaches this.
        return new CompilationResult(module, unit, descriptors, diagnostics, config, SearchPaths, imports);
    }

    /// <summary>
    /// The help line on an unresolved import: where the compiler looked, or why it had nowhere to
    /// look.
    /// </summary>
    /// <remarks>
    /// A buffer that has never been saved contributes no directory of its own to the search path, so
    /// with no include paths there is genuinely nowhere to have looked. "Searched: " followed by
    /// nothing tells the reader less than saying so. A source with a path always contributes its own
    /// directory and can never reach that branch, which is why CLI output does not move.
    /// </remarks>
    private string ImportSearchHelp(IReadOnlyList<string> resolvePaths)
        => ConfigDirectory is null && resolvePaths.Count == 0
            ? "No include directories were given, and this source has no directory of its own to "
                + "fall back on. Pass an include path, or save the file first."
            : "Searched: " + string.Join(", ", resolvePaths);

    /// <param name="unusable">
    /// The include paths that could not be normalized, in the order they were given. Reported rather
    /// than thrown: see <see cref="UnusableIncludePath"/>.
    /// </param>
    private static List<string> BuildSearchPaths(
        IEnumerable<SourceIdentity> sources,
        IReadOnlyList<string> includePaths,
        out IReadOnlyList<UnusableIncludePath> unusable)
    {
        var searchPaths = new List<string>();
        var rejected = new List<UnusableIncludePath>();
        unusable = rejected;

        foreach (var includePath in includePaths)
        {
            string full;
            try
            {
                full = Path.GetFullPath(includePath);
            }
            catch (Exception ex)
                when (ex is ArgumentException or PathTooLongException or NotSupportedException or IOException)
            {
                rejected.Add(new UnusableIncludePath(includePath, ex.Message));
                continue;
            }

            Add(full);
        }

        // Behind the caller's directories, so a project that names a proto root explicitly keeps it
        // ahead of whatever happens to sit beside the source. A source with no directory -- an
        // unsaved buffer -- contributes nothing rather than crashing on an empty path.
        foreach (var source in sources)
        {
            if (source.Directory is { } directory)
            {
                Add(directory);
            }
        }

        return searchPaths;

        void Add(string path)
        {
            if (!searchPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                searchPaths.Add(path);
            }
        }
    }

    /// <summary>Looks for the schema one import names, and records where it looked.</summary>
    /// <remarks>
    /// Returns what happened rather than whether it worked, because the two failures are not the
    /// same failure: a path that is not there has been searched for and is worth a diagnostic, and a
    /// path that was never written has not been searched for and has already had one. Every later
    /// question about imports -- which file backs which declaration, whether two of them name the
    /// same schema, whether one missing schema should stop the rest -- is a question about this
    /// value, so it is one object rather than a flag beside a flag.
    /// </remarks>
    private static ImportResolution Resolve(ImportDeclaration import, IReadOnlyList<string> searchPaths)
    {
        if (import.PathIsMissing)
        {
            return new ImportResolution(import, ImportOutcome.Unwritten, null, searchPaths);
        }

        foreach (var searchPath in searchPaths)
        {
            var candidate = Path.Combine(searchPath, import.Path);
            if (File.Exists(candidate))
            {
                return new ImportResolution(import, ImportOutcome.Resolved, candidate, searchPaths);
            }
        }

        return new ImportResolution(import, ImportOutcome.NotFound, null, searchPaths);
    }
}
