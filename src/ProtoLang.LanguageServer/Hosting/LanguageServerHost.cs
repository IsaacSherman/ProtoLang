using System.Reflection;
using System.Text.Json;
using ProtoLang.LanguageServer.Protocol;
using ProtoLang.LanguageServer.Protocol.Lsp;
using ProtoLang.LanguageServer.Workspace;
using LspFolder = ProtoLang.LanguageServer.Protocol.Lsp.WorkspaceFolder;

namespace ProtoLang.LanguageServer.Hosting;

/// <summary>Where the conversation has got to.</summary>
/// <remarks>
/// LSP's lifecycle is a sequence, not a set of independent messages, and the transitions carry
/// obligations: nothing may be answered before <c>initialize</c>, nothing after <c>shutdown</c>, and
/// an <c>exit</c> that was not preceded by a <c>shutdown</c> is a failure the process reports through
/// its exit code. Stated as a state rather than as three booleans, because three booleans admit
/// combinations that mean nothing.
/// </remarks>
public enum ServerState
{
    NotInitialized,
    Running,
    ShuttingDown,
    Exited,
}

/// <summary>
/// The ProtoLang language server: one process, one client, every open document.
/// </summary>
/// <remarks>
/// <para>
/// Holds the parts and wires them to methods; the work itself is elsewhere. Documents live in
/// <see cref="DocumentStore"/>, settings in <see cref="ConfigurationSync"/> over the model spec 10.4.1
/// defines, when to compile in <see cref="CompileScheduler"/>, and who gets told what in
/// <see cref="DiagnosticRouter"/>. What is left here is the protocol: which method means which of
/// those, and what may be said when.
/// </para>
/// <para>
/// <b>Capabilities are honoured, not assumed.</b> Semantic tokens are advertised only to a client that
/// asked about them, help text is rendered one way or the other depending on what the client declared,
/// and settings are pulled only from a client that said it would answer. The alternative -- assuming
/// VS Code -- produces a server that works in one editor and fails silently in the other, which is
/// precisely what "one server, two clients" was meant to avoid.
/// </para>
/// </remarks>
public sealed class LanguageServerHost : IDisposable
{
    private readonly JsonRpcConnection _connection;
    private readonly ServerLog _log;
    private readonly DocumentStore _documents = new();
    private readonly ConfigurationSync _configuration;
    private readonly LoaderPool _loaders;
    private readonly DiagnosticRouter _router;
    private readonly DocumentSemantics _semantics;
    private readonly CompileScheduler _scheduler;
    private readonly CompletionProvider _completion;
    private readonly HoverProvider _hover;
    private readonly DefinitionProvider _definition;
    private readonly ClassificationProvider _classification;
    private readonly ReferenceProvider _references;
    private readonly HighlightProvider _highlights;
    private readonly SignatureHelpProvider _signatures;

    private DiagnosticMapper _mapper = new(relatedInformationSupported: false);

    /// <summary>Whether this client can show an outline that nests.</summary>
    /// <remarks>
    /// Volatile for the reason <see cref="DefinitionProvider.LinkSupport"/> is: negotiated once, on
    /// the worker that reads the wire, and read wherever an answer is produced.
    /// </remarks>
    /// <inheritdoc cref="SymbolInformation" path="/remarks"/>
    private volatile bool _outlineNests;

    private volatile ServerState _state = ServerState.NotInitialized;

    public LanguageServerHost(Stream input, Stream output, ServerLog? log = null, TimeSpan? debounce = null)
    {
        _log = log ?? new ServerLog();
        _connection = new JsonRpcConnection(input, output, _log);
        _log.Sink = Publish;

        _configuration = new ConfigurationSync(_connection, _log);
        _loaders = new LoaderPool(_log) { OnProtocMissing = ShowProblem };
        _router = new DiagnosticRouter(
            parameters => _connection.NotifyAsync(Methods.PublishDiagnostics, parameters),
            uri => _documents.Find(uri)?.Version);

        // One per server rather than one per component, because it is the point of it: a compile the
        // scheduler ran and a question a request asks about the same untouched buffer are the same
        // compile, and two of these would be two answers about one document with nothing keeping them
        // in agreement.
        _semantics = new DocumentSemantics(_loaders);

        _scheduler = new CompileScheduler(
            _documents,
            _configuration,
            _loaders,
            _router,
            () => _mapper,
            _log,
            debounce,
            semantics: _semantics);

        _completion = new CompletionProvider(
            _documents, _configuration, _loaders, semantics: _semantics);

        _hover = new HoverProvider(_documents, _configuration, _loaders, semantics: _semantics);
        _definition = new DefinitionProvider(_documents, _configuration, _loaders, semantics: _semantics);
        _classification = new ClassificationProvider(
            _documents, _configuration, _loaders, semantics: _semantics);
        _references = new ReferenceProvider(_documents, _configuration, _loaders, semantics: _semantics);
        _highlights = new HighlightProvider(_documents, _configuration, _loaders, semantics: _semantics);
        _signatures = new SignatureHelpProvider(
            _documents, _configuration, _loaders, semantics: _semantics);

        Register();
    }

    /// <summary>
    /// What the process should return: zero after a <c>shutdown</c>, one otherwise.
    /// </summary>
    /// <remarks>
    /// The protocol asks for exactly this. A client that exits without shutting down has abandoned the
    /// server, and a server that reported success would leave the client unable to tell an orderly
    /// stop from a crash.
    /// </remarks>
    public int ExitCode { get; private set; } = 1;

    /// <summary>The server's own view of the lifecycle, for a test and for #58.</summary>
    public ServerState State => _state;

    /// <summary>Compilations that have actually run, as opposed to been scheduled.</summary>
    public int Compilations => _scheduler.Compilations;

    /// <summary>What answers a completion request, for a test and for #58.</summary>
    /// <remarks>
    /// Published for the same reason <see cref="Compilations"/> is: the property most worth holding in
    /// place -- that this handler gives the reading worker back before it touches the file system --
    /// cannot be observed from outside without making one walk slow on purpose, and there is nowhere
    /// else to stand to do that.
    /// </remarks>
    public CompletionProvider Completion => _completion;

    /// <summary>What answers a hover request, for a test and for #58.</summary>
    /// <inheritdoc cref="Completion" path="/remarks"/>
    public HoverProvider Hover => _hover;

    /// <summary>What answers a go-to-definition request, for a test and for #58.</summary>
    /// <inheritdoc cref="Completion" path="/remarks"/>
    public DefinitionProvider Definition => _definition;

    /// <summary>What answers a semantic token request, for a test and for #58.</summary>
    /// <inheritdoc cref="Completion" path="/remarks"/>
    public ClassificationProvider Classification => _classification;

    /// <summary>What answers a find-references request, for a test and for #58.</summary>
    /// <inheritdoc cref="Completion" path="/remarks"/>
    public ReferenceProvider References => _references;

    /// <summary>What answers an occurrence-highlight request, for a test and for #58.</summary>
    /// <inheritdoc cref="Completion" path="/remarks"/>
    public HighlightProvider Highlights => _highlights;

    /// <summary>What answers a signature help request, for a test and for #58.</summary>
    /// <inheritdoc cref="Completion" path="/remarks"/>
    public SignatureHelpProvider Signatures => _signatures;

    /// <summary>What compiles a buffer for the questions asked between keystrokes, for a test and #58.</summary>
    /// <remarks>
    /// Published so that "this buffer is compiled once however many questions are asked of it" is a
    /// measurement rather than an argument, which is the same reason <see cref="Compilations"/> is
    /// published and the only way to tell a cache that is working from one that silently is not.
    /// </remarks>
    public DocumentSemantics Semantics => _semantics;

    /// <summary>Serves until the client goes away or <c>exit</c> arrives.</summary>
    public Task RunAsync(CancellationToken cancellationToken = default)
        => _connection.RunAsync(cancellationToken);

    public void Dispose() => _connection.Dispose();

    // ------------------------------------------------------- wiring

    private void Register()
    {
        _connection.OnRequest(Methods.Initialize, (parameters, _) => Initialize(parameters));
        _connection.OnRequest(Methods.Shutdown, (_, _) => Shutdown());
        _connection.OnRequest(Methods.SemanticTokensFull, Classify, concurrent: true);
        _connection.OnRequest(Methods.SemanticTokensFullDelta, ClassifyDelta, concurrent: true);
        _connection.OnRequest(Methods.Completion, Complete, concurrent: true);
        _connection.OnRequest(
            Methods.Hover,
            (parameters, token) => AtPosition(parameters, _hover.Read, _hover.AnswerAsync, token),
            concurrent: true);

        _connection.OnRequest(
            Methods.Definition,
            (parameters, token) => AtPosition(parameters, _definition.Read, _definition.AnswerAsync, token),
            concurrent: true);
        _connection.OnRequest(
            Methods.DocumentSymbol, (parameters, _) => Answer<DocumentSymbolParams>(parameters, Outline));
        _connection.OnRequest(Methods.References, FindReferences, concurrent: true);
        _connection.OnRequest(
            Methods.DocumentHighlight,
            (parameters, token) => AtPosition(parameters, _highlights.Read, _highlights.AnswerAsync, token),
            concurrent: true);
        _connection.OnRequest(
            Methods.SignatureHelp,
            (parameters, token) => AtPosition(parameters, _signatures.Read, _signatures.AnswerAsync, token),
            concurrent: true);

        _connection.OnNotification(Methods.Initialized, (_, token) => Initialized(token));
        _connection.OnNotification(Methods.Exit, (_, _) => Exit());
        _connection.OnNotification(Methods.SetTrace, (parameters, _) => SetTrace(parameters));

        _connection.OnNotification(Methods.DidOpen, (parameters, _) => Act<DidOpenTextDocumentParams>(parameters, DidOpen));
        _connection.OnNotification(Methods.DidChange, (parameters, _) => Act<DidChangeTextDocumentParams>(parameters, DidChange));
        _connection.OnNotification(Methods.DidClose, (parameters, _) => Act<DidCloseTextDocumentParams>(parameters, DidClose));
        _connection.OnNotification(Methods.DidSave, (_, _) => Task.CompletedTask);

        _connection.OnNotification(
            Methods.DidChangeConfiguration,
            (parameters, token) => Act<DidChangeConfigurationParams>(parameters, message => ConfigurationChanged(message, token)));

        _connection.OnNotification(
            Methods.DidChangeWorkspaceFolders,
            (parameters, token) => Act<DidChangeWorkspaceFoldersParams>(parameters, message => FoldersChanged(message, token)));
    }

    /// <summary>Runs a request handler, once the lifecycle allows one to run at all.</summary>
    /// <exception cref="InvalidOperationException">
    /// Turned into a JSON-RPC error by the connection. The two states are distinguishable to the
    /// client through the codes, which is what lets a client tell "too early" from "too late".
    /// </exception>
    private Task<object?> Answer<T>(JsonElement? parameters, Func<T, object?> handler)
    {
        RequireRunning();

        return Task.FromResult(handler(LspJson.Read<T>(parameters) ?? throw Missing<T>()));
    }

    /// <summary>
    /// Answers a completion: read here, in order with everything else, and walked anywhere.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The connection drains one queue with one worker, in order, and <see cref="Answer{T}"/> runs its
    /// handler on that worker -- which is right for a handler that is arithmetic over a buffer it
    /// already has, and wrong for one that opens a directory. A completion answered inline holds the
    /// worker for the length of the walk, and behind it sit every <c>didChange</c>, every
    /// <c>didClose</c>, and the <c>$/cancelRequest</c> that would have shortened it. On an include
    /// path that is a network mount, that is the buffer ceasing to sync while the user types.
    /// <c>CompileScheduler.Schedule</c> was changed for the same reason and says so.
    /// </para>
    /// <para>
    /// <b>What may not move off this worker is deciding which buffer the request is about.</b>
    /// <c>CompletionProvider.Read</c> is called here, before this method returns and therefore before
    /// the next message is dequeued, precisely so that a <c>didChange</c> queued behind the request
    /// cannot be applied first. Deferring it would leave the position measured against text the client
    /// had not sent when it asked -- and every staleness check afterwards would agree, because they
    /// would all be asking about the same wrong document.
    /// </para>
    /// <para>
    /// The lifecycle check and the deserialization stay here for the same reason they always did: both
    /// are cheap, and a request that arrived too early or carried nothing is refused rather than
    /// scheduled. What leaves is the walk, and it takes the request's own token with it.
    /// </para>
    /// </remarks>
    private async Task<object?> Complete(JsonElement? parameters, CancellationToken cancellationToken)
    {
        RequireRunning();

        var message = LspJson.Read<CompletionParams>(parameters) ?? throw Missing<CompletionParams>();

        if (_completion.Read(message) is not { } asked)
        {
            return CompletionProvider.Nothing;
        }

        return await _completion.AnswerAsync(asked, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Answers a request about one caret: read here, in order with everything else, and produced
    /// anywhere.
    /// </summary>
    /// <remarks>
    /// One method for hover and go-to-definition, and for whatever #51 adds beside them, because
    /// what differs between them is which provider answers and what does not differ is the part
    /// that must not move: <paramref name="read"/> runs before this returns and therefore before the
    /// next message is dequeued. See <see cref="Complete"/> for why, at length.
    /// </remarks>
    private async Task<object?> AtPosition<T>(
        JsonElement? parameters,
        Func<TextDocumentPositionParams, PositionRequest?> read,
        Func<PositionRequest, CancellationToken, Task<T>> answer,
        CancellationToken cancellationToken)
    {
        RequireRunning();

        var message = LspJson.Read<TextDocumentPositionParams>(parameters)
            ?? throw Missing<TextDocumentPositionParams>();

        return read(message) is not { } asked
            ? null
            : await answer(asked, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs a notification handler, dropping the message when it arrives out of turn.</summary>
    /// <remarks>
    /// Dropped rather than refused: a notification has no response to carry a complaint, and the
    /// protocol says a server should ignore anything but <c>exit</c> once it is shutting down. A
    /// message that will not deserialize is dropped for the same reason and logged.
    /// </remarks>
    private Task Act<T>(JsonElement? parameters, Func<T, Task> handler)
    {
        if (_state is not ServerState.Running)
        {
            _log.Trace($"Ignoring a notification received while the server is {_state}.");
            return Task.CompletedTask;
        }

        if (LspJson.Read<T>(parameters) is not { } message)
        {
            _log.Warning($"A {typeof(T).Name} notification carried no parameters and was dropped.");
            return Task.CompletedTask;
        }

        return handler(message);
    }

    private void RequireRunning()
    {
        if (_state is ServerState.NotInitialized)
        {
            throw new JsonRpcException(
                new ResponseError(ErrorCodes.ServerNotInitialized, "This server has not been initialized yet."));
        }

        if (_state is not ServerState.Running)
        {
            throw new JsonRpcException(
                new ResponseError(ErrorCodes.InvalidRequest, "This server is shutting down and is not answering."));
        }
    }

    private static JsonRpcException Missing<T>()
        => new(new ResponseError(ErrorCodes.InvalidParams, $"A {typeof(T).Name} request carried no parameters."));

    // ------------------------------------------------------- lifecycle

    private Task<object?> Initialize(JsonElement? parameters)
    {
        if (_state is not ServerState.NotInitialized)
        {
            throw new JsonRpcException(
                new ResponseError(ErrorCodes.InvalidRequest, "This server has already been initialized."));
        }

        var message = LspJson.Read<InitializeParams>(parameters) ?? new InitializeParams();
        var capabilities = message.Capabilities;

        _mapper = new DiagnosticMapper(capabilities?.TextDocument?.PublishDiagnostics?.RelatedInformation is true);

        // Only when the client says something. A client that omits trace has stated no preference, and
        // taking the default here would quietly undo a --log-level given on the command line -- which
        // is the one thing somebody debugging a broken session has reached for.
        if (message.Trace is { Length: > 0 } trace)
        {
            _log.Level = TraceLevel.Parse(trace);
        }

        // Asked once and used twice below -- what is retained and what is advertised are two halves of
        // one promise, and a server that retains an answer it did not offer to send, or offers one it
        // is not keeping, has broken the half nobody is looking at.
        var deltas = WantsDeltas(capabilities);

        _definition.LinkSupport = capabilities?.TextDocument?.Definition?.LinkSupport is true;
        _classification.Client = ClientLegend.Of(capabilities?.TextDocument?.SemanticTokens);
        _classification.Deltas = deltas;
        _outlineNests = capabilities?.TextDocument?.DocumentSymbol?.HierarchicalDocumentSymbolSupport is true;

        _configuration.Negotiate(capabilities);
        _configuration.SetFolders(FoldersOf(message));

        RequireUtf16(capabilities);

        _state = ServerState.Running;

        return Task.FromResult<object?>(new InitializeResult
        {
            Capabilities = new ServerCapabilities
            {
                TextDocumentSync = new TextDocumentSyncOptions { Save = new SaveOptions() },
                SemanticTokensProvider = capabilities?.TextDocument?.SemanticTokens is null
                    ? null
                    : new SemanticTokensOptions
                    {
                        Legend = SemanticTokenLegend.Wire,
                        Full = new SemanticTokensFullOptions { Delta = deltas },
                    },
                CompletionProvider = capabilities?.TextDocument?.Completion is null
                    ? null
                    : new CompletionOptions { TriggerCharacters = CompletionProvider.TriggerCharacters },
                HoverProvider = capabilities?.TextDocument?.Hover is null ? null : true,
                DefinitionProvider = capabilities?.TextDocument?.Definition is null ? null : true,
                DocumentSymbolProvider = capabilities?.TextDocument?.DocumentSymbol is null ? null : true,
                ReferencesProvider = capabilities?.TextDocument?.References is null ? null : true,
                DocumentHighlightProvider =
                    capabilities?.TextDocument?.DocumentHighlight is null ? null : true,
                SignatureHelpProvider = capabilities?.TextDocument?.SignatureHelp is null
                    ? null
                    : new SignatureHelpOptions
                    {
                        TriggerCharacters = SignatureHelpProvider.TriggerCharacters,
                        RetriggerCharacters = SignatureHelpProvider.RetriggerCharacters,
                    },
                Workspace = new WorkspaceServerCapabilities
                {
                    WorkspaceFolders = new WorkspaceFoldersServerCapabilities(),
                },
            },
            ServerInfo = new ServerInfo("protolang-server", Version),
        });
    }

    /// <summary>
    /// Everywhere a name is used: read here, in order, and produced anywhere.
    /// </summary>
    /// <remarks>
    /// Its own handler rather than <see cref="AtPosition"/>, because the params carry a caret
    /// <em>and</em> whether the declaration belongs in the answer. What may not move off this worker
    /// is unchanged and is the reason the shape exists at all: which buffer the request is about is
    /// settled before this returns. See <see cref="Complete"/> for why, at length.
    /// </remarks>
    private async Task<object?> FindReferences(
        JsonElement? parameters, CancellationToken cancellationToken)
    {
        RequireRunning();

        var message = LspJson.Read<ReferenceParams>(parameters) ?? throw Missing<ReferenceParams>();

        return _references.Read(message) is not { } asked
            ? null
            : await _references
                .AnswerAsync(asked, message.Context.IncludeDeclaration, cancellationToken)
                .ConfigureAwait(false);
    }

    /// <summary>Whether this client asked to be sent differences rather than whole answers.</summary>
    /// <remarks>
    /// Offered only where it was asked for. LSP spells the request two ways -- a bare <c>true</c>
    /// means whole answers only -- and <see cref="SemanticTokensFullRequest"/> is where the two
    /// spellings become one. Advertising a delta to a client that will never ask for one promises
    /// something nobody collects, and pays for the promise with a retained answer per open document.
    /// </remarks>
    private static bool WantsDeltas(ClientCapabilities? capabilities)
        => capabilities?.TextDocument?.SemanticTokens?.Requests?.Full?.Delta is true;

    /// <summary>
    /// The folders the client opened, taking <c>rootUri</c> as one when it named no folders at all.
    /// </summary>
    /// <remarks>
    /// <c>rootUri</c> is deprecated and still the only thing an older client sends. Ignoring it would
    /// leave such a client with no folder scope and no directory to resolve a relative include path
    /// against, which is spec 10.4.1's worst case rather than its ordinary one.
    /// </remarks>
    private static IEnumerable<LspFolder> FoldersOf(InitializeParams message)
    {
        if (message.WorkspaceFolders is { Count: > 0 } folders)
        {
            return folders;
        }

        return message.RootUri is { Length: > 0 } root ? [new LspFolder { Uri = root }] : [];
    }

    /// <remarks>
    /// Every range this server produces counts UTF-16 code units, which is LSP's default and what
    /// <see cref="Diagnostics.SourcePosition"/> already measures in. A client that offers a list
    /// without it would need every column converted; saying so in the log beats shifting every squiggle
    /// on any line holding an astral character and never mentioning it.
    /// </remarks>
    private void RequireUtf16(ClientCapabilities? capabilities)
    {
        if (capabilities?.General?.PositionEncodings is not { Count: > 0 } encodings)
        {
            return;
        }

        if (!encodings.Contains("utf-16", StringComparer.OrdinalIgnoreCase))
        {
            _log.Warning(
                $"This client accepts the position encodings {string.Join(", ", encodings)} and this server "
                    + "produces utf-16, which is the protocol's default. Columns on lines containing "
                    + "characters outside the basic plane may be off.");
        }
    }

    private async Task Initialized(CancellationToken cancellationToken)
    {
        if (_state is not ServerState.Running)
        {
            return;
        }

        await _configuration.PullAsync(cancellationToken).ConfigureAwait(false);

        _log.Info($"protolang-server {Version} is ready.");
    }

    private Task<object?> Shutdown()
    {
        RequireRunning();

        _state = ServerState.ShuttingDown;
        _log.Info("Shutting down.");

        return Task.FromResult<object?>(null);
    }

    /// <remarks>
    /// The result is a present null rather than an absent one, which
    /// <see cref="ResponseMessage"/> takes care of; a client that sees no <c>result</c> member at all
    /// is entitled to treat the response as malformed.
    /// </remarks>
    private Task Exit()
    {
        // The first exit settles the verdict. A second one -- a client that sends it twice, or a
        // shutdown script racing the editor -- must not turn a clean stop into a reported failure.
        if (_state is not ServerState.Exited)
        {
            ExitCode = _state is ServerState.ShuttingDown ? 0 : 1;
            _state = ServerState.Exited;
        }

        _connection.Stop();

        return Task.CompletedTask;
    }

    private Task SetTrace(JsonElement? parameters)
    {
        if (LspJson.Read<SetTraceParams>(parameters) is { } message)
        {
            _log.Level = TraceLevel.Parse(message.Value);
        }

        return Task.CompletedTask;
    }

    // ------------------------------------------------------- documents

    private Task DidOpen(DidOpenTextDocumentParams message)
    {
        if (!DocumentUri.TryParse(message.TextDocument.Uri, out var uri))
        {
            _log.Warning($"Ignoring an opened document named '{message.TextDocument.Uri}', which is not a usable URI.");
            return Task.CompletedTask;
        }

        _documents.Open(uri, message.TextDocument.LanguageId, message.TextDocument.Version, message.TextDocument.Text);
        _scheduler.Schedule(uri);

        return Task.CompletedTask;
    }

    private Task DidChange(DidChangeTextDocumentParams message)
    {
        if (!DocumentUri.TryParse(message.TextDocument.Uri, out var uri))
        {
            return Task.CompletedTask;
        }

        if (_documents.Apply(uri, message.TextDocument.Version, message.ContentChanges) is null)
        {
            _log.Warning($"'{uri}' was changed before it was opened, so the change was dropped.");
            return Task.CompletedTask;
        }

        _scheduler.Schedule(uri);

        return Task.CompletedTask;
    }

    private Task DidClose(DidCloseTextDocumentParams message)
    {
        if (!DocumentUri.TryParse(message.TextDocument.Uri, out var uri))
        {
            return Task.CompletedTask;
        }

        _documents.Close(uri);

        // Every kind of outstanding work for this document, not just the compile. Each of these can
        // be queued behind a slow one for as long as that one takes, and nothing else would ever
        // tell it the buffer it describes has gone.
        _completion.Forget(uri);
        _hover.Forget(uri);
        _definition.Forget(uri);
        _classification.Forget(uri);
        _references.Forget(uri);
        _highlights.Forget(uri);
        _signatures.Forget(uri);

        // And what was remembered about it. Every question comes through the store, so once the
        // document is closed nothing can ask -- and an entry nothing can ask for is a syntax tree and
        // an IR module held until the process exits.
        _semantics.Forget(uri);

        return _scheduler.ForgetAsync(uri);
    }

    /// <summary>The whole document's classification: read here, in order, and produced anywhere.</summary>
    /// <remarks>
    /// <para>
    /// <b>This handler used to be free and is not any more.</b> #42 answered it from the lexer on this
    /// worker, in the same instant it was read, and the remark that used to sit here said what such a
    /// handler owes: nothing, because the read and the answer were one moment. Refining an identifier
    /// needs the binder, so classification now leaves the process exactly as hover, completion and
    /// go-to-definition do, and owes what they owe -- which is <see cref="DeferredAnswers"/>'s to
    /// state and <see cref="ClassificationProvider"/>'s to obey.
    /// </para>
    /// <para>
    /// What may not move off this worker is deciding which buffer is being classified.
    /// <see cref="ClassificationProvider.Read(SemanticTokensParams)"/> is called before this returns,
    /// for the reason <see cref="Complete"/> gives at length.
    /// </para>
    /// </remarks>
    private async Task<object?> Classify(JsonElement? parameters, CancellationToken cancellationToken)
    {
        RequireRunning();

        var message = LspJson.Read<SemanticTokensParams>(parameters)
            ?? throw Missing<SemanticTokensParams>();

        return await Classified(_classification.Read(message), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The difference from the classification the client says it is holding.</summary>
    /// <inheritdoc cref="Classify" path="/remarks"/>
    private async Task<object?> ClassifyDelta(JsonElement? parameters, CancellationToken cancellationToken)
    {
        RequireRunning();

        var message = LspJson.Read<SemanticTokensDeltaParams>(parameters)
            ?? throw Missing<SemanticTokensDeltaParams>();

        return await Classified(_classification.Read(message), cancellationToken).ConfigureAwait(false);
    }

    /// <remarks>
    /// An unopened document produces an empty classification rather than an error, and rather than
    /// null: the client may have closed it between asking and being answered, it has done nothing
    /// wrong, and a document with no tokens is the truth about one this server does not hold. That is
    /// what #42 answered here and it has not changed.
    /// </remarks>
    private async Task<object?> Classified(
        ClassificationRequest? asked, CancellationToken cancellationToken)
        => asked is null
            ? new SemanticTokens()
            : await _classification.AnswerAsync(asked, cancellationToken).ConfigureAwait(false);

    /// <summary>The file's declarations, as far as it parses.</summary>
    /// <remarks>
    /// <para>
    /// Parsed rather than compiled, so it answers for a file that does not parse and never waits on
    /// protoc -- which is what makes it safe to run on the ordered worker beside
    /// <see cref="Classify"/>, and what keeps the outline from vanishing at exactly the moment
    /// somebody is navigating a file they are halfway through editing. An unopened document produces
    /// an empty outline rather than an error: the client may have closed it between asking and being
    /// answered.
    /// </para>
    /// <para>
    /// Nothing is refused here because nothing can be: the read and the answer are the same instant,
    /// which is the property <see cref="Classify"/> states at length and this one shares.
    /// </para>
    /// </remarks>
    private object? Outline(DocumentSymbolParams message)
    {
        if (!DocumentUri.TryParse(message.TextDocument.Uri, out var uri)
            || _documents.Find(uri) is not { } document)
        {
            return Array.Empty<DocumentSymbol>();
        }

        var outline = DocumentOutline.Of(document.Text);

        return _outlineNests ? outline : DocumentOutline.Flattened(outline, uri!.ToString());
    }

    // ------------------------------------------------------- configuration

    /// <remarks>
    /// Everything open is recompiled, because a changed include path or protoc changes what every
    /// document means. Nothing has to be restarted and nothing is cached across the change: the
    /// configuration is immutable, a change makes a new one with a higher generation, and work already
    /// running under the old one is discarded when it finishes.
    /// </remarks>
    private async Task ConfigurationChanged(DidChangeConfigurationParams message, CancellationToken cancellationToken)
    {
        if (_configuration.CanPull)
        {
            await _configuration.PullAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _configuration.ApplyPush(message.Settings);
        }

        _scheduler.ScheduleAll();
    }

    private async Task FoldersChanged(DidChangeWorkspaceFoldersParams message, CancellationToken cancellationToken)
    {
        _configuration.ChangeFolders(message.Event);

        await _configuration.PullAsync(cancellationToken).ConfigureAwait(false);

        _scheduler.ScheduleAll();
    }

    // ------------------------------------------------------- talking to the client

    private void Publish(LogLevel level, string message)
        => _ = _connection.NotifyAsync(Methods.LogMessage, new LogMessageParams((int)level, message));

    /// <remarks>
    /// A message box, not a log line, and only for something the user cannot otherwise discover.
    /// protoc missing means nothing in the workspace can be compiled at all, and the diagnostics that
    /// would have said so are the ones that are not being produced.
    /// </remarks>
    private void ShowProblem(string message)
        => _ = _connection.NotifyAsync(Methods.ShowMessage, new ShowMessageParams((int)LogLevel.Error, message));

    private static string Version { get; } =
        typeof(LanguageServerHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "0.0.0";
}
