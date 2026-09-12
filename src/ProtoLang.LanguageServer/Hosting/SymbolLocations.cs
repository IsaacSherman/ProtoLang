using ProtoLang.Diagnostics;
using ProtoLang.Symbols;
using ProtoLang.LanguageServer.Protocol.Lsp;
using ProtoLang.LanguageServer.Workspace;

namespace ProtoLang.LanguageServer.Hosting;

/// <summary>
/// Which document a span is in and how to spell that document to the client that asked.
/// </summary>
/// <remarks>
/// <para>
/// One home because three surfaces need it and they must agree: go-to-definition sends the client to
/// one declaration, find-all-references sends it to every use, and occurrence highlighting describes
/// the ranges in the document it was asked about. A second copy of the spelling rule below is a
/// second answer to "is this the file you already have open", and the two disagree on the day one of
/// them is taught about a path and the other is not.
/// </para>
/// <para>
/// <b>A reference is in the file it was written in, not the file its symbol was declared in.</b> For
/// anything the schema declares those are different files -- <c>quantity</c> is declared in a
/// <c>.proto</c> and written in a ProtoLang buffer -- so a list of references is a list about this
/// buffer however many of its symbols came from elsewhere. Only the declaration itself can be on the
/// other side of that boundary, which is why it is the one thing here that asks
/// <see cref="DeclaredSymbol"/> which compiler owns it.
/// </para>
/// </remarks>
internal static class SymbolLocations
{
    /// <summary>Where a name was written, as the client spells documents.</summary>
    public static Location LocationOf(SymbolReference reference, DocumentUri asked)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(asked);

        return new Location(UriOf(reference.Document, asked), EditorPositions.RangeOf(reference.Span));
    }

    /// <summary>Where a symbol is declared, whichever compiler owns the file it is written in.</summary>
    /// <remarks>
    /// The ProtoLang side first, because it is the side with no reason to fail: a declaration this
    /// compilation produced is in the buffer that produced it. The schema side answers for
    /// everything else and may decline, which is what an unreadable <c>.proto</c> looks like from
    /// here.
    /// </remarks>
    public static Declared? DeclarationOf(DeclaredSymbol symbol, DocumentUri asked)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        ArgumentNullException.ThrowIfNull(asked);

        if (symbol.Site is { } site)
        {
            return new Declared(UriOf(site.Document, asked), site.Extent, site.Name.Span);
        }

        return symbol.Schema?.Site is { } declared
            ? new Declared(DocumentUri.FromPath(declared.Path).ToString(), declared.Extent, declared.Name)
            : null;
    }

    /// <summary>Which document a ProtoLang span is in, as the client spells documents.</summary>
    /// <remarks>
    /// <para>
    /// <b>The spelling the client sent, wherever that is the file being described.</b> A URI derived
    /// from a path is this server's spelling of it, and the two differ: an editor sends
    /// <c>file:///c%3A/Users/...</c> and <see cref="DocumentUri.FromPath"/> produces
    /// <c>file:///C:/Users/...</c>. A client that matches the target against its own open documents
    /// by string then fails to recognize the buffer the caret is already in, and opens a second
    /// editor onto the same file. The request already carries the spelling that works, so nothing is
    /// gained by re-deriving one.
    /// </para>
    /// <para>
    /// Whether it <em>is</em> the same file is asked of <see cref="PathIdentity"/> rather than
    /// assumed, although a compilation holds one source today: #27 makes it stop being true, and a
    /// declaration that silently claimed to be in the wrong file would navigate to the right line of
    /// the wrong buffer. A source with no path is an unsaved buffer, whose only handle is the URI the
    /// client opened it under -- which is this one.
    /// </para>
    /// </remarks>
    public static string UriOf(SourceIdentity source, DocumentUri asked)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(asked);

        return source.Path is { } path && !PathIdentity.AreSame(path, asked.Path)
            ? DocumentUri.FromPath(path).ToString()
            : asked.ToString();
    }

    /// <summary>A declaration reduced to what the wire carries: a document and two ranges.</summary>
    /// <remarks>
    /// Nested, because <c>Declared</c> says nothing on its own in a namespace that also holds
    /// <see cref="DeclaredSymbol"/> and answers about <see cref="Symbols.DeclarationSite"/>. Qualified
    /// by the type that produces it, it says which of the three it is.
    /// </remarks>
    internal sealed record Declared(string Uri, SourceSpan Extent, SourceSpan Name);
}
