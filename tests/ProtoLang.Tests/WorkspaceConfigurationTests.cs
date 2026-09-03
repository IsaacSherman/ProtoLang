using Google.Protobuf.Reflection;
using ProtoLang.Binding;
using ProtoLang.Config;
using ProtoLang.Diagnostics;
using ProtoLang.LanguageServer.Workspace;
using Xunit;

namespace ProtoLang.Tests;

/// <summary>
/// The workspace configuration model: which document a URI names, which folder it belongs to, and
/// which of the several places a setting can be written wins (spec 10.4.1).
/// </summary>
/// <remarks>
/// The properties worth holding onto are that one file is one document however it is spelled, that
/// precedence is the documented order and not an emergent one, and that anything the user wrote which
/// is not being used says so. The last is not a nicety: a setting silently ignored leaves a user
/// unable to tell a typo from a refusal from a bug.
/// </remarks>
public class WorkspaceConfigurationTests
{
    private static string TempDirectory(string label = "workspace")
    {
        var directory = Path.Combine(Path.GetTempPath(), "protolang-" + label, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string TempFile(string directory, string name, string content = "")
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static ProtoLangSettings Read(out DiagnosticBag diagnostics, params SettingValue[] values)
    {
        diagnostics = new DiagnosticBag();
        return ProtoLangSettings.Read(values, ConfigurationSource.WorkspaceSetting, diagnostics);
    }

    private static WorkspaceConfiguration Workspace(params WorkspaceFolder[] folders)
        => WorkspaceConfiguration.Empty with
        {
            Folders = folders,
            ReadEnvironmentVariable = _ => null,
        };

    private static DocumentUri Document(string directory, string name = "source.protolang")
        => DocumentUri.FromPath(Path.Combine(directory, name));

    private static void RequireCaseInsensitivePaths()
    {
        if (PathIdentity.IsCaseSensitive)
        {
            Assert.Skip("Paths are case-sensitive here, so two casings genuinely are two files.");
        }
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Drive letters and backslashes are a Windows spelling.");
        }
    }

    private const string CheckedOverflow = """
        <?xml version="1.0" encoding="utf-8"?>
        <ProtoLang>
          <Arithmetic>
            <Overflow>Checked</Overflow>
          </Arithmetic>
        </ProtoLang>
        """;

    private const string UnreadableConfig = """
        <?xml version="1.0" encoding="utf-8"?>
        <ProtoLang>
          <Arithmetic>
            <Overflow>Sideways</Overflow>
          </Arithmetic>
        </ProtoLang>
        """;

    // ---------------------------------------------------------------- one file, one document

    [Fact]
    public void APathAndItsFileUriAreOneDocument()
    {
        var path = Path.Combine(TempDirectory(), "source.protolang");

        Assert.Equal(DocumentUri.FromPath(path), DocumentUri.Parse(new Uri(path).AbsoluteUri));
    }

    [Fact]
    public void APercentEncodedSpaceDoesNotMakeASecondDocument()
    {
        var directory = Directory.CreateDirectory(Path.Combine(TempDirectory(), "has space")).FullName;
        var path = Path.Combine(directory, "source.protolang");

        Assert.Equal(DocumentUri.FromPath(path), DocumentUri.Parse(new Uri(path).AbsoluteUri));
    }

    [Fact]
    public void ARedundantSegmentDoesNotMakeASecondDocument()
    {
        var directory = TempDirectory();
        var straight = Path.Combine(directory, "source.protolang");
        var roundabout = Path.Combine(directory, ".", "source.protolang");

        Assert.Equal(DocumentUri.FromPath(straight), DocumentUri.FromPath(roundabout));
    }

    [Fact]
    public void ATrailingSeparatorDoesNotMakeASecondFolder()
    {
        var directory = TempDirectory();

        Assert.Equal(
            WorkspaceFolder.FromPath(directory).Key,
            WorkspaceFolder.FromPath(directory + Path.DirectorySeparatorChar).Key);
    }

    [Fact]
    public void ADriveLetterOfEitherCaseNamesOneDocument()
    {
        RequireCaseInsensitivePaths();

        var path = Path.Combine(TempDirectory(), "source.protolang");
        var shouted = path.ToUpperInvariant();

        Assert.Equal(DocumentUri.FromPath(path), DocumentUri.FromPath(shouted));
    }

    [Fact]
    public void APercentEncodedDriveColonNamesTheSameFileAsAPlainOne()
    {
        RequireWindows();

        var plain = DocumentUri.Parse("file:///C:/src/app/source.protolang");
        var encoded = DocumentUri.Parse("file:///C%3A/src/app/source.protolang");

        Assert.Equal(plain.Path, encoded.Path);
        Assert.Equal(plain, encoded);
    }

    [Fact]
    public void AForwardSlashedWindowsPathIsTheSameDocumentAsABackslashedOne()
    {
        RequireWindows();

        Assert.Equal(
            DocumentUri.FromPath(@"C:\src\app\source.protolang"),
            DocumentUri.FromPath("C:/src/app/source.protolang"));
    }

    [Fact]
    public void AUncFileUriAndAUncPathAreOneDocument()
    {
        RequireWindows();

        Assert.Equal(
            DocumentUri.FromPath(@"\\server\share\source.protolang"),
            DocumentUri.Parse("file://server/share/source.protolang"));
    }

    [Fact]
    public void ADocumentKeepsTheUriTheClientSent()
    {
        const string sent = "file:///C%3A/src/app/source.protolang";

        Assert.Equal(sent, DocumentUri.Parse(sent).Text);
    }

    [Fact]
    public void AnUntitledBufferIsADocumentWithNoPath()
    {
        var untitled = DocumentUri.Parse("untitled:Untitled-1");

        Assert.False(untitled.IsFile);
        Assert.Null(untitled.Path);
        Assert.Null(untitled.Directory);
        Assert.Equal("untitled", untitled.Scheme);
    }

    [Fact]
    public void TwoUntitledBuffersAreTwoDocuments()
        => Assert.NotEqual(DocumentUri.Parse("untitled:Untitled-1"), DocumentUri.Parse("untitled:Untitled-2"));

    [Fact]
    public void TextThatNamesNeitherAUriNorAPathIsRefused()
    {
        Assert.False(DocumentUri.TryParse("source.protolang", out _));
        Assert.False(DocumentUri.TryParse("   ", out _));
        Assert.False(DocumentUri.TryParse(null, out _));
    }

    /// <summary>
    /// A client can send anything, and reading a document name is not a request the server may fail:
    /// a Try method that throws is one every caller has to wrap, and the one that forgets takes the
    /// session down over a string somebody typed.
    /// </summary>
    [Fact]
    public void APathThisPlatformWillNotAcceptIsRefusedRatherThanThrown()
    {
        RequireWindows();

        Assert.False(DocumentUri.TryParse(@"C:\holds" + '\u0000' + "a nul", out _));
        Assert.False(DocumentUri.TryParse("C:\\" + new string('x', 70_000), out _));
    }

    // ---------------------------------------------------------------- which folder a document is in

    [Fact]
    public void TheInnermostFolderWinsWhenFoldersNest()
    {
        var outer = TempDirectory();
        var inner = Directory.CreateDirectory(Path.Combine(outer, "nested")).FullName;

        var workspace = Workspace(WorkspaceFolder.FromPath(outer), WorkspaceFolder.FromPath(inner));

        Assert.Equal(inner, workspace.FolderFor(Document(inner))?.Path);
        Assert.Equal(outer, workspace.FolderFor(Document(outer))?.Path);
    }

    [Fact]
    public void AFolderDoesNotHoldASiblingWhoseNameItIsAPrefixOf()
    {
        var root = TempDirectory();
        var app = Directory.CreateDirectory(Path.Combine(root, "app")).FullName;
        var appx = Directory.CreateDirectory(Path.Combine(root, "appx")).FullName;

        Assert.False(WorkspaceFolder.FromPath(app).Contains(Document(appx)));
    }

    [Fact]
    public void ADocumentOutsideEveryFolderBelongsToNone()
    {
        var workspace = Workspace(WorkspaceFolder.FromPath(TempDirectory()));

        Assert.Null(workspace.FolderFor(Document(TempDirectory())));
    }

    [Fact]
    public void AnUntitledBufferInheritsTheOnlyFolder()
    {
        var only = TempDirectory();

        Assert.Equal(
            only,
            Workspace(WorkspaceFolder.FromPath(only)).FolderFor(DocumentUri.Parse("untitled:Untitled-1"))?.Path);
    }

    [Fact]
    public void AnUntitledBufferInheritsNoFolderWhenSeveralAreOpen()
    {
        var workspace = Workspace(
            WorkspaceFolder.FromPath(TempDirectory()),
            WorkspaceFolder.FromPath(TempDirectory()));

        Assert.Null(workspace.FolderFor(DocumentUri.Parse("untitled:Untitled-1")));
    }

    [Fact]
    public void TwoDocumentsInDifferentFoldersResolveDifferentFolderSettings()
    {
        var first = TempDirectory("first-folder");
        var second = TempDirectory("second-folder");
        var firstProtoc = TempFile(first, "protoc-one");
        var secondProtoc = TempFile(second, "protoc-two");
        var firstInclude = Directory.CreateDirectory(Path.Combine(first, "schemas")).FullName;
        var secondInclude = Directory.CreateDirectory(Path.Combine(second, "schemas")).FullName;

        var workspace = Workspace(
            WorkspaceFolder.FromPath(
                first,
                settings: new ProtoLangSettings { ProtocPath = firstProtoc, IncludePaths = ["schemas"] }),
            WorkspaceFolder.FromPath(
                second,
                settings: new ProtoLangSettings { ProtocPath = secondProtoc, IncludePaths = ["schemas"] }));

        var resolvedFirst = workspace.Resolve(Document(first));
        var resolvedSecond = workspace.Resolve(Document(second));

        Assert.Equal(firstProtoc, resolvedFirst.ProtocPath);
        Assert.Equal([firstInclude], resolvedFirst.IncludePaths.Select(include => include.Path));
        Assert.Equal(secondProtoc, resolvedSecond.ProtocPath);
        Assert.Equal([secondInclude], resolvedSecond.IncludePaths.Select(include => include.Path));
    }

    // ---------------------------------------------------------------- precedence

    [Fact]
    public void AFolderSettingWinsOverAWorkspaceSetting()
    {
        var directory = TempDirectory();
        var wanted = TempFile(directory, "wanted-protoc");
        var folder = WorkspaceFolder.FromPath(directory, settings: new ProtoLangSettings { ProtocPath = wanted });

        var resolved = Workspace(folder)
            .WithWorkspaceSettings(new ProtoLangSettings { ProtocPath = TempFile(directory, "other-protoc") })
            .Resolve(Document(directory));

        Assert.Equal(wanted, resolved.ProtocPath);
        Assert.Equal(ConfigurationSource.FolderSetting, resolved.ProtocPathSource);
    }

    [Fact]
    public void AWorkspaceSettingWinsOverAUserSetting()
    {
        var directory = TempDirectory();
        var wanted = TempFile(directory, "wanted-protoc");

        var resolved = Workspace(WorkspaceFolder.FromPath(directory))
            .WithWorkspaceSettings(new ProtoLangSettings { ProtocPath = wanted })
            .WithUserSettings(new ProtoLangSettings { ProtocPath = TempFile(directory, "other-protoc") })
            .Resolve(Document(directory));

        Assert.Equal(wanted, resolved.ProtocPath);
        Assert.Equal(ConfigurationSource.WorkspaceSetting, resolved.ProtocPathSource);
    }

    [Fact]
    public void AnEditorSettingWinsOverTheEnvironmentVariable()
    {
        var directory = TempDirectory();
        var wanted = TempFile(directory, "wanted-protoc");
        var fromEnvironment = TempFile(directory, "environment-protoc");

        var resolved = (Workspace(WorkspaceFolder.FromPath(directory)) with
        {
            ReadEnvironmentVariable = _ => fromEnvironment,
        })
            .WithUserSettings(new ProtoLangSettings { ProtocPath = wanted })
            .Resolve(Document(directory));

        Assert.Equal(wanted, resolved.ProtocPath);
        Assert.Equal(ConfigurationSource.UserSetting, resolved.ProtocPathSource);
    }

    [Fact]
    public void TheEnvironmentVariableIsUsedWhenNoSettingNamesProtoc()
    {
        var directory = TempDirectory();
        var fromEnvironment = TempFile(directory, "environment-protoc");

        var resolved = (Workspace(WorkspaceFolder.FromPath(directory)) with
        {
            ReadEnvironmentVariable = name =>
                name == ProtocLocator.OverrideEnvironmentVariable ? fromEnvironment : null,
        })
            .Resolve(Document(directory));

        Assert.Equal(fromEnvironment, resolved.ProtocPath);
        Assert.Equal(ConfigurationSource.Environment, resolved.ProtocPathSource);
    }

    [Fact]
    public void ProtocIsLeftToBeLocatedWhenNothingNamesOne()
    {
        var resolved = Workspace().Resolve(Document(TempDirectory()));

        Assert.Null(resolved.ProtocPath);
        Assert.Equal(ConfigurationSource.Discovery, resolved.ProtocPathSource);
    }

    [Fact]
    public void AProtocThatDoesNotExistIsReportedAndTheNextSourceIsUsed()
    {
        var directory = TempDirectory();
        var real = TempFile(directory, "real-protoc");

        var resolved = (Workspace(WorkspaceFolder.FromPath(directory)) with
        {
            ReadEnvironmentVariable = _ => real,
        })
            .WithUserSettings(new ProtoLangSettings { ProtocPath = Path.Combine(directory, "missing-protoc") })
            .Resolve(Document(directory));

        Assert.Equal(real, resolved.ProtocPath);
        Assert.Equal(ConfigurationSource.Environment, resolved.ProtocPathSource);
        Assert.Contains(resolved.Diagnostics, diagnostic => diagnostic.Code == "PL2105");
    }

    [Fact]
    public void TheWholePrecedenceStackResolvesConsistently()
    {
        var directory = TempDirectory();
        var folderProtoc = TempFile(directory, "folder-protoc");
        var workspaceProtoc = TempFile(directory, "workspace-protoc");
        var userProtoc = TempFile(directory, "user-protoc");
        var environmentProtoc = TempFile(directory, "environment-protoc");
        var folderInclude = Directory.CreateDirectory(Path.Combine(directory, "folder-schemas")).FullName;
        var workspaceInclude = Directory.CreateDirectory(Path.Combine(directory, "workspace-schemas")).FullName;
        var userInclude = Directory.CreateDirectory(Path.Combine(directory, "user-schemas")).FullName;
        var folderConfig = TempFile(directory, "folder-policy.xml", CheckedOverflow);
        var workspaceConfig = TempFile(directory, "workspace-policy.xml", "<ProtoLang></ProtoLang>");
        var userConfig = TempFile(directory, "user-policy.xml", "<ProtoLang></ProtoLang>");

        var folder = WorkspaceFolder.FromPath(
            directory,
            settings: new ProtoLangSettings
            {
                ProtocPath = folderProtoc,
                IncludePaths = [folderInclude],
                ConfigPath = folderConfig,
            });

        var resolved = (Workspace(folder) with
        {
            ReadEnvironmentVariable = name =>
                name == ProtocLocator.OverrideEnvironmentVariable ? environmentProtoc : null,
        })
            .WithWorkspaceSettings(
                new ProtoLangSettings
                {
                    ProtocPath = workspaceProtoc,
                    IncludePaths = [workspaceInclude],
                    ConfigPath = workspaceConfig,
                })
            .WithUserSettings(
                new ProtoLangSettings
                {
                    ProtocPath = userProtoc,
                    IncludePaths = [userInclude],
                    ConfigPath = userConfig,
                })
            .Resolve(Document(directory));

        Assert.Equal(folderProtoc, resolved.ProtocPath);
        Assert.Equal(ConfigurationSource.FolderSetting, resolved.ProtocPathSource);
        Assert.Equal(
            [folderInclude, workspaceInclude, userInclude],
            resolved.IncludePaths.Select(include => include.Path));
        Assert.Equal(
            [
                ConfigurationSource.FolderSetting,
                ConfigurationSource.WorkspaceSetting,
                ConfigurationSource.UserSetting,
            ],
            resolved.IncludePaths.Select(include => include.Source));
        Assert.Equal(folderConfig, resolved.Config?.Path);
        Assert.Equal(OverflowPolicy.Checked, resolved.Config?.Overflow);
        Assert.Empty(resolved.Diagnostics);
    }

    [Fact]
    public void TheDeclaredPrecedenceIsTheOrderTheSourcesAreListedIn()
    {
        var precedence = ConfigurationSources.Precedence;

        Assert.Equal(
            [
                ConfigurationSource.FolderSetting,
                ConfigurationSource.WorkspaceSetting,
                ConfigurationSource.UserSetting,
                ConfigurationSource.Environment,
            ],
            precedence.Take(4));

        Assert.All(
            precedence,
            source => Assert.False(
                string.IsNullOrWhiteSpace(source.Label()),
                "every source must be able to name itself in a diagnostic"));
    }

    // ---------------------------------------------------------------- include paths

    [Fact]
    public void IncludePathsFromEveryScopeAreSearchedMostSpecificFirst()
    {
        var directory = TempDirectory();
        var folderRoot = Directory.CreateDirectory(Path.Combine(directory, "folder-schemas")).FullName;
        var userRoot = TempDirectory();

        var folder = WorkspaceFolder.FromPath(directory, settings: new ProtoLangSettings { IncludePaths = [folderRoot] });

        var resolved = Workspace(folder)
            .WithUserSettings(new ProtoLangSettings { IncludePaths = [userRoot] })
            .Resolve(Document(directory));

        Assert.Equal([folderRoot, userRoot], resolved.IncludePaths.Select(include => include.Path));
        Assert.Equal(
            [ConfigurationSource.FolderSetting, ConfigurationSource.UserSetting],
            resolved.IncludePaths.Select(include => include.Source));
    }

    [Fact]
    public void OneDirectoryNamedAtTwoScopesIsSearchedOnce()
    {
        var directory = TempDirectory();
        var shared = Directory.CreateDirectory(Path.Combine(directory, "schemas")).FullName;

        var folder = WorkspaceFolder.FromPath(directory, settings: new ProtoLangSettings { IncludePaths = [shared] });

        var resolved = Workspace(folder)
            .WithUserSettings(new ProtoLangSettings { IncludePaths = [shared + Path.DirectorySeparatorChar] })
            .Resolve(Document(directory));

        Assert.Equal([shared], resolved.IncludePaths.Select(include => include.Path));
        Assert.Equal(ConfigurationSource.FolderSetting, resolved.IncludePaths[0].Source);
    }

    [Fact]
    public void ARelativeIncludePathResolvesAgainstTheFolderThatSuppliedIt()
    {
        var directory = TempDirectory();
        var expected = Directory.CreateDirectory(Path.Combine(directory, "schemas")).FullName;

        var folder = WorkspaceFolder.FromPath(directory, settings: new ProtoLangSettings { IncludePaths = ["schemas"] });

        var resolved = Workspace(folder).Resolve(Document(directory));

        Assert.Equal([expected], resolved.IncludePaths.Select(include => include.Path));
        Assert.Equal("schemas", resolved.IncludePaths[0].AsWritten);
    }

    [Fact]
    public void ARelativeIncludePathAtUserScopeIsRefusedAndSaidSo()
    {
        var directory = TempDirectory();

        var resolved = Workspace(WorkspaceFolder.FromPath(directory))
            .WithUserSettings(new ProtoLangSettings { IncludePaths = ["schemas"] })
            .Resolve(Document(directory));

        Assert.Empty(resolved.IncludePaths);

        var refusal = Assert.Single(resolved.Diagnostics, diagnostic => diagnostic.Code == "PL2103");
        Assert.Equal(DiagnosticSeverity.Warning, refusal.Severity);
        Assert.Contains("schemas", refusal.Message);
        Assert.Equal(ConfigurationSource.UserSetting.Label(), refusal.Span.File);
    }

    [Fact]
    public void ARelativeWorkspaceSettingResolvesAgainstTheOnlyOpenFolder()
    {
        var directory = TempDirectory();
        var expected = Directory.CreateDirectory(Path.Combine(directory, "schemas")).FullName;

        var resolved = Workspace(WorkspaceFolder.FromPath(directory))
            .WithWorkspaceSettings(new ProtoLangSettings { IncludePaths = ["schemas"] })
            .Resolve(Document(directory));

        Assert.Equal([expected], resolved.IncludePaths.Select(include => include.Path));
    }

    [Fact]
    public void ARelativeWorkspaceSettingResolvesAgainstTheWorkspaceFileDirectory()
    {
        var workspaceDirectory = TempDirectory("workspace-file");
        var first = TempDirectory("first-root");
        var second = TempDirectory("second-root");
        var expected = Directory.CreateDirectory(Path.Combine(workspaceDirectory, "schemas")).FullName;

        var resolved = Workspace(WorkspaceFolder.FromPath(first), WorkspaceFolder.FromPath(second))
            .WithWorkspaceSettings(new ProtoLangSettings { IncludePaths = ["schemas"] }, workspaceDirectory)
            .Resolve(Document(first));

        Assert.Equal([expected], resolved.IncludePaths.Select(include => include.Path));
    }

    [Fact]
    public void ARelativeWorkspaceSettingIsRefusedWhenSeveralFoldersAreOpenAndNoWorkspaceFileSaysWhere()
    {
        var resolved = Workspace(
                WorkspaceFolder.FromPath(TempDirectory()),
                WorkspaceFolder.FromPath(TempDirectory()))
            .WithWorkspaceSettings(new ProtoLangSettings { IncludePaths = ["schemas"] })
            .Resolve(Document(TempDirectory()));

        Assert.Empty(resolved.IncludePaths);
        Assert.Contains(resolved.Diagnostics, diagnostic => diagnostic.Code == "PL2103");
    }

    [Fact]
    public void AnEmptyIncludePathListDoesNotHideLowerPriorityIncludePaths()
    {
        var directory = TempDirectory();
        var userRoot = TempDirectory("user-schemas");
        var folder = WorkspaceFolder.FromPath(directory, settings: new ProtoLangSettings { IncludePaths = [] });

        var resolved = Workspace(folder)
            .WithUserSettings(new ProtoLangSettings { IncludePaths = [userRoot] })
            .Resolve(Document(directory));

        Assert.Equal([userRoot], resolved.IncludePaths.Select(include => include.Path));
        Assert.Equal(ConfigurationSource.UserSetting, Assert.Single(resolved.IncludePaths).Source);
    }

    // ---------------------------------------------------------------- settings that are not used

    [Fact]
    public void ASettingThatStatesLanguagePolicyIsIgnoredAndSaidSo()
    {
        var settings = Read(out var diagnostics, new SettingValue("protolang.overflow", "Checked"));

        Assert.True(settings.StatesNothing);

        var refusal = Assert.Single(diagnostics);
        Assert.Equal("PL2101", refusal.Code);
        Assert.Equal(DiagnosticSeverity.Warning, refusal.Severity);
        Assert.Contains(ProjectConfig.FileName, refusal.Message);
        Assert.Contains(ProtoLangSettings.ConfigPathKey, refusal.Help!);
    }

    [Fact]
    public void EverySettingTheConfigFileOwnsIsRefusedByName()
    {
        foreach (var key in ProjectConfig.Keys)
        {
            var leaf = key.Split('/')[^1];
            Read(out var diagnostics, new SettingValue($"protolang.{leaf}", "whatever"));

            Assert.Equal("PL2101", Assert.Single(diagnostics).Code);
        }
    }

    [Fact]
    public void AnUnrecognizedSettingIsIgnoredAndSaidSo()
    {
        Read(out var diagnostics, new SettingValue("protolang.protokPath", "protoc"));

        var refusal = Assert.Single(diagnostics);
        Assert.Equal("PL2102", refusal.Code);
        Assert.Contains(ProtoLangSettings.ProtocPathKey, refusal.Help!);
    }

    [Fact]
    public void AQualifiedKeyAndABareOneAreTheSameSetting()
    {
        var qualified = Read(out var first, new SettingValue("protolang.protocPath", "protoc"));
        var bare = Read(out var second, new SettingValue("protocPath", "protoc"));

        Assert.Empty(first);
        Assert.Empty(second);
        Assert.Equal(qualified, bare);
    }

    [Fact]
    public void ASettingWrittenAndThenClearedIsTheSameAsOneNeverWritten()
    {
        var cleared = Read(out var diagnostics, new SettingValue("protolang.protocPath", "   "));

        Assert.Empty(diagnostics);
        Assert.True(cleared.StatesNothing);
    }

    /// <summary>
    /// Blank has to mean unset in the type and not merely in the reader, because a host is free to
    /// build settings by hand from what a client sent. A blank that survived reached ProtocLocator,
    /// which refuses a blank tool name, and took the resolution down with it.
    /// </summary>
    [Fact]
    public void ASettingClearedToBlankFallsThroughInsteadOfFailingTheResolution()
    {
        var settings = new ProtoLangSettings { ProtocPath = "", ConfigPath = "  ", IncludePaths = ["", " "] };

        Assert.True(settings.StatesNothing);

        var resolved = Workspace().WithUserSettings(settings).Resolve(DocumentUri.Parse("untitled:Untitled-1"));

        Assert.Null(resolved.ProtocPath);
        Assert.Equal(ConfigurationSource.Discovery, resolved.ProtocPathSource);
        Assert.Empty(resolved.IncludePaths);
        Assert.Empty(resolved.Diagnostics);
    }

    [Fact]
    public void ARefusalNamesTheScopeItWasWrittenAt()
    {
        var diagnostics = new DiagnosticBag();
        ProtoLangSettings.Read(
            [new SettingValue("protolang.nonsense", "x")],
            ConfigurationSource.FolderSetting,
            diagnostics);

        Assert.Equal(ConfigurationSource.FolderSetting.Label(), Assert.Single(diagnostics).Span.File);
    }

    // ---------------------------------------------------------------- language policy

    [Fact]
    public void ADiscoveredConfigFileSettlesPolicy()
    {
        var directory = TempDirectory();
        var path = TempFile(directory, ProjectConfig.FileName, CheckedOverflow);

        var resolved = Workspace(WorkspaceFolder.FromPath(directory)).Resolve(Document(directory));

        Assert.Equal(OverflowPolicy.Checked, resolved.Config?.Overflow);
        Assert.Equal(path, resolved.Config?.Path);
        Assert.Equal(ConfigurationSource.ConfigFile, resolved.ConfigSource);
    }

    [Fact]
    public void AConfiguredConfigPathIsUsedInsteadOfSearching()
    {
        var directory = TempDirectory();
        TempFile(directory, ProjectConfig.FileName, "<ProtoLang></ProtoLang>");
        var elsewhere = TempFile(TempDirectory(), "policy.xml", CheckedOverflow);

        var resolved = Workspace(WorkspaceFolder.FromPath(directory))
            .WithWorkspaceSettings(new ProtoLangSettings { ConfigPath = elsewhere })
            .Resolve(Document(directory));

        Assert.Equal(elsewhere, resolved.Config?.Path);
        Assert.Equal(OverflowPolicy.Checked, resolved.Config?.Overflow);
    }

    [Fact]
    public void ARelativeConfigPathResolvesAgainstTheFolderThatSuppliedIt()
    {
        var directory = TempDirectory();
        var path = TempFile(directory, "policy.xml", CheckedOverflow);
        var folder = WorkspaceFolder.FromPath(directory, settings: new ProtoLangSettings { ConfigPath = "policy.xml" });

        var resolved = Workspace(folder).Resolve(Document(directory));

        Assert.Equal(path, resolved.Config?.Path);
        Assert.Equal(OverflowPolicy.Checked, resolved.Config?.Overflow);
    }

    [Fact]
    public void ARelativeConfigPathAtWorkspaceScopeResolvesAgainstTheWorkspaceFileDirectory()
    {
        var workspaceDirectory = TempDirectory("workspace-file");
        var first = TempDirectory("first-root");
        var second = TempDirectory("second-root");
        var path = TempFile(workspaceDirectory, "policy.xml", CheckedOverflow);

        var resolved = Workspace(WorkspaceFolder.FromPath(first), WorkspaceFolder.FromPath(second))
            .WithWorkspaceSettings(new ProtoLangSettings { ConfigPath = "policy.xml" }, workspaceDirectory)
            .Resolve(Document(first));

        Assert.Equal(path, resolved.Config?.Path);
        Assert.Equal(OverflowPolicy.Checked, resolved.Config?.Overflow);
    }

    [Fact]
    public void AConfigPathThatNamesNothingIsReportedAndTheSearchHappensAnyway()
    {
        var directory = TempDirectory();
        var discovered = TempFile(directory, ProjectConfig.FileName, CheckedOverflow);

        var resolved = Workspace(WorkspaceFolder.FromPath(directory))
            .WithWorkspaceSettings(new ProtoLangSettings { ConfigPath = Path.Combine(directory, "gone.xml") })
            .Resolve(Document(directory));

        Assert.Equal("PL2104", Assert.Single(resolved.Diagnostics).Code);
        Assert.Equal(discovered, resolved.Config?.Path);
    }

    [Fact]
    public void AConfigFileThatCannotBeReadStopsTheDocumentRatherThanFallingBackToTheDefaults()
    {
        var directory = TempDirectory();
        TempFile(directory, ProjectConfig.FileName, UnreadableConfig);

        var resolved = Workspace(WorkspaceFolder.FromPath(directory)).Resolve(Document(directory));

        Assert.Null(resolved.Config);
        Assert.False(resolved.IsUsable);
        Assert.Contains(resolved.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.False(resolved.TryCreateCompilationOptions(null, out var options));
        Assert.Null(options);
    }

    [Fact]
    public void ARefusedConfigFileIsNamedAndSaysWhatItCosts()
    {
        var directory = TempDirectory();
        var path = TempFile(directory, ProjectConfig.FileName, UnreadableConfig);

        var resolved = Workspace(WorkspaceFolder.FromPath(directory)).Resolve(Document(directory));

        Assert.True(resolved.ConfigRefused);
        Assert.Equal(path, resolved.ConfigPath);

        var refusal = Assert.Single(resolved.Diagnostics, diagnostic => diagnostic.Code == "PL2106");
        Assert.Equal(DiagnosticSeverity.Error, refusal.Severity);
        Assert.Contains(path, refusal.Message);
        Assert.Contains("searching upward", refusal.Message);
        Assert.Contains("PL2002", refusal.Message);
        Assert.Contains("line 4", refusal.Message);
        Assert.NotNull(refusal.Help);
    }

    /// <summary>
    /// The provenance report is the thing a user consults when they cannot work out what is
    /// happening, so it must not answer "the defaults, from your configuration file" -- which is two
    /// facts that were never true together.
    /// </summary>
    [Fact]
    public void ARefusedConfigFileIsReportedAsRefusedRatherThanAsTheDefaults()
    {
        var directory = TempDirectory();
        var path = TempFile(directory, ProjectConfig.FileName, UnreadableConfig);

        var policy = Fact(Workspace(WorkspaceFolder.FromPath(directory)).Resolve(Document(directory)).Describe(), "language policy");

        Assert.Contains("refused", policy.Value);
        Assert.Contains(path, policy.Value);
        Assert.DoesNotContain("defaults", policy.Value);
    }

    [Fact]
    public void ARefusedConfigFileNamedByASettingSaysWhichSettingChoseIt()
    {
        var directory = TempDirectory();
        var path = TempFile(directory, "policy.xml", UnreadableConfig);

        var resolved = Workspace(WorkspaceFolder.FromPath(directory))
            .WithWorkspaceSettings(new ProtoLangSettings { ConfigPath = path })
            .Resolve(Document(directory));

        Assert.True(resolved.ConfigRefused);

        var refusal = Assert.Single(resolved.Diagnostics, diagnostic => diagnostic.Code == "PL2106");
        Assert.Contains(ProtoLangSettings.ConfigPathKey, refusal.Message);
        Assert.Contains(ConfigurationSource.WorkspaceSetting.Describe(), refusal.Message);
    }

    [Fact]
    public void AMissingConfigFileIsNotARefusal()
    {
        var resolved = Workspace(WorkspaceFolder.FromPath(TempDirectory())).Resolve(Document(TempDirectory()));

        Assert.False(resolved.ConfigRefused);
        Assert.Null(resolved.ConfigPath);
        Assert.DoesNotContain(resolved.Diagnostics, diagnostic => diagnostic.Code == "PL2106");
    }

    [Fact]
    public void PolicyIsFoundAboveADocumentThatIsInNoFolderAtAll()
    {
        var above = TempDirectory();
        var below = Directory.CreateDirectory(Path.Combine(above, "nested")).FullName;
        TempFile(above, ProjectConfig.FileName, CheckedOverflow);

        var resolved = Workspace().Resolve(Document(below));

        Assert.Null(resolved.Folder);
        Assert.Equal(OverflowPolicy.Checked, resolved.Config?.Overflow);
    }

    [Fact]
    public void AnUntitledBufferTakesThePolicyOfTheFolderItInherits()
    {
        var directory = TempDirectory();
        TempFile(directory, ProjectConfig.FileName, CheckedOverflow);

        var resolved = Workspace(WorkspaceFolder.FromPath(directory))
            .Resolve(DocumentUri.Parse("untitled:Untitled-1"));

        Assert.Equal(OverflowPolicy.Checked, resolved.Config?.Overflow);
    }

    [Fact]
    public void AnUntitledBufferInNoFolderTakesTheDefaults()
    {
        var resolved = Workspace().Resolve(DocumentUri.Parse("untitled:Untitled-1"));

        Assert.Equal(ProjectConfig.Default, resolved.Config);
        Assert.Equal(ConfigurationSource.Default, resolved.ConfigSource);
    }

    // ---------------------------------------------------------------- what a compilation is handed

    [Fact]
    public void TheResolvedIncludePathsAndPolicyAreWhatACompilationRunsWith()
    {
        var directory = TempDirectory();
        var schemas = Directory.CreateDirectory(Path.Combine(directory, "schemas")).FullName;
        TempFile(directory, ProjectConfig.FileName, CheckedOverflow);

        var folder = WorkspaceFolder.FromPath(directory, settings: new ProtoLangSettings { IncludePaths = ["schemas"] });
        var resolved = Workspace(folder).Resolve(Document(directory));

        Assert.True(resolved.TryCreateCompilationOptions(null, out var options));
        Assert.Equal([schemas], options!.IncludePaths);
        Assert.Equal(OverflowPolicy.Checked, options.Config?.Overflow);
    }

    /// <summary>
    /// CompilationOptions has no protoc of its own, so a caller that resolves one and then passes no
    /// loader compiles against whichever protoc the compiler found for itself -- while this object
    /// goes on reporting the user's setting as in force. The wrong answer is invisible; the exception
    /// is not.
    /// </summary>
    [Fact]
    public void ACompilationCannotBeBuiltWithoutTheProtocThatWasResolved()
    {
        var directory = TempDirectory();
        var protoc = TempFile(directory, "protoc-here");
        var folder = WorkspaceFolder.FromPath(directory, settings: new ProtoLangSettings { ProtocPath = protoc });

        var resolved = Workspace(folder).Resolve(Document(directory));

        var refusal = Assert.Throws<ArgumentNullException>(
            () => resolved.TryCreateCompilationOptions(null, out _));

        Assert.Contains(protoc, refusal.Message);
    }

    /// <summary>
    /// False is this method's "do not compile this document" answer, and a refused configuration file
    /// is exactly that. A caller taking the answer it was given -- with no loader, because it was not
    /// going to compile -- must get the answer rather than an exception about the loader.
    /// </summary>
    [Fact]
    public void ADocumentThatMustNotCompileSaysSoRatherThanAskingAboutTheLoader()
    {
        var directory = TempDirectory();
        var protoc = TempFile(directory, "protoc-here");
        TempFile(directory, ProjectConfig.FileName, UnreadableConfig);

        var folder = WorkspaceFolder.FromPath(directory, settings: new ProtoLangSettings { ProtocPath = protoc });
        var resolved = Workspace(folder).Resolve(Document(directory));

        Assert.Equal(protoc, resolved.ProtocPath);
        Assert.True(resolved.ConfigRefused);
        Assert.False(resolved.TryCreateCompilationOptions(null, out var options));
        Assert.Null(options);
    }

    [Fact]
    public void EveryResolvedValueSaysWhereItCameFrom()
    {
        var directory = TempDirectory();
        var protoc = TempFile(directory, "protoc-here");
        TempFile(directory, ProjectConfig.FileName, CheckedOverflow);

        var folder = WorkspaceFolder.FromPath(
            directory,
            settings: new ProtoLangSettings { ProtocPath = protoc, IncludePaths = ["schemas"] });

        var facts = Workspace(folder).Resolve(Document(directory)).Describe();

        Assert.Equal(ConfigurationSource.FolderSetting, Fact(facts, "protoc").Source);
        Assert.Equal(protoc, Fact(facts, "protoc").Value);
        Assert.Equal(ConfigurationSource.ConfigFile, Fact(facts, "language policy").Source);
        Assert.Equal(ConfigurationSource.FolderSetting, Fact(facts, "include path").Source);
    }

    private static ConfigurationFact Fact(IReadOnlyList<ConfigurationFact> facts, string setting)
        => facts.First(fact => fact.Setting == setting);

    // ---------------------------------------------------------------- change

    [Fact]
    public void ChangingASettingAdvancesTheGeneration()
    {
        var first = WorkspaceConfiguration.Empty;
        var second = first.WithUserSettings(new ProtoLangSettings { ProtocPath = "protoc" });

        Assert.Equal(first.Generation + 1, second.Generation);
        Assert.Equal(second.Generation + 1, second.WithFolders([]).Generation);
    }

    [Fact]
    public void AResolvedConfigurationRemembersTheGenerationItCameFrom()
    {
        var configuration = Workspace().WithFolders([]);

        Assert.Equal(configuration.Generation, configuration.Resolve(Document(TempDirectory())).Generation);
    }

    [Fact]
    public void AChangedSettingTakesEffectOnTheNextResolutionWithNothingRestarted()
    {
        var directory = TempDirectory();
        var first = Directory.CreateDirectory(Path.Combine(directory, "first")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(directory, "second")).FullName;

        var before = Workspace(WorkspaceFolder.FromPath(directory))
            .WithWorkspaceSettings(new ProtoLangSettings { IncludePaths = [first] });
        var after = before.WithWorkspaceSettings(new ProtoLangSettings { IncludePaths = [second] });

        Assert.Equal([first], before.Resolve(Document(directory)).IncludePaths.Select(include => include.Path));
        Assert.Equal([second], after.Resolve(Document(directory)).IncludePaths.Select(include => include.Path));
    }

    [Fact]
    public void AResolvedConfigurationIsASnapshotAfterSettingsChange()
    {
        var directory = TempDirectory();
        var first = Directory.CreateDirectory(Path.Combine(directory, "first")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(directory, "second")).FullName;

        var before = Workspace(WorkspaceFolder.FromPath(directory))
            .WithWorkspaceSettings(new ProtoLangSettings { IncludePaths = [first] });
        var resolvedBefore = before.Resolve(Document(directory));
        var after = before.WithWorkspaceSettings(new ProtoLangSettings { IncludePaths = [second] });
        var resolvedAfter = after.Resolve(Document(directory));

        Assert.Equal(before.Generation, resolvedBefore.Generation);
        Assert.Equal([first], resolvedBefore.IncludePaths.Select(include => include.Path));
        Assert.Equal(after.Generation, resolvedAfter.Generation);
        Assert.Equal([second], resolvedAfter.IncludePaths.Select(include => include.Path));
    }

    // ---------------------------------------------------------------- one path, one cache entry

    private static DescriptorRequest Request(string root)
        => new("protoc", 1, DateTime.UnixEpoch, [root], [], ["leaf.proto"]);

    private static (DescriptorBundle Bundle, string Root) Schema()
    {
        var root = TempDirectory("cache-root");
        TempFile(root, "leaf.proto", "syntax = \"proto3\";\nmessage Leaf { int32 value = 1; }\n");

        return (new DescriptorBundle([], new FileDescriptorSet(), SchemaClosure.Describe(["leaf.proto"], [root])), root);
    }

    [Fact]
    public void TwoSpellingsOfOneIncludePathAreOneCacheEntry()
    {
        var (bundle, root) = Schema();
        var cache = new DescriptorCache();
        var loads = 0;

        DescriptorBundle Load()
        {
            loads++;
            return bundle;
        }

        cache.GetOrLoad(Request(root), Load);
        cache.GetOrLoad(Request(root + Path.DirectorySeparatorChar), Load);

        Assert.Equal(1, loads);
        Assert.Equal(1, cache.Count);
        Assert.Equal(1, cache.Statistics.Hits);
    }

    [Fact]
    public void ResolvedIncludePathsUseTheSameIdentityTheDescriptorCacheUses()
    {
        var directory = TempDirectory();
        var schemas = Directory.CreateDirectory(Path.Combine(directory, "schemas")).FullName;
        var sameSchemas = Path.Combine(directory, ".", "schemas") + Path.DirectorySeparatorChar;
        var folder = WorkspaceFolder.FromPath(directory, settings: new ProtoLangSettings { IncludePaths = [sameSchemas] });
        var resolved = Workspace(folder).Resolve(Document(directory));

        Assert.Equal([schemas], resolved.IncludePaths.Select(include => include.Path));
        Assert.Equal(Request(schemas), Request(Assert.Single(resolved.IncludePaths).Path));
    }

    [Fact]
    public void TwoSpellingsOfOneRootDoNotMakeEachOthersEntriesLookStale()
    {
        RequireCaseInsensitivePaths();

        var (bundle, root) = Schema();
        var cache = new DescriptorCache();
        var loads = 0;

        DescriptorBundle Load()
        {
            loads++;
            return bundle;
        }

        cache.GetOrLoad(Request(root), Load);
        cache.GetOrLoad(Request(root.ToUpperInvariant()), Load);
        cache.GetOrLoad(Request(root), Load);

        Assert.Equal(1, loads);
        Assert.Equal(0, cache.Statistics.Invalidations);
    }

    [Fact]
    public void AChangedSchemaStillInvalidatesWhenTheRootIsSpelledDifferently()
    {
        RequireCaseInsensitivePaths();

        var (bundle, root) = Schema();
        var cache = new DescriptorCache();
        var loads = 0;

        DescriptorBundle Load()
        {
            loads++;
            return bundle;
        }

        cache.GetOrLoad(Request(root), Load);
        File.WriteAllText(Path.Combine(root, "leaf.proto"), "syntax = \"proto3\";\nmessage Leaf { }\n");
        cache.GetOrLoad(Request(root.ToUpperInvariant()), Load);

        Assert.Equal(2, loads);
        Assert.Equal(1, cache.Statistics.Invalidations);
    }
}
