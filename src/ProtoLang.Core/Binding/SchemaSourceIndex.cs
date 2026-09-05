using Google.Protobuf.Reflection;
using ProtoLang.Diagnostics;
using ProtoLang.Symbols;
using Location = Google.Protobuf.Reflection.SourceCodeInfo.Types.Location;

namespace ProtoLang.Binding;

/// <summary>
/// Everything one <c>.proto</c>'s source info says about the elements it declares, keyed by the same
/// identity the IR carries for each of them.
/// </summary>
/// <remarks>
/// <para>
/// <b>The problem this solves.</b> <c>SourceCodeInfo</c> is a flat list of locations, each addressed
/// by a <em>path</em>: a sequence of field numbers describing where the element sits in the
/// <c>FileDescriptorProto</c> message tree. A field of a message nested in a message is
/// <c>[4, i, 3, j, 2, k]</c>, and its name is that path with <c>1</c> appended. Nothing in the
/// runtime descriptors knows that, so somebody has to build the correspondence, and building it
/// wrong is silent -- an off-by-one in the walk reports the previous declaration's range rather than
/// no range at all.
/// </para>
/// <para>
/// <b>Downward, not upward.</b> The paths are built while descending the built descriptors, so each
/// element's index in its parent is a loop variable rather than something reconstructed by searching
/// the parent for the child. The position in <c>Fields.InDeclarationOrder()</c>,
/// <c>NestedTypes</c> and <c>EnumTypes</c> <em>is</em> the index into the corresponding repeated
/// field of the proto, because the runtime built those lists from that proto in order. That
/// invariant is what the whole file rests on, and the sweep in <c>SchemaDeclarationTests</c> is what
/// would catch it changing.
/// </para>
/// <para>
/// <b>Built once, whole.</b> Every element of the file is resolved when the index is, and the file's
/// text is released afterwards. Walking the file lazily per query would mean holding the source info
/// and the text for as long as the bundle lives and re-deciding per hover which part of it to read;
/// the walk is a few hundred elements and costs less than deciding not to do it.
/// </para>
/// </remarks>
internal sealed class SchemaSourceIndex
{
    private readonly Dictionary<SymbolId, SchemaDeclaration> _declarations;

    private SchemaSourceIndex(Dictionary<SymbolId, SchemaDeclaration> declarations)
        => _declarations = declarations;

    /// <summary>Reads one file's source info into declarations.</summary>
    /// <param name="descriptor">The built tree, which supplies the identities and the path indices.</param>
    /// <param name="proto">The same file unbuilt, which is where the source info is.</param>
    /// <param name="path">The file on disk, or null when nothing on disk backs this schema.</param>
    public static SchemaSourceIndex For(FileDescriptor descriptor, FileDescriptorProto proto, string? path)
    {
        var builder = new Builder(descriptor.Name, LocationsByPath(proto), SchemaText.Read(path));

        builder.AddFile(descriptor);

        return new SchemaSourceIndex(builder.Declarations);
    }

    /// <summary>What is known about <paramref name="symbol"/>, or null when this file declares no such thing.</summary>
    public SchemaDeclaration? DeclarationOf(SymbolId symbol) => _declarations.GetValueOrDefault(symbol);

    /// <remarks>
    /// First entry wins. <c>descriptor.proto</c> warns that several locations can share one path --
    /// an <c>extend</c> block spread over a file is its example -- and none of the four kinds indexed
    /// here can be one of those, so the collision is theoretical and the first is as good an answer
    /// as any.
    /// </remarks>
    private static Dictionary<string, Location> LocationsByPath(FileDescriptorProto proto)
    {
        var locations = new Dictionary<string, Location>(StringComparer.Ordinal);

        foreach (var location in proto.SourceCodeInfo?.Location ?? [])
        {
            locations.TryAdd(KeyFor(location.Path), location);
        }

        return locations;
    }

    private static string KeyFor(IEnumerable<int> path) => string.Join(',', path);

    /// <summary>Field numbers in <c>descriptor.proto</c>, which is what a source-info path is made of.</summary>
    /// <remarks>
    /// Named rather than written into the walk, because <c>[4, i, 3, j, 2, k]</c> is unreadable and
    /// unverifiable at the point of use. Each is checked against the <c>descriptor.proto</c> that
    /// ships beside the bundled protoc.
    /// </remarks>
    private static class ProtoField
    {
        /// <summary>
        /// <c>name</c>, which is field 1 of a message, an enum, a field and an enum value alike --
        /// one constant because it is one rule, not four that happen to agree.
        /// </summary>
        public const int Name = 1;

        public const int FileMessageType = 4;
        public const int FileEnumType = 5;
        public const int MessageField = 2;
        public const int MessageNestedType = 3;
        public const int MessageEnumType = 4;
        public const int EnumValue = 2;
    }

    /// <summary>The walk, and the state it needs while it is walking.</summary>
    /// <remarks>
    /// A type of its own so that the index itself holds nothing but its answers: the location table
    /// and the file's text are scaffolding, and keeping them as fields on the index would mean every
    /// cached bundle held every queried schema's text for as long as it lived.
    /// </remarks>
    private sealed class Builder(
        string schemaName,
        Dictionary<string, Location> locations,
        SchemaText? text)
    {
        public Dictionary<SymbolId, SchemaDeclaration> Declarations { get; } = [];

        public void AddFile(FileDescriptor file)
        {
            for (var index = 0; index < file.MessageTypes.Count; index++)
            {
                AddMessage(file.MessageTypes[index], [ProtoField.FileMessageType, index]);
            }

            for (var index = 0; index < file.EnumTypes.Count; index++)
            {
                AddEnum(file.EnumTypes[index], [ProtoField.FileEnumType, index]);
            }
        }

        private void AddMessage(MessageDescriptor message, int[] path)
        {
            Add(SymbolId.ForType(message), path);

            var fields = message.Fields.InDeclarationOrder();
            for (var index = 0; index < fields.Count; index++)
            {
                Add(SymbolId.ForField(fields[index]), [.. path, ProtoField.MessageField, index]);
            }

            for (var index = 0; index < message.NestedTypes.Count; index++)
            {
                AddMessage(message.NestedTypes[index], [.. path, ProtoField.MessageNestedType, index]);
            }

            for (var index = 0; index < message.EnumTypes.Count; index++)
            {
                AddEnum(message.EnumTypes[index], [.. path, ProtoField.MessageEnumType, index]);
            }
        }

        private void AddEnum(EnumDescriptor enumType, int[] path)
        {
            Add(SymbolId.ForType(enumType), path);

            for (var index = 0; index < enumType.Values.Count; index++)
            {
                Add(SymbolId.ForEnumValue(enumType.Values[index]), [.. path, ProtoField.EnumValue, index]);
            }
        }

        private void Add(SymbolId symbol, int[] path)
            => Declarations[symbol] = new SchemaDeclaration(symbol, schemaName, SiteOf(path), CommentsOf(path));

        /// <remarks>
        /// Null wherever any part of the answer is missing -- no file to open, no location recorded,
        /// a location that no longer fits the text. Half a site navigates somewhere wrong, which is
        /// worse than declining, and every one of those cases is ordinary rather than exceptional.
        /// </remarks>
        private SchemaSite? SiteOf(int[] path)
        {
            if (text is not { } source || LocationOf(path) is not { } declaration)
            {
                return null;
            }

            if (SpanOf(source, declaration) is not { } extent)
            {
                return null;
            }

            // The name of a declaration protoc accepted is always recorded, so the fallback is for a
            // descriptor set assembled by something other than protoc: selecting the whole
            // declaration is the honest answer when the narrower range is unknown.
            var name = LocationOf([.. path, ProtoField.Name]) is { } named ? SpanOf(source, named) : null;

            return new SchemaSite(source.Path, extent, name ?? extent);
        }

        private SchemaComments CommentsOf(int[] path)
        {
            if (LocationOf(path) is not { } location)
            {
                return SchemaComments.None;
            }

            List<string> detached = [];
            foreach (var paragraph in location.LeadingDetachedComments)
            {
                if (Clean(paragraph) is { } cleaned)
                {
                    detached.Add(cleaned);
                }
            }

            var comments = new SchemaComments(
                Clean(location.LeadingComments),
                Clean(location.TrailingComments),
                detached);

            return comments.IsEmpty ? SchemaComments.None : comments;
        }

        private Location? LocationOf(int[] path) => locations.GetValueOrDefault(KeyFor(path));

        /// <summary>The range a protoc location names, in this compiler's coordinates.</summary>
        /// <remarks>
        /// A span is <c>[startLine, startColumn, endLine, endColumn]</c>, or three elements when the
        /// declaration is on one line, and both coordinates are 0-based. Null when it names nothing
        /// this text has -- a file edited since protoc read it, which resolves on the next compile
        /// because the closure hash no longer matches.
        /// </remarks>
        private SourceSpan? SpanOf(SchemaText source, Location location)
        {
            var span = location.Span;

            if (span.Count is not (3 or 4))
            {
                return null;
            }

            var startLine = span[0];
            var endLine = span.Count == 3 ? startLine : span[2];

            if (source.PositionAt(startLine, span[1]) is not { } start
                || source.PositionAt(endLine, span[^1]) is not { } end
                || end.Offset < start.Offset)
            {
                return null;
            }

            return new SourceSpan(schemaName, start, end);
        }

        /// <summary>One comment as a reader would want it, or null when there is nothing left of it.</summary>
        /// <remarks>
        /// protoc has already removed <c>//</c>, the block delimiters, and the asterisks down the
        /// left of a block comment, leaving the space the author typed after each marker. Removing
        /// the indentation those spaces amount to is what makes the result render as prose rather
        /// than as a code block, and it is taken as the smallest indentation of any non-blank line so
        /// that a deliberately indented line inside the comment keeps its shape. Interior blank lines
        /// stay, because they are the paragraph breaks a hover card renders.
        /// </remarks>
        private static string? Clean(string? comment)
        {
            if (string.IsNullOrEmpty(comment))
            {
                return null;
            }

            var lines = comment.Split('\n');
            for (var index = 0; index < lines.Length; index++)
            {
                lines[index] = lines[index].TrimEnd();
            }

            var indent = lines
                .Where(line => line.Length > 0)
                .Select(line => line.Length - line.TrimStart(' ', '\t').Length)
                .DefaultIfEmpty(0)
                .Min();

            var cleaned = string.Join('\n', lines.Select(line => line.Length > indent ? line[indent..] : string.Empty));

            return cleaned.Trim('\n') is { Length: > 0 } text ? text : null;
        }
    }

    /// <summary>
    /// A <c>.proto</c>'s text, and the one question this file asks it: where is the position protoc
    /// described?
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>protoc does not count columns the way an editor does.</b> Its tokenizer advances the column
    /// by one per <em>byte</em>, and a tab to the next multiple of eight. So on a tab-indented schema
    /// every column it reports is several columns right of the character it means, and on a line
    /// holding any non-ASCII text everything after that text is shifted. A range built from those
    /// numbers puts the selection on the wrong characters, which reads as broken rather than as
    /// approximate.
    /// </para>
    /// <para>
    /// The fix is to replay protoc's own counting across the line and stop where it stops, which
    /// needs the text -- and the text is needed anyway, because a <see cref="SourceSpan"/> carries an
    /// offset as well as a line and a column, and neither can be derived from the other without it.
    /// </para>
    /// </remarks>
    private sealed class SchemaText
    {
        /// <summary>What protoc's tokenizer advances a tab to a multiple of.</summary>
        private const int TabWidth = 8;

        private readonly string _text;
        private readonly LineMap _map;

        private SchemaText(string path, string text)
        {
            Path = path;
            _text = text;
            _map = new LineMap(text);
        }

        /// <summary>
        /// The file this text was read from, carried so that a site and the text it was measured
        /// against cannot come from two different files.
        /// </summary>
        public string Path { get; }

        /// <summary>Reads the file, or answers null when there is nothing to read or it cannot be read.</summary>
        /// <remarks>
        /// A locked or deleted schema is a thing an editor meets, and it means "no location", not a
        /// failed compilation. <see cref="SchemaClosure"/> takes the same view of the same files.
        /// </remarks>
        public static SchemaText? Read(string? path)
        {
            if (path is null)
            {
                return null;
            }

            try
            {
                return new SchemaText(path, File.ReadAllText(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>
        /// Where a 0-based line and a column counted protoc's way is in this text, or null when the
        /// text has no such line.
        /// </summary>
        public SourcePosition? PositionAt(int line, int column)
        {
            if (line < 0 || line >= _map.LineCount || column < 0)
            {
                return null;
            }

            var start = _map.OffsetOf(line + 1, 1);
            var end = line + 1 < _map.LineCount ? _map.OffsetOf(line + 2, 1) : _text.Length;

            return _map.PositionOf(start + Utf16OffsetIn(_text.AsSpan(start, end - start).TrimEnd(['\r', '\n']), column));
        }

        /// <inheritdoc cref="SchemaText"/>
        private static int Utf16OffsetIn(ReadOnlySpan<char> line, int column)
        {
            var counted = 0;
            var index = 0;

            while (counted < column && index < line.Length)
            {
                if (line[index] == '\t')
                {
                    counted += TabWidth - (counted % TabWidth);
                    index++;
                    continue;
                }

                var pair = char.IsHighSurrogate(line[index])
                    && index + 1 < line.Length
                    && char.IsLowSurrogate(line[index + 1]);

                counted += pair ? 4 : Utf8LengthOf(line[index]);
                index += pair ? 2 : 1;
            }

            return index;
        }

        /// <summary>How many bytes one character costs protoc, which counts them rather than characters.</summary>
        private static int Utf8LengthOf(char value) => value switch
        {
            < (char)0x80 => 1,
            < (char)0x800 => 2,
            _ => 3,
        };
    }
}
