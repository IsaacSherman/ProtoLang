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
public sealed class CppBackend : ITestProjectScaffold
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

    /// <summary>Exit code a child uses when an <c>expect fail</c> body returned instead of dying.</summary>
    private const int DidNotTerminateExitCode = 91;

    /// <summary>Exit code a child uses when it does not recognize the requested test name.</summary>
    private const int UnknownTestExitCode = 92;

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

        var baseName = Path.GetFileNameWithoutExtension(options.SourceFileName);
        var writer = new SourceWriter("  ");

        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        var functionNames = module.Tests.ToDictionary(
            test => test,
            test => UniqueTestFunctionName(test, usedNames));

        var hasFailTests = module.Tests.Any(t => t.Expectation is IrTestFailExpectation);

        WriteTestHeader(writer, options, baseName, hasFailTests);

        foreach (var test in module.Tests)
        {
            writer.WriteLine(test.Expectation is IrTestFailExpectation
                ? $"static void {functionNames[test]}();"
                : $"static bool {functionNames[test]}();");
        }

        writer.WriteLine();
        EmitMain(writer, module, functionNames);

        foreach (var test in module.Tests)
        {
            writer.WriteLine();
            EmitCppTest(writer, test, functionNames[test]);
        }

        return [new GeneratedFile(baseName + ".tests.cc", writer.ToString())];
    }

    public IReadOnlyList<GeneratedFile> EmitTestProject(
        ScaffoldOptions options,
        DiagnosticBag diagnostics)
        => [new GeneratedFile(CppTestProject.FileName, CppTestProject.Build(options))];

    private static void WriteTestHeader(
        SourceWriter writer,
        BackendOptions options,
        string baseName,
        bool hasFailTests)
    {
        writer.WriteLine("// <auto-generated>");
        writer.WriteLine($"//     Generated by protolangc from {options.SourceFileName}.");
        writer.WriteLine("//     ProtoLang unit tests for generated behavior.");
        writer.WriteLine("//     Run with no arguments to run every test; the exit code is 0 when they");
        writer.WriteLine("//     all pass. '--run <name>' runs one test, which is how a test that expects");
        writer.WriteLine("//     the process to terminate is observed.");
        writer.WriteLine("//     Changes to this file will be lost when the code is regenerated.");
        writer.WriteLine("// </auto-generated>");
        writer.WriteLine();
        writer.WriteLine("#include <cstring>");
        writer.WriteLine("#include <iostream>");

        if (hasFailTests)
        {
            writer.WriteLine("#include <cstdlib>");
            writer.WriteLine("#include <string>");
            writer.WriteLine();
            writer.WriteLine("#if !defined(_WIN32)");
            writer.WriteLine("#include <sys/wait.h>");
            writer.WriteLine("#endif");
        }

        writer.WriteLine();
        writer.WriteLine($"#include \"{baseName}.pl.h\"");
        writer.WriteLine();

        writer.WriteLine("// Reported when '--run' names a test this driver does not have.");
        writer.WriteLine($"static const int kProtoLangUnknownTest = {UnknownTestExitCode};");

        if (hasFailTests)
        {
            writer.WriteLine();
            writer.WriteLine("// Reported when an 'expect fail' body returned, which is what tells the parent");
            writer.WriteLine("// that the method did not terminate rather than that it failed some other way.");
            writer.WriteLine($"static const int kProtoLangDidNotTerminate = {DidNotTerminateExitCode};");
            writer.WriteLine();
            EmitExpectFailHelpers(writer);
        }
        else
        {
            writer.WriteLine();
        }
    }

    /// <summary>
    /// Emits the helpers behind <c>expect fail</c>. What such a test asserts is that the call does
    /// not return, which cannot be observed from inside the process it ends, so the driver reruns
    /// itself for that one test and inspects how the child died.
    /// </summary>
    private static void EmitExpectFailHelpers(SourceWriter writer)
    {
        writer.WriteLine("// Turns what std::system reports into a plain exit code. POSIX returns a wait");
        writer.WriteLine("// status rather than the code itself, and a child killed by a signal has no exit");
        writer.WriteLine("// code at all, which -1 stands in for so it can never match the expected one.");
        using (writer.Block("static int protolang_exit_code(int status)"))
        {
            writer.WriteLine("#if defined(_WIN32)");
            writer.WriteLine("return status;");
            writer.WriteLine("#else");
            using (writer.Block("if (WIFEXITED(status))"))
            {
                writer.WriteLine("return WEXITSTATUS(status);");
            }

            writer.WriteLine();
            writer.WriteLine("return -1;");
            writer.WriteLine("#endif");
        }

        writer.WriteLine();
        writer.WriteLine("// Runs one test in a child process and reports whether it terminated as expected.");
        using (writer.Block(
            "static bool protolang_expect_fail(const char* self, const char* test, const char* identity)"))
        {
            writer.WriteLine("::std::string command = \"\\\"\";");
            writer.WriteLine("command += self;");
            writer.WriteLine("command += \"\\\" --run \";");
            writer.WriteLine("command += test;");
            writer.WriteLine();
            writer.WriteLine("#if defined(_WIN32)");
            writer.WriteLine("// cmd.exe strips the outermost pair of quotes from the command line it is");
            writer.WriteLine("// handed, so a path containing a space needs a second pair to survive.");
            writer.WriteLine("command = \"\\\"\" + command + \"\\\"\";");
            writer.WriteLine("#endif");
            writer.WriteLine();
            writer.WriteLine("const int code = protolang_exit_code(::std::system(command.c_str()));");
            writer.WriteLine();

            EmitExpectFailRejection(
                writer,
                "code == kProtoLangDidNotTerminate",
                "the method returned instead of terminating the process");
            EmitExpectFailRejection(
                writer,
                "code == kProtoLangUnknownTest",
                "the child process did not recognize the test name");

            // The exit code is normative (spec 10.2.1), so this is an equality check rather than
            // "died somehow": a child that crashed for an unrelated reason must not pass.
            using (writer.Block($"if (code != {CppRuntime.FailExitCode})"))
            {
                writer.WriteLine(
                    "::std::cout << \"[FAIL] \" << identity << \" (the child process exited with \" << code");
                writer.Indent();
                writer.WriteLine(
                    $"<< \", not the ProtoLang failure code {CppRuntime.FailExitCode})\" << ::std::endl;");
                writer.Unindent();
                writer.WriteLine("return false;");
            }

            writer.WriteLine();
            writer.WriteLine("::std::cout << \"[ok] \" << identity << ::std::endl;");
            writer.WriteLine("return true;");
        }

        writer.WriteLine();
    }

    private static void EmitExpectFailRejection(SourceWriter writer, string condition, string reason)
    {
        using (writer.Block($"if ({condition})"))
        {
            writer.WriteLine(
                $"::std::cout << \"[FAIL] \" << identity << \" ({EscapeString(reason)})\" << ::std::endl;");
            writer.WriteLine("return false;");
        }

        writer.WriteLine();
    }

    private static void EmitMain(
        SourceWriter writer,
        IrModule module,
        IReadOnlyDictionary<IrTest, string> functionNames)
    {
        using var scope = writer.Block("int main(int argc, char** argv)");

        writer.WriteLine("// Single-test mode, used by the parent process for 'expect fail' tests.");
        using (writer.Block("if (argc >= 3 && ::std::strcmp(argv[1], \"--run\") == 0)"))
        {
            foreach (var test in module.Tests)
            {
                var name = functionNames[test];
                using (writer.Block($"if (::std::strcmp(argv[2], \"{EscapeString(name)}\") == 0)"))
                {
                    if (test.Expectation is IrTestFailExpectation)
                    {
                        writer.WriteLine($"{name}();");
                        writer.WriteLine("return kProtoLangDidNotTerminate;");
                    }
                    else
                    {
                        writer.WriteLine($"return {name}() ? 0 : 1;");
                    }
                }

                writer.WriteLine();
            }

            writer.WriteLine("::std::cout << \"protolang: unknown test \" << argv[2] << ::std::endl;");
            writer.WriteLine("return kProtoLangUnknownTest;");
        }

        writer.WriteLine();
        writer.WriteLine("int failures = 0;");

        foreach (var test in module.Tests)
        {
            var name = functionNames[test];
            writer.WriteLine(test.Expectation is IrTestFailExpectation
                ? $"if (!protolang_expect_fail(argv[0], \"{EscapeString(name)}\", "
                    + $"\"{EscapeString(test.Identity)}\")) {{ ++failures; }}"
                : $"if (!{name}()) {{ ++failures; }}");
        }

        writer.WriteLine();

        // The summary line is what lets a harness tell a driver that ran every test from one that
        // ran none: both exit 0.
        writer.WriteLine(
            $"::std::cout << \"protolang: {module.Tests.Count} test(s), \" << failures << \" failed\" "
            + "<< ::std::endl;");
        writer.WriteLine("return failures == 0 ? 0 : 1;");
    }

    private static void EmitCppTest(SourceWriter writer, IrTest test, string functionName)
    {
        if (test.Expectation is IrTestFailExpectation)
        {
            EmitCppFailTest(writer, test, functionName);
            return;
        }

        if (test.Expectation is not IrTestReturnExpectation returnExpectation)
        {
            throw new ArgumentOutOfRangeException(nameof(test), test.Expectation, "Unhandled C++ test expectation.");
        }

        using var scope = writer.Block($"static bool {functionName}()");

        EmitCppReceiver(writer, test);

        writer.WriteLine($"const auto actual = {CppInvocation(test)};");
        writer.WriteLine($"const auto expected = {Expression(returnExpectation.Value)};");
        using (writer.Block("if (actual != expected)"))
        {
            // Printed on stdout rather than stderr so a harness reading the driver sees every
            // result line in the order they happened.
            writer.WriteLine(
                $"::std::cout << \"[FAIL] {EscapeString(test.Identity)}\""
                + " << \" (expected \" << expected << \", actual \" << actual << \")\" << ::std::endl;");
            writer.WriteLine("return false;");
        }

        writer.WriteLine();
        writer.WriteLine($"::std::cout << \"[ok] {EscapeString(test.Identity)}\" << ::std::endl;");
        writer.WriteLine("return true;");
    }

    /// <summary>
    /// Emits the body a child process runs for an <c>expect fail</c> test. It is never called in
    /// the parent, because the call it makes is expected to end the process.
    /// </summary>
    private static void EmitCppFailTest(SourceWriter writer, IrTest test, string functionName)
    {
        using var scope = writer.Block($"static void {functionName}()");

        EmitCppReceiver(writer, test);

        writer.WriteLine("// The call is expected not to return, so its result is deliberately unused.");
        writer.WriteLine(test.Target.ReturnType is VoidType
            ? $"{CppInvocation(test)};"
            : $"static_cast<void>({CppInvocation(test)});");
    }

    private static void EmitCppReceiver(SourceWriter writer, IrTest test)
    {
        writer.WriteLine($"{QualifiedTypeName(test.Target.Receiver)} receiver;");
        EmitCppFixtureFields(writer, "receiver", false, test.Receiver, new NameAllocator());
    }

    private static string CppInvocation(IrTest test)
    {
        var arguments = new List<string> { "receiver" };
        arguments.AddRange(test.Arguments.Select(a => Expression(a.Value)));

        return $"{QualifiedFunctionName(test.Target)}({string.Join(", ", arguments)})";
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
        foreach (var line in options.PolicyDescription)
        {
            writer.WriteLine($"//     {line}");
        }

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
                writer.WriteLine($"{Expression(assignment.Target)} = {Expression(assignment.Value)};");
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

        // Uniform in C++, unlike C#: protoc emits has_x() for every field with presence,
        // message-typed or not.
        IrFieldPresence presence => $"{Expression(presence.Receiver)}.has_{presence.Field.Name}()",
        IrMethodCall call => EmitCall(call),
        IrBinary binary => EmitBinary(binary),
        IrIntegerDivision division => EmitIntegerDivision(division),
        IrUnary unary => EmitUnary(unary),
        IrConversion conversion => EmitConversion(conversion),
        IrEnumValue enumValue => QualifiedEnumValueName(enumValue.Value),
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
            // Never a bare operator, under any policy: signed overflow is undefined behavior, so
            // even the wrapping case has to be spelled out in the unsigned domain.
            var stem = CppRuntime.Stem(binary.Behavior);
            return $"{RuntimeNamespace}::{stem}_{ArithmeticHelperName(binary.Operator)}_{HelperSuffix(scalar)}"
                + $"({left}, {right})";
        }

        return $"({left} {OperatorText(binary.Operator)} {right})";
    }

    private static string EmitIntegerDivision(IrIntegerDivision division)
    {
        if (division.ResultType is not ScalarType scalar)
        {
            throw new ArgumentOutOfRangeException(
                nameof(division), division.ResultType, "Integer division must produce a scalar.");
        }

        var left = Expression(division.Left);
        var right = Expression(division.Right);
        var stem = CppRuntime.Stem(division.Behavior)
            + (division.Operator == IrBinaryOperator.Modulo ? "_mod" : "_div");
        var suffix = HelperSuffix(scalar);

        return division.ZeroBehavior switch
        {
            ZeroDivisorBehavior.Unreachable => $"{RuntimeNamespace}::{stem}_{suffix}({left}, {right})",
            ZeroDivisorBehavior.Fail => $"{RuntimeNamespace}::{stem}_or_fail_{suffix}({left}, {right})",
            ZeroDivisorBehavior.Fallback =>
                $"{RuntimeNamespace}::{stem}_or_{suffix}({left}, {right}, {Expression(division.OnZero!)})",
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
                ? $"{RuntimeNamespace}::{CppRuntime.Stem(unary.Behavior)}_neg_{HelperSuffix(scalar)}({operand})"
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

    /// <summary>
    /// Emits an explicit conversion (spec 10.3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Integer narrowing needs no helper here, unlike <c>+</c>, <c>-</c>, and <c>*</c>: C++20
    /// defines the conversion to a signed type as two's complement (P0907R4), and the runtime
    /// header already asserts C++20, so <c>static_cast</c> is the explicit statement of the
    /// wrapping rule rather than a reliance on a default. The same goes for widening to a
    /// floating-point type.
    /// </para>
    /// <para>
    /// The two directions that lose range do need helpers, because C++ makes both undefined rather
    /// than merely implementation-defined: converting a <c>double</c> outside <c>float</c>'s range,
    /// and converting a floating-point value outside the target integer's range.
    /// </para>
    /// </remarks>
    private static string EmitConversion(IrConversion conversion)
    {
        if (conversion.Behavior != ConversionBehavior.WrapOrSaturate)
        {
            throw new ArgumentOutOfRangeException(
                nameof(conversion), conversion.Behavior, "Unhandled conversion behavior.");
        }

        var operand = Expression(conversion.Operand);
        var target = TypeName(conversion.TargetType);

        return conversion.Kind switch
        {
            ConversionKind.Identity => operand,
            ConversionKind.IntegerToInteger or ConversionKind.IntegerToFloat =>
                $"static_cast<{target}>({operand})",
            ConversionKind.FloatToFloat => conversion.TargetType.Kind == ScalarKind.Float
                ? $"{RuntimeNamespace}::narrow_f64_to_f32({operand})"
                : $"static_cast<{target}>({operand})",
            ConversionKind.FloatToInteger =>
                $"{RuntimeNamespace}::trunc_sat_f64_to_{HelperSuffix(conversion.TargetType)}"
                + $"(static_cast<double>({operand}))",
            _ => throw new ArgumentOutOfRangeException(
                nameof(conversion), conversion.Kind, "Unhandled conversion kind."),
        };
    }

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

    /// <summary>
    /// An enum constant, fully qualified. protoc emits enum values at namespace scope rather than
    /// as members of the enum, so this qualifies the value name and not the type name.
    /// </summary>
    private static string QualifiedEnumValueName(EnumValueDescriptor value)
    {
        var ns = NameConventions.GetCppNamespace(value.EnumDescriptor.File);
        var name = NameConventions.GetCppValueName(value);
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
