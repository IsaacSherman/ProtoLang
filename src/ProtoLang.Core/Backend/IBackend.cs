using Google.Protobuf.Reflection;
using ProtoLang.Diagnostics;
using ProtoLang.Ir;

namespace ProtoLang.Backend;

/// <summary>A single generated source file, with a path relative to the output directory.</summary>
public sealed record GeneratedFile(string RelativePath, string Contents);

public sealed record BackendOptions(string SourceFileName)
{
    /// <summary>
    /// The policy lines to print in the generated file's header, from
    /// <see cref="Config.ProjectConfig.DescribeForHeader"/>.
    /// </summary>
    /// <remarks>
    /// Prose, deliberately. A backend prints these and cannot branch on them: how an operation is
    /// emitted comes from the behavior annotation on its IR node, never from a policy object handed
    /// to the backend. The default describes the default policy, so a caller that compiles with no
    /// configuration still emits an accurate header rather than none.
    /// </remarks>
    public IReadOnlyList<string> PolicyDescription { get; init; } =
        Config.ProjectConfig.Default.DescribeForHeader();
}

/// <summary>
/// A code generator. Per spec 23 a conforming backend consumes only the typed IR, preserves
/// normative semantics, and rejects unsupported features at compile time rather than emitting
/// something that quietly differs.
/// </summary>
public interface IBackend
{
    /// <summary>Short identifier used on the command line, for example <c>csharp</c>.</summary>
    string Name { get; }

    IReadOnlyList<GeneratedFile> Emit(IrModule module, BackendOptions options, DiagnosticBag diagnostics);
}

public interface ITestBackend : IBackend
{
    IReadOnlyList<GeneratedFile> EmitTests(IrModule module, BackendOptions options, DiagnosticBag diagnostics);
}

/// <summary>
/// One imported protobuf schema, resolved to the include directory it was found under.
/// </summary>
/// <param name="ProtoRoot">
/// The include directory <paramref name="RelativePath"/> resolves against, relative to the test
/// output directory. Both build systems need the root and the path separately: protoc derives a
/// schema's package-relative identity from the root it was found under, not from the file name.
/// </param>
/// <param name="RelativePath">The import path as written in the ProtoLang source.</param>
public sealed record ProtoFileReference(string ProtoRoot, string RelativePath);

/// <summary>
/// Everything a backend needs to write a build file for generated tests. Paths only: scaffolding
/// consumes neither the AST nor the IR, so this does not weaken the spec 23 rule that backends see
/// only the typed IR.
/// </summary>
/// <param name="BehaviorDirectory">
/// Where the behavior emitted by <c>--out</c> lives, relative to the test output directory. The
/// generated C++ driver includes its behavior header unqualified, so this is an include root and
/// not merely a convenience.
/// </param>
/// <param name="ProtoFiles">Imported schemas, each paired with the include directory it resolved under.</param>
/// <param name="TestSourceFileNames">
/// Generated test sources the build file must compile, as emitted by
/// <see cref="ITestBackend.EmitTests"/>.
/// </param>
public sealed record ScaffoldOptions(
    string BehaviorDirectory,
    IReadOnlyList<ProtoFileReference> ProtoFiles,
    IReadOnlyList<string> TestSourceFileNames)
{
    /// <summary>
    /// Builds the options for one backend, resolving every path relative to the directory the build
    /// file will sit in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Here rather than in the driver because the path arithmetic is the part that is easy to get
    /// subtly wrong, and a build file with a nearly-right relative path fails at build time rather
    /// than at generation time.
    /// </para>
    /// <para>
    /// The schema list comes from the compilation's descriptors rather than from the source's
    /// <c>import proto</c> declarations, because those name only what the ProtoLang file imported
    /// directly. A schema that imports another still generates code referring to the second one, so
    /// listing only the direct imports produces a project that generates a type it never defines.
    /// <see cref="CompilationResult.Descriptors"/> is the whole closure in dependency order.
    /// </para>
    /// </remarks>
    /// <param name="descriptors">
    /// Every protobuf file the compilation used, from <see cref="CompilationResult.Descriptors"/>.
    /// </param>
    /// <remarks>
    /// The form for a caller that has a source path and no compilation in hand. It resolves imports
    /// through <see cref="Compilation.GetSearchPaths"/> so it searches exactly where the compiler
    /// searched; a caller holding a <see cref="CompilationResult"/> should pass
    /// <see cref="CompilationResult.SearchPaths"/> to the other overload and skip the re-derivation
    /// entirely, which is also the only route open to a compilation of a buffer that has no path.
    /// </remarks>
    public static ScaffoldOptions Create(
        string sourcePath,
        IReadOnlyList<string> includePaths,
        IReadOnlyList<FileDescriptor> descriptors,
        string behaviorDirectory,
        string testOutputDirectory,
        IReadOnlyList<string> testSourceFileNames)
        => Create(
            Compilation.GetSearchPaths(sourcePath, includePaths),
            descriptors,
            behaviorDirectory,
            testOutputDirectory,
            testSourceFileNames);

    /// <inheritdoc cref="Create(string, IReadOnlyList{string}, IReadOnlyList{FileDescriptor}, string, string, IReadOnlyList{string})"/>
    /// <param name="searchPaths">
    /// Where imports were resolved, from <see cref="CompilationResult.SearchPaths"/>. Taken rather
    /// than rebuilt because a compilation of an unsaved buffer has no source path to rebuild from,
    /// and because a build file with a nearly-right proto root fails at build time rather than here.
    /// </param>
    public static ScaffoldOptions Create(
        IReadOnlyList<string> searchPaths,
        IReadOnlyList<FileDescriptor> descriptors,
        string behaviorDirectory,
        string testOutputDirectory,
        IReadOnlyList<string> testSourceFileNames)
    {
        var projectDirectory = Path.GetFullPath(testOutputDirectory);
        var protoFiles = new List<ProtoFileReference>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var descriptor in descriptors)
        {
            // Well-known types are supplied already compiled by both targets' protobuf runtimes.
            // Generating them again would define the same types twice.
            if (IsWellKnownType(descriptor.Name) || !seen.Add(descriptor.Name))
            {
                continue;
            }

            foreach (var searchPath in searchPaths)
            {
                if (!File.Exists(Path.Combine(searchPath, descriptor.Name)))
                {
                    continue;
                }

                protoFiles.Add(new ProtoFileReference(
                    Path.GetRelativePath(projectDirectory, searchPath),
                    descriptor.Name));
                break;
            }
        }

        return new ScaffoldOptions(
            Path.GetRelativePath(projectDirectory, Path.GetFullPath(behaviorDirectory)),
            protoFiles,
            testSourceFileNames);
    }

    /// <summary>
    /// Whether a descriptor names one of protobuf's own bundled schemas, which live under a
    /// reserved directory and ship precompiled in every protobuf runtime.
    /// </summary>
    private static bool IsWellKnownType(string name)
        => name.StartsWith("google/protobuf/", StringComparison.Ordinal);
}

/// <summary>
/// A backend that can also write the build file its generated tests need.
/// </summary>
/// <remarks>
/// Separate from <see cref="ITestBackend"/> on purpose. Emitting test source and knowing how to
/// build it are different amounts of work, and a new backend should be able to do the first without
/// the second rather than being blocked on a build-system integration.
/// </remarks>
public interface ITestProjectScaffold : ITestBackend
{
    IReadOnlyList<GeneratedFile> EmitTestProject(ScaffoldOptions options, DiagnosticBag diagnostics);
}
