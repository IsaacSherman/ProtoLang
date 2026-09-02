using Google.Protobuf.Reflection;

namespace ProtoLang.Binding;

/// <summary>
/// Everything one descriptor load produced, with a single lifetime: the built descriptors the binder
/// resolves against, the descriptor set they were built from, and the files that went into it.
/// </summary>
/// <remarks>
/// <para>
/// The loader used to parse a <c>FileDescriptorSet</c>, convert each file to bytes, build
/// <see cref="FileDescriptor"/> objects from those bytes, and return only the objects. The set went
/// out of scope on the next line, taking with it every <c>FileDescriptorProto</c> and the
/// <c>SourceCodeInfo</c> that <c>--include_source_info</c> was asked for on every single run. Source
/// info is where a schema's declaration sites and its doc comments live, so the compiler was paying
/// protoc to produce the one thing it then discarded.
/// </para>
/// <para>
/// A bundle rather than a tuple of returns because it is what gets cached, and the pieces have to
/// share a lifetime to be cached at all: descriptors built from one set and source info from another
/// would be a mismatch nothing could detect. <see cref="Closure"/> travels with them for the same
/// reason -- it is both the record of which files this was built from, which is what tells a cache
/// whether the bundle is still good, and the map from a schema name to the file it came from, which
/// is what a reader needs to be sent to a declaration in a <c>.proto</c>.
/// </para>
/// <para>
/// A class rather than a record, deliberately. Two bundles are the same bundle when they are the same
/// object -- a cache hit -- and never because their contents compare equal. Structural equality here
/// would mean deep-comparing megabytes of protobuf message on any incidental <c>==</c>, and
/// <see cref="CompilationResult"/> carries a bundle, so its generated equality would inherit that
/// cost.
/// </para>
/// <para>
/// Everything reachable from here is either immutable or a copy. Protobuf's generated messages are
/// mutable and this object is shared by design, so the descriptor set is held privately and handed
/// out only through <see cref="ProtoFor"/> and <see cref="CloneSet"/>; see those for what that buys.
/// </para>
/// </remarks>
public sealed class DescriptorBundle
{
    private readonly FileDescriptorSet _set;
    private readonly Dictionary<string, FileDescriptorProto> _protos;
    private readonly Dictionary<string, SchemaFile> _files;

    public DescriptorBundle(
        IReadOnlyList<FileDescriptor> descriptors,
        FileDescriptorSet set,
        IReadOnlyList<SchemaFile> closure)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(closure);

        Descriptors = descriptors;
        Closure = closure;
        _set = set;

        // Last spelling wins in both, matching protoc: a descriptor set names each file once, and a
        // duplicate could only come from a caller assembling one by hand.
        _protos = [];
        foreach (var proto in set.File)
        {
            _protos[proto.Name] = proto;
        }

        _files = [];
        foreach (var file in closure)
        {
            _files[file.Name] = file;
        }
    }

    /// <summary>A load that named no schemas, which is what a source with no imports produces.</summary>
    /// <remarks>
    /// A new one each time rather than a shared instance. Protobuf messages are mutable, so a static
    /// empty <see cref="FileDescriptorSet"/> would be shared mutable state reachable from every
    /// caller -- and the one thing worse than a cache that serves stale descriptors is a constant
    /// that stops being empty.
    /// </remarks>
    public static DescriptorBundle Empty => new([], new FileDescriptorSet(), []);

    /// <summary>
    /// The runtime descriptors, dependency order, exactly as <see cref="DescriptorLoader.Load"/> has
    /// always returned them.
    /// </summary>
    public IReadOnlyList<FileDescriptor> Descriptors { get; }

    /// <summary>Every file in the transitive closure, and where each one came from.</summary>
    public IReadOnlyList<SchemaFile> Closure { get; }

    /// <summary>
    /// The unbuilt descriptor for one file of the closure -- source info and all -- or null when it
    /// holds no such file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A copy, and this is the whole reason the set is not simply a property. Protobuf's generated
    /// messages are mutable, and a bundle is shared: a cache hit hands the same instance to every
    /// caller, deliberately. Handing out the live <c>FileDescriptorProto</c> would mean one consumer
    /// clearing a field, or sorting a list in place, silently rewrites what every later hit returns
    /// -- a poisoned cache with no bad write anywhere near the cache, and no way to tell from the
    /// outside that it happened.
    /// </para>
    /// <para>
    /// The copy is of one file rather than of the closure, which is what makes it affordable: the
    /// questions this answers -- what comment sits above this message, which line is this field
    /// declared on -- are asked about the schema under the cursor, not about every schema at once.
    /// The built <see cref="Descriptors"/> need no such care, being immutable.
    /// </para>
    /// </remarks>
    public FileDescriptorProto? ProtoFor(string name) => _protos.GetValueOrDefault(name)?.Clone();

    /// <summary>
    /// The whole descriptor set protoc produced, source info included, as a copy the caller owns.
    /// </summary>
    /// <remarks>
    /// A method rather than a property, because it is neither free nor idempotent-looking: it deep
    /// copies every file in the closure. Prefer <see cref="ProtoFor"/>, which copies one. This exists
    /// for the caller that genuinely wants the set -- writing it back out, or handing it to a
    /// protoc plugin (#8) -- rather than one file of it.
    /// </remarks>
    public FileDescriptorSet CloneSet() => _set.Clone();

    /// <summary>
    /// The file on disk one schema name came from, or null when nothing on disk backs it -- either
    /// because the closure does not hold the name, or because protoc resolved it from its own
    /// compiled-in descriptors.
    /// </summary>
    public string? PathFor(string name) => _files.GetValueOrDefault(name)?.Path;
}
