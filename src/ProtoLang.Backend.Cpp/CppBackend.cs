using System.Globalization;
using System.Text;
using Google.Protobuf.Reflection;
using ProtoLang.Backend;
using ProtoLang.Diagnostics;
using ProtoLang.Ir;
using ProtoLang.Types;

namespace ProtoLang.Backend.Cpp;

/// <summary>
/// Emits a header-only C++ library of free functions over the generated protobuf messages.
/// </summary>
/// <remarks>
/// Spec 24.2 lists several candidate shapes. Free functions in the message's own namespace are
/// chosen here because they subclass nothing, require no protoc insertion points, and work
/// identically whether the protobuf codegen is regenerated or vendored. Declarations are emitted
/// ahead of definitions so methods may call one another in any order.
/// </remarks>
public sealed class CppBackend : ITestBackend
{
    private static readonly HashSet<string> ReservedWords = new(StringComparer.Ordinal)
    {
        "alignas", "alignof", "and", "asm", "auto", "bitand", "bitor", "bool", "break", "case",
        "catch", "char", "class", "compl", "concept", "const", "consteval", "constexpr", "continue",
        "co_await", "co_return", "co_yield", "decltype", "default", "delete", "do", "double",
        "dynamic_cast", "else", "enum", "explicit", "export", "extern", "false", "float", "for",
        "friend", "goto", "if", "inline", "int", "long", "mutable", "namespace", "new", "noexcept",
        "not", "nullptr", "operator", "or", "private", "protected", "public", "register",
        "reinterpret_cast", "requires", "return", "short", "signed", "sizeof", "static",
        "static_assert", "static_cast", "struct", "switch", "template", "this", "thread_local",
        "throw", "true", "try", "typedef", "typeid", "typename", "union", "unsigned", "using",
        "virtual", "void", "volatile", "wchar_t", "while", "xor",
    };

    private const string ReceiverName = "self";
    private const string RuntimeNamespace = "::protolang_runtime";

    public string Name => "cpp";

    public IReadOnlyList<GeneratedFile> Emit(
        IrModule module,
        BackendOptions options,
        DiagnosticBag diagnostics)
    {
        foreach (var method in module.Methods.Where(m => m.IsVirtual))
        {
            diagnostics.Error(
                "PL1101",
                "virtual methods are not supported by the C++ backend",
                $"'{method.Name}' is declared virtual. Override semantics are still an open "
                + "design question (spec 17), so this backend rejects them rather than guessing.",
                method.Span);
        }

        if (diagnostics.HasErrors)
        {
            return [];
        }

        var baseName = Path.GetFileNameWithoutExtension(options.SourceFileName);
        var writer = new SourceWriter("  ");
        var guard = MakeIncludeGuard(baseName);

        WriteHeader(writer, options, guard, module);

        var byNamespace = module.Methods
            .GroupBy(m => NameConventions.GetCppNamespace(m.Receiver.File))
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        foreach (var namespaceGroup in byNamespace)
        {
            var methods = namespaceGroup.OrderBy(m => m.Receiver.FullName, StringComparer.Ordinal)
                .ThenBy(m => m.Name, StringComparer.Ordinal)
                .ToList();

            var hasNamespace = !string.IsNullOrEmpty(namespaceGroup.Key);
            IDisposable? namespaceScope = hasNamespace
                ? writer.Block($"namespace {namespaceGroup.Key}", $"}}  // namespace {namespaceGroup.Key}")
                : null;

            writer.WriteLine("// Declarations precede definitions so methods may call one another");
            writer.WriteLine("// regardless of the order they appear in the ProtoLang source.");
            foreach (var method in methods)
            {
                writer.WriteLine(Signature(method) + ";");
            }

            writer.WriteLine();

            var first = true;
            foreach (var method in methods)
            {
                if (!first)
                {
                    writer.WriteLine();
                }

                first = false;
                EmitMethod(writer, method);
            }

            namespaceScope?.Dispose();
        }

        writer.WriteLine();
        writer.WriteLine($"#endif  // {guard}");

        return
        [
            new GeneratedFile(CppRuntime.FileName, CppRuntime.Source),
            new GeneratedFile(baseName + ".pl.h", writer.ToString()),
        ];
    }

    public IReadOnlyList<GeneratedFile> EmitTests(
        IrModule module,
        BackendOptions options,
        DiagnosticBag diagnostics)
    {
        if (module.Tests.Count == 0)
        {
            return [];
        }

        foreach (var test in module.Tests.Where(t => t.Expectation is IrTestFailExpectation))
        {
            diagnostics.Error(
                "PL1102",
                "expected-fail tests are not supported by the C++ backend",
                $"Test '{test.Name}' expects terminal failure, but the C++ runtime maps that to abort.",
                test.Span,
                "A later test backend can run these out-of-process.");
        }

        if (diagnostics.HasErrors)
        {
            return [];
        }

        var baseName = Path.GetFileNameWithoutExtension(options.SourceFileName);
        var writer = new SourceWriter("  ");
        WriteTestHeader(writer, options, baseName);

        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        var functionNames = new List<string>();
        foreach (var test in module.Tests)
        {
            var functionName = UniqueTestFunctionName(test, usedNames);
            functionNames.Add(functionName);
            writer.WriteLine($"static bool {functionName}();");
        }

        writer.WriteLine();
        using (writer.Block("int main()"))
        {
            writer.WriteLine("int failures = 0;");
            foreach (var name in functionNames)
            {
                writer.WriteLine($"if (!{name}()) {{ ++failures; }}");
            }

            writer.WriteLine("return failures == 0 ? 0 : 1;");
        }

        usedNames.Clear();
        foreach (var test in module.Tests)
        {
            writer.WriteLine();
            EmitCppTest(writer, test, usedNames);
        }

        return [new GeneratedFile(baseName + ".tests.cc", writer.ToString())];
    }

    private static void WriteTestHeader(SourceWriter writer, BackendOptions options, string baseName)
    {
        writer.WriteLine("// <auto-generated>");
        writer.WriteLine($"//     Generated by protolangc from {options.SourceFileName}.");
        writer.WriteLine("//     ProtoLang unit tests for generated behavior.");
        writer.WriteLine("//     Changes to this file will be lost when the code is regenerated.");
        writer.WriteLine("// </auto-generated>");
        writer.WriteLine();
        writer.WriteLine("#include <iostream>");
        writer.WriteLine();
        writer.WriteLine($"#include \"{baseName}.pl.h\"");
        writer.WriteLine();
    }

    private static void EmitCppTest(SourceWriter writer, IrTest test, HashSet<string> usedNames)
    {
        if (test.Expectation is not IrTestReturnExpectation returnExpectation)
        {
            throw new ArgumentOutOfRangeException(nameof(test), test.Expectation, "Unhandled C++ test expectation.");
        }

        var functionName = UniqueTestFunctionName(test, usedNames);
        using var scope = writer.Block($"static bool {functionName}()");

        writer.WriteLine($"{QualifiedTypeName(test.Target.Receiver)} receiver;");
        var fixtureLocals = new NameAllocator();
        EmitCppFixtureFields(writer, "receiver", false, test.Receiver, fixtureLocals);

        var arguments = new List<string> { "receiver" };
        arguments.AddRange(test.Arguments.Select(a => Expression(a.Value)));
        var actual = $"{QualifiedFunctionName(test.Target)}({string.Join(", ", arguments)})";
        writer.WriteLine($"const auto actual = {actual};");
        writer.WriteLine($"const auto expected = {Expression(returnExpectation.Value)};");
        using (writer.Block("if (actual != expected)"))
        {
            writer.WriteLine($"::std::cerr << \"{EscapeString(functionName)} failed\" << ::std::endl;");
            writer.WriteLine("return false;");
        }

        writer.WriteLine("return true;");
    }

    private static void EmitCppFixtureFields(
        SourceWriter writer,
        string target,
        bool targetIsPointer,
        IrTestMessageValue message,
        NameAllocator names)
    {
        var access = targetIsPointer ? "->" : ".";
        foreach (var value in message.Fields.OrderBy(v => v.Field.FieldNumber))
        {
            var field = value.Field;
            if (value.MessageValue is not null)
            {
                var local = names.Next(field.Name);
                var mutator = field.IsRepeated ? $"add_{field.Name}" : $"mutable_{field.Name}";
                writer.WriteLine($"auto* {local} = {target}{access}{mutator}();");
                EmitCppFixtureFields(writer, local, true, value.MessageValue, names);
                continue;
            }

            var setter = field.IsRepeated ? $"add_{field.Name}" : $"set_{field.Name}";
            writer.WriteLine($"{target}{access}{setter}({Expression(value.ScalarValue!)});");
        }
    }

    private static void WriteHeader(SourceWriter writer, BackendOptions options, string guard, IrModule module)
    {
        writer.WriteLine("// <auto-generated>");
        writer.WriteLine($"//     Generated by protolangc from {options.SourceFileName}.");
        writer.WriteLine("//     Integer arithmetic uses ProtoLang wrapping semantics (two's complement).");
        writer.WriteLine("//     Signed overflow is undefined behavior in C++, so arithmetic routes through");
        writer.WriteLine($"//     {CppRuntime.FileName} rather than using the built-in operators directly.");
        writer.WriteLine("//     Changes to this file will be lost when the code is regenerated.");
        writer.WriteLine("// </auto-generated>");
        writer.WriteLine();
        writer.WriteLine($"#ifndef {guard}");
        writer.WriteLine($"#define {guard}");
        writer.WriteLine();
        writer.WriteLine("#include <cstdint>");
        writer.WriteLine("#include <limits>");
        writer.WriteLine("#include <string>");
        writer.WriteLine();

        var protoHeaders = module.Methods
            .Select(m => NameConventions.GetCppProtoHeader(m.Receiver.File))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(header => header, StringComparer.Ordinal);

        foreach (var header in protoHeaders)
        {
            writer.WriteLine($"#include \"{header}\"");
        }

        writer.WriteLine($"#include \"{CppRuntime.FileName}\"");
        writer.WriteLine();
    }

    private static string MakeIncludeGuard(string baseName)
    {
        var builder = new StringBuilder("PROTOLANG_");
        foreach (var c in baseName)
        {
            builder.Append(char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_');
        }

        builder.Append("_PL_H_");
        return builder.ToString();
    }

    private static string Signature(IrMethod method)
    {
        var parameters = new List<string> { $"const {QualifiedTypeName(method.Receiver)}& {ReceiverName}" };
        parameters.AddRange(method.Parameters.Select(p => $"{ParameterTypeName(p.Type)} {Escape(p.Name)}"));

        // The method name needs escaping too: ProtoLang names are snake_case and every C++ keyword
        // is lowercase, so 'fn class()' or 'fn operator()' would otherwise emit invalid C++.
        return $"inline {TypeName(method.ReturnType)} {Escape(method.Name)}({string.Join(", ", parameters)})";
    }

    private static void EmitMethod(SourceWriter writer, IrMethod method)
    {
        using var scope = writer.Block(Signature(method));
        EmitStatements(writer, method.Body.Statements);
    }

    private static void EmitStatements(SourceWriter writer, IReadOnlyList<IrStatement> statements)
    {
        foreach (var statement in statements)
        {
            EmitStatement(writer, statement);
        }
    }

    private static void EmitStatement(SourceWriter writer, IrStatement statement)
    {
        switch (statement)
        {
            case IrBlock block:
            {
                using var scope = writer.Block(string.Empty);
                EmitStatements(writer, block.Statements);
                break;
            }

            case IrVariableDeclaration declaration:
                writer.WriteLine(
                    $"{TypeName(declaration.Local.Type)} {Escape(declaration.Local.Name)} = "
                    + $"{Expression(declaration.Initializer)};");
                break;

            case IrAssignment assignment:
                writer.WriteLine($"{Escape(assignment.Target.Name)} = {Expression(assignment.Value)};");
                break;

            case IrReturn { Value: null }:
                writer.WriteLine("return;");
                break;

            case IrReturn returnStatement:
                writer.WriteLine($"return {Expression(returnStatement.Value!)};");
                break;

            case IrForEach forEach:
            {
                using var scope = writer.Block(
                    $"for (const auto& {Escape(forEach.Loop.Name)} : {Expression(forEach.Collection)})");
                EmitStatements(writer, forEach.Body.Statements);
                break;
            }

            case IrIf ifStatement:
                EmitIf(writer, ifStatement);
                break;

            case IrWhile whileStatement:
            {
                using var scope = writer.Block($"while ({Expression(whileStatement.Condition)})");
                EmitStatements(writer, whileStatement.Body.Statements);
                break;
            }

            case IrBreak:
                writer.WriteLine("break;");
                break;

            case IrContinue:
                writer.WriteLine("continue;");
                break;

            case IrExpressionStatement expression:
                writer.WriteLine($"{Expression(expression.Expression)};");
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(statement), statement, "Unhandled statement.");
        }
    }

    /// <summary>
    /// Emits an if/else chain. The chain is flattened rather than nested, so an 'else if' in the
    /// source stays an 'else if' in the output instead of gaining a brace level per branch.
    /// </summary>
    private static void EmitIf(SourceWriter writer, IrIf statement)
    {
        var keyword = "if";

        while (true)
        {
            using (writer.Block($"{keyword} ({Expression(statement.Condition)})"))
            {
                EmitStatements(writer, statement.Then.Statements);
            }

            // The binder only ever puts a block or a nested 'if' in the else branch.
            if (statement.Else is IrIf nested)
            {
                statement = nested;
                keyword = "else if";
                continue;
            }

            if (statement.Else is IrBlock elseBlock)
            {
                using var scope = writer.Block("else");
                EmitStatements(writer, elseBlock.Statements);
            }

            return;
        }
    }

    private static string Expression(IrExpression expression) => expression switch
    {
        IrThis => ReceiverName,
        IrLocalReference local => Escape(local.Local.Name),
        IrParameterReference parameter => Escape(parameter.Parameter.Name),
        IrFieldAccess field => $"{Expression(field.Receiver)}.{field.Field.Name}()",
        IrMethodCall call => EmitCall(call),
        IrBinary binary => EmitBinary(binary),
        IrIntegerDivision division => EmitIntegerDivision(division),
        IrUnary unary => EmitUnary(unary),
        IrLiteral literal => EmitLiteral(literal),
        _ => throw new ArgumentOutOfRangeException(nameof(expression), expression, "Unhandled expression."),
    };

    private static string EmitCall(IrMethodCall call)
    {
        var arguments = new List<string> { Expression(call.Receiver) };
        arguments.AddRange(call.Arguments.Select(Expression));

        var ns = NameConventions.GetCppNamespace(call.Target.Receiver.File);
        var name = Escape(call.Target.Name);
        var qualified = string.IsNullOrEmpty(ns) ? $"::{name}" : $"::{ns}::{name}";

        return $"{qualified}({string.Join(", ", arguments)})";
    }

    private static string EmitBinary(IrBinary binary)
    {
        var left = Expression(binary.Left);
        var right = Expression(binary.Right);

        if (binary.IsArithmetic && binary.ResultType is ScalarType { IsInteger: true } scalar)
        {
            return binary.Behavior switch
            {
                ArithmeticBehavior.Wrap =>
                    $"{RuntimeNamespace}::wrap_{ArithmeticHelperName(binary.Operator)}_{HelperSuffix(scalar)}"
                    + $"({left}, {right})",
                _ => throw new ArgumentOutOfRangeException(
                    nameof(binary), binary.Behavior, "Unhandled arithmetic behavior."),
            };
        }

        return $"({left} {OperatorText(binary.Operator)} {right})";
    }

    private static string EmitIntegerDivision(IrIntegerDivision division)
    {
        if (division.Behavior != ArithmeticBehavior.Wrap)
        {
            throw new ArgumentOutOfRangeException(
                nameof(division), division.Behavior, "Unhandled arithmetic behavior.");
        }

        if (division.ResultType is not ScalarType scalar)
        {
            throw new ArgumentOutOfRangeException(
                nameof(division), division.ResultType, "Integer division must produce a scalar.");
        }

        var left = Expression(division.Left);
        var right = Expression(division.Right);
        var stem = division.Operator == IrBinaryOperator.Modulo ? "mod" : "div";
        var suffix = HelperSuffix(scalar);

        return division.ZeroBehavior switch
        {
            ZeroDivisorBehavior.Unreachable => $"{RuntimeNamespace}::wrap_{stem}_{suffix}({left}, {right})",
            ZeroDivisorBehavior.Fail => $"{RuntimeNamespace}::wrap_{stem}_or_fail_{suffix}({left}, {right})",
            ZeroDivisorBehavior.Fallback =>
                $"{RuntimeNamespace}::wrap_{stem}_or_{suffix}({left}, {right}, {Expression(division.OnZero!)})",
            _ => throw new ArgumentOutOfRangeException(
                nameof(division), division.ZeroBehavior, "Unhandled zero-divisor behavior."),
        };
    }

    private static string EmitUnary(IrUnary unary)
    {
        var operand = Expression(unary.Operand);

        if (unary.Operator == IrUnaryOperator.Negate)
        {
            return unary.ResultType is ScalarType { IsInteger: true } scalar
                ? $"{RuntimeNamespace}::wrap_neg_{HelperSuffix(scalar)}({operand})"
                : $"(-{operand})";
        }

        return $"(!{operand})";
    }

    private static string ArithmeticHelperName(IrBinaryOperator op) => op switch
    {
        IrBinaryOperator.Add => "add",
        IrBinaryOperator.Subtract => "sub",
        IrBinaryOperator.Multiply => "mul",
        IrBinaryOperator.Divide => "div",
        IrBinaryOperator.Modulo => "mod",
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, "Not an arithmetic operator."),
    };

    private static string HelperSuffix(ScalarType scalar) => scalar.Kind switch
    {
        ScalarKind.Int32 => "i32",
        ScalarKind.Int64 => "i64",
        ScalarKind.UInt32 => "u32",
        ScalarKind.UInt64 => "u64",
        _ => throw new ArgumentOutOfRangeException(nameof(scalar), scalar.Kind, "Not an integer kind."),
    };

    private static string EmitLiteral(IrLiteral literal) => literal.Value switch
    {
        null => "{}",
        bool value => value ? "true" : "false",
        long value when literal.LiteralType is ScalarType scalar => FormatInteger(value, scalar),
        long value => value.ToString(CultureInfo.InvariantCulture),
        double value => FormatDouble(value, literal.LiteralType),
        string value => FormatString(value),
        _ => throw new ArgumentOutOfRangeException(nameof(literal), literal.Value, "Unhandled literal."),
    };

    /// <summary>
    /// Formats a string literal. The lexer decodes escapes, so the IR holds real control
    /// characters; re-escaping them here is what keeps the emitted literal on one line.
    /// </summary>
    private static string FormatString(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');

        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': builder.Append("\\\\"); break;
                case '"': builder.Append("\\\""); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (char.IsControl(c))
                    {
                        // Octal, not \x: a hex escape in C++ is greedy and would swallow any hex
                        // digit that happens to follow it. Octal escapes stop after three digits.
                        builder.Append('\\').Append(Convert.ToString(c, 8).PadLeft(3, '0'));
                    }
                    else
                    {
                        // Non-ASCII passes through as UTF-8; generated files are written as UTF-8.
                        builder.Append(c);
                    }

                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }

    private static string FormatInteger(long value, ScalarType scalar)
    {
        // long.MinValue has no positive counterpart, so the literal cannot be written directly:
        // the lexer would parse '-9223372036854775808' as negation of an out-of-range literal.
        if (value == long.MinValue)
        {
            return "(-9223372036854775807LL - 1)";
        }

        var text = value.ToString(CultureInfo.InvariantCulture);
        return scalar.Kind switch
        {
            ScalarKind.Int64 => text + "LL",
            ScalarKind.UInt64 => text + "ULL",
            ScalarKind.UInt32 => text + "U",
            _ => text,
        };
    }

    private static string FormatDouble(double value, PlType type)
    {
        var isFloat = type is ScalarType { Kind: ScalarKind.Float };

        // No literal form in C++; these come from <limits> via the numeric_limits template.
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            var cppType = isFloat ? "float" : "double";
            var accessor = double.IsNaN(value) ? "quiet_NaN()" : "infinity()";
            var text2 = $"::std::numeric_limits<{cppType}>::{accessor}";
            return double.IsNegativeInfinity(value) ? $"(-{text2})" : text2;
        }

        var text = value.ToString("R", CultureInfo.InvariantCulture);
        if (!text.Contains('.', StringComparison.Ordinal)
            && !text.Contains('E', StringComparison.Ordinal)
            && !text.Contains('e', StringComparison.Ordinal))
        {
            text += ".0";
        }

        return type is ScalarType { Kind: ScalarKind.Float } ? text + "f" : text;
    }

    private static string OperatorText(IrBinaryOperator op) => op switch
    {
        IrBinaryOperator.Add => "+",
        IrBinaryOperator.Subtract => "-",
        IrBinaryOperator.Multiply => "*",
        IrBinaryOperator.Divide => "/",
        IrBinaryOperator.Modulo => "%",
        IrBinaryOperator.Equal => "==",
        IrBinaryOperator.NotEqual => "!=",
        IrBinaryOperator.LessThan => "<",
        IrBinaryOperator.LessThanOrEqual => "<=",
        IrBinaryOperator.GreaterThan => ">",
        IrBinaryOperator.GreaterThanOrEqual => ">=",
        IrBinaryOperator.LogicalAnd => "&&",
        IrBinaryOperator.LogicalOr => "||",
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, "Unhandled operator."),
    };

    private static string QualifiedTypeName(MessageDescriptor message)
    {
        var ns = NameConventions.GetCppNamespace(message.File);
        var name = NameConventions.GetCppTypeName(message);
        return string.IsNullOrEmpty(ns) ? $"::{name}" : $"::{ns}::{name}";
    }

    private static string QualifiedFunctionName(IrMethodSignature signature)
    {
        var ns = NameConventions.GetCppNamespace(signature.Receiver.File);
        var name = Escape(signature.Name);
        return string.IsNullOrEmpty(ns) ? $"::{name}" : $"::{ns}::{name}";
    }

    private static string TypeName(PlType type) => type switch
    {
        VoidType => "void",
        ScalarType scalar => scalar.Kind switch
        {
            ScalarKind.Double => "double",
            ScalarKind.Float => "float",
            ScalarKind.Int32 => "::std::int32_t",
            ScalarKind.Int64 => "::std::int64_t",
            ScalarKind.UInt32 => "::std::uint32_t",
            ScalarKind.UInt64 => "::std::uint64_t",
            ScalarKind.Bool => "bool",
            ScalarKind.String or ScalarKind.Bytes => "::std::string",
            _ => throw new ArgumentOutOfRangeException(nameof(type), scalar.Kind, "Unhandled scalar."),
        },
        MessageType message => QualifiedTypeName(message.Descriptor),
        EnumPlType enumType => QualifiedEnumName(enumType.Descriptor),
        RepeatedType repeated => RepeatedTypeName(repeated),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unhandled type."),
    };

    private static string QualifiedEnumName(EnumDescriptor descriptor)
    {
        var ns = NameConventions.GetCppNamespace(descriptor.File);
        var name = NameConventions.GetCppTypeName(descriptor);
        return string.IsNullOrEmpty(ns) ? $"::{name}" : $"::{ns}::{name}";
    }

    private static string RepeatedTypeName(RepeatedType repeated)
    {
        // protobuf stores message and string elements in RepeatedPtrField, everything else in
        // RepeatedField. Repeated enums are stored as int, not as the enum type.
        var container = repeated.ElementType is MessageType
            or ScalarType { Kind: ScalarKind.String or ScalarKind.Bytes }
            ? "RepeatedPtrField"
            : "RepeatedField";

        var element = repeated.ElementType is EnumPlType ? "int" : TypeName(repeated.ElementType);
        return $"::google::protobuf::{container}<{element}>";
    }

    /// <summary>Message-typed parameters are passed by const reference; scalars by value.</summary>
    private static string ParameterTypeName(PlType type) => type switch
    {
        MessageType or RepeatedType or ScalarType { Kind: ScalarKind.String or ScalarKind.Bytes }
            => $"const {TypeName(type)}&",
        _ => TypeName(type),
    };

    private static string Escape(string name) => ReservedWords.Contains(name) ? name + "_" : name;

    private static string UniqueTestFunctionName(IrTest test, HashSet<string> usedNames)
    {
        var baseName = ToIdentifier($"{test.Target.Receiver.Name}_{test.Target.Name}_{test.Name}");
        var candidate = baseName;
        var suffix = 2;
        while (!usedNames.Add(candidate))
        {
            candidate = baseName + suffix.ToString(CultureInfo.InvariantCulture);
            suffix++;
        }

        return candidate;
    }

    private static string ToIdentifier(string text)
    {
        var builder = new StringBuilder(text.Length);

        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(c);
                continue;
            }

            if (builder.Length > 0 && builder[^1] != '_')
            {
                builder.Append('_');
            }
        }

        if (builder.Length == 0 || !(char.IsLetter(builder[0]) || builder[0] == '_'))
        {
            builder.Insert(0, "test_");
        }

        var identifier = builder.ToString().TrimEnd('_');
        return ReservedWords.Contains(identifier) ? identifier + "_" : identifier;
    }

    private static string EscapeString(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is '"' or '\\')
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    private sealed class NameAllocator
    {
        private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

        public string Next(string stem)
        {
            var escaped = Escape(stem);
            _counts.TryGetValue(escaped, out var count);
            _counts[escaped] = count + 1;
            return count == 0 ? escaped : escaped + count.ToString(CultureInfo.InvariantCulture);
        }
    }
}
