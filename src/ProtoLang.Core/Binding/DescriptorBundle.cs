using System.Collections.Concurrent;
using Google.Protobuf.Reflection;
using ProtoLang.Symbols;

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
    private readonly Dictionary<string, FileDescriptor> _descriptors;

    /// <summary>What each file's source info says, read on first ask and kept.</summary>
    /// <remarks>
    /// <para>
    /// The only mutable state here, and it is a memo: an entry is derived entirely from data this
    /// bundle already holds, so building one twice produces two equal answers and building none
    /// changes nothing but the cost. Concurrent rather than locked because a cached bundle is shared
    /// by every compile worker at once and this is a query path -- two workers asking about one
    /// schema at the same moment should both be answered, not queued behind each other, and the
    /// loser's copy costs one wasted walk.
    /// </para>
    /// <para>
    /// Per file rather than per bundle, because the questions this answers -- where is this
    /// declared, what does it say -- are asked about the schema under the cursor. A closure holding
    /// <c>descriptor.proto</c> is not walked because something imported a timestamp.
    /// </para>
    /// <para>
    /// An answer that was diminished by something outside this bundle -- a schema locked or edited
    /// when it was read -- is not filed here at all, because it would outlive the condition that
    /// diminished it. See <see cref="SchemaSourceIndex.IsProvisional"/>.
    /// </para>
    /// </remarks>
    private readonly ConcurrentDictionary<string, SchemaSourceIndex> _sources = new(StringComparer.Ordinal);

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

        // Last spelling wins in each, matching protoc: a descriptor set names each file once, and a
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

        _descriptors = [];
        foreach (var descriptor in descriptors)
        {
            _descriptors[descriptor.Name] = descriptor;
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

    /// <summary>
    /// Where <paramref name="message"/> was declared and what was written about it, or null when this
    /// bundle does not hold the file that declares it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What <see cref="Semantics.SemanticModel.DeclarationOf"/> cannot answer, and says so: a field,
    /// an enum constant, a message or an enum type is declared in a <c>.proto</c>, and the ProtoLang
    /// source that uses it has no declaration to point at. Go to definition and hover both stop at
    /// that boundary without this, which is where they are most wanted -- most of what a ProtoLang
    /// file talks about is declared somewhere else.
    /// </para>
    /// <para>
    /// <b>Null and an answer with nothing in it are different.</b> Null means this bundle knows
    /// nothing of the file -- a descriptor from some other load. An answer whose
    /// <see cref="SchemaDeclaration.Site"/> is null, or whose documentation is empty, means the file
    /// is here and the information is not: a schema with no comments, a descriptor set built without
    /// source info, a well-known type protoc resolved from its own compiled-in descriptors, a file
    /// that could not be read, a file that has been edited since the descriptors were built. Each of
    /// those is ordinary and none of them is an error.
    /// </para>
    /// <para>
    /// The answer describes what <em>this</em> bundle holds. A descriptor from another load with the
    /// same file name is answered from this bundle's tree, which is the only tree whose source info
    /// is here to be read.
    /// </para>
    /// </remarks>
    public SchemaDeclaration? DeclarationOf(MessageDescriptor message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return DeclarationIn(message.File.Name, SymbolId.ForType(message));
    }

    /// <inheritdoc cref="DeclarationOf(MessageDescriptor)"/>
    public SchemaDeclaration? DeclarationOf(EnumDescriptor enumType)
    {
        ArgumentNullException.ThrowIfNull(enumType);

        return DeclarationIn(enumType.File.Name, SymbolId.ForType(enumType));
    }

    /// <inheritdoc cref="DeclarationOf(MessageDescriptor)"/>
    public SchemaDeclaration? DeclarationOf(FieldDescriptor field)
    {
        ArgumentNullException.ThrowIfNull(field);

        return DeclarationIn(field.File.Name, SymbolId.ForField(field));
    }

    /// <inheritdoc cref="DeclarationOf(MessageDescriptor)"/>
    public SchemaDeclaration? DeclarationOf(EnumValueDescriptor value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return DeclarationIn(value.File.Name, SymbolId.ForEnumValue(value));
    }

    private SchemaDeclaration? DeclarationIn(string schemaName, SymbolId symbol)
        => SourceOf(schemaName)?.DeclarationOf(symbol);

    /// <remarks>
    /// Every part comes from this bundle: the built tree supplies the identities and the index of
    /// each element within its parent, the unbuilt one supplies the source info those indices
    /// address, and the closure entry says which file to measure the positions against and how to
    /// tell whether it is still the file protoc read. Taking any of them from anywhere else would be
    /// reading one file's source info against another file's shape.
    /// </remarks>
    private SchemaSourceIndex? SourceOf(string schemaName)
    {
        if (_sources.TryGetValue(schemaName, out var kept))
        {
            return kept;
        }

        if (!_descriptors.TryGetValue(schemaName, out var descriptor)
            || !_protos.TryGetValue(schemaName, out var proto))
        {
            return null;
        }

        var index = SchemaSourceIndex.For(descriptor, proto, _files.GetValueOrDefault(schemaName));

        // An index that could not read a file the closure describes is asked again next time; see
        // SchemaSourceIndex.IsProvisional. The cost is one file read and one walk per query while a
        // schema is out of step with the descriptors, which lasts until the next compilation.
        return index.IsProvisional ? index : _sources.GetOrAdd(schemaName, index);
    }
}
