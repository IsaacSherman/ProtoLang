using ProtoLang.Diagnostics;
using ProtoLang.Ir;

namespace ProtoLang.Backend;

/// <summary>A single generated source file, with a path relative to the output directory.</summary>
public sealed record GeneratedFile(string RelativePath, string Contents);

public sealed record BackendOptions(string SourceFileName);

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
