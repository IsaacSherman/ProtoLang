using System.Text.Json;
using ProtoLang.Diagnostics;
using ProtoLang.LanguageServer.Protocol;
using ProtoLang.LanguageServer.Protocol.Lsp;
using ProtoLang.LanguageServer.Workspace;
using Diagnostic = ProtoLang.Diagnostics.Diagnostic;
using LspFolder = ProtoLang.LanguageServer.Protocol.Lsp.WorkspaceFolder;

namespace ProtoLang.LanguageServer.Hosting;

/// <summary>
/// Keeps the workspace configuration in step with what the client has been told, and reports what it
/// will not use.
/// </summary>
/// <remarks>
/// <para>
/// Spec 10.4.1 is the model; this is the wire. What it adds is the awkward part of the protocol:
/// <c>workspace/configuration</c> returns the value a client has <em>already merged</em> across user,
/// workspace and folder scope, so the three are not separable from here. A folder-scoped answer
/// therefore becomes this server's folder scope, the unscoped answer becomes its workspace scope, and
/// user scope stays empty -- not because it does not exist, but because the client has already applied
/// it and asking again would count the same value twice. The precedence in
/// <see cref="WorkspaceConfiguration"/> is unchanged and still decides between the two scopes a server
/// can actually see.
/// </para>
/// <para>
/// Both directions of the protocol are handled. A client that supports pulling gets asked; a client
/// that only pushes sends its settings tree on <c>workspace/didChangeConfiguration</c>, and refusing
/// that would leave a whole class of clients unable to change a setting at all.
/// </para>
/// </remarks>
public sealed class ConfigurationSync(JsonRpcConnection connection, ServerLog log)
{
    /// <summary>
    /// The key a client uses for its own tracing preference, which is not a compile setting.
    /// </summary>
    /// <remarks>
    /// Handled here rather than passed to <see cref="ProtoLangSettings.Read"/>, which would report it
    /// as a setting this server does not understand. <c>protolang.trace.server</c> is the conventional
    /// spelling an extension declares for exactly this, and warning about a setting the editor's own
    /// template told the user to write is the sort of false alarm that teaches people to ignore
    /// warnings.
    /// </remarks>
    private const string TraceKey = "trace";

    private readonly Lock _gate = new();

    private WorkspaceConfiguration _configuration = WorkspaceConfiguration.Empty;
    private IReadOnlyList<Diagnostic> _settingsDiagnostics = [];
    private bool _clientAnswersConfiguration;

    /// <summary>What the server currently believes about the workspace.</summary>
    public WorkspaceConfiguration Current
    {
        get
        {
            lock (_gate)
            {
                return _configuration;
            }
        }
    }

    /// <summary>
    /// What is wrong with the settings themselves, as opposed to with any one document.
    /// </summary>
    /// <remarks>
    /// <c>PL2101</c> and <c>PL2102</c> are produced when settings are read, once per scope, not once
    /// per document -- but a setting being ignored is precisely the thing a user has to be able to see,
    /// and a line in a log channel nobody opens is not seeing it. The host publishes these against
    /// every open document, alongside the per-document diagnostics that
    /// <see cref="WorkspaceConfiguration.Resolve"/> produces. They really do affect every document,
    /// and the identical ones collapse on the way out.
    /// </remarks>
    public IReadOnlyList<Diagnostic> SettingsDiagnostics
    {
        get
        {
            lock (_gate)
            {
                return _settingsDiagnostics;
            }
        }
    }

    /// <summary>Whether the client said it would answer <c>workspace/configuration</c>.</summary>
    public bool CanPull => _clientAnswersConfiguration;

    /// <summary>Reads what the client can do, so nothing is assumed of it later.</summary>
    public void Negotiate(ClientCapabilities? capabilities)
        => _clientAnswersConfiguration = capabilities?.Workspace?.Configuration is true;

    /// <summary>Replaces the open folders with the ones the client named.</summary>
    public void SetFolders(IEnumerable<LspFolder>? folders)
    {
        var replacement = Convert(folders);

        lock (_gate)
        {
            _configuration = _configuration.WithFolders(replacement);
        }
    }

    /// <summary>Adds and removes folders as the client reports them.</summary>
    /// <remarks>
    /// Settings for a removed folder go with it, because they live on the folder. A parallel map would
    /// be how a server comes to resolve a document against settings for a folder nobody has open.
    /// </remarks>
    public void ChangeFolders(WorkspaceFoldersChangeEvent change)
    {
        ArgumentNullException.ThrowIfNull(change);

        var removed = Convert(change.Removed).Select(folder => folder.Key).ToHashSet(StringComparer.Ordinal);
        var added = Convert(change.Added);

        lock (_gate)
        {
            var kept = _configuration.Folders.Where(folder => !removed.Contains(folder.Key));

            _configuration = _configuration.WithFolders([.. kept, .. added]);
        }
    }

    /// <summary>Asks the client for its settings, one question per folder plus one unscoped.</summary>
    /// <remarks>
    /// Awaited from a handler, which is safe: the connection reads answers on a different path from
    /// the one that runs handlers, precisely so that a server may ask a client a question.
    /// </remarks>
    public async Task PullAsync(CancellationToken cancellationToken)
    {
        if (!_clientAnswersConfiguration)
        {
            return;
        }

        var folders = Current.Folders;

        var items = new List<ConfigurationItem>(folders.Count + 1);
        items.AddRange(
            folders.Select(folder => new ConfigurationItem { ScopeUri = folder.Uri.Text, Section = ProtoLangSettings.Section }));
        items.Add(new ConfigurationItem { Section = ProtoLangSettings.Section });

        IReadOnlyList<JsonElement>? answers;

        try
        {
            answers = await connection
                .RequestAsync<IReadOnlyList<JsonElement>>(Methods.Configuration, new ConfigurationParams { Items = items }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonRpcException ex)
        {
            log.Warning("The client refused to supply its settings, so the previous ones stay in force.", ex);
            return;
        }

        if (answers is null || answers.Count != items.Count)
        {
            log.Warning(
                $"The client answered {answers?.Count ?? 0} of {items.Count} settings scopes, so the "
                    + "previous settings stay in force.");
            return;
        }

        Apply(folders, answers);
    }

    /// <summary>Takes settings a client pushed instead of waiting to be asked.</summary>
    public void ApplyPush(JsonElement? settings)
    {
        if (settings is not { ValueKind: JsonValueKind.Object } tree)
        {
            return;
        }

        // Either the protolang section on its own, or a whole settings tree with it inside.
        var section = tree.TryGetProperty(ProtoLangSettings.Section, out var nested) ? nested : tree;

        var diagnostics = new DiagnosticBag();
        var workspace = ReadScope(section, ConfigurationSource.WorkspaceSetting, diagnostics);

        lock (_gate)
        {
            _configuration = _configuration.WithWorkspaceSettings(workspace);
            _settingsDiagnostics = [.. diagnostics];
        }

        Report(diagnostics);
    }

    private void Apply(IReadOnlyList<Workspace.WorkspaceFolder> folders, IReadOnlyList<JsonElement> answers)
    {
        var diagnostics = new DiagnosticBag();

        var settled = new List<Workspace.WorkspaceFolder>(folders.Count);
        for (var index = 0; index < folders.Count; index++)
        {
            settled.Add(folders[index] with
            {
                Settings = ReadScope(answers[index], ConfigurationSource.FolderSetting, diagnostics),
            });
        }

        var workspace = ReadScope(answers[^1], ConfigurationSource.WorkspaceSetting, diagnostics);

        lock (_gate)
        {
            _configuration = _configuration.WithFolders(settled).WithWorkspaceSettings(workspace);
            _settingsDiagnostics = [.. diagnostics];
        }

        Report(diagnostics);
    }

    private void Report(DiagnosticBag diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            log.Warning($"{diagnostic.Code}: {diagnostic.Message}");
        }
    }

    /// <summary>Reads one scope's settings out of the JSON a client sent for it.</summary>
    private ProtoLangSettings ReadScope(JsonElement element, ConfigurationSource scope, DiagnosticBag diagnostics)
    {
        if (element.ValueKind is not JsonValueKind.Object)
        {
            return ProtoLangSettings.None;
        }

        var values = new List<SettingValue>();

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, TraceKey, StringComparison.OrdinalIgnoreCase))
            {
                ApplyTrace(property.Value);
                continue;
            }

            values.Add(new SettingValue(property.Name, Strings(property.Value)));
        }

        return ProtoLangSettings.Read(values, scope, diagnostics);
    }

    /// <summary>
    /// Flattens one setting's JSON to the strings the model reads.
    /// </summary>
    /// <remarks>
    /// A list stays a list and everything else becomes one value, including a number or a boolean
    /// written where a string belongs. Turning those into their text rather than dropping them is what
    /// lets the model say the setting is being ignored: silently discarding a value of the wrong type
    /// is the failure spec 10.4.1 exists to prevent. A JSON null is the same as absent -- an editor
    /// clears a setting by writing one -- and becomes a blank, which the model already treats as
    /// stating nothing.
    /// </remarks>
    private static IReadOnlyList<string> Strings(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Array => [.. value.EnumerateArray().Select(item => item.ValueKind switch
        {
            JsonValueKind.String => item.GetString() ?? string.Empty,
            JsonValueKind.Null => string.Empty,
            _ => item.GetRawText(),
        })],
        JsonValueKind.String => [value.GetString() ?? string.Empty],
        JsonValueKind.Null or JsonValueKind.Undefined => [string.Empty],
        _ => [value.GetRawText()],
    };

    /// <remarks>
    /// Accepts both the flat <c>trace</c> and the nested <c>trace.server</c> a client template writes.
    /// </remarks>
    private void ApplyTrace(JsonElement value)
    {
        var stated = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Object when value.TryGetProperty("server", out var server) => server.GetString(),
            _ => null,
        };

        if (stated is not null)
        {
            log.Level = TraceLevel.Parse(stated);
        }
    }

    /// <remarks>
    /// A folder that is not a directory on disk is skipped rather than refused: an editor can open a
    /// virtual workspace over a scheme with no file system behind it, the compiler reads schemas from
    /// disk, and a folder it cannot walk resolves nothing. Saying so once is more use than failing
    /// <c>initialize</c>.
    /// </remarks>
    private List<Workspace.WorkspaceFolder> Convert(IEnumerable<LspFolder>? folders)
    {
        var converted = new List<Workspace.WorkspaceFolder>();

        foreach (var folder in folders ?? [])
        {
            if (!DocumentUri.TryParse(folder.Uri, out var uri) || !uri.IsFile)
            {
                log.Warning($"Ignoring the workspace folder '{folder.Uri}', which is not a directory on disk.");
                continue;
            }

            converted.Add(new Workspace.WorkspaceFolder(uri, folder.Name));
        }

        return converted;
    }
}

/// <summary>The three values LSP's trace setting takes, as a log level.</summary>
public static class TraceLevel
{
    public static LogLevel Parse(string? value) => value switch
    {
        "verbose" => LogLevel.Trace,
        "messages" => LogLevel.Info,
        "off" => LogLevel.Error,
        _ => LogLevel.Info,
    };
}
