using Google.Protobuf.Reflection;
using ProtoLang.Config;
using ProtoLang.Diagnostics;
using ProtoLang.Ir;
using ProtoLang.Syntax;
using ProtoLang.Types;

namespace ProtoLang.Binding;

/// <summary>
/// Resolves names against protobuf descriptors, type-checks the AST, and lowers it to the typed
/// IR. Runs in two passes so a method may call another method declared later in the file, or in a
/// different extend block.
/// </summary>
public sealed class Binder
{
    private readonly DiagnosticBag _diagnostics;
    private readonly Dictionary<string, MessageDescriptor> _messagesByFullName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<MessageDescriptor>> _messagesBySimpleName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EnumDescriptor> _enumsByFullName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<EnumDescriptor>> _enumsBySimpleName = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Receiver, string Method), IrMethodSignature> _methods = new();
    private readonly NumericPolicy _policy;
    private readonly ProjectConfig _config;

    public Binder(
        IReadOnlyList<FileDescriptor> files,
        DiagnosticBag diagnostics,
        NumericPolicy? policy = null,
        ProjectConfig? config = null)
    {
        _diagnostics = diagnostics;
        _config = config ?? ProjectConfig.Default;
        _policy = policy ?? new NumericPolicy(_config);

        foreach (var file in files)
        {
            foreach (var message in file.MessageTypes)
            {
                IndexMessage(message);
            }

            foreach (var enumType in file.EnumTypes)
            {
                IndexEnum(enumType);
            }
        }
    }

    private void IndexMessage(MessageDescriptor message)
    {
        _messagesByFullName[message.FullName] = message;

        if (!_messagesBySimpleName.TryGetValue(message.Name, out var list))
        {
            list = [];
            _messagesBySimpleName[message.Name] = list;
        }

        list.Add(message);

        // Enums nested in a message are only reachable through this walk, so they are indexed here
        // rather than in the constructor's top-level loop.
        foreach (var nested in message.EnumTypes)
        {
            IndexEnum(nested);
        }

        foreach (var nested in message.NestedTypes)
        {
            IndexMessage(nested);
        }
    }

    private void IndexEnum(EnumDescriptor enumType)
    {
        _enumsByFullName[enumType.FullName] = enumType;

        if (!_enumsBySimpleName.TryGetValue(enumType.Name, out var list))
        {
            list = [];
            _enumsBySimpleName[enumType.Name] = list;
        }

        list.Add(enumType);
    }

    public IrModule Bind(CompilationUnit unit)
    {
        // Pass 1: resolve extend targets and collect signatures.
        var resolvedExtends = new List<(ExtendDeclaration Declaration, MessageDescriptor Receiver)>();

        foreach (var extend in unit.Extends)
        {
            var receiver = ResolveMessage(extend.MessageName, extend.Span);
            if (receiver is null)
            {
                continue;
            }

            resolvedExtends.Add((extend, receiver));
            WarnIfWellKnown(receiver, extend);

            foreach (var method in extend.Methods)
            {
                DeclareMethod(receiver, method);
            }
        }

        // Pass 2: bind bodies now that every signature is visible.
        var methods = new List<IrMethod>();

        foreach (var (declaration, receiver) in resolvedExtends)
        {
            foreach (var method in declaration.Methods)
            {
                var bound = BindMethod(receiver, method);
                if (bound is not null)
                {
                    methods.Add(bound);
                }
            }
        }

        var tests = new List<IrTest>();
        foreach (var test in unit.Tests)
        {
            var bound = BindTest(test);
            if (bound is not null)
            {
                tests.Add(bound);
            }
        }

        return new IrModule(methods, tests);
    }

    /// <summary>
    /// Extending a well-known type is allowed, but it is not self-contained the way extending a
    /// project's own message is, and nothing in the source says so.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A project's message and its ProtoLang behavior are generated together, so a consumer given
    /// the message classes gets the behavior with them. Timestamp does not work that way: the
    /// consumer already has it from their protobuf runtime, without the extensions. Code written
    /// against it therefore only compiles for consumers who also reference the library holding the
    /// generated behavior, which makes that library a real dependency rather than something assumed
    /// present because protobuf is.
    /// </para>
    /// <para>
    /// A warning rather than an error, because the code is valid and the emission strategy is sound.
    /// ProtoLang only ever generates extensions, never types, so extending Timestamp does not touch
    /// Timestamp -- which is also why the extension form travels further than a member function
    /// would. In the spirit of PL0056 on an unreachable on_zero: something true about the program
    /// that the program itself cannot say.
    /// </para>
    /// </remarks>
    private void WarnIfWellKnown(MessageDescriptor receiver, ExtendDeclaration extend)
    {
        // The same rule ScaffoldOptions applies when deciding which schemas an emitted project
        // should generate, and for the same reason: these arrive with the runtime.
        if (!receiver.File.Name.StartsWith("google/protobuf/", StringComparison.Ordinal))
        {
            return;
        }

        _diagnostics.Warning(
            "PL0077",
            "extending a well-known type",
            $"'{receiver.Name}' comes from the protobuf runtime, so consumers have it without this "
            + "behavior. The generated extensions have to ship as their own library for anyone to "
            + "call them.",
            extend.Span,
            "Two libraries that both extend this type also emit their extension classes into a "
            + "namespace neither owns, and a consumer referencing both gets an ambiguous call.");
    }

    private MessageDescriptor? ResolveMessage(string name, SourceSpan span)
    {
        if (_messagesByFullName.TryGetValue(name, out var byFullName))
        {
            return byFullName;
        }

        if (_messagesBySimpleName.TryGetValue(name, out var candidates))
        {
            if (candidates.Count == 1)
            {
                return candidates[0];
            }

            _diagnostics.Error(
                "PL0020",
                "ambiguous message name",
                $"'{name}' matches {candidates.Count} messages: "
                + string.Join(", ", candidates.Select(c => c.FullName)) + ".",
                span,
                "Qualify the name with its protobuf package.");
            return null;
        }

        _diagnostics.Error(
            "PL0021",
            "unknown message type",
            $"No protobuf message named '{name}' was found in the imported schemas.",
            span,
            "Check the 'import proto' declarations and the --proto_path include directories.");
        return null;
    }

    private void DeclareMethod(MessageDescriptor receiver, MethodDeclaration method)
    {
        var key = (receiver.FullName, method.Name);

        if (_methods.ContainsKey(key))
        {
            _diagnostics.Error(
                "PL0022",
                "duplicate method",
                $"'{receiver.FullName}' already defines a method named '{method.Name}'.",
                method.Span,
                "Overloading is not supported; give the method a distinct name.");
            return;
        }

        if (receiver.FindFieldByName(method.Name) is not null)
        {
            _diagnostics.Error(
                "PL0023",
                "method name collides with a field",
                $"'{receiver.FullName}' has a field named '{method.Name}'.",
                method.Span,
                "Methods and protobuf fields share one name space on a message.");
            return;
        }

        var returnType = method.ReturnType is null
            ? VoidType.Instance
            : ResolveTypeReference(method.ReturnType);

        var parameterNames = new List<string>();
        var parameterTypes = new List<PlType>();
        foreach (var parameter in method.Parameters)
        {
            parameterNames.Add(parameter.Name);
            var type = ResolveTypeReference(parameter.Type);
            if (type is VoidType)
            {
                _diagnostics.Error(
                    "PL0024",
                    "void is not a value type",
                    $"Parameter '{parameter.Name}' cannot be declared void.",
                    parameter.Span,
                    "void is a return marker only (spec 8.1).");
                type = ErrorType.Instance;
            }

            parameterTypes.Add(type);
        }

        _methods[key] = new IrMethodSignature(receiver, method.Name, returnType, parameterNames, parameterTypes);
    }

    private void ReportAmbiguousTypeName(string name, SourceSpan span, IEnumerable<string> fullNames)
    {
        var ordered = fullNames.Order(StringComparer.Ordinal).ToList();

        _diagnostics.Error(
            "PL0074",
            "ambiguous type name",
            $"'{name}' matches {ordered.Count} types: " + string.Join(", ", ordered) + ".",
            span,
            "Qualify the name with its protobuf package.");
    }

    private PlType ResolveTypeReference(TypeReference reference)
    {
        if (reference.Name == "void")
        {
            return VoidType.Instance;
        }

        var scalar = TypeFactory.TryGetScalar(reference.Name);
        if (scalar is not null)
        {
            return scalar;
        }

        // A fully qualified name is unambiguous by construction, so it is tried before any
        // simple-name lookup that could report a false ambiguity.
        if (_messagesByFullName.TryGetValue(reference.Name, out var messageByFullName))
        {
            return new MessageType(messageByFullName);
        }

        if (_enumsByFullName.TryGetValue(reference.Name, out var enumByFullName))
        {
            return new EnumPlType(enumByFullName);
        }

        _messagesBySimpleName.TryGetValue(reference.Name, out var messages);
        _enumsBySimpleName.TryGetValue(reference.Name, out var enums);

        var candidateCount = (messages?.Count ?? 0) + (enums?.Count ?? 0);

        if (candidateCount > 1)
        {
            // Messages and enums share one type name space here, so a name matching one of each is
            // just as ambiguous as a name matching two enums.
            var fullNames = (messages ?? Enumerable.Empty<MessageDescriptor>()).Select(m => m.FullName)
                .Concat((enums ?? Enumerable.Empty<EnumDescriptor>()).Select(e => e.FullName));

            ReportAmbiguousTypeName(reference.Name, reference.Span, fullNames);
            return ErrorType.Instance;
        }

        if (messages is { Count: 1 })
        {
            return new MessageType(messages[0]);
        }

        if (enums is { Count: 1 })
        {
            return new EnumPlType(enums[0]);
        }

        _diagnostics.Error(
            "PL0025",
            "unknown type",
            $"'{reference.Name}' is not a protobuf scalar, message, or enum type.",
            reference.Span,
            "ProtoLang types come only from the protobuf type universe (spec 8.1).");
        return ErrorType.Instance;
    }

    private IrMethod? BindMethod(MessageDescriptor receiver, MethodDeclaration method)
    {
        if (!_methods.TryGetValue((receiver.FullName, method.Name), out var signature))
        {
            // Pass 1 already reported why this method was rejected.
            return null;
        }

        var parameters = new List<IrParameter>();
        var scope = new Scope(null);

        for (var i = 0; i < method.Parameters.Count; i++)
        {
            var parameter = new IrParameter(method.Parameters[i].Name, signature.ParameterTypes[i]);
            parameters.Add(parameter);

            if (!scope.TryDeclareParameter(parameter))
            {
                _diagnostics.Error(
                    "PL0026",
                    "duplicate parameter",
                    $"A parameter named '{parameter.Name}' is already declared.",
                    method.Parameters[i].Span);
            }
        }

        var context = new MethodContext(receiver, signature.ReturnType, parameters);
        var body = BindBlock(method.Body, scope, context);

        if (signature.ReturnType is not VoidType && !NeverFallsThrough(body))
        {
            _diagnostics.Error(
                "PL0027",
                "missing return statement",
                $"'{method.Name}' declares a return type of "
                + $"'{signature.ReturnType.DisplayName}' but not all paths return a value.",
                method.Span);
        }

        return new IrMethod(signature, parameters, body, method.IsVirtual, method.Span);
    }

    private IrTest? BindTest(TestDeclaration test)
    {
        var signature = ResolveTestTarget(test.TargetName, test.Span);
        if (signature is null)
        {
            return null;
        }

        var context = new MethodContext(
            signature.Receiver,
            signature.ReturnType,
            [],
            AllowImplicitReceiverFields: false);
        var receiver = BindTestReceiver(test.Receiver, signature.Receiver, context);
        var arguments = BindTestArguments(test, signature, context);
        var expectation = BindTestExpectation(test.Expectation, signature, context);

        return new IrTest(signature, test.Name, receiver, arguments, expectation, test.Span);
    }

    private IrMethodSignature? ResolveTestTarget(string targetName, SourceSpan span)
    {
        var dot = targetName.LastIndexOf('.');
        if (dot <= 0 || dot == targetName.Length - 1)
        {
            _diagnostics.Error(
                "PL0057",
                "invalid test target",
                $"'{targetName}' is not a method target.",
                span,
                "Write tests against a receiver method, for example 'test Invoice.total_cents'.");
            return null;
        }

        var receiverName = targetName[..dot];
        var methodName = targetName[(dot + 1)..];
        var receiver = ResolveMessage(receiverName, span);
        if (receiver is null)
        {
            return null;
        }

        if (_methods.TryGetValue((receiver.FullName, methodName), out var signature))
        {
            return signature;
        }

        _diagnostics.Error(
            "PL0058",
            "unknown test target",
            $"'{receiver.FullName}' has no ProtoLang method named '{methodName}'.",
            span,
            "Tests can only target methods declared in an extend block.");
        return null;
    }

    private IrTestMessageValue BindTestReceiver(
        TestReceiverFixture receiver,
        MessageDescriptor descriptor,
        MethodContext context)
        => BindTestMessageValue(receiver.Fields, descriptor, receiver.Span, context);

    private IrTestMessageValue BindTestMessageValue(
        IReadOnlyList<TestFieldInitializer> fields,
        MessageDescriptor descriptor,
        SourceSpan span,
        MethodContext context)
    {
        var values = new List<IrTestFieldValue>();
        var seenSingular = new HashSet<string>(StringComparer.Ordinal);

        foreach (var field in fields)
        {
            var descriptorField = descriptor.FindFieldByName(field.FieldName);
            if (descriptorField is null)
            {
                _diagnostics.Error(
                    "PL0059",
                    "unknown fixture field",
                    $"'{descriptor.FullName}' has no field named '{field.FieldName}'.",
                    field.Span);
                continue;
            }

            if (descriptorField.IsMap)
            {
                _diagnostics.Error(
                    "PL0060",
                    "maps are not supported in test fixtures",
                    $"Field '{descriptorField.Name}' is a map, which this compiler version does not support.",
                    field.Span);
                continue;
            }

            if (!descriptorField.IsRepeated && !seenSingular.Add(descriptorField.Name))
            {
                _diagnostics.Error(
                    "PL0061",
                    "duplicate fixture field",
                    $"Field '{descriptorField.Name}' is set more than once.",
                    field.Span,
                    "Repeated fields may be listed multiple times; singular fields may not.");
                continue;
            }

            switch (field)
            {
                case TestScalarFieldInitializer scalar:
                {
                    var expectedType = TypeFactory.FromFieldValue(descriptorField);

                    // Enums are not excluded here: an enum field is set from a named constant, which
                    // is an ordinary expression. PL0063 below catches one of the wrong enum type.
                    if (expectedType is MessageType)
                    {
                        _diagnostics.Error(
                            "PL0062",
                            "fixture field requires a nested value",
                            $"Field '{descriptorField.Name}' is message '{expectedType.DisplayName}' and cannot be set from an expression.",
                            field.Span,
                            $"Write '{descriptorField.Name} {{ ... }}' to build the nested message.");
                        continue;
                    }

                    var value = BindExpression(scalar.Value, new Scope(null), context, expectedType);
                    if (value.Type is not ErrorType && !TypesMatch(expectedType, value.Type))
                    {
                        _diagnostics.Error(
                            "PL0063",
                            "fixture field type mismatch",
                            $"Field '{descriptorField.Name}' expects '{expectedType.DisplayName}' but got '{value.Type.DisplayName}'.",
                            field.Span);
                    }

                    values.Add(new IrTestFieldValue(descriptorField, value, null, field.Span));
                    break;
                }

                case TestMessageFieldInitializer message:
                {
                    var fieldType = TypeFactory.FromFieldValue(descriptorField);
                    if (fieldType is not MessageType messageType)
                    {
                        _diagnostics.Error(
                            "PL0064",
                            "fixture field is not a message",
                            $"Field '{descriptorField.Name}' has type '{fieldType.DisplayName}' and cannot contain nested fields.",
                            field.Span);
                        continue;
                    }

                    var value = BindTestMessageValue(message.Fields, messageType.Descriptor, message.Span, context);
                    values.Add(new IrTestFieldValue(descriptorField, null, value, field.Span));
                    break;
                }
            }
        }

        return new IrTestMessageValue(descriptor, values, span);
    }

    private IReadOnlyList<IrTestArgument> BindTestArguments(
        TestDeclaration test,
        IrMethodSignature signature,
        MethodContext context)
    {
        var declared = new Dictionary<string, TestArgumentDeclaration>(StringComparer.Ordinal);
        foreach (var argument in test.Arguments)
        {
            if (!declared.TryAdd(argument.Name, argument))
            {
                _diagnostics.Error(
                    "PL0065",
                    "duplicate test argument",
                    $"Argument '{argument.Name}' is supplied more than once.",
                    argument.Span);
            }
        }

        var arguments = new List<IrTestArgument>();
        for (var i = 0; i < signature.ParameterNames.Count; i++)
        {
            var name = signature.ParameterNames[i];
            if (!declared.TryGetValue(name, out var declaration))
            {
                _diagnostics.Error(
                    "PL0066",
                    "missing test argument",
                    $"Test '{test.Name}' does not supply argument '{name}'.",
                    test.Span);
                continue;
            }

            var expectedType = signature.ParameterTypes[i];
            var value = BindExpression(declaration.Value, new Scope(null), context, expectedType);
            if (value.Type is not ErrorType && !TypesMatch(expectedType, value.Type))
            {
                _diagnostics.Error(
                    "PL0067",
                    "test argument type mismatch",
                    $"Argument '{name}' expects '{expectedType.DisplayName}' but got '{value.Type.DisplayName}'.",
                    declaration.Span);
            }

            arguments.Add(new IrTestArgument(name, value, declaration.Span));
        }

        foreach (var extra in declared.Keys.Except(signature.ParameterNames, StringComparer.Ordinal))
        {
            _diagnostics.Error(
                "PL0068",
                "unknown test argument",
                $"'{signature.Name}' has no parameter named '{extra}'.",
                declared[extra].Span);
        }

        return arguments;
    }

    private IrTestExpectation BindTestExpectation(
        TestExpectation expectation,
        IrMethodSignature signature,
        MethodContext context)
    {
        switch (expectation)
        {
            case TestFailExpectation fail:
                return new IrTestFailExpectation(fail.Span);

            case TestReturnExpectation returns:
            {
                if (signature.ReturnType is VoidType)
                {
                    _diagnostics.Error(
                        "PL0069",
                        "void method cannot expect a return value",
                        $"'{signature.Name}' does not return a value.",
                        returns.Span);
                }

                var value = BindExpression(returns.Value, new Scope(null), context, signature.ReturnType);
                if (signature.ReturnType is not VoidType
                    && value.Type is not ErrorType
                    && !TypesMatch(signature.ReturnType, value.Type))
                {
                    _diagnostics.Error(
                        "PL0070",
                        "test expectation type mismatch",
                        $"'{signature.Name}' returns '{signature.ReturnType.DisplayName}' but the expectation is '{value.Type.DisplayName}'.",
                        returns.Span);
                }

                return new IrTestReturnExpectation(value, returns.Span);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(expectation), expectation, "Unhandled expectation.");
        }
    }

    /// <summary>
    /// All-paths-return analysis. A method that declares a return type is well formed when control
    /// cannot reach the end of its body, so this asks the inverse question: after this statement
    /// runs, can the statement following it run?
    /// </summary>
    /// <remarks>
    /// <c>break</c> and <c>continue</c> do not return a value, but they do stop the enclosing block
    /// from falling through, which is what this predicate measures. A method body ending in a stray
    /// <c>break</c> therefore escapes PL0027 -- but PL0072 has already rejected it.
    /// </remarks>
    private static bool NeverFallsThrough(IrStatement statement) => statement switch
    {
        IrReturn or IrBreak or IrContinue => true,

        // Anything after a terminator in the same block is unreachable, so its position does not
        // matter: the block as a whole cannot fall through.
        IrBlock block => block.Statements.Any(NeverFallsThrough),

        IrIf ifStatement => ifStatement.Else is not null
            && NeverFallsThrough(ifStatement.Then)
            && NeverFallsThrough(ifStatement.Else),

        // A loop with a real condition may run zero times, and 'for' iterates a repeated field that
        // may be empty, so neither terminates the flow. 'while true' does: the only way out is a
        // 'break', or a 'return' that this predicate credits at the enclosing level anyway.
        IrWhile { Condition: IrLiteral { Value: true } } loop => !ContainsBreak(loop.Body),

        _ => false,
    };

    /// <summary>
    /// Whether a <c>break</c> would exit the loop whose body is <paramref name="statement"/>.
    /// Nested loops are not searched: a <c>break</c> inside one binds to that loop, not to this one.
    /// </summary>
    private static bool ContainsBreak(IrStatement statement) => statement switch
    {
        IrBreak => true,
        IrBlock block => block.Statements.Any(ContainsBreak),
        IrIf ifStatement => ContainsBreak(ifStatement.Then)
            || (ifStatement.Else is not null && ContainsBreak(ifStatement.Else)),
        _ => false,
    };

    private IrBlock BindBlock(BlockStatement block, Scope parent, MethodContext context)
    {
        var scope = new Scope(parent);
        var statements = new List<IrStatement>();

        foreach (var statement in block.Statements)
        {
            statements.Add(BindStatement(statement, scope, context));
        }

        return new IrBlock(statements, block.Span);
    }

    private IrStatement BindStatement(Statement statement, Scope scope, MethodContext context) => statement switch
    {
        BlockStatement block => BindBlock(block, scope, context),
        VariableDeclarationStatement declaration => BindVariableDeclaration(declaration, scope, context),
        ReturnStatement returnStatement => BindReturn(returnStatement, scope, context),
        IfStatement ifStatement => BindIf(ifStatement, scope, context),
        WhileStatement whileStatement => BindWhile(whileStatement, scope, context),
        BreakStatement breakStatement => BindBreak(breakStatement, context),
        ContinueStatement continueStatement => BindContinue(continueStatement, context),
        ForInStatement forIn => BindForIn(forIn, scope, context),
        AssignmentStatement assignment => BindAssignment(assignment, scope, context),
        ExpressionStatement expression => new IrExpressionStatement(
            BindExpression(expression.Expression, scope, context, null),
            expression.Span),
        _ => throw new ArgumentOutOfRangeException(nameof(statement), statement, "Unhandled statement."),
    };

    private IrStatement BindVariableDeclaration(
        VariableDeclarationStatement declaration,
        Scope scope,
        MethodContext context)
    {
        PlType? declaredType = declaration.DeclaredType is null
            ? null
            : ResolveTypeReference(declaration.DeclaredType);

        if (declaredType is VoidType)
        {
            _diagnostics.Error(
                "PL0024",
                "void is not a value type",
                $"Variable '{declaration.Name}' cannot be declared void.",
                declaration.Span,
                "void is a return marker only (spec 8.1).");
            declaredType = ErrorType.Instance;
        }

        var initializer = BindExpression(declaration.Initializer, scope, context, declaredType);

        if (declaredType is not null
            && declaredType is not ErrorType
            && initializer.Type is not ErrorType
            && !TypesMatch(declaredType, initializer.Type))
        {
            _diagnostics.Error(
                "PL0028",
                "type mismatch in variable initializer",
                $"Cannot initialize '{declaration.Name}' of type '{declaredType.DisplayName}' "
                + $"with a value of type '{initializer.Type.DisplayName}'.",
                declaration.Span,
                "ProtoLang does not apply implicit numeric conversions.");
        }

        var local = new IrLocal(declaration.Name, declaredType ?? initializer.Type);

        if (!scope.TryDeclareLocal(local))
        {
            _diagnostics.Error(
                "PL0029",
                "duplicate variable",
                $"A variable named '{declaration.Name}' is already in scope.",
                declaration.Span);
        }

        return new IrVariableDeclaration(local, initializer, declaration.Span);
    }

    private IrStatement BindReturn(ReturnStatement statement, Scope scope, MethodContext context)
    {
        if (statement.Value is null)
        {
            if (context.ReturnType is not VoidType)
            {
                _diagnostics.Error(
                    "PL0030",
                    "missing return value",
                    $"This method must return a value of type '{context.ReturnType.DisplayName}'.",
                    statement.Span);
            }

            return new IrReturn(null, statement.Span);
        }

        var value = BindExpression(statement.Value, scope, context, context.ReturnType);

        if (context.ReturnType is VoidType)
        {
            _diagnostics.Error(
                "PL0031",
                "unexpected return value",
                "This method does not declare a return type.",
                statement.Span);
        }
        else if (value.Type is not ErrorType && !TypesMatch(context.ReturnType, value.Type))
        {
            _diagnostics.Error(
                "PL0032",
                "return type mismatch",
                $"Cannot return a value of type '{value.Type.DisplayName}' from a method "
                + $"declared '{context.ReturnType.DisplayName}'.",
                statement.Span,
                "ProtoLang does not apply implicit numeric conversions.");
        }

        return new IrReturn(value, statement.Span);
    }

    private IrStatement BindForIn(ForInStatement statement, Scope scope, MethodContext context)
    {
        var collection = BindExpression(statement.Collection, scope, context, null);

        PlType elementType;
        if (collection.Type is RepeatedType repeated)
        {
            elementType = repeated.ElementType;
        }
        else
        {
            if (collection.Type is not ErrorType)
            {
                _diagnostics.Error(
                    "PL0033",
                    "not iterable",
                    $"Cannot iterate a value of type '{collection.Type.DisplayName}'.",
                    statement.Collection.Span,
                    "'for' iterates protobuf repeated fields (spec 14).");
            }

            elementType = ErrorType.Instance;
        }

        var loopScope = new Scope(scope);
        var loop = new IrLocal(statement.VariableName, elementType);

        if (!loopScope.TryDeclareLocal(loop))
        {
            _diagnostics.Error(
                "PL0029",
                "duplicate variable",
                $"A variable named '{statement.VariableName}' is already in scope.",
                statement.Span);
        }

        var body = BindBlock(statement.Body, loopScope, context with { LoopDepth = context.LoopDepth + 1 });
        return new IrForEach(loop, collection, body, statement.Span);
    }

    private IrStatement BindIf(IfStatement statement, Scope scope, MethodContext context)
    {
        var condition = BindCondition(statement.Condition, scope, context, "if");
        var then = BindBlock(statement.Then, scope, context);

        // The parser only ever puts a block or a nested 'if' here, and BindStatement handles both.
        var elseBranch = statement.Else is null ? null : BindStatement(statement.Else, scope, context);

        return new IrIf(condition, then, elseBranch, statement.Span);
    }

    private IrStatement BindWhile(WhileStatement statement, Scope scope, MethodContext context)
    {
        var condition = BindCondition(statement.Condition, scope, context, "while");
        var body = BindBlock(statement.Body, scope, context with { LoopDepth = context.LoopDepth + 1 });

        return new IrWhile(condition, body, statement.Span);
    }

    /// <summary>
    /// Binds a branch or loop condition. ProtoLang has no truthiness, so the condition must already
    /// be a bool -- the same rule the logical operators follow (PL0047).
    /// </summary>
    private IrExpression BindCondition(Expression condition, Scope scope, MethodContext context, string keyword)
    {
        var bound = BindExpression(condition, scope, context, ScalarType.BoolType);

        if (bound.Type is not ErrorType && !TypesMatch(bound.Type, ScalarType.BoolType))
        {
            _diagnostics.Error(
                "PL0071",
                "condition must be bool",
                $"The '{keyword}' condition has type '{bound.Type.DisplayName}'.",
                condition.Span,
                "ProtoLang does not treat non-bool values as true or false; compare explicitly.");
        }

        return bound;
    }

    private IrStatement BindBreak(BreakStatement statement, MethodContext context)
    {
        if (context.LoopDepth == 0)
        {
            _diagnostics.Error(
                "PL0072",
                "'break' outside a loop",
                "'break' can only appear inside a 'for' or 'while' loop.",
                statement.Span);
        }

        return new IrBreak(statement.Span);
    }

    private IrStatement BindContinue(ContinueStatement statement, MethodContext context)
    {
        if (context.LoopDepth == 0)
        {
            _diagnostics.Error(
                "PL0073",
                "'continue' outside a loop",
                "'continue' can only appear inside a 'for' or 'while' loop.",
                statement.Span);
        }

        return new IrContinue(statement.Span);
    }

    private IrStatement BindAssignment(AssignmentStatement statement, Scope scope, MethodContext context)
    {
        if (statement.Target is not NameExpression name || scope.LookupLocal(name.Name) is not { } local)
        {
            _diagnostics.Error(
                "PL0034",
                "invalid assignment target",
                "Only local variables can be assigned.",
                statement.Target.Span,
                "Whether methods may mutate the receiver is still an open question (spec 16.1).");

            var boundValue = BindExpression(statement.Value, scope, context, null);
            return new IrExpressionStatement(boundValue, statement.Span);
        }

        var value = BindExpression(statement.Value, scope, context, local.Type);

        if (value.Type is not ErrorType && local.Type is not ErrorType && !TypesMatch(local.Type, value.Type))
        {
            _diagnostics.Error(
                "PL0035",
                "type mismatch in assignment",
                $"Cannot assign a value of type '{value.Type.DisplayName}' to '{local.Name}' "
                + $"of type '{local.Type.DisplayName}'.",
                statement.Span,
                "ProtoLang does not apply implicit numeric conversions.");
        }

        return new IrAssignment(local, value, statement.Span);
    }

    private IrExpression BindExpression(
        Expression expression,
        Scope scope,
        MethodContext context,
        PlType? expectedType) => expression switch
        {
            IntegerLiteralExpression literal => BindIntegerLiteral(literal, expectedType),
            FloatLiteralExpression literal => new IrLiteral(
                literal.Value,
                expectedType is ScalarType { Kind: ScalarKind.Float } ? ScalarType.FloatType : ScalarType.DoubleType,
                literal.Span),
            BooleanLiteralExpression literal => new IrLiteral(literal.Value, ScalarType.BoolType, literal.Span),
            StringLiteralExpression literal => new IrLiteral(literal.Value, ScalarType.StringType, literal.Span),
            NameExpression name => BindName(name, scope, context),
            MemberAccessExpression member => BindMemberAccess(member, scope, context),
            InvocationExpression invocation => BindInvocation(invocation, scope, context),
            BinaryExpression binary => BindBinary(binary, scope, context, expectedType),
            UnaryExpression unary => BindUnary(unary, scope, context, expectedType),
            CastExpression cast => BindCast(cast, scope, context),
            ErrorExpression error => new IrLiteral(null, ErrorType.Instance, error.Span),
            _ => throw new ArgumentOutOfRangeException(nameof(expression), expression, "Unhandled expression."),
        };

    /// <summary>
    /// Binds an explicit conversion, <c>x as int64</c> (spec 10.3). This is the only way to change
    /// the width or signedness of a value, and the reason mixed-width arithmetic is expressible at
    /// all.
    /// </summary>
    /// <remarks>
    /// The operand is bound with no expected type. The cast already states the target, so the
    /// operand keeps whatever type it has on its own: an integer literal takes its natural
    /// <c>int64</c> and a float literal its natural <c>double</c>. That is what makes
    /// <c>3000000000 as int32</c> a narrowing conversion that wraps, rather than a literal that
    /// silently retypes itself and then reports PL0036 for not fitting.
    /// </remarks>
    private IrExpression BindCast(CastExpression cast, Scope scope, MethodContext context)
    {
        var operand = BindExpression(cast.Operand, scope, context, null);
        var target = ResolveTypeReference(cast.TargetType);

        // ResolveTypeReference has already reported an unknown or ambiguous target, and a failed
        // operand has already reported whatever went wrong there.
        if (operand.Type is ErrorType || target is ErrorType)
        {
            return new IrLiteral(null, ErrorType.Instance, cast.Span);
        }

        if (operand.Type is not ScalarType { IsNumeric: true } source
            || target is not ScalarType { IsNumeric: true } destination)
        {
            _diagnostics.Error(
                "PL0075",
                "invalid conversion",
                $"Cannot convert '{operand.Type.DisplayName}' to '{target.DisplayName}'.",
                cast.Span,
                "'as' converts between numeric scalar types only (spec 10.3).");
            return new IrLiteral(null, ErrorType.Instance, cast.Span);
        }

        return new IrConversion(
            operand,
            destination,
            ClassifyConversion(source, destination),
            _policy.ResolveConversion(source, destination),
            cast.Span);
    }

    private static ConversionKind ClassifyConversion(ScalarType source, ScalarType destination)
    {
        if (source.Kind == destination.Kind)
        {
            return ConversionKind.Identity;
        }

        if (source.IsInteger)
        {
            return destination.IsInteger ? ConversionKind.IntegerToInteger : ConversionKind.IntegerToFloat;
        }

        return destination.IsInteger ? ConversionKind.FloatToInteger : ConversionKind.FloatToFloat;
    }

    /// <summary>
    /// Integer literals adopt the expected integer type when the value fits, so
    /// <c>var total: int64 = 0;</c> does not require a suffix or a cast.
    /// </summary>
    private IrExpression BindIntegerLiteral(IntegerLiteralExpression literal, PlType? expectedType)
    {
        if (expectedType is ScalarType scalar)
        {
            if (scalar.IsFloatingPoint)
            {
                return new IrLiteral((double)literal.Value, scalar, literal.Span);
            }

            if (scalar.IsInteger && FitsIn(literal.Value, scalar))
            {
                return new IrLiteral(literal.Value, scalar, literal.Span);
            }

            if (scalar.IsInteger)
            {
                _diagnostics.Error(
                    "PL0036",
                    "integer literal out of range",
                    $"{literal.Value} is outside the range of '{scalar.DisplayName}'.",
                    literal.Span);
                return new IrLiteral(literal.Value, scalar, literal.Span);
            }
        }

        return new IrLiteral(literal.Value, ScalarType.Int64Type, literal.Span);
    }

    private static bool FitsIn(long value, ScalarType scalar) => scalar.Kind switch
    {
        ScalarKind.Int32 => value is >= int.MinValue and <= int.MaxValue,
        ScalarKind.Int64 => true,
        ScalarKind.UInt32 => value is >= 0 and <= uint.MaxValue,
        ScalarKind.UInt64 => value >= 0,
        _ => false,
    };

    private IrExpression BindName(NameExpression name, Scope scope, MethodContext context)
    {
        if (scope.LookupLocal(name.Name) is { } local)
        {
            return new IrLocalReference(local, name.Span);
        }

        if (scope.LookupParameter(name.Name) is { } parameter)
        {
            return new IrParameterReference(parameter, name.Span);
        }

        if (context.AllowImplicitReceiverFields)
        {
            // A bare identifier may be a field of the implicit receiver, as in `quantity`.
            var field = context.Receiver.FindFieldByName(name.Name);
            if (field is not null)
            {
                return BindFieldAccess(new IrThis(new MessageType(context.Receiver), name.Span), field, name.Span);
            }
        }

        _diagnostics.Error(
            "PL0037",
            "unknown name",
            $"'{name.Name}' is not a variable, parameter, or field of "
            + $"'{context.Receiver.FullName}'.",
            name.Span);
        return new IrLiteral(null, ErrorType.Instance, name.Span);
    }

    private IrExpression BindFieldAccess(IrExpression receiver, FieldDescriptor field, SourceSpan span)
    {
        if (field.IsMap)
        {
            _diagnostics.Error(
                "PL0038",
                "maps are not supported",
                $"Field '{field.Name}' is a map, which this compiler version does not support.",
                span);
            return new IrLiteral(null, ErrorType.Instance, span);
        }

        return new IrFieldAccess(receiver, field, TypeFactory.FromField(field), span);
    }

    /// <summary>
    /// Resolves <c>SomeEnum.SOME_VALUE</c>, or returns null when the member access is not naming an
    /// enum constant and should be bound the ordinary way.
    /// </summary>
    /// <remarks>
    /// Values win over types. If the leading identifier is a local, a parameter, or a field of the
    /// implicit receiver, this is a field access on that value and the enum lookup does not happen
    /// at all -- so a schema whose field name matches an enum type name keeps resolving as it did.
    /// </remarks>
    private IrExpression? TryBindEnumValue(MemberAccessExpression member, Scope scope, MethodContext context)
    {
        if (!TryFlattenName(member.Receiver, out var typeName, out var leadingName))
        {
            return null;
        }

        if (IsValueName(leadingName, scope, context))
        {
            return null;
        }

        // A fully qualified name is unambiguous by construction, so it is tried before the
        // simple-name lookup that could report a false ambiguity.
        if (!_enumsByFullName.TryGetValue(typeName, out var descriptor))
        {
            if (!_enumsBySimpleName.TryGetValue(typeName, out var candidates))
            {
                // Not an enum. Whatever this is, the ordinary path reports it.
                return null;
            }

            if (candidates.Count > 1)
            {
                ReportAmbiguousTypeName(typeName, member.Receiver.Span, candidates.Select(e => e.FullName));
                return new IrLiteral(null, ErrorType.Instance, member.Span);
            }

            descriptor = candidates[0];
        }

        var value = descriptor.FindValueByName(member.Name);
        if (value is null)
        {
            _diagnostics.Error(
                "PL0076",
                "unknown enum value",
                $"'{member.Name}' is not a value of enum '{descriptor.FullName}'.",
                member.Span,
                "Enum values are written exactly as the .proto file spells them.");
            return new IrLiteral(null, ErrorType.Instance, member.Span);
        }

        return new IrEnumValue(value, new EnumPlType(descriptor), member.Span);
    }

    /// <summary>
    /// Flattens a chain of member accesses over a bare identifier back into a dotted name, and
    /// reports the leading identifier separately. Returns false for anything else, such as a call
    /// or a literal at the root.
    /// </summary>
    private static bool TryFlattenName(Expression expression, out string name, out string leadingName)
    {
        var parts = new List<string>();
        var current = expression;

        while (current is MemberAccessExpression member)
        {
            parts.Insert(0, member.Name);
            current = member.Receiver;
        }

        if (current is not NameExpression root)
        {
            name = string.Empty;
            leadingName = string.Empty;
            return false;
        }

        parts.Insert(0, root.Name);
        name = string.Join('.', parts);
        leadingName = root.Name;
        return true;
    }

    /// <summary>Whether an identifier names a value in scope, using the same order as BindName.</summary>
    private static bool IsValueName(string name, Scope scope, MethodContext context)
        => scope.LookupLocal(name) is not null
        || scope.LookupParameter(name) is not null
        || (context.AllowImplicitReceiverFields && context.Receiver.FindFieldByName(name) is not null);

    private IrExpression BindMemberAccess(MemberAccessExpression member, Scope scope, MethodContext context)
    {
        // A member access whose receiver is a plain dotted name may be naming an enum constant
        // rather than reaching into a value, as in `Level.LEVEL_HIGH`. That has to be settled before
        // the receiver is bound: `Level` is a type, so binding it as an expression reports PL0037
        // and the error short circuit below would swallow the real question.
        if (TryBindEnumValue(member, scope, context) is { } enumValue)
        {
            return enumValue;
        }

        var receiver = BindExpression(member.Receiver, scope, context, null);

        if (receiver.Type is ErrorType)
        {
            return new IrLiteral(null, ErrorType.Instance, member.Span);
        }

        if (receiver.Type is not MessageType messageType)
        {
            _diagnostics.Error(
                "PL0039",
                "member access on a non-message value",
                $"Type '{receiver.Type.DisplayName}' has no members.",
                member.Span);
            return new IrLiteral(null, ErrorType.Instance, member.Span);
        }

        var field = messageType.Descriptor.FindFieldByName(member.Name);
        if (field is not null)
        {
            return BindFieldAccess(receiver, field, member.Span);
        }

        if (_methods.ContainsKey((messageType.Descriptor.FullName, member.Name)))
        {
            _diagnostics.Error(
                "PL0040",
                "method used as a value",
                $"'{member.Name}' is a method and must be called.",
                member.Span,
                $"Write '{member.Name}()'.");
            return new IrLiteral(null, ErrorType.Instance, member.Span);
        }

        _diagnostics.Error(
            "PL0041",
            "unknown field",
            $"'{messageType.Descriptor.FullName}' has no field named '{member.Name}'.",
            member.Span);
        return new IrLiteral(null, ErrorType.Instance, member.Span);
    }

    private IrExpression BindInvocation(InvocationExpression invocation, Scope scope, MethodContext context)
    {
        IrExpression receiver;
        string methodName;
        MessageDescriptor receiverDescriptor;

        switch (invocation.Callee)
        {
            case MemberAccessExpression member:
            {
                var boundReceiver = BindExpression(member.Receiver, scope, context, null);
                if (boundReceiver.Type is ErrorType)
                {
                    return new IrLiteral(null, ErrorType.Instance, invocation.Span);
                }

                if (boundReceiver.Type is not MessageType messageType)
                {
                    _diagnostics.Error(
                        "PL0042",
                        "method call on a non-message value",
                        $"Type '{boundReceiver.Type.DisplayName}' has no methods.",
                        invocation.Span);
                    return new IrLiteral(null, ErrorType.Instance, invocation.Span);
                }

                receiver = boundReceiver;
                methodName = member.Name;
                receiverDescriptor = messageType.Descriptor;
                break;
            }

            case NameExpression name:
                receiver = new IrThis(new MessageType(context.Receiver), name.Span);
                methodName = name.Name;
                receiverDescriptor = context.Receiver;
                break;

            default:
                _diagnostics.Error(
                    "PL0043",
                    "expression is not callable",
                    "Only ProtoLang methods can be called.",
                    invocation.Span,
                    "Calling target-language functions is not permitted (spec 20).");
                return new IrLiteral(null, ErrorType.Instance, invocation.Span);
        }

        if (!_methods.TryGetValue((receiverDescriptor.FullName, methodName), out var signature))
        {
            _diagnostics.Error(
                "PL0044",
                "unknown method",
                $"'{receiverDescriptor.FullName}' has no ProtoLang method named '{methodName}'.",
                invocation.Span,
                "Methods must be defined in an extend block for that message.");
            return new IrLiteral(null, ErrorType.Instance, invocation.Span);
        }

        var arguments = new List<IrExpression>();
        for (var i = 0; i < invocation.Arguments.Count; i++)
        {
            var expected = i < signature.ParameterTypes.Count ? signature.ParameterTypes[i] : null;
            arguments.Add(BindExpression(invocation.Arguments[i], scope, context, expected));
        }

        if (arguments.Count != signature.ParameterTypes.Count)
        {
            _diagnostics.Error(
                "PL0045",
                "wrong number of arguments",
                $"'{methodName}' takes {signature.ParameterTypes.Count} argument(s) "
                + $"but {arguments.Count} were supplied.",
                invocation.Span);
            return new IrLiteral(null, ErrorType.Instance, invocation.Span);
        }

        for (var i = 0; i < arguments.Count; i++)
        {
            if (arguments[i].Type is ErrorType)
            {
                continue;
            }

            if (!TypesMatch(signature.ParameterTypes[i], arguments[i].Type))
            {
                _diagnostics.Error(
                    "PL0046",
                    "argument type mismatch",
                    $"Argument {i + 1} of '{methodName}' expects "
                    + $"'{signature.ParameterTypes[i].DisplayName}' but got "
                    + $"'{arguments[i].Type.DisplayName}'.",
                    invocation.Arguments[i].Span);
            }
        }

        return new IrMethodCall(receiver, signature, arguments, invocation.Span);
    }

    private IrExpression BindBinary(
        BinaryExpression binary,
        Scope scope,
        MethodContext context,
        PlType? expectedType)
    {
        var isComparison = binary.Operator
            is BinaryOperatorKind.Equal or BinaryOperatorKind.NotEqual
            or BinaryOperatorKind.LessThan or BinaryOperatorKind.LessThanOrEqual
            or BinaryOperatorKind.GreaterThan or BinaryOperatorKind.GreaterThanOrEqual;

        var isLogical = binary.Operator is BinaryOperatorKind.LogicalAnd or BinaryOperatorKind.LogicalOr;

        // Comparisons and logical operators produce bool, so the outer expectation says nothing
        // about the operands.
        var operandHint = isComparison || isLogical ? null : expectedType;

        var left = BindExpression(binary.Left, scope, context, operandHint);
        var right = BindExpression(binary.Right, scope, context, left.Type is ErrorType ? operandHint : left.Type);

        // An untyped integer literal on the left should take its type from the right operand.
        if (binary.Left is IntegerLiteralExpression && right.Type is ScalarType && !TypesMatch(left.Type, right.Type))
        {
            left = BindExpression(binary.Left, scope, context, right.Type);
        }

        if (left.Type is ErrorType || right.Type is ErrorType)
        {
            return new IrBinary(
                ToIrOperator(binary.Operator),
                left,
                right,
                ErrorType.Instance,
                ArithmeticBehavior.Wrap,
                binary.Span);
        }

        var op = ToIrOperator(binary.Operator);

        if (isLogical)
        {
            if (!TypesMatch(left.Type, ScalarType.BoolType) || !TypesMatch(right.Type, ScalarType.BoolType))
            {
                _diagnostics.Error(
                    "PL0047",
                    "logical operator requires bool operands",
                    $"Cannot apply '{Describe(binary.Operator)}' to "
                    + $"'{left.Type.DisplayName}' and '{right.Type.DisplayName}'.",
                    binary.Span);
            }

            return new IrBinary(op, left, right, ScalarType.BoolType, ArithmeticBehavior.Wrap, binary.Span);
        }

        if (!TypesMatch(left.Type, right.Type))
        {
            _diagnostics.Error(
                "PL0048",
                "operand type mismatch",
                $"Cannot apply '{Describe(binary.Operator)}' to "
                + $"'{left.Type.DisplayName}' and '{right.Type.DisplayName}'.",
                binary.Span,
                "ProtoLang does not apply implicit numeric conversions; both operands must "
                + "already have the same type.");
            return new IrBinary(op, left, right, ErrorType.Instance, ArithmeticBehavior.Wrap, binary.Span);
        }

        if (isComparison)
        {
            var ordered = binary.Operator is not (BinaryOperatorKind.Equal or BinaryOperatorKind.NotEqual);
            if (ordered && left.Type is not ScalarType { IsNumeric: true })
            {
                _diagnostics.Error(
                    "PL0049",
                    "operands are not ordered",
                    $"'{Describe(binary.Operator)}' requires numeric operands, "
                    + $"but both are '{left.Type.DisplayName}'.",
                    binary.Span);
            }

            return new IrBinary(op, left, right, ScalarType.BoolType, ArithmeticBehavior.Wrap, binary.Span);
        }

        if (left.Type is not ScalarType { IsNumeric: true } resultType)
        {
            _diagnostics.Error(
                "PL0050",
                "arithmetic on a non-numeric type",
                $"Cannot apply '{Describe(binary.Operator)}' to '{left.Type.DisplayName}'.",
                binary.Span);
            return new IrBinary(op, left, right, ErrorType.Instance, ArithmeticBehavior.Wrap, binary.Span);
        }

        // Integer division is the only operation that can fail on a value rather than overflow, so
        // it takes a different path. Floating-point division follows IEEE 754 and yields inf or
        // NaN, which needs no declaration.
        if (op is (IrBinaryOperator.Divide or IrBinaryOperator.Modulo) && resultType.IsInteger)
        {
            return BindIntegerDivision(binary, op, left, right, resultType, scope, context);
        }

        if (binary.OnZero is not null)
        {
            _diagnostics.Error(
                "PL0015",
                "on_zero is only valid on integer division",
                $"'{resultType.DisplayName}' division follows IEEE 754 and yields infinity or NaN "
                + "rather than failing.",
                binary.OnZero.Span);
        }

        return new IrBinary(
            op, left, right, resultType, _policy.ResolveArithmetic(op, resultType), binary.Span);
    }

    /// <summary>
    /// Binds integer <c>/</c> or <c>%</c>. The divisor must either be a literal that is provably
    /// non-zero, or be accompanied by an <c>on_zero</c> clause naming the result to use instead.
    /// There is no third option: leaving it to the target would mean an exception in C#, a crash on
    /// x86 in C++, and a silent zero on ARM, all from one source file.
    /// </summary>
    private IrExpression BindIntegerDivision(
        BinaryExpression binary,
        IrBinaryOperator op,
        IrExpression left,
        IrExpression right,
        ScalarType resultType,
        Scope scope,
        MethodContext context)
    {
        var divisorIsProvenNonZero = right is IrLiteral { Value: long literal } && literal != 0;

        if (divisorIsProvenNonZero)
        {
            if (binary.OnZero is not null)
            {
                _diagnostics.Warning(
                    "PL0056",
                    "unnecessary on_zero clause",
                    "The divisor is a non-zero literal, so this clause is unreachable.",
                    binary.OnZero.Span);
            }

            return new IrIntegerDivision(
                op, left, right, ZeroDivisorBehavior.Unreachable, null, resultType,
                _policy.ResolveDivision(op, resultType), binary.Span);
        }

        if (binary.OnZero is null)
        {
            _diagnostics.Error(
                "PL0054",
                "integer division requires an on_zero clause",
                $"'{Describe(binary.Operator)}' on '{resultType.DisplayName}' must state what to "
                + "produce when the divisor is zero.",
                binary.Span,
                $"Write '{Describe(binary.Operator)} <divisor> on_zero <fallback>', or "
                + $"'{Describe(binary.Operator)} <divisor> on_zero fail' if no value is correct.");

            return new IrIntegerDivision(
                op, left, right, ZeroDivisorBehavior.Fallback, null, ErrorType.Instance,
                ArithmeticBehavior.Wrap, binary.Span);
        }

        if (binary.OnZero.IsFail)
        {
            return new IrIntegerDivision(
                op, left, right, ZeroDivisorBehavior.Fail, null, resultType,
                _policy.ResolveDivision(op, resultType), binary.Span);
        }

        var onZero = BindExpression(binary.OnZero.Fallback!, scope, context, resultType);

        if (onZero.Type is not ErrorType && !TypesMatch(resultType, onZero.Type))
        {
            _diagnostics.Error(
                "PL0055",
                "on_zero type mismatch",
                $"The fallback has type '{onZero.Type.DisplayName}' but the division produces "
                + $"'{resultType.DisplayName}'.",
                binary.OnZero.Span,
                "ProtoLang does not apply implicit numeric conversions.");
        }

        return new IrIntegerDivision(
            op, left, right, ZeroDivisorBehavior.Fallback, onZero, resultType,
            _policy.ResolveDivision(op, resultType), binary.Span);
    }

    private IrExpression BindUnary(
        UnaryExpression unary,
        Scope scope,
        MethodContext context,
        PlType? expectedType)
    {
        var operand = BindExpression(unary.Operand, scope, context, expectedType);

        if (operand.Type is ErrorType)
        {
            return new IrUnary(
                unary.Operator == UnaryOperatorKind.Negate ? IrUnaryOperator.Negate : IrUnaryOperator.LogicalNot,
                operand,
                ErrorType.Instance,
                ArithmeticBehavior.Wrap,
                unary.Span);
        }

        if (unary.Operator == UnaryOperatorKind.Negate)
        {
            if (operand.Type is not ScalarType { IsNumeric: true } scalar)
            {
                _diagnostics.Error(
                    "PL0051",
                    "negation requires a numeric operand",
                    $"Cannot negate a value of type '{operand.Type.DisplayName}'.",
                    unary.Span);
                return new IrUnary(
                    IrUnaryOperator.Negate, operand, ErrorType.Instance, ArithmeticBehavior.Wrap, unary.Span);
            }

            if (scalar.IsInteger && !scalar.IsSigned)
            {
                _diagnostics.Error(
                    "PL0052",
                    "negation of an unsigned type",
                    $"'{scalar.DisplayName}' is unsigned and cannot be negated.",
                    unary.Span);
            }

            return new IrUnary(
                IrUnaryOperator.Negate, operand, scalar, _policy.ResolveNegation(scalar), unary.Span);
        }

        if (!TypesMatch(operand.Type, ScalarType.BoolType))
        {
            _diagnostics.Error(
                "PL0053",
                "logical not requires a bool operand",
                $"Cannot apply 'not' to a value of type '{operand.Type.DisplayName}'.",
                unary.Span);
        }

        return new IrUnary(
            IrUnaryOperator.LogicalNot, operand, ScalarType.BoolType, ArithmeticBehavior.Wrap, unary.Span);
    }

    private static IrBinaryOperator ToIrOperator(BinaryOperatorKind kind) => kind switch
    {
        BinaryOperatorKind.Add => IrBinaryOperator.Add,
        BinaryOperatorKind.Subtract => IrBinaryOperator.Subtract,
        BinaryOperatorKind.Multiply => IrBinaryOperator.Multiply,
        BinaryOperatorKind.Divide => IrBinaryOperator.Divide,
        BinaryOperatorKind.Modulo => IrBinaryOperator.Modulo,
        BinaryOperatorKind.Equal => IrBinaryOperator.Equal,
        BinaryOperatorKind.NotEqual => IrBinaryOperator.NotEqual,
        BinaryOperatorKind.LessThan => IrBinaryOperator.LessThan,
        BinaryOperatorKind.LessThanOrEqual => IrBinaryOperator.LessThanOrEqual,
        BinaryOperatorKind.GreaterThan => IrBinaryOperator.GreaterThan,
        BinaryOperatorKind.GreaterThanOrEqual => IrBinaryOperator.GreaterThanOrEqual,
        BinaryOperatorKind.LogicalAnd => IrBinaryOperator.LogicalAnd,
        BinaryOperatorKind.LogicalOr => IrBinaryOperator.LogicalOr,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled operator."),
    };

    private static string Describe(BinaryOperatorKind kind) => kind switch
    {
        BinaryOperatorKind.Add => "+",
        BinaryOperatorKind.Subtract => "-",
        BinaryOperatorKind.Multiply => "*",
        BinaryOperatorKind.Divide => "/",
        BinaryOperatorKind.Modulo => "%",
        BinaryOperatorKind.Equal => "==",
        BinaryOperatorKind.NotEqual => "!=",
        BinaryOperatorKind.LessThan => "<",
        BinaryOperatorKind.LessThanOrEqual => "<=",
        BinaryOperatorKind.GreaterThan => ">",
        BinaryOperatorKind.GreaterThanOrEqual => ">=",
        BinaryOperatorKind.LogicalAnd => "and",
        BinaryOperatorKind.LogicalOr => "or",
        _ => kind.ToString(),
    };

    private static bool TypesMatch(PlType left, PlType right) => left.Equals(right);

    /// <param name="LoopDepth">
    /// How many enclosing loops the statement being bound sits inside. Zero means 'break' and
    /// 'continue' have nothing to bind to.
    /// </param>
    private sealed record MethodContext(
        MessageDescriptor Receiver,
        PlType ReturnType,
        IReadOnlyList<IrParameter> Parameters,
        bool AllowImplicitReceiverFields = true,
        int LoopDepth = 0);

    private sealed class Scope
    {
        private readonly Scope? _parent;
        private readonly Dictionary<string, IrLocal> _locals = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IrParameter> _parameters = new(StringComparer.Ordinal);

        public Scope(Scope? parent) => _parent = parent;

        public bool TryDeclareLocal(IrLocal local)
        {
            if (LookupLocal(local.Name) is not null || LookupParameter(local.Name) is not null)
            {
                return false;
            }

            _locals[local.Name] = local;
            return true;
        }

        public bool TryDeclareParameter(IrParameter parameter)
        {
            if (LookupParameter(parameter.Name) is not null)
            {
                return false;
            }

            _parameters[parameter.Name] = parameter;
            return true;
        }

        public IrLocal? LookupLocal(string name)
        {
            for (var scope = this; scope is not null; scope = scope._parent)
            {
                if (scope._locals.TryGetValue(name, out var local))
                {
                    return local;
                }
            }

            return null;
        }

        public IrParameter? LookupParameter(string name)
        {
            for (var scope = this; scope is not null; scope = scope._parent)
            {
                if (scope._parameters.TryGetValue(name, out var parameter))
                {
                    return parameter;
                }
            }

            return null;
        }
    }
}
