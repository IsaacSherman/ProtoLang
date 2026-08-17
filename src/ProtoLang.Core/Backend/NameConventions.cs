using System.Text;
using Google.Protobuf.Reflection;

namespace ProtoLang.Backend;

/// <summary>
/// Shared name mapping. Spec 21.2 requires each backend to document how ProtoLang names map to
/// target-language conventions; these helpers are that mapping, and they deliberately match what
/// protoc's own generators do so hand-written and generated code agree.
/// </summary>
public static class NameConventions
{
    /// <summary>
    /// Converts a protobuf <c>snake_case</c> name to <c>PascalCase</c>, matching the C# protobuf
    /// generator so <c>unit_price_cents</c> lines up with the generated <c>UnitPriceCents</c>.
    /// </summary>
    public static string ToPascalCase(string name)
    {
        var builder = new StringBuilder(name.Length);
        var capitalizeNext = true;

        foreach (var c in name)
        {
            if (c == '_')
            {
                capitalizeNext = true;
                continue;
            }

            builder.Append(capitalizeNext ? char.ToUpperInvariant(c) : c);
            capitalizeNext = false;
        }

        return builder.ToString();
    }

    /// <summary>
    /// The C# namespace protoc would use for a file: the explicit <c>csharp_namespace</c> option
    /// when present, otherwise the PascalCased protobuf package.
    /// </summary>
    public static string GetCSharpNamespace(FileDescriptor file)
    {
        var explicitNamespace = file.GetOptions()?.CsharpNamespace;
        if (!string.IsNullOrEmpty(explicitNamespace))
        {
            return explicitNamespace;
        }

        if (string.IsNullOrEmpty(file.Package))
        {
            return string.Empty;
        }

        return string.Join('.', file.Package.Split('.').Select(ToPascalCase));
    }

    /// <summary>
    /// The C++ namespace protoc would use: the protobuf package with dots replaced by <c>::</c>.
    /// </summary>
    public static string GetCppNamespace(FileDescriptor file)
        => string.IsNullOrEmpty(file.Package) ? string.Empty : file.Package.Replace(".", "::", StringComparison.Ordinal);

    /// <summary>The generated protobuf C++ header for a .proto file: <c>foo.proto</c> to <c>foo.pb.h</c>.</summary>
    public static string GetCppProtoHeader(FileDescriptor file)
    {
        var name = file.Name;
        return name.EndsWith(".proto", StringComparison.Ordinal)
            ? string.Concat(name.AsSpan(0, name.Length - ".proto".Length), ".pb.h")
            : name + ".pb.h";
    }

    /// <summary>
    /// The C++ nested-type qualifier for a message, for example <c>Outer_Inner</c> for a message
    /// nested inside <c>Outer</c>.
    /// </summary>
    public static string GetCppTypeName(MessageDescriptor message)
    {
        var parts = new List<string>();
        for (var current = message; current is not null; current = current.ContainingType)
        {
            parts.Insert(0, current.Name);
        }

        return string.Join('_', parts);
    }

    /// <summary>The C# type name for a message, including the outer-class nesting protoc applies.</summary>
    public static string GetCSharpTypeName(MessageDescriptor message)
        => QualifyCSharp(message.File, string.Join(".Types.", EnclosingNames(message.ContainingType, message.Name)));

    /// <summary>
    /// The C# type name for an enum. protoc puts nested enums inside the containing message's
    /// <c>Types</c> class, so <c>Outer.E</c> becomes <c>Outer.Types.E</c>.
    /// </summary>
    public static string GetCSharpTypeName(EnumDescriptor enumType)
        => QualifyCSharp(enumType.File, string.Join(".Types.", EnclosingNames(enumType.ContainingType, enumType.Name)));

    /// <summary>
    /// The C++ type name for an enum. protoc flattens nested types into the namespace with
    /// underscores, so <c>Outer.E</c> becomes <c>Outer_E</c>.
    /// </summary>
    public static string GetCppTypeName(EnumDescriptor enumType)
        => string.Join('_', EnclosingNames(enumType.ContainingType, enumType.Name));

    /// <summary>The chain of enclosing message names, outermost first, ending with <paramref name="leaf"/>.</summary>
    private static List<string> EnclosingNames(MessageDescriptor? containingType, string leaf)
    {
        var parts = new List<string>();
        for (var current = containingType; current is not null; current = current.ContainingType)
        {
            parts.Insert(0, current.Name);
        }

        parts.Add(leaf);
        return parts;
    }

    /// <summary>Prefixes a namespace, tolerating files that declare no protobuf package.</summary>
    private static string QualifyCSharp(FileDescriptor file, string name)
    {
        var ns = GetCSharpNamespace(file);
        return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
    }
}
