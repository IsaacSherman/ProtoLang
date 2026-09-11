using System.Collections.Concurrent;
using Google.Protobuf.Reflection;
using ProtoLang.Binding;
using ProtoLang.Diagnostics;
using ProtoLang.Ir;
using ProtoLang.Semantics;
using ProtoLang.Symbols;
using ProtoLang.Syntax;
using ProtoLang.Types;
using ProtoLang.LanguageServer.Protocol;
using ProtoLang.LanguageServer.Protocol.Lsp;
using ProtoLang.LanguageServer.Workspace;
using Range = ProtoLang.LanguageServer.Protocol.Lsp.Range;
using SymbolKind = ProtoLang.Symbols.SymbolKind;

namespace ProtoLang.LanguageServer.Hosting;

/// <summary>
/// One completion request, pinned to the buffer and the configuration it was asked about.
/// </summary>
/// <remarks>
/// Everything here is settled while messages are still being read in order, and none of it can move
/// afterwards: <see cref="OpenDocument"/> and <see cref="WorkspaceConfiguration"/> are both immutable,
/// so holding the objects holds the question. What comes later -- listing directories, compiling -- may take as
/// long as it takes without changing what the answer is about, and can be checked against these two
/// before it is sent.
/// </remarks>
public sealed class CompletionRequest : DocumentRequest
{
    internal CompletionRequest(
        DocumentUri uri,
        OpenDocument document,
        WorkspaceConfiguration configuration,
        CompletionSubject context)
        : base(uri, document, configuration)
        => Context = context;

    /// <summary>Which kind of place the caret turned out to be in.</summary>
    public CompletionContextKind Kind => Context.Kind;

    /// <summary>What the caret is in, decided while nothing could race the decision.</summary>
    internal CompletionSubject Context { get; }
}

/// <summary>
/// What could be typed at a position: the schemas an <c>import proto</c> path could name, and the
/// members a dot could reach.
/// </summary>
/// <remarks>
/// <para>
/// <b>Context first, candidates second.</b> This asks which of the language's completion contexts the
/// cursor is in before it asks what belongs there. A bare identifier, a type position, a receiver
/// after <c>extend</c> and a name inside a <c>test</c> are the ones still to come, and each is an arm
/// in <see cref="Read"/> and in the switch that answers, rather than a second entry point beside
/// them. A cursor in no recognized context offers nothing, which is the only correct answer -- an
/// editor that guesses produces a list the user has to dismiss on every keystroke.
/// </para>
/// <para>
/// <b>An import path is answered without compiling anything.</b> The buffer is lexed and the include
/// roots are listed, both of which answer while the user is still typing; waiting on a schema load
/// would make completion arrive after the character that invalidated it. It follows that a document
/// whose configuration file was refused still completes its imports, which is deliberate: a broken
/// <c>protolang.config.xml</c> stops compilation, and being unable to fix an import while it is
/// broken would be the second problem caused by the first.
/// </para>
/// <para>
/// <b>A schema context has no such option.</b> What may follow a dot, and what names are in scope at
/// a position, are questions only the binder can answer, so those contexts compile the buffer the
/// request was read against -- through <see cref="DocumentSemantics"/>, which is also what keeps that
/// from being a compile per keystroke. The contrast is the useful part rather than an inconsistency:
/// the cheap contexts stay cheap, and the ones that cannot be cheap say why.
/// </para>
/// <para>
/// <b>Read in order, answered out of it.</b> <see cref="Read"/> runs on the one worker that reads the
/// wire and <see cref="AnswerAsync"/> runs anywhere, and the split is where the correctness lives. A
/// request is about a position in a buffer, and which buffer that is has to be settled before the
/// next message is dequeued -- otherwise a <c>didChange</c> queued behind the request lands first,
/// the position is measured against text the client was not looking at, and every check afterwards
/// agrees with itself because it is asking about the wrong document.
/// </para>
/// <para>
/// <b>It answers about what it read, or it refuses.</b> This is the first handler in this server that
/// can genuinely go stale -- semantic tokens reads and answers in one instant, while this one goes to
/// the file system in between -- so before replying it checks that the store still holds the same
/// document object and that the configuration has not moved on, and returns LSP's
/// <c>ContentModified</c> otherwise. The check is object identity rather than a version number
/// because a version number is only unique within one open session: close a document and reopen it and
/// the client starts again at one, which compares equal to the version this request read and describes
/// entirely different text. Spec 26.1 requires the refusal, and the stakes are higher than for
/// diagnostics -- a stale completion does not merely mislead, it inserts text at an offset that has
/// stopped meaning what it meant.
/// </para>
/// <para>
/// <b>One walk per document, four at a time.</b> A completion supersedes the outstanding one for its
/// document, exactly as a keystroke supersedes a scheduled compile, so a client that asks on every
/// character cannot accumulate walks; across documents a semaphore bounds how many run at once. Both
/// exist because the per-walk budget bounds one walk and says nothing about how many there are, and a
/// slow root turns that into threads and open handles piling up behind a user who is simply typing.
/// </para>
/// <para>
/// <b>Waiting for a slot is part of the request, not part of the queue.</b> Most of a busy request's
/// life is spent waiting, and most of the reasons a request ends -- withdrawn, superseded, closed --
/// arrive while it waits. So the wait sits inside the same cleanup as the walk: whichever way it ends
/// the entry is retired, and the slot is given back only if it was ever taken. The alternative, an
/// unconditional release, hands the pool a slot for a wait that failed and quietly raises the limit by
/// one for every completion the client thought better of.
/// </para>
/// </remarks>
public sealed class CompletionProvider
{
    /// <inheritdoc cref="DeferredAnswers.DefaultConcurrency"/>
    public const int DefaultConcurrency = DeferredAnswers.DefaultConcurrency;

    private readonly DocumentStore _documents;
    private readonly ConfigurationSync _configuration;
    private readonly LoaderPool _loaders;
    private readonly DocumentSemantics _semantics;
    private readonly DeferredAnswers _deferred;

    public CompletionProvider(
        DocumentStore documents,
        ConfigurationSync configuration,
        LoaderPool loaders,
        int concurrency = DefaultConcurrency,
        DocumentSemantics? semantics = null)
    {
        _documents = documents ?? throw new ArgumentNullException(nameof(documents));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _loaders = loaders ?? throw new ArgumentNullException(nameof(loaders));

        // Shared where it is given, for the reason CompileScheduler takes the same argument: a
        // compile a keystroke scheduled and a list asked for between two keystrokes should be one
        // compile. A caller with no interest in that gets one of its own.
        _semantics = semantics ?? new DocumentSemantics(loaders);

        _deferred = new DeferredAnswers("completion", documents, configuration, concurrency);
    }

    /// <summary>The characters that should make a client ask without being asked to.</summary>
    /// <remarks>
    /// <para>
    /// Two open a path segment. The quote starts a path and the separator starts a segment inside
    /// one, and since candidates are enumerated a directory at a time, the separator is precisely the
    /// keystroke after which the previous answer stopped describing anything.
    /// </para>
    /// <para>
    /// The dot is the third, and it is the one that makes member completion work at all: it is typed
    /// at the moment the member name is genuinely missing, which is the state the binder keeps the
    /// receiver's type through. One flat list rather than one per context, because the protocol has
    /// no way to scope a trigger character to a position -- so the list is the union, and each
    /// context still decides for itself whether it has anything to say.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> TriggerCharacters { get; } = ["\"", "/", "."];

    /// <summary>Where candidates come from.</summary>
    /// <remarks>
    /// <see cref="SchemaCatalog.Enumerate"/>, and a seam for a test. It is the one step here that
    /// leaves this process -- lexing and resolving are arithmetic, listing a directory is not -- so it
    /// is also the only window in which the buffer can move while a request is being answered. A test
    /// that has to prove the refusal above has to be able to move it inside that window, and there is
    /// nowhere else to stand.
    /// </remarks>
    public Func<string, IReadOnlyList<string>, CancellationToken, SchemaListing> Enumerate { get; set; }
        = (directory, roots, cancellationToken)
            => SchemaCatalog.Enumerate(directory, roots, cancellationToken: cancellationToken);

    /// <summary>Documents with a completion still outstanding.</summary>
    /// <inheritdoc cref="DeferredAnswers.Outstanding" path="/remarks"/>
    public int Outstanding => _deferred.Outstanding;

    /// <summary>Walks that are past the gate and have not yet returned.</summary>
    public int InFlight => _deferred.InFlight;

    /// <summary>The most walks that have ever been past the gate at one moment.</summary>
    /// <inheritdoc cref="CompileScheduler.PeakInFlight" path="/remarks"/>
    public int PeakInFlight => _deferred.PeakInFlight;

    /// <summary>Nothing to offer, which is not the same as a failure to offer it.</summary>
    public static CompletionList Nothing { get; } = new() { Items = [] };

    /// <summary>
    /// Which buffer a request is about and where in it -- settled while messages are still being read
    /// in order -- or null when the position is nowhere anything can be offered.
    /// </summary>
    /// <remarks>
    /// <b>This half must not be deferred.</b> Everything after it is allowed to take as long as the
    /// file system does, and none of it is allowed to decide <em>which text</em> the answer is about:
    /// a <c>didChange</c> sitting behind this request in the queue would otherwise be applied first,
    /// and the position would be measured against a buffer the client had not sent when it asked. The
    /// configuration is captured here for the same reason and by the same means -- it is immutable and
    /// stamped with a generation, so holding the object holds the answer.
    /// </remarks>
    public CompletionRequest? Read(CompletionParams message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!DocumentUri.TryParse(message.TextDocument.Uri, out var uri) || _documents.Find(uri) is not { } document)
        {
            // Closed before it was read, or never opened. Nothing rather than an error: the client has
            // done nothing wrong, and there is genuinely nothing to offer.
            return null;
        }

        var offset = EditorPositions.OffsetOf(document.Lines, message.Position);

        // Import first, and nothing currently depends on that. What keeps the two apart is that a
        // caret inside an import path is inside a string literal, and the general probe declines
        // every string literal on its own -- so reversing these two lines produces the same answers.
        // Said explicitly because it is the kind of ordering a reader assumes is load-bearing and
        // then preserves for the wrong reason: a context added later that does mean to answer inside
        // a string has to say so where strings are refused, not rely on being probed second.
        if (ImportPathContext.TryFind(document.Text, offset, out var path))
        {
            return new CompletionRequest(uri!, document, _configuration.Current, path!);
        }

        return SchemaSubject.TryFind(document.Text, offset, out var subject)
            ? new CompletionRequest(uri!, document, _configuration.Current, subject!)
            : null;
    }

    /// <summary>Everything that could be typed at the position <see cref="Read"/> settled.</summary>
    /// <exception cref="JsonRpcException">
    /// The buffer or the configuration moved while this was being answered. See the type's remarks.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// The client withdrew the request, or a newer one for the same document superseded it. Either
    /// way nobody is waiting on the answer, and finishing the walk is work spent on nothing.
    /// </exception>
    public Task<CompletionList> AnswerAsync(CompletionRequest asked, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asked);

        return _deferred.AnswerAsync(asked, token => Answer(asked, token), cancellationToken);
    }

    /// <remarks>
    /// Runs behind <see cref="DeferredAnswers"/>, which has already checked that the buffer is still
    /// the one this was read against and will check again before the list is sent. What is left here
    /// is the arm the type's remarks promised.
    /// </remarks>
    private CompletionList Answer(CompletionRequest asked, CancellationToken cancellationToken)
    {
        // Each context produces its own candidates and says for itself whether the list is finished,
        // because the two answers are one decision: a list is incomplete exactly when typing another
        // character would widen it.
        var (items, incomplete) = asked.Context switch
        {
            ImportPathContext path => (Schemas(asked, path, cancellationToken), true),
            SchemaSubject subject => (Symbols(asked, subject, cancellationToken), false),
            _ => ([], true),
        };

        return new CompletionList { Items = items, IsIncomplete = incomplete };
    }

    /// <summary>Abandons a document's outstanding completion, because it is no longer open.</summary>
    /// <inheritdoc cref="DeferredAnswers.Forget" path="/remarks"/>
    public void Forget(DocumentUri document) => _deferred.Forget(document);

    /// <summary>What the include roots hold at the directory the cursor is in.</summary>
    /// <remarks>
    /// <para>
    /// The roots are the compilation's own, asked for through the same two functions the compilation
    /// asks -- so a candidate offered here is one <c>SchemaLookup</c> would find, in the root it would
    /// find it in. A loader that could not be built costs only the well-known schemas; the user's own
    /// roots still answer, which is the case that matters most, because a workspace with no protoc is
    /// one where nothing else is telling the user anything at all.
    /// </para>
    /// <para>
    /// <b>The cheap half of the configuration, not the whole of it.</b>
    /// <c>WorkspaceConfiguration.Resolve</c> also settles the language policy, which means searching
    /// upward for a <c>protolang.config.xml</c> and parsing it -- a directory walk and an XML parse,
    /// for a value completion never reads, on a request that runs per keystroke rather than per
    /// debounced compile. <c>ResolveImportRoots</c> is the same resolution minus that step, and it
    /// shares the two resolvers with <c>Resolve</c> rather than restating their precedence.
    /// </para>
    /// </remarks>
    /// <summary>What this file could import, one directory level of every root.</summary>
    private IReadOnlyList<CompletionItem> Schemas(
        CompletionRequest asked, ImportPathContext context, CancellationToken cancellationToken)
    {
        var document = asked.Document;

        // Resolved from the configuration this request read rather than from whatever is current, so
        // the roots walked and the roots checked at the end are the same roots.
        var settings = asked.Configuration.ResolveImportRoots(asked.Uri);

        _loaders.TryGet(settings.ProtocPath, out var loader, out _);

        var searchPaths = Compilation.GetSearchPaths(
            document.ToSource(settings.Folder?.Path).Identity,
            settings.IncludeDirectories);

        var roots = SchemaCatalog.RootsFor(searchPaths, loader);

        // A listing cut short by its budget is still offered. The list is already declared incomplete
        // to the client, which re-asks on the next keystroke, and a person narrowing a very wide
        // directory by typing is better served by a prefix of it than by nothing. The near match on a
        // failed import makes the opposite choice, and says why.
        return
        [
            .. Enumerate(context.Directory, roots, cancellationToken).Candidates
                .Select(candidate => Item(candidate, context, document)),
        ];
    }

    /// <summary>What the schema and the binder's own rules allow where the caret is.</summary>
    /// <remarks>
    /// <para>
    /// The contexts land one at a time; a caret in one that has not landed offers nothing, which is
    /// what it offered before this arm existed.
    /// </para>
    /// <para>
    /// <b>The two name contexts are asked before the dot is read as a member access</b>, because in
    /// both of them a dot is part of the name rather than a reach into a value. An <c>extend</c>
    /// receiver and a type reference are each written as one qualified name, so
    /// <c>protolang.tests.Ou|ter</c> is not a member of <c>protolang.tests</c> -- and a caret there
    /// dispatched on the dot alone asks for the members of something that is not a value and gets
    /// nothing, in a position where the whole type universe applies. Both answer null where the caret
    /// is not in one, which is what leaves an ordinary dot meaning what it usually means.
    /// </para>
    /// </remarks>
    private IReadOnlyList<CompletionItem> Symbols(
        CompletionRequest asked, SchemaSubject subject, CancellationToken cancellationToken)
    {
        var compiled = _semantics.For(asked.Document, asked.Configuration, cancellationToken);

        if (compiled.Semantics is not { } model || compiled.Result is not { } result)
        {
            return [];
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (ExtendedAt(model, subject, asked.Document) is { } extended)
        {
            return extended.Writable(Receivers(result, extended.Subject, asked.Document));
        }

        if (Targeted(model, result, subject, asked.Document) is { } target)
        {
            return target;
        }

        // Before the scope query as well as before the dot, because a type position is one of the
        // places that query declines on purpose -- it returns nothing inside a type reference, and
        // taking that for "no names here" would leave the whole context silent.
        if (TypesAt(model, result, subject, asked.Document) is { } typePosition)
        {
            return typePosition;
        }

        // Settled once and handed to both arms, because each is one question about the caret and the
        // two arms would otherwise each ask it of a different thing.
        var presence = NamesAPresenceField(model, subject);
        var writingAReceiver = subject.FollowedByDot || NamesAReceiver(model, subject);

        if (subject.PrecededByDot)
        {
            return Members(
                ReceiverAt(model, subject), result, subject, presence, writingAReceiver, asked.Document);
        }

        if (Fixture(model, result, subject, asked.Document) is { } names)
        {
            return names;
        }

        return InScope(model, result, subject, presence, writingAReceiver, asked.Document);
    }

    /// <summary>
    /// Whether the caret is writing the name that has to be a field for a <c>has</c> to mean
    /// anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>has</c> takes a field and nothing else. <c>BindHas</c> accepts two shapes -- a bare name
    /// that is a field of the implicit receiver, and a member access whose member is a field -- and
    /// refuses everything else with <c>PL0080</c>, whose help says why: a local, a parameter and a
    /// method result always hold a value, so there is nothing to ask about. So the ordinary answer is
    /// wrong here in a way that is invisible to the sweep, which was not told that <c>PL0080</c> is a
    /// name failing to bind.
    /// </para>
    /// <para>
    /// <b>One name in the operand is constrained and the rest are not.</b> Everything before the
    /// field is computing the receiver it hangs off, and that may be any message-valued expression at
    /// all: <c>has other.inner.stamp</c> reaches through a parameter and a message field,
    /// <c>has (other).optional_count</c> through a parenthesized one, and
    /// <c>has identity(other).optional_count</c> through a call. None of those is a field of the
    /// thing <c>has</c> is finally asked about, and all of them are perfectly good where they stand.
    /// </para>
    /// <para>
    /// <b>So the question is asked of the operand's shape, not of the token after the caret.</b>
    /// Looking for a following dot was a proxy for "the last name", and it is a proxy that a
    /// parenthesis or an argument list breaks: the token after <c>other</c> in
    /// <c>has (other).optional_count</c> is a close paren, so the proxy called it the field being
    /// tested and withheld every name that could compute it. <c>BindHas</c> names the field in
    /// exactly two places -- a bare operand, or the member of a member access -- and those two are
    /// what this reads, so the answer here is the same one the binder is about to reach.
    /// </para>
    /// </remarks>
    private static bool NamesAPresenceField(SemanticModel model, SchemaSubject subject)
        => model.SyntaxAt(subject.Start)?.Enclosing<HasExpression>() is { } has
            && EndsAt(has.Operand) is { } field
            && Covers(field, subject.Start);

    /// <summary>
    /// Whether the caret is writing a name that something else is about to take a member off.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only a value with members can go in front of a dot, and the two lists both say so by asking
    /// <see cref="SchemaSubject.FollowedByDot"/> -- which is a fact about the next <b>token</b>. That
    /// answers for <c>other.count</c> and lies for <c>(other).count</c>, where the token after
    /// <c>other</c> is a close paren: nothing said it was a receiver, so every scalar field was
    /// offered, and accepting one wrote <c>(count).count</c> for <c>PL0039</c> -- or, inside a
    /// <c>has</c>, <c>PL0080</c>. Twelve of the twenty names offered at that caret were invalid.
    /// </para>
    /// <para>
    /// <b>The parser builds no node for a parenthesized expression</b> -- it returns what was inside
    /// -- so the parentheses are not what has to be recognized. What has to be recognized is that the
    /// receiver of a member access <i>ends at</i> the name under the caret, whatever was written
    /// around it. That is the same question <see cref="NamesAPresenceField"/> asks of a <c>has</c>
    /// operand, so it is the same helper.
    /// </para>
    /// <para>
    /// Every enclosing access is asked rather than the innermost, because parentheses nest:
    /// <c>(other.inner).count</c> puts the caret on <c>inner</c> inside one member access and at the
    /// end of another's receiver, and only the outer one knows that a dot is coming.
    /// </para>
    /// <para>
    /// A receiver that is a call ends in no name at all, and that is the answer rather than a gap.
    /// Neither name in <c>identity(oth|er).count</c> is the receiver: the argument is free to be any
    /// type the parameter takes, and the method is a call rather than a value, which
    /// <see cref="SchemaSubject.FollowedByCall"/> already governs. Narrowing either to message-valued
    /// names would withhold every name that belongs.
    /// </para>
    /// <para>
    /// The token fact is kept beside this rather than replaced by it. <c>other.|</c> mid-keystroke is
    /// the state completion is usually asked in, and a buffer that has not parsed has no member
    /// access to find -- so the two are a union, each answering where the other cannot.
    /// </para>
    /// </remarks>
    private static bool NamesAReceiver(SemanticModel model, SchemaSubject subject)
        => model.SyntaxAt(subject.Start) is { } location
            && location.Path.OfType<MemberAccessExpression>().Any(access
                => EndsAt(access.Receiver) is { } name && Covers(name, subject.Start));

    /// <summary>Where an expression's trailing name is written, or null when it ends in no name.</summary>
    /// <remarks>
    /// Two shapes end in a name -- a bare identifier, and a member taken off something -- and every
    /// other expression ends in punctuation, a literal, or an operand. That is also exactly the pair
    /// <c>BindHas</c> accepts, and not by coincidence: a field reference is an expression that ends
    /// in the field's name, which is the whole of what <c>has</c> is allowed to be handed.
    /// </remarks>
    private static SourceSpan? EndsAt(Expression expression)
        => expression switch
        {
            MemberAccessExpression member => member.Name.Span,
            NameExpression bare => bare.Name.Span,
            _ => null,
        };

    /// <summary>
    /// The caret's subject widened to the whole <c>extend</c> receiver it is writing, or null when it
    /// is writing something else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The parser is what knows where the receiver begins and ends, because it is what decided that
    /// <c>protolang.tests.Outer</c> was one qualified name rather than three. Replacing only the
    /// segment under the caret turns <c>extend proto|lang.tests.Outer</c> into
    /// <c>extend Inner.tests.Outer</c>, which is <c>PL0021</c> -- the same defect
    /// <see cref="TypesAt"/> fixes for a type reference, in the one other place a qualified name is
    /// written.
    /// </para>
    /// <para>
    /// The token fact is kept as the fallback rather than replaced by the tree, and it is not
    /// redundant: <c>extend |</c> in a buffer that has not parsed at all has no declaration to ask,
    /// and that is precisely the state the list is requested in.
    /// </para>
    /// </remarks>
    private static QualifiedName? ExtendedAt(
        SemanticModel model, SchemaSubject subject, OpenDocument document)
    {
        if (model.SyntaxAt(subject.Start)?.Enclosing<ExtendDeclaration>() is { } extend
            && Covers(extend.MessageName.Span, subject.Start))
        {
            return Replacing(subject, extend.MessageName.Span, document);
        }

        return subject.PrecededByExtend ? new QualifiedName(subject, string.Empty, string.Empty) : null;
    }

    /// <summary>
    /// Where an item's text will be written, and what of the name around it will still be there
    /// afterwards.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both halves travel together because neither is usable alone. A range that replaces the whole
    /// written name retains nothing, and every candidate spelling may be offered against it. A range
    /// that replaces one segment of it -- which is all LSP allows when the name is spread over two
    /// lines -- leaves the rest of the name standing, and an item is then only writable if the name it
    /// composes with what stayed is itself a name the compiler would accept.
    /// </para>
    /// <para>
    /// The retained text is stripped of whitespace, because within a qualified name whitespace only
    /// ever sits around a dot: the newline and the indentation of <c>protolang.</c> then
    /// <c>tests.Outer</c> are between segments of one name, and the name it composes is
    /// <c>protolang.tests.Outer</c>.
    /// </para>
    /// </remarks>
    private sealed record QualifiedName(SchemaSubject Subject, string Prefix, string Suffix)
    {
        /// <summary>
        /// <paramref name="offered"/> rewritten into what can actually be written here.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Untouched where the edit replaces the whole name -- which is every ordinary caret, so the
        /// common path allocates nothing and decides nothing.
        /// </para>
        /// <para>
        /// <b>Otherwise each spelling is cut down to the part this edit could write, rather than being
        /// kept or dropped whole.</b> Filtering was the first attempt and it is wrong in both
        /// directions at once, because a segment of a name is not a name. Where two packages declare
        /// <c>Common</c>, no simple spelling is offered at all -- writing it unqualified is
        /// <c>PL0074</c> -- so a retained <c>a.</c> that makes it unambiguous had nothing left to keep
        /// and the caret went silent. And a package segment is not a type in any list, so a caret on
        /// the <c>a</c> of <c>a.Common</c> could never be answered by choosing among type names.
        /// </para>
        /// <para>
        /// What is written here is a fragment of some whole name, so the fragments are taken from the
        /// whole names: a spelling that begins with what stays in front and ends with what stays
        /// behind contributes the piece in between. That piece is offered exactly when writing it
        /// reconstructs that spelling, so it inherits the spelling's own guarantee and this decides
        /// nothing about what resolves. It also cannot span a dot, because the range is one word.
        /// </para>
        /// </remarks>
        public IReadOnlyList<CompletionItem> Writable(IReadOnlyList<CompletionItem> offered)
        {
            if (Prefix.Length == 0 && Suffix.Length == 0)
            {
                return offered;
            }

            var written = new List<CompletionItem>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var item in offered)
            {
                if (Fragment(item.Label) is { } fragment && seen.Add(fragment))
                {
                    // The inserted text as well as the label, because they are two statements of one
                    // thing and a client honours the second. Rewriting only the label offers 'Mapped'
                    // and writes 'protolang.tests.Mapped' into a range holding one segment, which
                    // composes the qualifier twice -- the very defect this exists to prevent, and it
                    // reads correctly in the list right up until it is accepted.
                    //
                    // The detail keeps the whole name, which is what is being completed to and now the
                    // only place a reader can see it. The sort text keeps the rank it was given, so
                    // kinds stay grouped as they do everywhere else.
                    written.Add(item with
                    {
                        Label = fragment,
                        FilterText = fragment,
                        TextEdit = item.TextEdit is { } edit ? edit with { NewText = fragment } : null,
                    });
                }
            }

            return written;
        }

        /// <summary>The part of <paramref name="spelling"/> this edit would write, or null when it
        /// could not write this spelling at all.</summary>
        private string? Fragment(string spelling)
        {
            if (spelling.Length <= Prefix.Length + Suffix.Length
                || !spelling.StartsWith(Prefix, StringComparison.Ordinal)
                || !spelling.EndsWith(Suffix, StringComparison.Ordinal))
            {
                return null;
            }

            var fragment = spelling[Prefix.Length..(spelling.Length - Suffix.Length)];

            return fragment.Contains('.') ? null : fragment;
        }
    }

    /// <summary>The subject with its edit range moved to the whole of a name already written.</summary>
    /// <remarks>
    /// <para>
    /// One home for it because both qualified-name contexts need it and the reason is the same in
    /// each: <see cref="SchemaSubject.Start"/> and <see cref="SchemaSubject.End"/> come from walking
    /// word characters outward, which stops at a dot, and a name with dots in it is still one name.
    /// </para>
    /// <para>
    /// <b>A range LSP cannot express is refused rather than returned</b>, and both halves of that are
    /// the protocol's rule rather than a preference: a completion's edit range must contain the
    /// position the request was made at, and it must begin and end on one line. A client is entitled
    /// to discard or misapply an item that breaks either, so an item that cannot replace the whole
    /// name falls back to replacing the word under the caret, which satisfies both by construction --
    /// it is where the caret is, and a word stops at a newline.
    /// </para>
    /// <para>
    /// Each half has its own case. A name the parser recovered rather than read has an empty span at
    /// the insertion point where the name would have gone, which for <c>var local: | = inner</c> sits
    /// back against the colon while the caret is a space further on; there is no written name to
    /// replace, so the caret's own range was already the right one. A name written across two lines --
    /// <c>protolang.</c> then <c>tests.Outer</c> -- is legal source that no single-line range can
    /// cover, and the fallback replaces the last segment instead. <b>That is the one place this file
    /// offers something whose acceptance may not bind</b>, because no range exists that would: the
    /// item is a whole name and only part of the name is reachable. Silence was the alternative and is
    /// worse, since the overwhelmingly common acceptance is the name already written, which the
    /// fallback reproduces exactly.
    /// </para>
    /// <para>
    /// Enforced here rather than at each caller because it is one invariant about every item this file
    /// produces, and the sweep asserts it of every one of them. What the fallback then leaves standing
    /// is reported alongside it, because an item that cannot replace the whole name has to be judged
    /// against the part of the name that stays; see <see cref="QualifiedName"/>.
    /// </para>
    /// </remarks>
    private static QualifiedName Replacing(SchemaSubject subject, SourceSpan name, OpenDocument document)
    {
        if (name.Start.Line == name.End.Line
            && name.Start.Offset <= subject.Offset
            && subject.Offset <= name.End.Offset)
        {
            return new QualifiedName(
                subject with { Start = name.Start.Offset, End = name.End.Offset },
                string.Empty,
                string.Empty);
        }

        var (prefix, suffix) = Qualification(document.Text, name, subject);

        return new QualifiedName(subject, prefix, suffix);
    }

    /// <summary>The segments of a written name that an edit on one of them leaves standing.</summary>
    /// <remarks>
    /// <para>
    /// Taken from the name's own tokens rather than from the text between its ends, because the text
    /// between its ends is not the name. Whitespace lives there, which is what makes a name span two
    /// lines at all -- and so do comments: <c>protolang. /* receiver */ tests.Outer</c> is one
    /// qualified name and one piece of trivia, and a qualifier reconstructed by copying characters
    /// carries the comment into it and matches no spelling of anything. Stripping whitespace was the
    /// first attempt and it fixed the newline while leaving the comment, which is the same mistake
    /// with a smaller blast radius.
    /// </para>
    /// <para>
    /// The lexer already draws that line and is the one that drew it for the parser, so it is asked
    /// rather than imitated. It runs over the name alone rather than the buffer, which is why this
    /// can afford to be a second lex: the region is one qualified name long.
    /// </para>
    /// </remarks>
    private static (string Prefix, string Suffix) Qualification(
        string text, SourceSpan name, SchemaSubject subject)
    {
        var start = name.Start.Offset;
        var written = new Lexer(
            text[start..name.End.Offset], SourceIdentity.UnsavedName, new DiagnosticBag()).Tokenize();

        var segments = written.Where(token => token.Kind is TokenKind.Identifier).ToList();

        return (
            string.Concat(segments
                .Where(token => start + token.Span.End.Offset <= subject.Start)
                .Select(token => token.Text + ".")),
            string.Concat(segments
                .Where(token => start + token.Span.Start.Offset >= subject.End)
                .Select(token => "." + token.Text)));
    }

    /// <summary>
    /// What the two halves of a <c>test</c> target can name, or null when the caret is in neither.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A target is a message and one of its methods, and both are names the author did not invent:
    /// the message is in a schema and the method is a few lines above, so this is the context where
    /// completion has the most to say and said nothing at all until now. It is its own arm rather
    /// than a case of the scope query because a target is a declaration header, and
    /// <c>ScopeAt</c> declines in headers on purpose -- the names there are not values.
    /// </para>
    /// <para>
    /// <b>The two halves constrain each other, and that is what keeps either from stranding the
    /// other.</b> A target binds only when the message resolves <em>and</em> declares the method, so
    /// an item that replaces one half has to be one the other half survives. The method list is
    /// therefore the methods of the receiver already written, and the receiver list is the messages
    /// that declare the method already written -- which is not a filter on top of a general answer
    /// but the answer itself, since a receiver without that method produces <c>PL0058</c> the moment
    /// it is accepted.
    /// </para>
    /// <para>
    /// <b>A target with no receiver is left alone.</b> Written without a dot, <c>test f</c> is a
    /// method and no receiver -- the parser says so, and <see cref="TestTarget"/> explains why it
    /// reads it that way -- and there is no single name that completes it, because what is missing is
    /// a name <em>and</em> a dot. Offering a message there produces <c>test EnumCase</c>, which is
    /// <c>PL0057</c>. Something could be offered that inserts both, and it would not be one of these:
    /// every item here replaces one name with one name.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<CompletionItem>? Targeted(
        SemanticModel model, CompilationResult result, SchemaSubject subject, OpenDocument document)
    {
        if (model.SyntaxAt(subject.Start)?.Enclosing<TestTarget>() is not { } target
            || result.Module is not { } module)
        {
            return null;
        }

        // The method first, because a missing method's insertion point is the position just after
        // the dot, and a missing receiver's is just before the method -- so at 'test Outer.|' both
        // are empty ranges at nearly the same place, and only one of them is what is being written.
        if (Covers(target.Method.Span, subject.Start))
        {
            return Methods(module, result, target, Replacing(subject, target.Method.Span, document), document);
        }

        return Covers(target.Receiver.Span, subject.Start) && !target.Receiver.IsMissing
            ? Receivers(module, result, target, Replacing(subject, target.Receiver.Span, document), document)
            : null;
    }

    /// <summary>The methods a test could target on the receiver its header already names.</summary>
    /// <inheritdoc cref="Targeted" path="/remarks/para[2]"/>
    private static IReadOnlyList<CompletionItem> Methods(
        IrModule module,
        CompilationResult result,
        TestTarget target,
        QualifiedName written,
        OpenDocument document)
    {
        if (target.Receiver.IsMissing || result.Types.ResolveReceiver(target.Receiver.Text) is not { } receiver)
        {
            return [];
        }

        return written.Writable(
        [
            .. module.MethodsOn(receiver.FullName).Select(method => Member(
                method.Name,
                CompletionItemKind.Method,
                method.Signature.DisplayName,
                null,
                "0",
                written.Subject,
                document)),
        ]);
    }

    /// <summary>The messages a test could target, given the method its header already names.</summary>
    /// <inheritdoc cref="Targeted" path="/remarks/para[2]"/>
    private static IReadOnlyList<CompletionItem> Receivers(
        IrModule module,
        CompilationResult result,
        TestTarget target,
        QualifiedName written,
        OpenDocument document)
        => written.Writable(
        [
            .. result.Types.All
                .Where(type => type.IsMessage && Declares(module, type.FullName, target.Method))
                .SelectMany(type => Receiver(type, result, written.Subject, document)),
        ]);

    /// <summary>Whether this message declares the method a target names, or any at all when it names none.</summary>
    private static bool Declares(IrModule module, string receiver, SyntaxName method)
    {
        var declared = module.MethodsOn(receiver);

        return method.IsMissing
            ? declared.Count > 0
            : declared.Any(candidate => string.Equals(candidate.Name, method.Text, StringComparison.Ordinal));
    }

    /// <summary>The messages that could receive an <c>extend</c> block.</summary>
    /// <remarks>
    /// Messages alone. <c>ResolveMessage</c> never looks at enums, so an enum offered here would be
    /// <c>PL0021</c> the moment it was accepted. Ambiguity is the receiver question rather than the
    /// type question -- a message whose simple name an enum happens to share is still unambiguous as
    /// a receiver -- so it is asked of the index by that name, and answering it with the type rule
    /// would withhold a name the compiler accepts.
    /// </remarks>
    private static IReadOnlyList<CompletionItem> Receivers(
        CompilationResult result, SchemaSubject subject, OpenDocument document)
        =>
        [
            .. result.Types.All
                .Where(type => type.IsMessage)
                .SelectMany(type => Receiver(type, result, subject, document)),
        ];

    private static IEnumerable<CompletionItem> Receiver(
        SchemaTypeName type, CompilationResult result, SchemaSubject subject, OpenDocument document)
    {
        var documentation = Documentation(result, type);

        if (!result.Types.IsAmbiguousAsAReceiverName(type.SimpleName))
        {
            yield return Member(
                type.SimpleName, CompletionItemKind.Class, type.FullName, documentation, "0", subject, document);
        }

        yield return Member(
            type.FullName, CompletionItemKind.Class, type.FullName, documentation, "1", subject, document)
            with
            {
                FilterText = type.SimpleName,
            };
    }

    /// <summary>
    /// The names a <c>test</c> declaration can write: a field of the message being built, or an
    /// argument of the method under test. Null when the caret is in neither.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both sets are known exactly, which is what makes this worth doing at all: the author is typing
    /// names they did not write, from a schema and a signature that are both in front of the compiler.
    /// </para>
    /// <para>
    /// <b>Nothing is offered until the target resolves.</b> <c>BindTest</c> returns null when it
    /// cannot, so a half-written <c>test</c> header has no <c>IrTest</c> at all -- and there is
    /// genuinely nothing to say until the compiler knows which message and which method the fixture
    /// is for.
    /// </para>
    /// <para>
    /// A field already given a value is dropped, because a singular field written twice is
    /// <c>PL0061</c>. A map field is dropped as everywhere else, this time because a map in a fixture
    /// is <c>PL0060</c> rather than <c>PL0038</c> -- a different code for the same unsupported thing.
    /// Repeated fields stay, since a repeated field may be written as many times as the author likes.
    /// </para>
    /// <para>
    /// <b>Being inside a fixture is not the same as naming one of its fields</b>, and the values are
    /// inside it too. A fixture field's value is an ordinary expression bound against an empty scope
    /// with no implicit receiver, so a field name accepted at <c>count = tr|ue</c> writes
    /// <c>count = count</c> and is <c>PL0037</c> -- a name that resolves nowhere, offered because the
    /// enclosing message value was found and nothing asked whether the caret was in a name position
    /// at all. An expression under the caret is what says it is not, and the value region then
    /// answers the way every other expression in a test does.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<CompletionItem>? Fixture(
        SemanticModel model, CompilationResult result, SchemaSubject subject, OpenDocument document)
    {
        if (model.IrAt(subject.Start) is not { } at || at.Enclosing<IrTest>() is not { } test)
        {
            return null;
        }

        if (at.Enclosing<IrExpression>() is not null)
        {
            return null;
        }

        if (subject.PrecededByArg)
        {
            // The one being written does not count as written, whatever it currently reads. The
            // caret is inside it, so it is the name the author is choosing -- and marking it spent
            // removes it from the one list where they are deciding whether to keep it. #56 found the
            // same thing about the import being edited, and it is the same mistake.
            var written = test.Arguments
                .Where(argument => !Covers(argument.Span, subject.Start))
                .Select(argument => argument.Name)
                .ToHashSet(StringComparer.Ordinal);

            return
            [
                .. test.Target.Parameters
                    .Where(parameter => !written.Contains(parameter.Name))
                    .Select(parameter => Member(
                        parameter.Name,
                        CompletionItemKind.Variable,
                        parameter.Type.DisplayName,
                        null,
                        "0",
                        subject,
                        document)),
            ];
        }

        // Which message the caret is naming a field of, and the two ways to get it wrong are opposite.
        // A value's span covers its fields and not the braces around them, so a caret on the blank
        // line just inside 'items {' falls outside the nested value and would take the outer
        // message's fields. But a caret on the word 'items' itself is inside that same field value
        // and is naming a field of the outer message, not of the nested one. What separates them is
        // that a field value begins at its own name: sitting there means naming it, and anywhere else
        // inside it means being within the block it opens.
        var holder = at.Enclosing<IrTestFieldValue>();

        var level = holder is { MessageValue: { } nested } && Within(holder.Span, subject.Start)
            ? nested
            : at.Enclosing<IrTestMessageValue>();

        if (level is null)
        {
            return null;
        }

        var already = level.Fields
            .Where(field => !field.Field.IsRepeated && !Covers(field.Span, subject.Start))
            .Select(field => field.Field.Name)
            .ToHashSet(StringComparer.Ordinal);

        return
        [
            .. level.Descriptor.Fields.InDeclarationOrder()
                .Where(field => !field.IsMap && !already.Contains(field.Name))
                .Select(field => Member(
                    field.Name,
                    CompletionItemKind.Field,
                    TypeFactory.FromField(field).DisplayName,
                    Documentation(result, field),
                    "0",
                    subject,
                    document)),
        ];
    }

    /// <summary>Both ends inclusive, so a caret that has just finished typing a name is still in it.</summary>
    private static bool Covers(SourceSpan span, int offset)
        => offset >= span.Start.Offset && offset <= span.End.Offset;

    /// <summary>Strictly inside, which is what "in the block this opens" means.</summary>
    /// <remarks>
    /// Neither end counts, and each is excluded for its own reason. The start is where the field's
    /// own name is written, so a caret there is naming that field rather than filling it in. The end
    /// is the brace that closes it, so a caret there has left the block and is back among the fields
    /// of the message outside. Containment elsewhere is inclusive at both ends, deliberately, which is
    /// exactly why this needs saying rather than reusing it.
    /// </remarks>
    private static bool Within(SourceSpan span, int offset)
        => offset > span.Start.Offset && offset < span.End.Offset;

    /// <summary>The types that could be named where the caret is, or null when it is not a type position.</summary>
    /// <remarks>
    /// <para>
    /// A type position is a caret inside a <c>TypeReference</c>, which is the same question
    /// <c>ScopeAt</c> asks in order to answer nothing there -- or in the gap where one is still to be
    /// written, which <see cref="TypeSlotAt"/> settles. Asked of the tree rather than of the tokens
    /// because the parser knows the four places a type may be written -- a parameter, a declared
    /// variable, a return type, a cast target -- and a token-level guess would be a fifth opinion
    /// about the grammar.
    /// </para>
    /// <para>
    /// <b>An ambiguous simple name is offered only qualified.</b> Where two packages declare the same
    /// simple name, writing it unqualified is <c>PL0074</c>, so offering it would be offering a name
    /// the compiler is about to refuse -- and the help on that very diagnostic says to qualify it. The
    /// simple name goes in the filter text instead, so typing it still surfaces the qualified forms.
    /// Whether a name is ambiguous is asked of the same index the binder resolves against, which is
    /// what keeps the two answers the same answer.
    /// </para>
    /// <para>
    /// <c>void</c> is offered only as a return type, which is the one place the language accepts it.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<CompletionItem>? TypesAt(
        SemanticModel model, CompilationResult result, SchemaSubject subject, OpenDocument document)
    {
        if (model.SyntaxAt(subject.Start) is not { } at)
        {
            return null;
        }

        if ((at.Enclosing<TypeReference>() ?? TypeSlotAt(at, subject.Start)) is not { } reference)
        {
            return null;
        }

        var returning = at.Method?.ReturnType is { } declared && ReferenceEquals(declared, reference);

        // The whole written name, dots included. A qualified type is one name rather than a chain of
        // members, so replacing only the segment under the caret turns 'protolang.tests.Outer' into
        // 'Duration.tests.Outer'. The parser's own idea of where the name starts and ends is used,
        // because it is the one that decided this was a single qualified name in the first place.
        var written = Replacing(subject, reference.Name.Span, document);

        subject = written.Subject;

        return written.Writable(
        [
            // The spellings the scalar factory accepts, taken from the one keyword table rather than
            // written out again -- so a scalar added to the language is offered without this line
            // being touched, and one that is only a keyword is never offered as a type.
            .. Lexer.Keywords.Keys
                .Where(spelling => TypeFactory.TryGetScalar(spelling) is not null
                    || (returning && spelling == "void"))
                .Order(StringComparer.Ordinal)
                .Select(spelling => Member(
                    spelling, CompletionItemKind.Keyword, "scalar type", null, "0", subject, document)),

            .. result.Types.All.SelectMany(type => Spellings(type, result, subject, document)),
        ]);
    }

    /// <summary>
    /// The type slot of a declaration the caret is standing in, or null when it is standing anywhere
    /// else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Recovery is the whole reason this is needed. A type that has not been written leaves a
    /// <c>TypeReference</c> standing on the token that stopped the parse -- the <c>=</c> of
    /// <c>var local: = inner;</c> -- so the caret in the gap after the colon is inside no
    /// <c>TypeReference</c> at all. Reading that as "not a type position" sends the request on to the
    /// scope query, which offers the locals and fields that cannot be written there: accepting one
    /// produces <c>var local: count = inner;</c> and <c>PL0025</c>, in the position where the author
    /// most obviously wants a list of types.
    /// </para>
    /// <para>
    /// The gap is bounded by two things the tree does know, and neither is a guess about the grammar:
    /// the name the declaration already carries, and the far end of whatever ended up in the type
    /// slot. Everything between them is the <c>:</c> and the type, so a caret there is in the type and
    /// a caret before the name or past the type is not. The slot's own node is returned rather than a
    /// synthetic one, so the range that gets replaced is the parser's -- an empty range at the
    /// insertion point when nothing was written, which is exactly where the text belongs.
    /// </para>
    /// </remarks>
    private static TypeReference? TypeSlotAt(SyntaxLocation at, int offset)
    {
        if (at.Enclosing<ParameterDeclaration>() is { } parameter)
        {
            return Slot(parameter.Name, parameter.Type, offset);
        }

        return at.Enclosing<VariableDeclarationStatement>() is { } local
            ? Slot(local.Name, local.DeclaredType, offset)
            : null;
    }

    /// <inheritdoc cref="TypeSlotAt"/>
    private static TypeReference? Slot(SyntaxName name, TypeReference? declared, int offset)
        => declared is not null
            && offset > name.Span.End.Offset
            && offset <= declared.Span.End.Offset
                ? declared
                : null;

    /// <summary>The ways one schema type may be written here: qualified always, simple when it is unambiguous.</summary>
    private static IEnumerable<CompletionItem> Spellings(
        SchemaTypeName type, CompilationResult result, SchemaSubject subject, OpenDocument document)
    {
        var kind = type.IsMessage ? CompletionItemKind.Class : CompletionItemKind.Enum;
        var documentation = Documentation(result, type);

        if (!result.Types.IsAmbiguousAsATypeName(type.SimpleName))
        {
            yield return Member(
                type.SimpleName, kind, type.FullName, documentation, "1", subject, document);
        }

        yield return Member(type.FullName, kind, type.FullName, documentation, "2", subject, document)
            with
            {
                // So that typing the simple name still surfaces the qualified forms, which is the
                // whole of what an author can write when the simple one is ambiguous.
                FilterText = type.SimpleName,
            };
    }

    private static string? Documentation(CompilationResult result, SchemaTypeName type)
        => type switch
        {
            SchemaMessageName message => result.Schema?.DeclarationOf(message.Descriptor)?.Documentation.Leading,
            SchemaEnumName enumeration => result.Schema?.DeclarationOf(enumeration.Descriptor)?.Documentation.Leading,
            _ => null,
        };

    /// <summary>What a bare identifier could name where the caret is.</summary>
    /// <remarks>
    /// <para>
    /// The names come from <c>ScopeAt</c> rather than from a walk of the tree, which is what makes
    /// this correct rather than approximately correct: that query already drops a field shadowed by a
    /// local of the same name, and a map field, for the same reasons the binder would refuse them.
    /// Restating either rule here would be a second copy that agrees until it does not.
    /// </para>
    /// <para>
    /// Methods are offered too, and as calls. A bare name resolves against the implicit receiver, so
    /// <c>helper()</c> binds exactly as <c>this.helper()</c> would -- while a bare <c>helper</c> with
    /// no parentheses is <c>PL0037</c>, an unknown name, because <c>BindName</c> never looks at
    /// methods. Offering the name alone would be offering something that cannot bind.
    /// </para>
    /// <para>
    /// A null scope is not a bare-identifier position at all: outside a method body, or inside a type
    /// reference, which the query declines on purpose. Both offer nothing here.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<CompletionItem> InScope(
        SemanticModel model,
        CompilationResult result,
        SchemaSubject subject,
        bool presence,
        bool writingAReceiver,
        OpenDocument document)
    {
        if (model.ScopeAt(subject.Start) is not { } scope)
        {
            return [];
        }

        // The operand of 'has' is a field or it is PL0080, so everything else this would otherwise
        // offer here is a name that resolves and then refuses: a local and a parameter because they
        // always hold a value, a call because its result does, and a keyword because 'has true' names
        // nothing at all. A field of the implicit receiver is the whole of what is left.
        if (presence)
        {
            return
            [
                .. scope.Names
                    .Where(visible => visible.Symbol.Kind is SymbolKind.Field)
                    .Select(visible => Member(
                        visible.Name,
                        CompletionItemKind.Field,
                        visible.Type.DisplayName,
                        null,
                        rank: "0",
                        subject,
                        document)),
            ];
        }

        // What the caret's name is being used for decides what may replace it. A name being called
        // can only be a method; a name something takes a member off can only be something with
        // members. Ignoring either offers a candidate that is perfectly good on its own and does not
        // bind where it lands -- 'quantity' over the name in 'case_count(3)', or an int64 local in
        // front of '.'.
        var methods = result.Module is { } module
            ? module.MethodsOn(scope.Receiver.Descriptor.FullName)
            : [];

        if (subject.FollowedByCall)
        {
            return
            [
                .. methods.Select(method => Member(
                    // Without parentheses: there are already some, and adding a second pair is the
                    // same class of mistake as offering the name of a method that must be called.
                    method.Name,
                    CompletionItemKind.Method,
                    method.Signature.DisplayName,
                    null,
                    "0",
                    subject,
                    document)),
            ];
        }

        return
        [
            .. scope.Names
                .Where(visible => !writingAReceiver || visible.Type is MessageType)
                .Select(visible => Member(
                    visible.Name,
                    visible.Symbol.Kind is SymbolKind.Field
                        ? CompletionItemKind.Field
                        : CompletionItemKind.Variable,
                    visible.Type.DisplayName,
                    null,

                    // A name the author introduced ahead of one the schema did, because a local
                    // written three lines up is more likely to be what is being typed than a field of
                    // the receiver, and the two are otherwise indistinguishable in a list.
                    rank: visible.Symbol.Kind is SymbolKind.Field ? "1" : "0",
                    subject,
                    document)),

            // No method is offered where a receiver goes, and that is a floor rather than the rule:
            // 'identity(other).count' compiles, so a method returning a message could stand here. The
            // one that cannot is a method returning a scalar, and separating them is a question about
            // a return type that nothing asks yet. Until something does, none is offered -- which
            // withholds a name that would have bound and never offers one that would not.
            .. writingAReceiver
                ? []
                : methods.Select(method => Member(
                    method.Name + "()",
                    CompletionItemKind.Method,
                    method.Signature.DisplayName,
                    null,
                    rank: "2",
                    subject,
                    document)),

            .. writingAReceiver ? [] : Keywords(model, subject, document),
        ];
    }

    /// <summary>The keywords that could legally begin what is being written here.</summary>
    /// <remarks>
    /// <para>
    /// Two sets, from the grammar in spec 7.1, and <b>which of them applies depends on where the
    /// caret is</b>. A keyword that starts a statement can only go where a statement can start:
    /// half-way through <c>return lo|cal</c> the caret is inside an expression, and <c>return
    /// return;</c> is not something an author can accept. Expression starters go in either place,
    /// since a statement may be an expression.
    /// </para>
    /// <para>
    /// <c>break</c> and <c>continue</c> are narrower again -- statements, and only inside a loop.
    /// Offering them elsewhere offers something the parser takes and the binder then refuses.
    /// </para>
    /// <para>
    /// Written out here rather than derived, because the parser publishes no list of what may start a
    /// statement and inventing one there would be a second grammar. What holds it honest instead is
    /// the sweep: every keyword offered is applied and recompiled like any other item.
    /// </para>
    /// </remarks>
    private static IEnumerable<CompletionItem> Keywords(
        SemanticModel model, SchemaSubject subject, OpenDocument document)
    {
        var at = model.SyntaxAt(subject.Start);

        // Between statements, or on the first word of one. A block as the innermost statement means
        // the caret is inside the block and inside none of its children; and a word beginning exactly
        // where the enclosing statement begins is that statement's own first token, which is a
        // statement-start position however completely the rest of it has parsed. Without the second
        // half, a caret one character into a written 'var' is judged to be inside an expression, and
        // an operator keyword offered there replaces 'var' and swallows the name after it.
        var starting = at?.Enclosing<Statement>() is not { } statement
            || statement is BlockStatement
            || statement.Span.Start.Offset == subject.Start;

        var inLoop = at is not null
            && at.Ancestors.Any(node => node is ForInStatement or WhileStatement);

        // Exclusive rather than nested. Where a statement begins, the caret is in front of whatever
        // is already written, and an operator keyword accepted there swallows the next name as its
        // operand: 'has' inserted before 'local = ...' asks for the presence of a local, which is
        // PL0041. Where an expression is being written the caret is on a name and replaces it, so the
        // operators are the ones that fit and a statement keyword is the one that cannot.
        var keywords = starting
            ? inLoop ? StatementStarters.Concat(LoopOnly) : StatementStarters
            : ExpressionStarters;

        return keywords.Select(keyword => Member(
            keyword, CompletionItemKind.Keyword, "keyword", null, rank: "8", subject, document));
    }

    private static readonly IReadOnlyList<string> StatementStarters =
        ["var", "return", "if", "while", "for"];

    private static readonly IReadOnlyList<string> LoopOnly = ["break", "continue"];

    private static readonly IReadOnlyList<string> ExpressionStarters = ["has", "not", "true", "false"];

    /// <summary>The type whose members may be written after the caret's dot, or null when there is none.</summary>
    /// <remarks>
    /// <para>
    /// Four shapes, and the first is the one that matters. <see cref="IrMissingMemberAccess"/> is what
    /// the binder leaves where a member name has not been written yet -- <c>line.</c> with the caret
    /// after the dot, which is the state completion is triggered in -- and it exists precisely so the
    /// receiver's type survives a binding that otherwise failed. Everything else about that
    /// expression is an error; the one thing that is not is the answer.
    /// </para>
    /// <para>
    /// The others are accesses that did resolve, so that invoking completion on a name already
    /// written offers its siblings rather than nothing. A field access, a presence test and a method
    /// call each keep the receiver they resolved against; an enum constant keeps no receiver because
    /// it never had one, and its own type is what the name before the dot named. A presence test is
    /// in that list because <c>has inner.stamp</c> is a member access like any other and reads
    /// nothing like one in the IR: it binds to its own node, so a list of the node kinds that carry a
    /// receiver is a list that can be, and was, incomplete.
    /// </para>
    /// <para>
    /// <b>An enum-typed receiver is the one case where knowing the type is not enough.</b> Constants
    /// are reached through the enum's <em>name</em> and never through a value of it, so
    /// <c>status.TOP_LEVEL_STATUS_OK</c> is <c>PL0039</c> however certainly <c>status</c> is a
    /// <c>TopLevelStatus</c>. What tells the two apart is the receiver the binder built:
    /// <c>Binder.BindReceiverAwaitingAMember</c> puts a valueless literal of the enum's type where a
    /// type name was written, and every actual value of an enum -- a field, a local, a parameter, a
    /// call -- arrives as the node that produced it. So the literal is the type name and everything
    /// else is a value, which is the same order of precedence <c>TryResolveEnumReceiver</c> applies
    /// on the way in.
    /// </para>
    /// <para>
    /// <b>A name written but unresolved is the gap, and it is deliberate for now.</b> The binder
    /// collapses <c>line.nosuch</c> to an error literal and the receiver goes with it, so an explicit
    /// invocation part-way through typing a member name that names nothing offers nothing. It is not
    /// the path the client ordinarily takes -- the list is requested when the dot is typed, when the
    /// name is genuinely missing, and is declared complete so the client filters the rest locally --
    /// and closing it means having the binder keep the receiver there as it already does one branch
    /// above, which is a change to what the IR preserves rather than to this file.
    /// </para>
    /// </remarks>
    private static PlType? ReceiverAt(SemanticModel model, SchemaSubject subject)
    {
        if (model.IrAt(subject.Start) is not { } at)
        {
            return null;
        }

        if (at.Enclosing<IrEnumValue>() is { } constant)
        {
            return constant.EnumType;
        }

        var receiver = at.Enclosing<IrMissingMemberAccess>()?.Receiver
            ?? at.Enclosing<IrFieldAccess>()?.Receiver
            ?? at.Enclosing<IrFieldPresence>()?.Receiver
            ?? at.Enclosing<IrMethodCall>()?.Receiver;

        return receiver switch
        {
            { Type: MessageType } => receiver.Type,
            IrLiteral { Value: null, Type: EnumPlType } => receiver.Type,
            _ => null,
        };
    }

    /// <summary>What may be written after a dot on a value of this type.</summary>
    /// <remarks>
    /// <para>
    /// The rules are the binder's and are borrowed rather than restated. A map field is excluded on
    /// the descriptor, because reading one is <c>PL0038</c> and a map never becomes a
    /// <see cref="PlType"/> at all -- the same exclusion, for the same reason, that
    /// <c>ScopeSearch.ReachableFields</c> makes. A method is offered because it is reachable through a
    /// call, and it is offered with its parentheses because naming one without calling it is
    /// <c>PL0040</c>.
    /// </para>
    /// <para>
    /// A repeated field offers nothing, which is the whole answer rather than an omission: there is no
    /// member access into a repetition, only <c>for x in ...</c>, so offering the element type's
    /// members would offer names that cannot be written where the caret is. An unknown receiver
    /// offers nothing for the same reason -- returning a wrong list is worse than returning none.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<CompletionItem> Members(
        PlType? receiver,
        CompilationResult result,
        SchemaSubject subject,
        bool presence,
        bool writingAReceiver,
        OpenDocument document)
        => receiver switch
        {
            MessageType message =>
            [
                // What the caret's name is used for constrains this exactly as it constrains a bare
                // name. A member something else takes a member off is itself a receiver, so only a
                // singular message field can go there -- 'inner.weight.seconds' asks an int64 for a
                // member it cannot have. And parentheses already written mean a call, which a field
                // can never be: 'other.count()' is PL0044, an unknown method, rather than a field
                // read with punctuation after it.
                .. message.Descriptor.Fields.InDeclarationOrder()
                    .Where(field => !field.IsMap
                        && !subject.FollowedByCall
                        && (!writingAReceiver || TypeFactory.FromField(field) is MessageType))
                    .Select(field => Member(
                        field.Name,
                        CompletionItemKind.Field,
                        TypeFactory.FromField(field).DisplayName,
                        Documentation(result, field),
                        rank: "0",
                        subject,
                        document)),

                // The same floor as for a bare name, one level in, and for the same reason. A method
                // is not the operand of 'has' under any circumstances though: that needs a field, so
                // 'has other.helper()' is PL0080, and a method result is the example its help gives
                // of something that always holds a value and has nothing to be asked about.
                .. result.Module is { } module && !writingAReceiver && !presence
                    ? module.MethodsOn(message.Descriptor.FullName).Select(method => Member(
                        subject.FollowedByCall ? method.Name : method.Name + "()",
                        CompletionItemKind.Method,
                        method.Signature.DisplayName,
                        null,
                        rank: "1",
                        subject,
                        document))
                    : [],
            ],

            // A constant has no members of its own and is not callable, so where a receiver or a call
            // goes there is nothing here to name. That the enum's name rather than a value of it was
            // written before the dot is settled by ReceiverAt, which is where the reason is.
            EnumPlType enumeration when !writingAReceiver && !subject.FollowedByCall =>
            [
                .. enumeration.Descriptor.Values.Select(value => Member(
                    value.Name,
                    CompletionItemKind.EnumMember,
                    enumeration.Descriptor.FullName,
                    Documentation(result, value),
                    rank: "0",
                    subject,
                    document)),
            ],

            _ => [],
        };

    /// <summary>The leading comment written about a schema declaration, where there is one.</summary>
    private static string? Documentation(CompilationResult result, FieldDescriptor field)
        => result.Schema?.DeclarationOf(field)?.Documentation.Leading;

    private static string? Documentation(CompilationResult result, EnumValueDescriptor value)
        => result.Schema?.DeclarationOf(value)?.Documentation.Leading;

    private static CompletionItem Member(
        string label,
        CompletionItemKind kind,
        string detail,
        string? documentation,
        string rank,
        SchemaSubject subject,
        OpenDocument document)
        => new()
        {
            Label = label,
            Kind = kind,
            Detail = detail,
            Documentation = documentation,
            FilterText = label,

            // A rank ahead of the name, so the kinds stay grouped however the client sorts within
            // them. Ordinal on the name inside a rank, which is the order the schema declared them.
            SortText = rank + label,
            InsertTextFormat = InsertTextFormat.PlainText,

            // The whole identifier under the caret, not the part typed before the list was built. The
            // list is declared complete, so the client re-applies an item it already holds after the
            // user types more -- and a range covering only the old prefix would leave the rest behind.
            TextEdit = new TextEdit(
                new Range(At(document, subject.Start), At(document, subject.End)), label),
        };

    private static CompletionItem Item(SchemaCandidate candidate, ImportPathContext context, OpenDocument document)
    {
        // Through PathIdentity rather than ordinally, because "is this the same path?" has one home
        // and the answer differs by platform. On a volume that folds case, an import already written
        // as 'Billing/Invoice.proto' resolves to the file offered here as 'billing/invoice.proto',
        // and an ordinal comparison would offer it again unmarked -- which is the one thing this line
        // exists to prevent.
        var imported = !candidate.IsDirectory
            && context.Imported.Contains(candidate.Path, PathIdentity.Comparer);

        return new CompletionItem
        {
            Label = Segment(candidate.Path),
            Kind = candidate.IsDirectory ? CompletionItemKind.Folder : CompletionItemKind.File,
            Detail = imported ? $"already imported, from {candidate.Root}" : candidate.Root,
            Documentation = Shadowing(candidate),

            // The whole path, not the segment the label shows. By the time a second segment is being
            // typed the user has a separator on screen, and a client filtering a multi-segment word
            // against a one-segment string discards every item it was about to show.
            FilterText = candidate.Path,

            // Directories ahead of schemas, because a directory is a step towards an answer and a
            // schema is one. Within each, the path's own order.
            SortText = (candidate.IsDirectory ? "0" : "1") + candidate.Path,
            InsertTextFormat = InsertTextFormat.PlainText,
            TextEdit = new TextEdit(Replacing(context, document), candidate.Path),
        };
    }

    /// <summary>The last segment of a path, keeping the separator that says it is a directory.</summary>
    private static string Segment(string path)
        => path[(path.TrimEnd('/').LastIndexOf('/') + 1)..];

    /// <summary>What else holds this path, for a user who cannot otherwise tell.</summary>
    private static string? Shadowing(SchemaCandidate candidate)
        => candidate.ShadowedRoots.Count == 0
            ? null
            : $"Also in {string.Join(", ", candidate.ShadowedRoots)}. This one wins, because include "
                + "paths are searched in order and the first match is taken.";

    /// <summary>
    /// The whole path between the quotes, whatever part of it the cursor is in.
    /// </summary>
    /// <remarks>
    /// Not the segment being typed, and not the text before the cursor. A candidate is a complete
    /// path, so applying one has to replace a complete path -- otherwise picking
    /// <c>billing/invoice.proto</c> while the caret sits inside <c>billing/inv|oice.proto</c> leaves
    /// the tail of what was already there.
    /// </remarks>
    private static Range Replacing(ImportPathContext context, OpenDocument document)
        => EditorPositions.Between(document.Lines, context.Start, context.End);

    private static Position At(OpenDocument document, int offset)
        => EditorPositions.PositionAt(document.Lines, offset);
}
