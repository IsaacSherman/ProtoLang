using Google.Protobuf.Reflection;

namespace ProtoLang.Types;

/// <summary>
/// ProtoLang value kinds. Spec 8.2 lists every protobuf scalar separately, but several of those
/// differ only in wire encoding: sint64, sfixed64, and int64 are all 64-bit signed integers once
/// decoded. Encoding is a schema concern, so the type system collapses them by value domain.
/// </summary>
public enum ScalarKind
{
    Double,
    Float,
    Int32,
    Int64,
    UInt32,
    UInt64,
    Bool,
    String,
    Bytes,
}

public abstract record PlType
{
    public abstract string DisplayName { get; }
}

public sealed record ScalarType(ScalarKind Kind) : PlType
{
    public override string DisplayName => Kind switch
    {
        ScalarKind.Double => "double",
        ScalarKind.Float => "float",
        ScalarKind.Int32 => "int32",
        ScalarKind.Int64 => "int64",
        ScalarKind.UInt32 => "uint32",
        ScalarKind.UInt64 => "uint64",
        ScalarKind.Bool => "bool",
        ScalarKind.String => "string",
        ScalarKind.Bytes => "bytes",
        _ => Kind.ToString().ToLowerInvariant(),
    };

    public bool IsInteger => Kind
        is ScalarKind.Int32 or ScalarKind.Int64 or ScalarKind.UInt32 or ScalarKind.UInt64;

    public bool IsFloatingPoint => Kind is ScalarKind.Double or ScalarKind.Float;

    public bool IsNumeric => IsInteger || IsFloatingPoint;

    public bool IsSigned => Kind is ScalarKind.Int32 or ScalarKind.Int64;

    /// <summary>Bit width for integer kinds; 0 for everything else.</summary>
    public int IntegerWidth => Kind switch
    {
        ScalarKind.Int32 or ScalarKind.UInt32 => 32,
        ScalarKind.Int64 or ScalarKind.UInt64 => 64,
        _ => 0,
    };

    public static readonly ScalarType Int32Type = new(ScalarKind.Int32);
    public static readonly ScalarType Int64Type = new(ScalarKind.Int64);
    public static readonly ScalarType UInt32Type = new(ScalarKind.UInt32);
    public static readonly ScalarType UInt64Type = new(ScalarKind.UInt64);
    public static readonly ScalarType DoubleType = new(ScalarKind.Double);
    public static readonly ScalarType FloatType = new(ScalarKind.Float);
    public static readonly ScalarType BoolType = new(ScalarKind.Bool);
    public static readonly ScalarType StringType = new(ScalarKind.String);
    public static readonly ScalarType BytesType = new(ScalarKind.Bytes);
}

public sealed record MessageType(MessageDescriptor Descriptor) : PlType
{
    public override string DisplayName => Descriptor.FullName;
}

public sealed record EnumPlType(EnumDescriptor Descriptor) : PlType
{
    public override string DisplayName => Descriptor.FullName;
}

/// <summary>A protobuf repeated field. Iterable with <c>for</c>, per spec 14.</summary>
public sealed record RepeatedType(PlType ElementType) : PlType
{
    public override string DisplayName => $"repeated {ElementType.DisplayName}";
}

/// <summary>
/// Method return marker only. Spec 8.1 is explicit that void is not a protobuf value type and
/// cannot be used for fields, variables, or parameters.
/// </summary>
public sealed record VoidType : PlType
{
    public static readonly VoidType Instance = new();

    public override string DisplayName => "void";
}

/// <summary>Stand-in after a binding failure, so one error does not cascade.</summary>
public sealed record ErrorType : PlType
{
    public static readonly ErrorType Instance = new();

    public override string DisplayName => "<error>";
}

public static class TypeFactory
{
    private static readonly Dictionary<string, ScalarType> ScalarsBySpelling = new(StringComparer.Ordinal)
    {
        ["double"] = ScalarType.DoubleType,
        ["float"] = ScalarType.FloatType,
        ["int32"] = ScalarType.Int32Type,
        ["int64"] = ScalarType.Int64Type,
        ["uint32"] = ScalarType.UInt32Type,
        ["uint64"] = ScalarType.UInt64Type,
        ["sint32"] = ScalarType.Int32Type,
        ["sint64"] = ScalarType.Int64Type,
        ["fixed32"] = ScalarType.UInt32Type,
        ["fixed64"] = ScalarType.UInt64Type,
        ["sfixed32"] = ScalarType.Int32Type,
        ["sfixed64"] = ScalarType.Int64Type,
        ["bool"] = ScalarType.BoolType,
        ["string"] = ScalarType.StringType,
        ["bytes"] = ScalarType.BytesType,
    };

    public static ScalarType? TryGetScalar(string spelling)
        => ScalarsBySpelling.TryGetValue(spelling, out var type) ? type : null;

    /// <summary>Maps a protobuf field to its ProtoLang type, including repeated wrapping.</summary>
    public static PlType FromField(FieldDescriptor field)
    {
        var element = FromFieldValue(field);
        return field.IsRepeated ? new RepeatedType(element) : element;
    }

    /// <summary>The type of a single value of <paramref name="field"/>, ignoring repetition.</summary>
    public static PlType FromFieldValue(FieldDescriptor field) => field.FieldType switch
    {
        FieldType.Double => ScalarType.DoubleType,
        FieldType.Float => ScalarType.FloatType,
        FieldType.Int64 or FieldType.SInt64 or FieldType.SFixed64 => ScalarType.Int64Type,
        FieldType.UInt64 or FieldType.Fixed64 => ScalarType.UInt64Type,
        FieldType.Int32 or FieldType.SInt32 or FieldType.SFixed32 => ScalarType.Int32Type,
        FieldType.UInt32 or FieldType.Fixed32 => ScalarType.UInt32Type,
        FieldType.Bool => ScalarType.BoolType,
        FieldType.String => ScalarType.StringType,
        FieldType.Bytes => ScalarType.BytesType,
        FieldType.Message or FieldType.Group => new MessageType(field.MessageType),
        FieldType.Enum => new EnumPlType(field.EnumType),
        _ => ErrorType.Instance,
    };
}
