using ProtoLang.Diagnostics;

namespace ProtoLang.Symbols;

/// <summary>
/// Where in a <c>.proto</c> a schema element was written: the file to open, the whole declaration,
/// and the name inside it.
/// </summary>
/// <remarks>
/// <para>
/// Two ranges for the reason <see cref="DeclarationSite"/> gives -- an editor asks for both and
/// derives neither -- and one path, because a range without a file to measure it in navigates
/// nowhere. All three are present or the whole site is absent; see <see cref="SchemaDeclaration"/>.
/// </para>
/// <para>
/// The path is the file the compiler actually read, resolved through the include paths protoc was
/// given rather than reconstructed from the schema name afterwards. The name a span carries is the
/// schema name -- <c>google/protobuf/timestamp.proto</c> -- which is what protoc prints and what a
/// reader recognizes; the path is where that name was found on this machine, which is not the same
/// thing and is sometimes a package cache.
/// </para>
/// </remarks>
public sealed record SchemaSite
{
    /// <param name="path">The file on disk, which the compiler has read.</param>
    /// <param name="extent">The whole declaration, from its first keyword to its closing token.</param>
    /// <param name="name">The declared name alone, which is what a client selects on arrival.</param>
    public SchemaSite(string path, SourceSpan extent, SourceSpan name)
    {
        ArgumentNullException.ThrowIfNull(path);

        Path = path;
        Name = name;

        // Widened for the reason DeclarationSite widens its own: LSP requires the selection range to
        // lie inside the range it selects from, and a client handed the other way round has no
        // defined behavior. protoc nests them already, so this is a no-op that cannot be forgotten.
        Extent = SourceSpan.Union(extent, name);
    }

    /// <summary>The file an editor opens to show this declaration.</summary>
    public string Path { get; }

    /// <summary>The whole declaring construct, for a client that shows it in context.</summary>
    public SourceSpan Extent { get; }

    /// <summary>The declared name, and nothing around it. Always inside <see cref="Extent"/>.</summary>
    public SourceSpan Name { get; }
}

/// <summary>
/// What the author wrote about a schema element, with the comment markers already gone.
/// </summary>
/// <remarks>
/// <para>
/// Three kinds because protoc distinguishes three and they read differently: the leading comment
/// documents the declaration, a trailing comment annotates it in passing
/// (<c>int64 count = 1;  // how many</c>), and a detached paragraph is prose that sits above the
/// declaration with a blank line between, which usually belongs to the section rather than to the
/// element. They arrive in one <c>SourceCodeInfo.Location</c>, so keeping them apart costs nothing
/// and leaves how a hover card composes them to the thing rendering the hover card.
/// </para>
/// <para>
/// Text, not Markdown. Each is cleaned -- markers stripped by protoc, the common indentation and
/// trailing whitespace stripped here, interior blank lines kept so paragraphs survive -- and
/// nothing is escaped, because escaping is a fact about the renderer rather than about the comment.
/// </para>
/// </remarks>
/// <param name="Leading">The comment immediately above the declaration, or null when there is none.</param>
/// <param name="Trailing">The comment on or after the declaration, or null when there is none.</param>
/// <param name="Detached">Paragraphs above the declaration but separated from it, in order.</param>
public sealed record SchemaComments(string? Leading, string? Trailing, IReadOnlyList<string> Detached)
{
    /// <summary>What an undocumented element has to say.</summary>
    /// <remarks>
    /// Shared, and safe to share: every member is immutable, and the empty list is a
    /// <see cref="Array.Empty{T}"/> singleton rather than a list anything could append to.
    /// </remarks>
    public static SchemaComments None { get; } = new(null, null, []);

    /// <summary>Whether the author wrote nothing about this element at all.</summary>
    public bool IsEmpty => Leading is null && Trailing is null && Detached.Count == 0;
}

/// <summary>
/// Where a protobuf message, enum, field, or enum value was declared, and what was written about it.
/// </summary>
/// <remarks>
/// <para>
/// The schema-side counterpart of <see cref="DeclarationSite"/>, which answers the same question for
/// everything ProtoLang declares. Deliberately a separate type rather than the same one: a
/// <see cref="DeclarationSite"/> names its declaration with a <see cref="Syntax.SyntaxName"/>, which
/// exists to describe a name an author may not have finished typing, and a <c>.proto</c> that protoc
/// accepted has no such thing. What the two share is the identity -- <see cref="SymbolId"/> -- so a
/// caller holding one can ask either question without a translation step.
/// </para>
/// <para>
/// <b>Absence is ordinary and is not an error.</b> A schema with no comments, a descriptor set built
/// without source info, a well-known type protoc resolved from descriptors compiled into itself, a
/// file the compiler could not read: each of them yields a declaration with no
/// <see cref="Site"/>, or empty <see cref="Documentation"/>, or both. The two are independent
/// questions -- protoc's own schemas are richly documented and, on a recent protoc, nowhere on disk
/// -- so a caller that wants to navigate asks about the site and a caller that wants to explain
/// asks about the documentation.
/// </para>
/// </remarks>
/// <param name="Id">
/// What this element is, in the identity the IR already carries for it. See
/// <see cref="SymbolId.ForField"/> and its neighbours.
/// </param>
/// <param name="SchemaName">
/// The name protoc knows the file by: relative to a proto root, forward-slashed. The same string
/// <see cref="Binding.SchemaFile.Name"/> carries, and the label on every span in
/// <see cref="Site"/>.
/// </param>
/// <param name="Site">Where it is written, or null when nothing readable on disk holds it.</param>
/// <param name="Documentation">What was written about it, possibly nothing.</param>
public sealed record SchemaDeclaration(
    SymbolId Id,
    string SchemaName,
    SchemaSite? Site,
    SchemaComments Documentation);
