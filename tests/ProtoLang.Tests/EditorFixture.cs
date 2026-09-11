using ProtoLang.LanguageServer.Hosting;
using ProtoLang.LanguageServer.Protocol;
using ProtoLang.LanguageServer.Protocol.Lsp;
using ProtoLang.LanguageServer.Workspace;
using Xunit;

namespace ProtoLang.Tests;

/// <summary>
/// One open document and the parts a provider needs around it, without a server.
/// </summary>
/// <remarks>
/// The setup every suite that drives a provider directly was writing for itself. It is worth one
/// home rather than one copy per file because the fiddly part is not the parts -- it is that the
/// document has to live in a directory the fixture schemas can be imported from, which is a fact
/// about this repository rather than about any one feature.
/// </remarks>
internal static class EditorFixture
{
    /// <summary>A configuration sync attached to a connection that never says anything.</summary>
    /// <remarks>
    /// Enough for a provider: what it consults is <see cref="ConfigurationSync.Current"/>, which is
    /// the workspace's default until a client answers <c>workspace/configuration</c> -- and a test
    /// driving a provider rather than a server has no client to answer.
    /// </remarks>
    public static ConfigurationSync Configuration()
    {
        var log = new ServerLog();

        return new ConfigurationSync(new JsonRpcConnection(Stream.Null, Stream.Null, log), log);
    }

    public static LoaderPool Loaders() => new(new ServerLog());

    /// <summary>
    /// Opens <paramref name="text"/> as a document in a directory of its own, beside a copy of the
    /// fixture schemas, so that <c>import proto</c> resolves the way it does in a real workspace.
    /// </summary>
    public static (DocumentStore Documents, DocumentUri Uri) Open(string text)
    {
        var directory = TestPaths.CreateTempDirectory();

        foreach (var schema in Directory.EnumerateFiles(TestPaths.FixtureProtoDirectory, "*.proto"))
        {
            File.Copy(schema, Path.Combine(directory, Path.GetFileName(schema)));
        }

        var uri = DocumentUri.FromPath(Path.Combine(directory, "source.protolang"));
        var documents = new DocumentStore();

        documents.Open(uri, "protolang", 1, text);

        return (documents, uri);
    }

    /// <summary>Where <paramref name="marker"/> begins, which is how a test names a caret.</summary>
    /// <remarks>
    /// Computed from the fixture rather than written as a number, so the offsets in a test go on
    /// meaning what they meant after somebody edits the source above them.
    /// </remarks>
    public static int At(string text, string marker)
    {
        var offset = text.IndexOf(marker, StringComparison.Ordinal);

        Assert.True(offset >= 0, $"the fixture must contain '{marker}'");
        return offset;
    }

    /// <summary>The caret immediately after <paramref name="marker"/>.</summary>
    public static int After(string text, string marker) => At(text, marker) + marker.Length;

    /// <summary>One position, as a client sends it.</summary>
    public static TextDocumentPositionParams Ask(DocumentUri uri, string text, int offset)
        => new()
        {
            TextDocument = new TextDocumentIdentifier { Uri = uri.ToString() },
            Position = EditorPositions.PositionAt(new Diagnostics.LineMap(text), offset),
        };
}
