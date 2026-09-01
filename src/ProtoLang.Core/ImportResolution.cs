using ProtoLang.Diagnostics;
using ProtoLang.Syntax;

namespace ProtoLang;

/// <summary>What became of one <c>import proto</c> declaration.</summary>
public enum ImportOutcome
{
    /// <summary>Found in an include directory. This is the schema protoc will be handed.</summary>
    Resolved,

    /// <summary>Looked for, and in none of the directories that were searched.</summary>
    NotFound,

    /// <summary>
    /// Not looked for, because the declaration names no path: the author is still typing it, and the
    /// parser has already said so.
    /// </summary>
    Unwritten,
}

/// <summary>
/// One <c>import proto</c> declaration and what the compiler made of it, including where it looked.
/// </summary>
/// <remarks>
/// <para>
/// Published because "did the schemas all arrive?" is a question the pipeline has to ask, callers
/// have to ask, and a growing list of issues each have their own reason to ask -- a descriptor cache
/// that needs to know which file backs which import, a duplicate-import check, a fallback when one
/// schema of several is missing. Answering it from a local flag meant every one of those would add
/// another flag beside it, and the flags would eventually disagree about the same import.
/// </para>
/// <para>
/// It carries the declaration rather than copying pieces out of it, so the span a diagnostic wants
/// and the path a loader wants come from the one place the parser put them. <see cref="SearchedPaths"/>
/// is the list this import was actually resolved against, which is not always the compilation's
/// search paths -- protoc contributes its own bundled schema directory, and a caller that reports
/// where a schema came from must not recompute a list it hopes was the same one.
/// </para>
/// </remarks>
/// <param name="ResolvedPath">
/// The file backing this import, or null when there is none. Absolute; the relative path the author
/// wrote is on the <paramref name="Declaration"/>, and it is the relative one protoc is given.
/// </param>
public sealed record ImportResolution(
    ImportDeclaration Declaration,
    ImportOutcome Outcome,
    string? ResolvedPath,
    IReadOnlyList<string> SearchedPaths)
{
    /// <summary>Whether a schema was found for this import.</summary>
    public bool IsResolved => Outcome is ImportOutcome.Resolved;

    /// <summary>The path as the author wrote it, empty when they have not written one.</summary>
    public string Path => Declaration.Path;

    /// <summary>Where the declaration is, for a caller reporting against it.</summary>
    public SourceSpan Span => Declaration.Span;
}
