using System.Globalization;
using System.Text;
using Google.Protobuf.Reflection;
using ProtoLang.Backend;
using ProtoLang.Diagnostics;
using ProtoLang.Ir;
using ProtoLang.Types;

namespace ProtoLang.Backend.CSharp;

/// <summary>
/// Emits C# extension methods over the generated protobuf classes.
/// </summary>
/// <remarks>
/// <para>
/// Spec 24.1 leaves the integration shape open. This backend uses extension methods because they
/// impose nothing on the protobuf codegen: the generated messages may live in another assembly,
/// and nothing here depends on them being partial.
/// </para>
/// <para>
/// Arithmetic is emitted inside <c>unchecked(...)</c>. C# integer arithmetic is already unchecked
/// by default, but a consumer can set <c>CheckForOverflowUnderflow</c> at the project level, which
/// would silently turn wrapping into an exception. Stating it per-operation makes the semantics a
/// property of the generated code rather than of the consumer's build.
/// </para>
/// </remarks>
public sealed class CSharpBackend : ITestProjectScaffold
{
    private static readonly HashSet<string> ReservedWords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
        "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
        "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
        "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
        "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true",
        "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual",
        "void", "volatile", "while",
    };

    /// <summary>Name of the extension-method receiver parameter.</summary>
    private const string ReceiverName = "self";

    public string Name => "csharp";

    public IReadOnlyList<GeneratedFile> Emit(
        IrModule module,
        BackendOptions options,
        DiagnosticBag diagnostics)
    {
        foreach (var method in module.Methods.Where(m => m.IsVirtual))
        {
            diagnostics.Error(
                "PL1001",
                "virtual methods are not supported by the C# backend",
                $"'{method.Name}' is declared virtual. Override semantics are still an open "
                + "design question (spec 17), so this backend rejects them rather than guessing.",
                method.Span);
        }

        if (diagnostics.HasErrors)
        {
            return [];
        }

        var writer = new SourceWriter();
        WriteHeader(writer, options);

        var byNamespace = module.Methods
            .GroupBy(m => NameConventions.GetCSharpNamespace(m.Receiver.File))
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        foreach (var namespaceGroup in byNamespace)
        {
            var hasNamespace = !string.IsNullOrEmpty(namespaceGroup.Key);
            IDisposable? namespaceScope = hasNamespace ? writer.Block($"namespace {namespaceGroup.Key}") : null;

            var byReceiver = namespaceGroup
                .GroupBy(m => m.Receiver.FullName, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal);

            var firstClass = true;
            foreach (var receiverGroup in byReceiver)
            {
                if (!firstClass)
                {
                    writer.WriteLine();
                }

                firstClass = false;
                var methods = receiverGroup.ToList();
                EmitReceiverClass(writer, methods[0].Receiver, methods);
            }

            namespaceScope?.Dispose();
        }

        var fileName = Path.GetFileNameWithoutExtension(options.SourceFileName) + ".g.cs";
        return
        [
            new GeneratedFile(CSharpRuntime.FileName, CSharpRuntime.Source),
            new GeneratedFile(fileName, writer.ToString()),
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

        var sourceName = Path.GetFileNameWithoutExtension(options.SourceFileName);
        var writer = new SourceWriter();
        WriteTestHeader(writer, options);

        // Names are allocated once and reused, because an 'expect fail' test needs the same
        // identifier in three places: the fact, the key it passes to the child process, and the
        // case label the child dispatches on.
        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        var methodNames = module.Tests.ToDictionary(test => test, test => UniqueTestMethodName(test, usedNames));
        var failTests = module.Tests.Where(t => t.Expectation is IrTestFailExpectation).ToList();

        using (writer.Block("namespace ProtoLang.GeneratedTests"))
        {
            var className = ToIdentifier(sourceName) + "ProtoLangTests";
            using var classScope = writer.Block($"public sealed class {className}");

            var first = true;
            foreach (var test in module.Tests)
            {
                if (!first)
                {
                    writer.WriteLine();
                }

                first = false;
                EmitTest(writer, test, methodNames[test], sourceName);
            }

            if (failTests.Count > 0)
            {
                writer.WriteLine();
                EmitFailTestDispatcher(writer, failTests, methodNames, sourceName);
            }
        }

        var fileName = sourceName + ".tests.g.cs";

        // The support file is emitted only when something needs it, so a source with no 'expect
        // fail' test does not gain a file that starts child processes.
        return failTests.Count > 0
            ?
            [
                new GeneratedFile(CSharpTestRuntime.FileName, CSharpTestRuntime.Source),
                new GeneratedFile(fileName, writer.ToString()),
            ]
            : [new GeneratedFile(fileName, writer.ToString())];
    }

    public IReadOnlyList<GeneratedFile> EmitTestProject(
        ScaffoldOptions options,
        DiagnosticBag diagnostics)
        => [new GeneratedFile(CSharpTestProject.FileName, CSharpTestProject.Build(options))];

    private static void WriteHeader(SourceWriter writer, BackendOptions options)
    {
        writer.WriteLine("// <auto-generated>");
        writer.WriteLine($"//     Generated by protolangc from {options.SourceFileName}.");
        writer.WriteLine("//     Integer arithmetic uses ProtoLang wrapping semantics (two's complement),");
        writer.WriteLine("//     stated explicitly so project-level 'checked' settings cannot change it.");
        writer.WriteLine("//     Changes to this file will be lost when the code is regenerated.");
        writer.WriteLine("// </auto-generated>");
        writer.WriteLine();
        writer.WriteLine("#nullable enable");
        writer.WriteLine();
        writer.WriteLine("// A ProtoLang comparison is emitted exactly as written, including 'x != x', which is");
        writer.WriteLine("// how a NaN is detected. Generated code must build in a project that treats");
        writer.WriteLine("// warnings as errors.");
        writer.WriteLine("#pragma warning disable CS1718 // Comparison made to same variable");
        writer.WriteLine();
    }

    private static void WriteTestHeader(SourceWriter writer, BackendOptions options)
    {
        writer.WriteLine("// <auto-generated>");
        writer.WriteLine($"//     Generated by protolangc from {options.SourceFileName}.");
        writer.WriteLine("//     ProtoLang unit tests for generated behavior.");
        writer.WriteLine("//     Changes to this file will be lost when the code is regenerated.");
        writer.WriteLine("// </auto-generated>");
        writer.WriteLine();
        writer.WriteLine("#nullable enable");
        writer.WriteLine();
        writer.WriteLine("// Every expectation is asserted the same way, whatever its type, so that the");
        writer.WriteLine("// emitted assertion follows from the declared 'expect return' rather than from the");
        writer.WriteLine("// backend guessing which overload reads better.");
        writer.WriteLine("#pragma warning disable xUnit2004 // Do not use Assert.Equal for boolean conditions");
        writer.WriteLine();
    }

    private static void EmitTest(SourceWriter writer, IrTest test, string methodName, string sourceName)
    {
        // The display name is the backend-independent identity rather than the mangled method name,
        // so a conformance harness reading the test log sees the same string every backend reports.
        writer.WriteLine($"[global::Xunit.Fact(DisplayName = {FormatString(test.Identity)})]");
        using var scope = writer.Block($"public void {methodName}()");

        if (test.Expectation is IrTestFailExpectation)
        {
            EmitFailTest(writer, test, methodName, sourceName);
            return;
        }

        if (test.Expectation is not IrTestReturnExpectation returnExpectation)
        {
            throw new ArgumentOutOfRangeException(nameof(test), test.Expectation, "Unhandled C# test expectation.");
        }

        EmitReceiverCreation(writer, test.Receiver);
        writer.WriteLine(
            $"global::Xunit.Assert.Equal({Expression(returnExpectation.Value, "receiver")}, {Invocation(test)});");
    }

    /// <summary>
    /// Emits an <c>expect fail</c> test. What it asserts is that the call does not return, which no
    /// assertion inside the same process can observe, so the call happens in a child process and
    /// this only inspects how that process ended.
    /// </summary>
    private static void EmitFailTest(SourceWriter writer, IrTest test, string methodName, string sourceName)
    {
        var key = FormatString(sourceName + ":" + methodName);

        writer.WriteLine($"var unexpected = {CSharpTestRuntime.TypeName}.DescribeExpectFail({key});");
        writer.WriteLine("global::Xunit.Assert.True(");
        writer.Indent();
        writer.WriteLine("unexpected is null,");
        writer.WriteLine($"{FormatString(test.Identity + " expected terminal failure, but ")} + unexpected);");
        writer.Unindent();
    }

    /// <summary>
    /// Emits the module initializer a child process lands in. It runs before the test host's entry
    /// point, so a child never starts a normal test run.
    /// </summary>
    private static void EmitFailTestDispatcher(
        SourceWriter writer,
        IReadOnlyList<IrTest> failTests,
        IReadOnlyDictionary<IrTest, string> methodNames,
        string sourceName)
    {
        writer.WriteLine("/// <summary>");
        writer.WriteLine("/// Runs the single 'expect fail' test this process was launched for, and returns");
        writer.WriteLine("/// immediately during an ordinary test run.");
        writer.WriteLine("/// </summary>");
        writer.WriteLine("[global::System.Runtime.CompilerServices.ModuleInitializer]");
        using var scope = writer.Block("internal static void RunRequestedProtoLangFailTest()");

        writer.WriteLine(
            $"var requested = {CSharpTestRuntime.TypeName}.RequestedTest({FormatString(sourceName)});");
        using (writer.Block("if (requested is null)"))
        {
            writer.WriteLine("return;");
        }

        writer.WriteLine();
        writer.WriteLine($"var key = {FormatString(sourceName + ":")} + requested;");
        writer.WriteLine();
        writer.WriteLine("// The parent reads these two markers rather than an exit code: how a host");
        writer.WriteLine("// reports a fail-fast, and how long it takes to, is not something a test can");
        writer.WriteLine("// depend on. The first says the body ran; the second says it came back, which");
        writer.WriteLine("// is exactly the failure an 'expect fail' test is looking for.");
        writer.WriteLine($"global::System.Console.Out.WriteLine({CSharpTestRuntime.TypeName}.StartedMarker + key);");
        writer.WriteLine("global::System.Console.Out.Flush();");
        writer.WriteLine();
        using (writer.Block("switch (requested)"))
        {
            foreach (var test in failTests)
            {
                // Each case gets its own block: the receiver local would otherwise collide with the
                // one declared by the case before it.
                writer.WriteLine($"case {FormatString(methodNames[test])}:");
                writer.WriteLine("{");
                writer.Indent();

                EmitReceiverCreation(writer, test.Receiver);

                // The call is expected not to return, so its result is deliberately unused.
                writer.WriteLine(test.Target.ReturnType is VoidType
                    ? $"{Invocation(test)};"
                    : $"_ = {Invocation(test)};");
                writer.WriteLine("break;");

                writer.Unindent();
                writer.WriteLine("}");
                writer.WriteLine();
            }

            writer.WriteLine("default:");
            writer.Indent();
            writer.WriteLine($"global::System.Environment.Exit({CSharpTestRuntime.TypeName}.UnknownTest);");
            writer.WriteLine("break;");
            writer.Unindent();
        }

        writer.WriteLine();
        writer.WriteLine("// Reached only when the method returned instead of terminating the process.");
        writer.WriteLine($"global::System.Console.Out.WriteLine({CSharpTestRuntime.TypeName}.ReturnedMarker + key);");
        writer.WriteLine("global::System.Console.Out.Flush();");
        writer.WriteLine($"global::System.Environment.Exit({CSharpTestRuntime.TypeName}.DidNotTerminate);");
    }

    /// <summary>The call under test, against the local named <c>receiver</c>.</summary>
    private static string Invocation(IrTest test)
    {
        var arguments = new List<string> { "receiver" };
        arguments.AddRange(test.Arguments.Select(a => Expression(a.Value, "receiver")));

        return $"{QualifiedExtensionClassName(test.Target.Receiver)}."
            + $"{NameConventions.ToPascalCase(test.Target.Name)}({string.Join(", ", arguments)})";
    }

    private static void EmitReceiverCreation(SourceWriter writer, IrTestMessageValue receiver)
    {
        var typeName = "global::" + NameConventions.GetCSharpTypeName(receiver.Descriptor);
        if (receiver.Fields.Count == 0)
        {
            writer.WriteLine($"var receiver = new {typeName}();");
            return;
        }

        writer.WriteLine($"var receiver = new {typeName}");
        writer.WriteLine("{");
        writer.Indent();
        EmitTestFieldInitializers(writer, receiver);
        writer.Unindent();
        writer.WriteLine("};");
    }

    private static void EmitTestFieldInitializers(SourceWriter writer, IrTestMessageValue message)
    {
        foreach (var group in message.Fields.GroupBy(v => v.Field.FieldNumber).OrderBy(g => g.Key))
        {
            var field = group.First().Field;
            var property = NameConventions.ToPascalCase(field.Name);

            if (field.IsRepeated)
            {
                writer.WriteLine($"{property} =");
                writer.WriteLine("{");
                writer.Indent();
                foreach (var value in group)
                {
                    EmitTestCollectionElement(writer, value);
                }

                writer.Unindent();
                writer.WriteLine("},");
                continue;
            }

            var fieldValue = group.Single();
            if (fieldValue.MessageValue is not null)
            {
                writer.WriteLine($"{property} =");
                EmitTestMessageValue(writer, fieldValue.MessageValue, "},");
                continue;
            }

            writer.WriteLine($"{property} = {Expression(fieldValue.ScalarValue!, "receiver")},");
        }
    }

    private static void EmitTestCollectionElement(SourceWriter writer, IrTestFieldValue value)
    {
        if (value.MessageValue is not null)
        {
            EmitTestMessageValue(writer, value.MessageValue, "},");
            return;
        }

        writer.WriteLine($"{Expression(value.ScalarValue!, "receiver")},");
    }

    private static void EmitTestMessageValue(SourceWriter writer, IrTestMessageValue value, string closer)
    {
        writer.WriteLine($"new global::{NameConventions.GetCSharpTypeName(value.Descriptor)}");
        writer.WriteLine("{");
        writer.Indent();
        EmitTestFieldInitializers(writer, value);
        writer.Unindent();
        writer.WriteLine(closer);
    }

    private static void EmitReceiverClass(
        SourceWriter writer,
        MessageDescriptor receiver,
        IReadOnlyList<IrMethod> methods)
    {
        var className = ExtensionClassName(receiver);

        writer.WriteLine($"/// <summary>ProtoLang behavior for <c>{receiver.FullName}</c>.</summary>");
        using var classScope = writer.Block($"public static class {className}");

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
    }

    private static void EmitMethod(SourceWriter writer, IrMethod method)
    {
        var returnType = TypeName(method.ReturnType);
        var methodName = NameConventions.ToPascalCase(method.Name);
        var receiverType = "global::" + NameConventions.GetCSharpTypeName(method.Receiver);

        var parameters = new List<string> { $"this {receiverType} {ReceiverName}" };
        parameters.AddRange(method.Parameters.Select(p => $"{TypeName(p.Type)} {Escape(p.Name)}"));

        using var methodScope = writer.Block(
            $"public static {returnType} {methodName}({string.Join(", ", parameters)})");

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
                    $"foreach (var {Escape(forEach.Loop.Name)} in {Expression(forEach.Collection)})");
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

    private static string Expression(IrExpression expression, string receiverName = ReceiverName) => expression switch
    {
        IrThis => receiverName,
        IrLocalReference local => Escape(local.Local.Name),
        IrParameterReference parameter => Escape(parameter.Parameter.Name),
        IrFieldAccess field => $"{Expression(field.Receiver, receiverName)}.{NameConventions.ToPascalCase(field.Field.Name)}",
        IrMethodCall call => EmitCall(call, receiverName),
        IrBinary binary => EmitBinary(binary, receiverName),
        IrIntegerDivision division => EmitIntegerDivision(division, receiverName),
        IrUnary unary => EmitUnary(unary, receiverName),
        IrConversion conversion => EmitConversion(conversion, receiverName),
        IrEnumValue enumValue => "global::"
            + NameConventions.GetCSharpTypeName(enumValue.EnumType.Descriptor)
            + "." + NameConventions.GetCSharpValueName(enumValue.Value),
        IrLiteral literal => EmitLiteral(literal),
        _ => throw new ArgumentOutOfRangeException(nameof(expression), expression, "Unhandled expression."),
    };

    private static string EmitCall(IrMethodCall call, string receiverName)
    {
        var arguments = new List<string> { Expression(call.Receiver, receiverName) };
        arguments.AddRange(call.Arguments.Select(a => Expression(a, receiverName)));

        var methodName = NameConventions.ToPascalCase(call.Target.Name);
        return $"{QualifiedExtensionClassName(call.Target.Receiver)}.{methodName}({string.Join(", ", arguments)})";
    }

    private static string QualifiedExtensionClassName(MessageDescriptor receiver)
    {
        var ns = NameConventions.GetCSharpNamespace(receiver.File);
        var className = ExtensionClassName(receiver);
        return string.IsNullOrEmpty(ns) ? $"global::{className}" : $"global::{ns}.{className}";
    }

    private static string ExtensionClassName(MessageDescriptor receiver)
    {
        var parts = new List<string>();
        for (var current = receiver; current is not null; current = current.ContainingType)
        {
            parts.Insert(0, NameConventions.ToPascalCase(current.Name));
        }

        return string.Join('_', parts) + "ProtoLangExtensions";
    }

    private static string EmitBinary(IrBinary binary, string receiverName)
    {
        var left = Expression(binary.Left, receiverName);
        var right = Expression(binary.Right, receiverName);
        var op = OperatorText(binary.Operator);

        // Integer / and % arrive as IrIntegerDivision, so only + - * reach here.
        if (binary.IsArithmetic && binary.ResultType is ScalarType { IsInteger: true })
        {
            if (binary.Behavior != ArithmeticBehavior.Wrap)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(binary), binary.Behavior, "Unhandled arithmetic behavior.");
            }

            // unchecked() gives both the semantics and the grouping parentheses.
            return $"unchecked({left} {op} {right})";
        }

        return $"({left} {op} {right})";
    }

    private static string EmitIntegerDivision(IrIntegerDivision division, string receiverName)
    {
        if (division.Behavior != ArithmeticBehavior.Wrap)
        {
            throw new ArgumentOutOfRangeException(
                nameof(division), division.Behavior, "Unhandled arithmetic behavior.");
        }

        var left = Expression(division.Left, receiverName);
        var right = Expression(division.Right, receiverName);
        var stem = division.Operator == IrBinaryOperator.Modulo ? "WrapModulo" : "WrapDivide";

        // Every form goes through a helper, even Unreachable: MIN / -1 traps at the hardware level
        // regardless of unchecked.
        return division.ZeroBehavior switch
        {
            ZeroDivisorBehavior.Unreachable => $"{CSharpRuntime.TypeName}.{stem}({left}, {right})",
            ZeroDivisorBehavior.Fail => $"{CSharpRuntime.TypeName}.{stem}OrFail({left}, {right})",
            ZeroDivisorBehavior.Fallback =>
                $"{CSharpRuntime.TypeName}.{stem}Or({left}, {right}, {Expression(division.OnZero!, receiverName)})",
            _ => throw new ArgumentOutOfRangeException(
                nameof(division), division.ZeroBehavior, "Unhandled zero-divisor behavior."),
        };
    }

    private static string EmitUnary(IrUnary unary, string receiverName)
    {
        var operand = Expression(unary.Operand, receiverName);

        if (unary.Operator == IrUnaryOperator.Negate)
        {
            return unary.ResultType is ScalarType { IsInteger: true }
                ? $"unchecked(-{operand})"
                : $"(-{operand})";
        }

        return $"(!{operand})";
    }

    /// <summary>
    /// Emits an explicit conversion (spec 10.3).
    /// </summary>
    /// <remarks>
    /// Integer targets are wrapped in <c>unchecked</c>, which states the wrapping and also stops a
    /// consumer's <c>CheckForOverflowUnderflow</c> from turning a deliberate narrowing into an
    /// <see cref="OverflowException"/>. Conversions to a floating-point type are fully defined in
    /// C# and unaffected by checked context, so a plain cast says everything. A floating-point
    /// source converting to an integer is the one case the language leaves unspecified when the
    /// value is out of range -- and throws under a checked context -- so it goes through the
    /// runtime, where the saturating result is spelled out.
    /// </remarks>
    private static string EmitConversion(IrConversion conversion, string receiverName)
    {
        if (conversion.Behavior != ConversionBehavior.WrapOrSaturate)
        {
            throw new ArgumentOutOfRangeException(
                nameof(conversion), conversion.Behavior, "Unhandled conversion behavior.");
        }

        // Every Expression() result is self-delimiting -- an identifier, a member access, a call, a
        // non-negative literal, or something already wrapped in parentheses -- so a cast can be
        // prefixed without re-parenthesizing the operand.
        var operand = Expression(conversion.Operand, receiverName);
        var target = TypeName(conversion.TargetType);

        return conversion.Kind switch
        {
            ConversionKind.Identity => operand,
            ConversionKind.IntegerToInteger => $"unchecked(({target}){operand})",
            ConversionKind.IntegerToFloat or ConversionKind.FloatToFloat => $"({target}){operand}",
            ConversionKind.FloatToInteger =>
                $"{CSharpRuntime.TypeName}.{FloatToIntegerHelper(conversion.TargetType)}((double){operand})",
            _ => throw new ArgumentOutOfRangeException(
                nameof(conversion), conversion.Kind, "Unhandled conversion kind."),
        };
    }

    /// <summary>
    /// The runtime helper for a floating-point to integer conversion. The source is widened to
    /// <c>double</c> at the call site, which is exact, so one helper per target covers both
    /// <c>float</c> and <c>double</c> sources.
    /// </summary>
    private static string FloatToIntegerHelper(ScalarType target) => target.Kind switch
    {
        ScalarKind.Int32 => "ToInt32",
        ScalarKind.Int64 => "ToInt64",
        ScalarKind.UInt32 => "ToUInt32",
        ScalarKind.UInt64 => "ToUInt64",
        _ => throw new ArgumentOutOfRangeException(nameof(target), target.Kind, "Not an integer kind."),
    };

    private static string EmitLiteral(IrLiteral literal) => literal.Value switch
    {
        null => "default",
        bool value => value ? "true" : "false",
        long value => literal.LiteralType is ScalarType scalar
            ? FormatInteger(value, scalar)
            : value.ToString(CultureInfo.InvariantCulture),
        double value => FormatFloatingPoint(value, literal.LiteralType),
        string value => FormatString(value),
        _ => throw new ArgumentOutOfRangeException(nameof(literal), literal.Value, "Unhandled literal."),
    };

    /// <summary>
    /// Formats a floating-point literal with the suffix its type requires. A <c>float</c>-typed
    /// literal must carry <c>f</c>: C# will not implicitly narrow a <c>double</c> literal.
    /// </summary>
    private static string FormatFloatingPoint(double value, PlType type)
    {
        var isFloat = type is ScalarType { Kind: ScalarKind.Float };
        var typeName = isFloat ? "float" : "double";

        // These have no literal form in C# and would otherwise emit as 'Infinityd'.
        if (double.IsNaN(value))
        {
            return $"{typeName}.NaN";
        }

        if (double.IsPositiveInfinity(value))
        {
            return $"{typeName}.PositiveInfinity";
        }

        if (double.IsNegativeInfinity(value))
        {
            return $"{typeName}.NegativeInfinity";
        }

        return value.ToString("R", CultureInfo.InvariantCulture) + (isFloat ? "f" : "d");
    }

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
                case '\0': builder.Append("\\0"); break;
                default:
                    // \uXXXX is fixed-width, so it cannot absorb the character that follows.
                    if (char.IsControl(c))
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
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
        var text = value.ToString(CultureInfo.InvariantCulture);
        return scalar.Kind switch
        {
            ScalarKind.Int64 => text + "L",
            ScalarKind.UInt64 => text + "UL",
            ScalarKind.UInt32 => text + "U",
            _ => text,
        };
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

    private static string TypeName(PlType type) => type switch
    {
        VoidType => "void",
        ScalarType scalar => scalar.Kind switch
        {
            ScalarKind.Double => "double",
            ScalarKind.Float => "float",
            ScalarKind.Int32 => "int",
            ScalarKind.Int64 => "long",
            ScalarKind.UInt32 => "uint",
            ScalarKind.UInt64 => "ulong",
            ScalarKind.Bool => "bool",
            ScalarKind.String => "string",
            ScalarKind.Bytes => "global::Google.Protobuf.ByteString",
            _ => throw new ArgumentOutOfRangeException(nameof(type), scalar.Kind, "Unhandled scalar."),
        },
        MessageType message => "global::" + NameConventions.GetCSharpTypeName(message.Descriptor),
        EnumPlType enumType => "global::" + NameConventions.GetCSharpTypeName(enumType.Descriptor),
        RepeatedType repeated =>
            $"global::Google.Protobuf.Collections.RepeatedField<{TypeName(repeated.ElementType)}>",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unhandled type."),
    };

    private static string Escape(string name) => ReservedWords.Contains(name) ? "@" + name : name;

    private static string UniqueTestMethodName(IrTest test, HashSet<string> usedNames)
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
        var capitalizeNext = true;

        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(capitalizeNext ? char.ToUpperInvariant(c) : c);
                capitalizeNext = false;
                continue;
            }

            capitalizeNext = true;
        }

        if (builder.Length == 0 || !char.IsLetter(builder[0]))
        {
            builder.Insert(0, 'T');
        }

        var identifier = builder.ToString();
        return ReservedWords.Contains(identifier) ? "@" + identifier : identifier;
    }
}
