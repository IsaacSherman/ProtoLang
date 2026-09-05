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
    private readonly CompileScheduler _scheduler;

    private DiagnosticMapper _mapper = new(relatedInformationSupported: false);
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

        _scheduler = new CompileScheduler(
            _documents,
            _configuration,
            _loaders,
            _router,
            () => _mapper,
            _log,
            debounce);

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

    /// <summary>Serves until the client goes away or <c>exit</c> arrives.</summary>
    public Task RunAsync(CancellationToken cancellationToken = default)
        => _connection.RunAsync(cancellationToken);

    public void Dispose() => _connection.Dispose();

    // ------------------------------------------------------- wiring

    private void Register()
    {
        _connection.OnRequest(Methods.Initialize, (parameters, _) => Initialize(parameters));
        _connection.OnRequest(Methods.Shutdown, (_, _) => Shutdown());
        _connection.OnRequest(Methods.SemanticTokensFull, (parameters, _) => Answer<SemanticTokensParams>(parameters, Classify));

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
                    : new SemanticTokensOptions { Legend = SemanticTokenLegend.Wire },
                Workspace = new WorkspaceServerCapabilities
                {
                    WorkspaceFolders = new WorkspaceFoldersServerCapabilities(),
                },
            },
            ServerInfo = new ServerInfo("protolang-server", Version),
        });
    }

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

        return _scheduler.ForgetAsync(uri);
    }

    /// <remarks>
    /// Lexes rather than compiles, so it answers for a file that does not parse and never waits on
    /// protoc -- which is what makes it safe to run on every request. An unopened document produces an
    /// empty result rather than an error: the client may have closed it between asking and being
    /// answered.
    /// </remarks>
    private object? Classify(SemanticTokensParams message)
    {
        if (!DocumentUri.TryParse(message.TextDocument.Uri, out var uri) || _documents.Find(uri) is not { } document)
        {
            return new SemanticTokens();
        }

        return SemanticTokenEncoder.Encode(document.Text, uri.Text);
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
