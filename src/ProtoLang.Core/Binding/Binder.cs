using Google.Protobuf.Reflection;
using ProtoLang.Config;
using ProtoLang.Diagnostics;
using ProtoLang.Ir;
using ProtoLang.Symbols;
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
    /// <summary>What a call can resolve to: one entry per name a receiver actually offers.</summary>
    private readonly Dictionary<(string Receiver, string Method), IrMethodSignature> _methods = new();

    /// <summary>What each declaration declares, whether or not anything may call it.</summary>
    /// <remarks>
    /// <para>
    /// Separate from <see cref="_methods"/> because the two answer different questions, and binding
    /// a body against the answer to the wrong one is how a second <c>fn f</c> came to be bound
    /// against the first one's parameter list -- and then to index past the end of it, which is an
    /// unhandled exception on a file that parses perfectly well.
    /// </para>
    /// <para>
    /// Keyed by node identity rather than by value: two declarations that differ in nothing but
    /// their span are still two declarations, and hashing a record whose value includes an entire
    /// method body is not something to do once per method.
    /// </para>
    /// </remarks>
    private readonly Dictionary<MethodDeclaration, IrMethodSignature> _signatures =
        new(ReferenceEqualityComparer.Instance);
    private readonly NumericPolicy _policy;
    private readonly ProjectConfig _config;
    private readonly SourceIdentity _document;

    /// <param name="document">
    /// What the source being bound is, which every declaration site records so that a reference can
    /// say not just where its declaration is but which file that is in. Optional because a caller
    /// that only wants diagnostics -- the resilience suite binds thousands of generated trees --
    /// has nothing to say here and no one to say it to. Omitting it leaves every declaration keyed
    /// under one anonymous buffer, which is sound for a single compilation and useless to index
    /// across several, so anything building an index must supply it. The pipeline always does.
    /// </param>
    public Binder(
        IReadOnlyList<FileDescriptor> files,
        DiagnosticBag diagnostics,
        NumericPolicy? policy = null,
        ProjectConfig? config = null,
        SourceIdentity? document = null)
    {
        _diagnostics = diagnostics;
        _config = config ?? ProjectConfig.Default;
        _policy = policy ?? new NumericPolicy(_config);
        _document = document ?? SourceIdentity.Unsaved();

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

    /// <summary>Binds a compilation unit to typed IR, whether or not it parsed cleanly.</summary>
    /// <remarks>
    /// <para>
    /// There is no tolerant mode, because there is nothing for a strict one to do differently. A
    /// tree that failed to parse differs from one that did not only in containing names the parser
    /// has already reported as missing, and every declaration that cannot be resolved is dropped
    /// here rather than half-built. What comes out is a module covering the parts that parsed --
    /// which is what an editor needs, since a buffer is broken for most of the time anyone is
    /// looking at it.
    /// </para>
    /// <para>
    /// Nothing downstream can mistake that module for a complete one: <c>CompilationResult.Success</c>
    /// requires no errors as well as a module, and the diagnostics are still there.
    /// </para>
    /// </remarks>
    public IrModule Bind(CompilationUnit unit)
    {
        // Pass 1: resolve extend targets and collect signatures.
        var resolvedExtends = new List<(ExtendDeclaration Declaration, MessageDescriptor Receiver)>();

        foreach (var extend in unit.Extends)
        {
            if (extend.MessageName.IsMissing)
            {
                continue;
            }

            var receiver = ResolveMessage(extend.MessageName.Text, extend.Span);
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

    /// <summary>
    /// Records what a method declares, and registers it as callable when its name is one a call
    /// could use.
    /// </summary>
    /// <remarks>
    /// Every declaration is described, including the ones refused below. A refused method still has
    /// a body, the body is still bound, and a body has to be bound against the types its own
    /// declaration states -- never against those of whichever other declaration happens to share its
    /// name. Refusal governs what may be *called*, and nothing else.
    /// </remarks>
    private void DeclareMethod(MessageDescriptor receiver, MethodDeclaration method)
    {
        _signatures[method] = DescribeMethod(receiver, method);

        if (method.Name.IsMissing)
        {
            // Nothing can name it, so nothing can call it. Registering it under the empty name would
            // also make a second half-typed method collide with the first and report a duplicate the
            // author never wrote.
            return;
        }

        var key = (receiver.FullName, method.Name.Text);

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

        if (receiver.FindFieldByName(method.Name.Text) is not null)
        {
            _diagnostics.Error(
                "PL0023",
                "method name collides with a field",
                $"'{receiver.FullName}' has a field named '{method.Name}'.",
                method.Span,
                "Methods and protobuf fields share one name space on a message.");
            return;
        }

        _methods[key] = _signatures[method];
    }

    /// <summary>Resolves what a method declares into the signature the IR carries.</summary>
    private IrMethodSignature DescribeMethod(MessageDescriptor receiver, MethodDeclaration method)
    {
        var returnType = method.ReturnType is null
            ? VoidType.Instance
            : ResolveTypeReference(method.ReturnType);

        var parameters = new List<IrParameter>();

        foreach (var parameter in method.Parameters)
        {
            var type = ResolveTypeReference(parameter.Type);
            if (type is VoidType)
            {
                _diagnostics.Error(
                    "PL0024",
                    "void is not a value type",
                    $"{Capitalized(Refer(parameter.Name, "parameter", "Parameter"))} cannot be "
                    + "declared void.",
                    parameter.Span,
                    "void is a return marker only (spec 8.1).");
                type = ErrorType.Instance;
            }

            parameters.Add(new IrParameter(
                new DeclarationSite(SymbolKind.Parameter, _document, parameter.Name, parameter.Span),
                type));
        }

        return new IrMethodSignature(
            receiver,
            new DeclarationSite(SymbolKind.Method, _document, method.Name, method.Span),
            returnType,
            parameters);
    }


    /// <summary>
    /// How a message refers to something the author has not named yet: <c>this method</c> rather
    /// than <c>''</c>.
    /// </summary>
    /// <param name="lead">
    /// The word that introduces a name that is there, for the messages that use one. "Parameter
    /// 'x'" is what that message has always said and is not worth moving to save a branch here.
    /// </param>
    /// <remarks>
    /// A name that was never written has no spelling to quote, and quoting the empty string reads as
    /// a defect in the compiler rather than as a fact about the program. The span already says where
    /// the thing is; the message only has to stop pretending it can name it. Wording for names that
    /// are there is untouched, because those messages are published output.
    /// </remarks>
    private static string Refer(SyntaxName name, string kind, string? lead = null)
        => name.IsMissing ? $"this {kind}"
            : lead is null ? $"'{name}'"
            : $"{lead} '{name}'";

    /// <summary>Upper-cases a leading letter, for a referent that opens a sentence.</summary>
    /// <remarks>
    /// A no-op on the quoted forms <see cref="Refer"/> produces for a name that exists, because a
    /// quote is not a letter -- which is what lets one referent serve both ends of a sentence.
    /// </remarks>
    private static string Capitalized(string text)
        => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

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

    /// <remarks>
    /// A name the parser never saw resolves to <see cref="ErrorType"/> in silence. It has already
    /// been reported as a syntax error at the position it is missing from, and an unknown-type
    /// diagnostic on top of that says the same thing twice.
    /// </remarks>
    private PlType ResolveTypeReference(TypeReference reference)
    {
        if (reference.Name.IsMissing)
        {
            return ErrorType.Instance;
        }

        var name = reference.Name.Text;

        if (name == "void")
        {
            return VoidType.Instance;
        }

        var scalar = TypeFactory.TryGetScalar(name);
        if (scalar is not null)
        {
            return scalar;
        }

        // A fully qualified name is unambiguous by construction, so it is tried before any
        // simple-name lookup that could report a false ambiguity.
        if (_messagesByFullName.TryGetValue(name, out var messageByFullName))
        {
            return new MessageType(messageByFullName);
        }

        if (_enumsByFullName.TryGetValue(name, out var enumByFullName))
        {
            return new EnumPlType(enumByFullName);
        }

        _messagesBySimpleName.TryGetValue(name, out var messages);
        _enumsBySimpleName.TryGetValue(name, out var enums);

        var candidateCount = (messages?.Count ?? 0) + (enums?.Count ?? 0);

        if (candidateCount > 1)
        {
            // Messages and enums share one type name space here, so a name matching one of each is
            // just as ambiguous as a name matching two enums.
            var fullNames = (messages ?? Enumerable.Empty<MessageDescriptor>()).Select(m => m.FullName)
                .Concat((enums ?? Enumerable.Empty<EnumDescriptor>()).Select(e => e.FullName));

            ReportAmbiguousTypeName(name, reference.Span, fullNames);
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
        if (!_signatures.TryGetValue(method, out var signature))
        {
            // Only reachable for a method in an extend block whose target did not resolve, which
            // pass 1 skips entirely -- there is no receiver to bind a body against.
            return null;
        }

        var scope = new Scope(null);

        foreach (var parameter in signature.Parameters)
        {
            // A parameter still being typed stays in the signature, so the positions line up with
            // the call sites, but is not put in scope: two of them would otherwise collide under the
            // empty name and be reported as a duplicate the author never wrote.
            if (parameter.Declaration.Name.IsMissing)
            {
                continue;
            }

            if (!scope.TryDeclareParameter(parameter))
            {
                _diagnostics.Error(
                    "PL0026",
                    "duplicate parameter",
                    $"A parameter named '{parameter.Name}' is already declared.",
                    parameter.Declaration.Extent);
            }
        }

        var context = new MethodContext(receiver, signature.ReturnType);
        var body = BindBlock(method.Body, scope, context);

        if (signature.ReturnType is not VoidType && !NeverFallsThrough(body))
        {
            _diagnostics.Error(
                "PL0027",
                "missing return statement",
                $"{Capitalized(Refer(method.Name, "method"))} declares a return type of "
                + $"'{signature.ReturnType.DisplayName}' but not all paths return a value.",
                method.Span);
        }

        return new IrMethod(signature, body, method.IsVirtual);
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
            AllowImplicitReceiverFields: false);
        var receiver = BindTestReceiver(test.Receiver, signature.Receiver, context);
        var arguments = BindTestArguments(test, signature, context);
        var expectation = BindTestExpectation(test.Expectation, signature, context);

        return new IrTest(signature, test.Name, receiver, arguments, expectation, test.Span);
    }

    /// <remarks>
    /// A target the author has not finished typing yields null in silence; the parser reported it
    /// as a syntax error where it is missing, and saying it again as an invalid test target adds
    /// nothing.
    /// </remarks>
    private IrMethodSignature? ResolveTestTarget(SyntaxName target, SourceSpan span)
    {
        if (target.IsMissing)
        {
            return null;
        }

        var targetName = target.Text;
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
            if (field.FieldName.IsMissing)
            {
                continue;
            }

            var descriptorField = descriptor.FindFieldByName(field.FieldName.Text);
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

        // An argument whose name is still being typed could be for any parameter, so nothing can be
        // said about which are missing until it is written. That silence is unavoidable and covers
        // the whole signature; a parameter with no name is a narrower problem, handled below.
        var argumentNamesAreComplete = true;

        foreach (var argument in test.Arguments)
        {
            if (argument.Name.IsMissing)
            {
                argumentNamesAreComplete = false;
                continue;
            }

            if (!declared.TryAdd(argument.Name.Text, argument))
            {
                _diagnostics.Error(
                    "PL0065",
                    "duplicate test argument",
                    $"Argument '{argument.Name}' is supplied more than once.",
                    argument.Span);
            }
        }

        var arguments = new List<IrTestArgument>();
        foreach (var parameter in signature.Parameters)
        {
            // A parameter nobody has named yet cannot be supplied and cannot be demanded: there is
            // no name for the test to write. Only this parameter goes unmentioned, though -- the
            // ones beside it that do have names are still checked.
            if (parameter.Declaration.Name.IsMissing)
            {
                continue;
            }

            var name = parameter.Name;
            if (!declared.TryGetValue(name, out var declaration))
            {
                if (argumentNamesAreComplete)
                {
                    _diagnostics.Error(
                        "PL0066",
                        "missing test argument",
                        $"Test '{test.Name}' does not supply argument '{name}'.",
                        test.Span);
                }

                continue;
            }

            var expectedType = parameter.Type;
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

        foreach (var extra in declared.Keys.Except(NamedParametersOf(signature), StringComparer.Ordinal))
        {
            _diagnostics.Error(
                "PL0068",
                "unknown test argument",
                $"'{signature.Name}' has no parameter named '{extra}'.",
                declared[extra].Span);
        }

        return arguments;
    }

    /// <summary>The parameters a test could name, which is the ones the author has named.</summary>
    private static IEnumerable<string> NamedParametersOf(IrMethodSignature signature)
        => signature.Parameters
            .Where(parameter => !parameter.Declaration.Name.IsMissing)
            .Select(parameter => parameter.Name);

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
            var bound = BindStatement(statement, scope, context);
            statements.Add(bound);

            // A guard clause establishes presence for everything after it, not only inside its own
            // branch: 'if not has x { return 0; }' leaves x set for the rest of the block.
            context = context with { Present = Advance(context.Present, bound) };
        }

        return new IrBlock(statements, block.Span);
    }

    /// <summary>
    /// The presence facts that hold after <paramref name="statement"/>, given those that held
    /// before it.
    /// </summary>
    /// <remarks>
    /// Only an <c>if</c> adds anything, and only when one of its branches cannot complete normally.
    /// <see cref="NeverFallsThrough"/> is the same predicate the all-paths-return check uses, which
    /// is what makes the early-return guard work without a second reachability analysis.
    /// </remarks>
    private static IReadOnlySet<string> Advance(IReadOnlySet<string> before, IrStatement statement)
    {
        if (statement is not IrIf conditional)
        {
            return before;
        }

        var (whenTrue, whenFalse) = PresenceFacts(conditional.Condition);

        if (NeverFallsThrough(conditional.Then))
        {
            before = Union(before, whenFalse);
        }

        if (conditional.Else is not null && NeverFallsThrough(conditional.Else))
        {
            before = Union(before, whenTrue);
        }

        return before;
    }

    /// <summary>
    /// The presence facts a condition establishes, for the case where it is true and for the case
    /// where it is false.
    /// </summary>
    /// <remarks>
    /// <c>and</c> proves both of its operands when true and neither when false; <c>or</c> is its
    /// mirror. Anything this does not recognise contributes nothing, which is always sound: an
    /// unrecognised condition costs a diagnostic the author can silence with an explicit guard,
    /// never a missed one.
    /// </remarks>
    private static (IReadOnlySet<string> WhenTrue, IReadOnlySet<string> WhenFalse) PresenceFacts(
        IrExpression condition)
    {
        switch (condition)
        {
            case IrFieldPresence presence when PresencePath(presence.Receiver, presence.Field) is { } path:
                return (new HashSet<string>(StringComparer.Ordinal) { path }, EmptyPresence);

            case IrUnary { Operator: IrUnaryOperator.LogicalNot } negation:
            {
                var (whenTrue, whenFalse) = PresenceFacts(negation.Operand);
                return (whenFalse, whenTrue);
            }

            case IrBinary { Operator: IrBinaryOperator.LogicalAnd } conjunction:
            {
                var left = PresenceFacts(conjunction.Left);
                var right = PresenceFacts(conjunction.Right);
                return (Union(left.WhenTrue, right.WhenTrue), EmptyPresence);
            }

            case IrBinary { Operator: IrBinaryOperator.LogicalOr } disjunction:
            {
                var left = PresenceFacts(disjunction.Left);
                var right = PresenceFacts(disjunction.Right);
                return (EmptyPresence, Union(left.WhenFalse, right.WhenFalse));
            }

            default:
                return (EmptyPresence, EmptyPresence);
        }
    }

    private static IReadOnlySet<string> Union(IReadOnlySet<string> left, IReadOnlySet<string> right)
    {
        if (right.Count == 0)
        {
            return left;
        }

        if (left.Count == 0)
        {
            return right;
        }

        var union = new HashSet<string>(left, StringComparer.Ordinal);
        union.UnionWith(right);
        return union;
    }

    /// <summary>
    /// A stable key for the field <paramref name="field"/> reached through
    /// <paramref name="receiver"/>, or null when the receiver is not something a presence test can
    /// name.
    /// </summary>
    /// <remarks>
    /// The roots -- the receiver, a parameter, a local, a loop binding -- are present by
    /// construction, so they need no key of their own beyond something unique. Local and parameter
    /// names are unique within a method, because shadowing is rejected at declaration.
    /// </remarks>
    private static string? PresencePath(IrExpression receiver, FieldDescriptor field)
        => PresenceRoot(receiver) is { } root ? $"{root}.{field.Name}" : null;

    private static string? PresenceRoot(IrExpression expression) => expression switch
    {
        IrThis => "this",
        IrLocalReference local => $"local:{local.Local.Name}",
        IrParameterReference parameter => $"param:{parameter.Parameter.Name}",

        // Only a singular message field extends a path; a scalar cannot be read through, and a
        // repeated field is reached by iteration rather than by name.
        IrFieldAccess field when IsSingularMessage(field.Field) => PresencePath(field.Receiver, field.Field),

        // A method result is present by construction -- every message value in the language comes
        // from a root or a guarded read -- but it has no name, so nothing reached through it can be
        // guarded. BindFieldAccess turns that into a diagnostic with a way out.
        _ => null,
    };

    private static bool IsSingularMessage(FieldDescriptor field)
        => !field.IsRepeated && !field.IsMap && field.FieldType is FieldType.Message or FieldType.Group;

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
                $"{Capitalized(Refer(declaration.Name, "variable", "Variable"))} cannot be "
                + "declared void.",
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
                $"Cannot initialize {Refer(declaration.Name, "variable")} of type "
                + $"'{declaredType.DisplayName}' with a value of type "
                + $"'{initializer.Type.DisplayName}'.",
                declaration.Span,
                "ProtoLang does not apply implicit numeric conversions.");
        }

        var local = new IrLocal(
            new DeclarationSite(SymbolKind.Local, _document, declaration.Name, declaration.Span),
            declaredType ?? initializer.Type);

        // Same reasoning as an unnamed parameter: the declaration stays in the IR so the body can
        // still be walked, but nothing goes into scope under a name that was never written.
        if (declaration.Name.IsMissing)
        {
            return new IrVariableDeclaration(local, initializer, declaration.Span);
        }

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

        // The extent is the whole loop rather than its header: the parser records no span for the
        // header alone, and a client showing a loop binding in context wants the loop it binds over.
        var loop = new IrLocal(
            new DeclarationSite(SymbolKind.LoopBinding, _document, statement.VariableName, statement.Span),
            elementType);

        if (!statement.VariableName.IsMissing && !loopScope.TryDeclareLocal(loop))
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
        var (whenTrue, whenFalse) = PresenceFacts(condition);

        var then = BindBlock(statement.Then, scope, context with { Present = Union(context.Present, whenTrue) });

        // The parser only ever puts a block or a nested 'if' here, and BindStatement handles both.
        var elseBranch = statement.Else is null
            ? null
            : BindStatement(statement.Else, scope, context with { Present = Union(context.Present, whenFalse) });

        return new IrIf(condition, then, elseBranch, statement.Span);
    }

    private IrStatement BindWhile(WhileStatement statement, Scope scope, MethodContext context)
    {
        var condition = BindCondition(statement.Condition, scope, context, "while");
        var (whenTrue, _) = PresenceFacts(condition);
        var body = BindBlock(
            statement.Body,
            scope,
            context with
            {
                LoopDepth = context.LoopDepth + 1,
                Present = Union(context.Present, whenTrue),
            });

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
        if (statement.Target is not NameExpression name || scope.LookupLocal(name.Name.Text) is not { } local)
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
            HasExpression has => BindHas(has, scope, context),
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
        if (scope.LookupLocal(name.Name.Text) is { } local)
        {
            return new IrLocalReference(local, name.Span);
        }

        if (scope.LookupParameter(name.Name.Text) is { } parameter)
        {
            return new IrParameterReference(parameter, name.Span);
        }

        if (context.AllowImplicitReceiverFields)
        {
            // A bare identifier may be a field of the implicit receiver, as in `quantity`.
            var field = context.Receiver.FindFieldByName(name.Name.Text);
            if (field is not null)
            {
                return BindFieldAccess(
                    new IrThis(new MessageType(context.Receiver), name.Span), field, name.Span, context);
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

    /// <summary>
    /// Binds a field read, and enforces the presence rule for message-typed fields (spec 13.1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reading an unset singular message field is the one place the two backends disagreed
    /// silently: C# yields null and throws at the next access, C++ yields the default instance and
    /// returns zero. Neither is wrong for its runtime, and neither can be made to match the other
    /// without a runtime check in every target, so the situation is made unrepresentable instead --
    /// the same choice <c>on_zero</c> makes for a zero divisor.
    /// </para>
    /// <para>
    /// The check is on the *value*, not on reading through it. Binding the field to a local or
    /// passing it as an argument launders exactly the same divergence, and this is the one point
    /// every one of those paths goes through.
    /// </para>
    /// </remarks>
    private IrExpression BindFieldAccess(
        IrExpression receiver,
        FieldDescriptor field,
        SourceSpan span,
        MethodContext context)
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

        if (IsSingularMessage(field))
        {
            var path = PresencePath(receiver, field);

            if (path is null)
            {
                _diagnostics.Error(
                    "PL0078",
                    "message field may be unset",
                    $"'{field.Name}' is reached through a value that has no name, so its presence "
                    + "cannot be established.",
                    span,
                    "Bind the intermediate value to a local first, then guard the field: "
                    + $"'var m: M = ...; if has m.{field.Name} {{ ... }}'.");
            }
            else if (!context.Present.Contains(path))
            {
                _diagnostics.Error(
                    "PL0078",
                    "message field may be unset",
                    $"'{field.Name}' is a message field, which may be unset. Reading it would mean "
                    + "different things in different backends.",
                    span,
                    $"Guard it: 'if has {field.Name} {{ ... }}', or return early with "
                    + $"'if not has {field.Name} {{ ... }}' (spec 13.1).");
            }
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
        if (!TryResolveEnumReceiver(member.Receiver, scope, context, out var descriptor))
        {
            return null;
        }

        if (descriptor is null)
        {
            return new IrLiteral(null, ErrorType.Instance, member.Span);
        }

        var value = descriptor.FindValueByName(member.Name.Text);
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
    /// The enum a member access reaches into, when its receiver names one rather than holding a
    /// value -- <c>Level</c> in <c>Level.LEVEL_HIGH</c>.
    /// </summary>
    /// <param name="descriptor">
    /// The enum named, or null when the name was ambiguous -- which has been reported.
    /// </param>
    /// <returns>
    /// False when the receiver does not name an enum at all, and the access should be bound the
    /// ordinary way.
    /// </returns>
    /// <remarks>
    /// Shared with the case where the member name has not been typed yet, so that <c>Level.</c> and
    /// <c>Level.LEVEL_HIGH</c> agree about what <c>Level</c> is. They disagreed once: the unfinished
    /// one bound the receiver as an expression and reported an unknown name for a type that
    /// resolves perfectly well.
    /// </remarks>
    private bool TryResolveEnumReceiver(
        Expression receiver,
        Scope scope,
        MethodContext context,
        out EnumDescriptor? descriptor)
    {
        descriptor = null;

        if (!TryFlattenName(receiver, out var typeName, out var leadingName))
        {
            return false;
        }

        if (IsValueName(leadingName, scope, context))
        {
            return false;
        }

        // A fully qualified name is unambiguous by construction, so it is tried before the
        // simple-name lookup that could report a false ambiguity.
        if (_enumsByFullName.TryGetValue(typeName, out var byFullName))
        {
            descriptor = byFullName;
            return true;
        }

        if (!_enumsBySimpleName.TryGetValue(typeName, out var candidates))
        {
            // Not an enum. Whatever this is, the ordinary path reports it.
            return false;
        }

        if (candidates.Count > 1)
        {
            ReportAmbiguousTypeName(typeName, receiver.Span, candidates.Select(e => e.FullName));
            return true;
        }

        descriptor = candidates[0];
        return true;
    }

    /// <summary>
    /// Binds the receiver of a dot the author has not finished, for the type it has rather than for
    /// a value it does not have.
    /// </summary>
    /// <remarks>
    /// An enum is settled first, exactly as it is for a completed access. Binding <c>Level</c> as an
    /// expression instead reports an unknown name -- a second diagnostic for a syntax error already
    /// reported -- and then hands a completion list an error type in place of the enum whose values
    /// it exists to offer. A placeholder carries the type, in the shape the binder already uses
    /// wherever it has a type to publish and no value to go with it.
    /// </remarks>
    private IrExpression BindReceiverAwaitingAMember(
        MemberAccessExpression member,
        Scope scope,
        MethodContext context)
    {
        if (!TryResolveEnumReceiver(member.Receiver, scope, context, out var descriptor))
        {
            return BindExpression(member.Receiver, scope, context, null);
        }

        return descriptor is null
            ? new IrLiteral(null, ErrorType.Instance, member.Receiver.Span)
            : new IrLiteral(null, new EnumPlType(descriptor), member.Receiver.Span);
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
            // A dotted name with a piece still unwritten does not name anything, so it must not be
            // flattened into one that happens to have an empty segment.
            if (member.Name.IsMissing)
            {
                name = string.Empty;
                leadingName = string.Empty;
                return false;
            }

            parts.Insert(0, member.Name.Text);
            current = member.Receiver;
        }

        if (current is not NameExpression root)
        {
            name = string.Empty;
            leadingName = string.Empty;
            return false;
        }

        parts.Insert(0, root.Name.Text);
        name = string.Join('.', parts);
        leadingName = root.Name.Text;
        return true;
    }

    /// <summary>Whether an identifier names a value in scope, using the same order as BindName.</summary>
    private static bool IsValueName(string name, Scope scope, MethodContext context)
        => scope.LookupLocal(name) is not null
        || scope.LookupParameter(name) is not null
        || (context.AllowImplicitReceiverFields && context.Receiver.FindFieldByName(name) is not null);

    /// <remarks>
    /// The missing-name case comes first and does the most work of any failure path here, because it
    /// is the one an editor asks about constantly: the author typed a dot and is waiting to be told
    /// what may follow it. Answering means binding the receiver and keeping it, which is why the
    /// result is an <see cref="IrMissingMemberAccess"/> rather than the error literal every other
    /// failure below collapses to.
    /// </remarks>
    private IrExpression BindMemberAccess(MemberAccessExpression member, Scope scope, MethodContext context)
    {
        if (member.Name.IsMissing)
        {
            return new IrMissingMemberAccess(
                BindReceiverAwaitingAMember(member, scope, context),
                member.Name.Span);
        }

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

        var field = messageType.Descriptor.FindFieldByName(member.Name.Text);
        if (field is not null)
        {
            return BindFieldAccess(receiver, field, member.Span, context);
        }

        if (_methods.ContainsKey((messageType.Descriptor.FullName, member.Name.Text)))
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

    /// <summary>Binds a call, whether or not there turns out to be anything to call.</summary>
    /// <remarks>
    /// <para>
    /// Six paths below decide there is no method here. Every one of them still keeps the arguments,
    /// in an <see cref="IrUncallableInvocation"/>, because the arguments are source the author wrote
    /// and a call that does not resolve is the ordinary state of one being typed. Collapsing to an
    /// error-typed literal spanning the whole call -- which is what every path used to do -- leaves
    /// nothing at the positions inside the parentheses, which is exactly the region completion and
    /// signature help ask about.
    /// </para>
    /// <para>
    /// Arguments are bound once and only once. The two paths that reach a failure with them already
    /// bound hand over the list they built; the four that fail before binding anything go through
    /// <c>Uncallable</c>, which binds with no expected type -- there is no signature to expect
    /// anything from. Binding twice would report every mistake inside an argument twice.
    /// </para>
    /// </remarks>
    private IrExpression BindInvocation(InvocationExpression invocation, Scope scope, MethodContext context)
    {
        IrExpression receiver;
        string methodName;
        MessageDescriptor receiverDescriptor;

        switch (invocation.Callee)
        {
            // A callee whose name is still being typed is bound for what it is -- a receiver
            // awaiting a member -- rather than looked up as a method called the empty string.
            case MemberAccessExpression { Name.IsMissing: true } member:
                return Uncallable(BindMemberAccess(member, scope, context));

            case MemberAccessExpression member:
            {
                var boundReceiver = BindExpression(member.Receiver, scope, context, null);

                // Nothing is reported here: whatever went wrong with the receiver has been reported
                // where it went wrong, and saying that a call on it also failed is that same mistake
                // told a second time.
                if (boundReceiver.Type is ErrorType)
                {
                    return Uncallable(boundReceiver);
                }

                if (boundReceiver.Type is not MessageType messageType)
                {
                    _diagnostics.Error(
                        "PL0042",
                        "method call on a non-message value",
                        $"Type '{boundReceiver.Type.DisplayName}' has no methods.",
                        invocation.Span);
                    return Uncallable(boundReceiver);
                }

                receiver = boundReceiver;
                methodName = member.Name.Text;
                receiverDescriptor = messageType.Descriptor;
                break;
            }

            case NameExpression name:
                receiver = new IrThis(new MessageType(context.Receiver), name.Span);
                methodName = name.Name.Text;
                receiverDescriptor = context.Receiver;
                break;

            default:
                _diagnostics.Error(
                    "PL0043",
                    "expression is not callable",
                    "Only ProtoLang methods can be called.",
                    invocation.Span,
                    "Calling target-language functions is not permitted (spec 20).");
                return Uncallable(null);
        }

        if (!_methods.TryGetValue((receiverDescriptor.FullName, methodName), out var signature))
        {
            _diagnostics.Error(
                "PL0044",
                "unknown method",
                $"'{receiverDescriptor.FullName}' has no ProtoLang method named '{methodName}'.",
                invocation.Span,
                "Methods must be defined in an extend block for that message.");
            return Uncallable(receiver);
        }

        var arguments = new List<IrExpression>();
        for (var i = 0; i < invocation.Arguments.Count; i++)
        {
            var expected = i < signature.Parameters.Count ? signature.Parameters[i].Type : null;
            arguments.Add(BindExpression(invocation.Arguments[i], scope, context, expected));
        }

        if (arguments.Count != signature.Parameters.Count)
        {
            _diagnostics.Error(
                "PL0045",
                "wrong number of arguments",
                $"'{methodName}' takes {signature.Parameters.Count} argument(s) "
                + $"but {arguments.Count} were supplied.",
                invocation.Span);

            // The list that was just built, not a second binding of the same expressions: the loop
            // above runs before this check precisely so that a mistake inside an argument is
            // reported whether or not the right number of them were supplied.
            return new IrUncallableInvocation(receiver, arguments, invocation.Span);
        }

        for (var i = 0; i < arguments.Count; i++)
        {
            if (arguments[i].Type is ErrorType)
            {
                continue;
            }

            if (!TypesMatch(signature.Parameters[i].Type, arguments[i].Type))
            {
                _diagnostics.Error(
                    "PL0046",
                    "argument type mismatch",
                    $"Argument {i + 1} of '{methodName}' expects "
                    + $"'{signature.Parameters[i].Type.DisplayName}' but got "
                    + $"'{arguments[i].Type.DisplayName}'.",
                    invocation.Arguments[i].Span);
            }
        }

        return new IrMethodCall(receiver, signature, arguments, invocation.Span);

        // For the four paths that give up before any argument has been looked at. There is no
        // signature to take an expected type from -- that is what they gave up on -- so each
        // argument is bound for whatever it is on its own.
        IrUncallableInvocation Uncallable(IrExpression? boundReceiver)
            => new(
                boundReceiver,
                [.. invocation.Arguments.Select(argument => BindExpression(argument, scope, context, null))],
                invocation.Span);
    }

    /// <summary>
    /// Binds a presence test, <c>has customer.email</c> (spec 8.4).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The operand is a field, not an arbitrary expression: <c>has</c> asks whether a field is set,
    /// and there is no such question to ask about a local, a literal, or a method result. Its
    /// receiver, though, is an ordinary expression bound the ordinary way -- asking about
    /// <c>a.b</c> means reading <c>a</c>, so <c>a</c> needs its own guard.
    /// </para>
    /// <para>
    /// Which fields admit the question is <see cref="FieldDescriptor.HasPresence"/>'s answer, and
    /// deliberately not this compiler's. It is already correct for proto2, for proto3 with and
    /// without <c>optional</c>, and for editions, which is why supporting presence properly turned
    /// out to need no syntax-version check at all (spec 21.3).
    /// </para>
    /// </remarks>
    private IrExpression BindHas(HasExpression has, Scope scope, MethodContext context)
    {
        IrExpression receiver;
        FieldDescriptor? field;
        string name;

        switch (has.Operand)
        {
            // 'has customer.' names no field yet, so it is bound as the member access it is rather
            // than reported as a field called the empty string.
            case MemberAccessExpression { Name.IsMissing: true } member:
                return BindMemberAccess(member, scope, context);

            case NameExpression bare when context.AllowImplicitReceiverFields
                    && scope.LookupLocal(bare.Name.Text) is null
                    && scope.LookupParameter(bare.Name.Text) is null:
                receiver = new IrThis(new MessageType(context.Receiver), bare.Span);
                field = context.Receiver.FindFieldByName(bare.Name.Text);
                name = bare.Name.Text;
                break;

            case MemberAccessExpression member:
            {
                var target = BindExpression(member.Receiver, scope, context, null);

                if (target.Type is ErrorType)
                {
                    return new IrLiteral(null, ErrorType.Instance, has.Span);
                }

                if (target.Type is not MessageType message)
                {
                    _diagnostics.Error(
                        "PL0080",
                        "'has' needs a field",
                        $"'{target.Type.DisplayName}' is not a message, so it has no fields to test.",
                        has.Span);
                    return new IrLiteral(null, ErrorType.Instance, has.Span);
                }

                receiver = target;
                field = message.Descriptor.FindFieldByName(member.Name.Text);
                name = member.Name.Text;
                break;
            }

            default:
                _diagnostics.Error(
                    "PL0080",
                    "'has' needs a field",
                    "The operand of 'has' must name a protobuf field.",
                    has.Span,
                    "Only a field can be unset. A local, a parameter, and a method result always "
                    + "hold a value, so there is nothing to ask about.");
                return new IrLiteral(null, ErrorType.Instance, has.Span);
        }

        if (field is null)
        {
            _diagnostics.Error(
                "PL0041",
                "unknown field",
                $"'{name}' is not a field of '{(receiver.Type as MessageType)?.Descriptor.FullName}'.",
                has.Span);
            return new IrLiteral(null, ErrorType.Instance, has.Span);
        }

        if (field.IsMap)
        {
            _diagnostics.Error(
                "PL0038",
                "maps are not supported",
                $"'{name}' is a map field.",
                has.Span);
            return new IrLiteral(null, ErrorType.Instance, has.Span);
        }

        if (!field.HasPresence)
        {
            _diagnostics.Error(
                "PL0079",
                "field has no presence",
                $"'{name}' cannot be tested for presence.",
                has.Span,
                field.IsRepeated
                    ? "A repeated field has no presence; an unset one is an empty one. Compare its "
                      + "length, or iterate it and let the loop run zero times (spec 14.1)."
                    : "This field has implicit presence, so an unset value and the type's default "
                      + "are the same value on the wire. Declaring it 'optional' in the .proto "
                      + "gives it explicit presence (spec 8.4).");
            return new IrLiteral(null, ErrorType.Instance, has.Span);
        }

        return new IrFieldPresence(receiver, field, has.Span);
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

        // 'has a and has a.b' has to type-check: 'and' short-circuits, so the right operand only
        // runs where the left one held, and asking about a.b means reading a.
        var rightContext = isLogical
            ? context with
            {
                Present = binary.Operator == BinaryOperatorKind.LogicalAnd
                    ? Union(context.Present, PresenceFacts(left).WhenTrue)
                    : Union(context.Present, PresenceFacts(left).WhenFalse),
            }
            : context;

        var right = BindExpression(
            binary.Right, scope, rightContext, left.Type is ErrorType ? operandHint : left.Type);

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
    /// <remarks>
    /// Carries no parameter list, deliberately. <see cref="Scope"/> is what answers a name, and it
    /// holds a filtered view of what a signature declares: a parameter nobody has named is not in
    /// it, and a duplicate is in it once rather than twice. A second list beside it would be the
    /// complete positional one, which reads like the authoritative answer and is the wrong list to
    /// resolve a name against.
    /// </remarks>
    private sealed record MethodContext(
        MessageDescriptor Receiver,
        PlType ReturnType,
        bool AllowImplicitReceiverFields = true,
        int LoopDepth = 0)
    {
        /// <summary>
        /// Access paths whose presence has been established on every path reaching the statement
        /// being bound (spec 13.1).
        /// </summary>
        /// <remarks>
        /// A set rather than a lattice, and no fixpoint over loops, because presence facts are
        /// monotone within a method: ProtoLang cannot assign to a field, so nothing that has been
        /// shown to be set can become unset before the method ends. If receiver mutation ever
        /// arrives (spec 18), this is the assumption that has to be revisited.
        /// </remarks>
        public IReadOnlySet<string> Present { get; init; } = EmptyPresence;
    }

    private static readonly IReadOnlySet<string> EmptyPresence =
        new HashSet<string>(StringComparer.Ordinal);

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
