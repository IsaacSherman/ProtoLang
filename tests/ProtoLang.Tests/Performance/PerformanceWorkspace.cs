using ProtoLang.LanguageServer.Hosting;
using ProtoLang.LanguageServer.Protocol.Lsp;
using ProtoLang.LanguageServer.Workspace;

namespace ProtoLang.Tests.Performance;

/// <summary>
/// One corpus file open in a server's worth of parts, with every provider sharing one
/// <see cref="DocumentSemantics"/> exactly as the host wires them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Providers rather than a server, and deliberately.</b> A measurement taken over the wire would
/// include JSON framing, a reader loop and a worker handoff, and would then be reported as the cost
/// of a hover. Those costs are real and #58 is where a user sees them end to end; what the budgets
/// in #57 constrain is the work an answer does, which is what a design decision can move.
/// </para>
/// <para>
/// <b>One <see cref="DocumentSemantics"/> across all of them, because that is the host's shape.</b>
/// Giving each provider its own would make every measurement a cold compile and the warm column
/// meaningless -- and would be measuring a server nobody ships.
/// </para>
/// </remarks>
internal sealed class PerformanceWorkspace
{
    private readonly ConfigurationSync _configuration;

    public PerformanceWorkspace(string which)
    {
        Which = which;
        Text = PerformanceCorpus.TextOf(which);

        var directory = TestPaths.CreateTempDirectory();

        foreach (var schema in Directory.EnumerateFiles(TestPaths.ExampleProtoDirectory, "*.proto"))
        {
            File.Copy(schema, System.IO.Path.Combine(directory, System.IO.Path.GetFileName(schema)));
        }

        Path = System.IO.Path.Combine(directory, $"{which}.protolang");
        File.WriteAllText(Path, Text);

        Uri = DocumentUri.FromPath(Path);
        Documents = new DocumentStore();
        Documents.Open(Uri, "protolang", 1, Text);

        _configuration = EditorFixture.Configuration();

        var loaders = EditorFixture.Loaders();

        Semantics = new DocumentSemantics(loaders);
        Completion = new CompletionProvider(Documents, _configuration, loaders, semantics: Semantics);
        Hover = new HoverProvider(Documents, _configuration, loaders, semantics: Semantics);
        Definition = new DefinitionProvider(Documents, _configuration, loaders, semantics: Semantics);
        Highlights = new HighlightProvider(Documents, _configuration, loaders, semantics: Semantics);
        References = new ReferenceProvider(Documents, _configuration, loaders, semantics: Semantics);
    }

    public string Which { get; }

    public string Text { get; }

    public string Path { get; }

    public DocumentUri Uri { get; }

    public DocumentStore Documents { get; }

    public DocumentSemantics Semantics { get; }

    public CompletionProvider Completion { get; }

    public HoverProvider Hover { get; }

    public DefinitionProvider Definition { get; }

    public HighlightProvider Highlights { get; }

    public ReferenceProvider References { get; }

    /// <summary>The document as it stands, which is what a provider is handed.</summary>
    public OpenDocument Document => Documents.Find(Uri)!;

    public WorkspaceConfiguration Configuration => _configuration.Current;

    /// <summary>One position, as a client sends it.</summary>
    public TextDocumentPositionParams Ask(int offset) => EditorFixture.Ask(Uri, Text, offset);

    /// <summary>Where <paramref name="marker"/> begins.</summary>
    public int At(string marker) => EditorFixture.At(Text, marker);

    /// <summary>The caret immediately after <paramref name="marker"/>.</summary>
    public int After(string marker) => EditorFixture.After(Text, marker);

    /// <summary>
    /// Compiles once so that every later question is answered from a held compilation.
    /// </summary>
    /// <remarks>
    /// This is what "warm" means in the budget table, and it is the state an editor is in for all
    /// but the first keystroke: the descriptors are loaded, the buffer has been compiled, and the
    /// model the next answer reads was built by the keystroke before it. Measuring without it
    /// measures protoc, which is the cold row and is reported separately.
    /// </remarks>
    public PerformanceWorkspace Warm()
    {
        Semantics.For(Document, Configuration, CancellationToken.None);
        return this;
    }
}
