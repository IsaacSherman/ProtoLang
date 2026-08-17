using ProtoLang.Binding;
using ProtoLang.Diagnostics;
using ProtoLang.Ir;
using ProtoLang.Syntax;

namespace ProtoLang;

public sealed record CompilationResult(
    IrModule? Module,
    CompilationUnit? SyntaxTree,
    DiagnosticBag Diagnostics)
{
    public bool Success => Module is not null && !Diagnostics.HasErrors;
}

/// <summary>
/// Drives the pipeline described in spec 22.1: source, lexer/parser, descriptor binding, name
/// resolution, type checking, typed IR. Backends run separately, over the IR this produces.
/// </summary>
public static class Compilation
{
    /// <summary>
    /// Compiles a single ProtoLang file to typed IR.
    /// </summary>
    /// <param name="sourcePath">Path to the .protolang file.</param>
    /// <param name="includePaths">
    /// Directories searched for the .proto files named in <c>import proto</c> declarations. The
    /// directory containing the source file is always searched as a fallback.
    /// </param>
    /// <param name="loader">Descriptor loader; defaults to a protoc-backed one.</param>
    public static CompilationResult Compile(
        string sourcePath,
        IReadOnlyList<string> includePaths,
        DescriptorLoader? loader = null)
    {
        var diagnostics = new DiagnosticBag();
        var text = File.ReadAllText(sourcePath);
        var fileName = Path.GetFileName(sourcePath);

        var tokens = new Lexer(text, fileName, diagnostics).Tokenize();
        var unit = new Parser(tokens, fileName, diagnostics).ParseCompilationUnit();

        if (diagnostics.HasErrors)
        {
            return new CompilationResult(null, unit, diagnostics);
        }

        var searchPaths = BuildSearchPaths(sourcePath, includePaths);

        if (unit.Imports.Count == 0)
        {
            diagnostics.Error(
                "PL0001",
                "no proto imports",
                "A ProtoLang file must import at least one protobuf schema.",
                unit.Span,
                "Add an 'import proto \"your.proto\";' declaration (spec 5.2).");
            return new CompilationResult(null, unit, diagnostics);
        }

        var protoFiles = new List<string>();
        foreach (var import in unit.Imports)
        {
            if (ResolveImport(import.Path, searchPaths) is null)
            {
                diagnostics.Error(
                    "PL0002",
                    "proto file not found",
                    $"Could not find '{import.Path}' in any include directory.",
                    import.Span,
                    "Searched: " + string.Join(", ", searchPaths));
                continue;
            }

            protoFiles.Add(import.Path);
        }

        if (diagnostics.HasErrors)
        {
            return new CompilationResult(null, unit, diagnostics);
        }

        IReadOnlyList<Google.Protobuf.Reflection.FileDescriptor> descriptors;
        try
        {
            loader ??= DescriptorLoader.CreateDefault();
            descriptors = loader.Load(protoFiles, searchPaths);
        }
        catch (DescriptorLoadException ex)
        {
            diagnostics.Error(
                "PL0003",
                "protobuf schema could not be loaded",
                ex.Message,
                unit.Imports.Count > 0 ? unit.Imports[0].Span : unit.Span);
            return new CompilationResult(null, unit, diagnostics);
        }

        var module = new Binder(descriptors, diagnostics).Bind(unit);

        return diagnostics.HasErrors
            ? new CompilationResult(null, unit, diagnostics)
            : new CompilationResult(module, unit, diagnostics);
    }

    private static List<string> BuildSearchPaths(string sourcePath, IReadOnlyList<string> includePaths)
    {
        var searchPaths = new List<string>();

        foreach (var includePath in includePaths)
        {
            var full = Path.GetFullPath(includePath);
            if (!searchPaths.Contains(full, StringComparer.OrdinalIgnoreCase))
            {
                searchPaths.Add(full);
            }
        }

        var sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(sourcePath));
        if (!string.IsNullOrEmpty(sourceDirectory)
            && !searchPaths.Contains(sourceDirectory, StringComparer.OrdinalIgnoreCase))
        {
            searchPaths.Add(sourceDirectory);
        }

        return searchPaths;
    }

    private static string? ResolveImport(string importPath, IReadOnlyList<string> searchPaths)
    {
        foreach (var searchPath in searchPaths)
        {
            var candidate = Path.Combine(searchPath, importPath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
