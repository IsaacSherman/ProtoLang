using System.Text;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using ProtoLang.Binding;
using ProtoLang.Diagnostics;
using ProtoLang.Symbols;
using Xunit;

namespace ProtoLang.Tests;

/// <summary>
/// Whether the compiler can say where a schema element was written, and what was written about it.
/// </summary>
/// <remarks>
/// <para>
/// The whole of this rests on one correspondence: a <c>SourceCodeInfo</c> location is addressed by a
/// path of field numbers into the <c>FileDescriptorProto</c> tree, and something has to turn a
/// descriptor into that path. Getting it wrong is silent -- an off-by-one anywhere in the walk
/// reports a neighbouring declaration's range rather than none -- so the test that matters most here
/// is the sweep: every element of a schema, checked against the text it was read from.
/// </para>
/// <para>
/// Schemas are written per test rather than shared, because the shapes being exercised -- a comment
/// above a field of a nested message, a tab-indented declaration, a line with non-ASCII text before
/// the declaration on it -- are not shapes the repository's fixtures should have to carry.
/// </para>
/// </remarks>
public class SchemaDeclarationTests
{
    private const string DocumentedSchema =
        """
        syntax = "proto3";

        package declarations.tests;

        // A paragraph about the section, detached from what follows.

        // The customer a bill is addressed to.
        //
        // Identified by email, because the ledger predates account ids.
        message Customer {
          // Where receipts are sent.
          string email = 1;

          int64 id = 2;  // Assigned by the ledger, not by us.

          /* A block comment attached
           * to the nested message.
           */
          message Address {
            // The line a courier reads first.
            string street = 1;

            enum Kind {
              KIND_UNKNOWN = 0;

              // Somewhere a person sleeps.
              KIND_RESIDENTIAL = 1;
            }

            Kind kind = 2;
          }

          Address address = 3;
        }

        enum Standing {
          STANDING_UNKNOWN = 0;
          STANDING_GOOD = 1;
        }
        """;

    /// <summary>Indented with tabs, which protoc counts as advancing to the next multiple of eight.</summary>
    /// <remarks>
    /// Written with escapes rather than as a raw literal, so that what is being tested is visible in
    /// the test and cannot be undone by an editor that converts tabs to spaces on save.
    /// </remarks>
    private const string TabIndentedSchema =
        "syntax = \"proto3\";\n\nmessage Tabbed {\n\tint64 count = 1;\n}\n";

    /// <summary>A declaration behind non-ASCII text, which protoc counts in bytes.</summary>
    /// <inheritdoc cref="TabIndentedSchema" path="/remarks"/>
    private const string NonAsciiSchema =
        "syntax = \"proto3\";\n\nmessage Wide {\n  /* caff\u00e8 */ int64 count = 1;\n}\n";

    private const string MappedSchema =
        """
        syntax = "proto3";

        package declarations.tests;

        message Ledger {
          map<string, int64> totals = 1;
        }
        """;

    // ------------------------------------------------------------------ the sweep

    /// <summary>
    /// The one that catches a path built wrong. Every message, enum, field and enum value in the
    /// schema, checked against the text protoc read: a walk that miscounts anywhere reports a range
    /// covering something other than the name it claims to cover.
    /// </summary>
    [Fact]
    public void EveryDeclarationsNameSpanCoversExactlyItsName()
    {
        var schema = Load(DocumentedSchema);

        foreach (var (name, declaration) in EverythingIn(schema))
        {
            Assert.True(declaration is not null, $"'{name}' is declared in the schema but not in the bundle");
            Assert.True(declaration!.Site is not null, $"'{name}' has no site, so nothing can navigate to it");

            var site = declaration.Site!;

            Assert.Equal(name, Slice(schema.Text, site.Name));
            Assert.True(
                site.Extent.Start.Offset <= site.Name.Start.Offset
                    && site.Extent.End.Offset >= site.Name.End.Offset,
                $"'{name}' selects a range that is not inside the range it selects from");
        }
    }

    [Fact]
    public void ADeclarationsExtentCoversTheWholeConstruct()
    {
        var schema = Load(DocumentedSchema);

        var address = schema.Bundle.DeclarationOf(Nested(Message(schema, "Customer"), "Address"));

        var extent = Slice(schema.Text, address!.Site!.Extent);
        Assert.StartsWith("message Address {", extent, StringComparison.Ordinal);
        Assert.EndsWith("}", extent, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ nesting

    [Fact]
    public void AFieldOfAMessageInsideAMessageResolves()
    {
        var schema = Load(DocumentedSchema);

        var field = Field(Nested(Message(schema, "Customer"), "Address"), "street");
        var street = schema.Bundle.DeclarationOf(field);

        Assert.Equal("street", Slice(schema.Text, street!.Site!.Name));
        Assert.Equal("The line a courier reads first.", street.Documentation.Leading);

        // The identity the IR already carries for this field, so a caller holding one can ask either
        // question of it without a translation step.
        Assert.Equal(SymbolId.ForField(field), street.Id);
    }

    [Fact]
    public void AnEnumValueOfAnEnumInsideAMessageResolves()
    {
        var schema = Load(DocumentedSchema);

        var kind = Nested(Message(schema, "Customer"), "Address").EnumTypes.Single(type => type.Name == "Kind");
        var residential = schema.Bundle.DeclarationOf(kind.Values.Single(value => value.Name == "KIND_RESIDENTIAL"));

        Assert.Equal("KIND_RESIDENTIAL", Slice(schema.Text, residential!.Site!.Name));
        Assert.Equal("Somewhere a person sleeps.", residential.Documentation.Leading);
    }

    /// <summary>
    /// The same question of a schema the repository really compiles, reached the way a compilation
    /// reaches it: three levels of nesting, resolved through an include path rather than a temporary
    /// directory this test wrote a moment ago.
    /// </summary>
    [Fact]
    public void ADeclarationInARepositorySchemaResolvesThroughTheIncludePaths()
    {
        var directory = TestPaths.FixtureProtoDirectory;
        var bundle = Loader().LoadBundle(["fixtures.proto"], [directory]);
        var text = File.ReadAllText(Path.Combine(directory, "fixtures.proto"));

        var deep = Nested(Message(FileIn(bundle, "fixtures.proto"), "Outer"), "Inner")
            .EnumTypes.Single(type => type.Name == "Deep");
        var none = bundle.DeclarationOf(deep.Values.Single(value => value.Name == "DEEP_NONE"));

        Assert.Equal("DEEP_NONE", Slice(text, none!.Site!.Name));
        Assert.True(
            PathIdentity.AreSame(Path.Combine(directory, "fixtures.proto"), none.Site.Path),
            $"'{none.Site.Path}' is not the file the compilation read");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExtensionFieldsHaveDeclarationSitesAndDocumentation(bool nested)
    {
        const string Extension = "// Extension documentation.\noptional string annotation = 100;";
        var block = "extend Customer {\n" + Extension + "\n}";
        var schema = Load("syntax = \"proto2\";\nmessage Customer { extensions 100 to max; }\n"
            + (nested ? "message Metadata {\n" + block + "\n}" : block));
        var file = FileIn(schema.Bundle, SchemaName);
        var extensions = nested ? Message(schema, "Metadata").Extensions : file.Extensions;
        var field = Assert.Single(extensions.UnorderedExtensions);

        Assert.True(field.IsExtension);
        var declaration = schema.Bundle.DeclarationOf(field);

        Assert.NotNull(declaration);
        Assert.Equal(SymbolId.ForField(field), declaration.Id);
        Assert.Equal("Extension documentation.", declaration.Documentation.Leading);
        Assert.NotNull(declaration.Site);
        Assert.Equal("annotation", Slice(schema.Text, declaration.Site.Name));
        Assert.Equal("optional string annotation = 100;", Slice(schema.Text, declaration.Site.Extent));
    }

    [Fact]
    public void AProto2GroupNamesItsWrittenDeclarationForBothDescriptors()
    {
        var schema = Load("syntax = \"proto2\";\nmessage Customer {\n"
            + "// Group documentation.\noptional group Details = 1 { optional string email = 2; }\n}");
        var customer = Message(schema, "Customer");
        var field = Field(customer, "details");
        var group = Nested(customer, "Details");

        foreach (var declaration in new[] { schema.Bundle.DeclarationOf(field), schema.Bundle.DeclarationOf(group) })
        {
            Assert.NotNull(declaration);
            Assert.NotNull(declaration.Site);
            Assert.Equal("Details", Slice(schema.Text, declaration.Site.Name));
        }
    }

    // ------------------------------------------------------------------ comments

    [Fact]
    public void ALeadingCommentIsRecoveredWithItsMarkersStripped()
    {
        var schema = Load(DocumentedSchema);

        var email = schema.Bundle.DeclarationOf(Field(Message(schema, "Customer"), "email"));

        Assert.Equal("Where receipts are sent.", email!.Documentation.Leading);
    }

    [Fact]
    public void ABlankLineInsideACommentSurvivesAsAParagraphBreak()
    {
        var schema = Load(DocumentedSchema);

        var customer = schema.Bundle.DeclarationOf(Message(schema, "Customer"));

        Assert.Equal(
            "The customer a bill is addressed to.\n\nIdentified by email, because the ledger predates account ids.",
            customer!.Documentation.Leading);
    }

    [Fact]
    public void ABlockCommentLosesItsAsterisks()
    {
        var schema = Load(DocumentedSchema);

        var address = schema.Bundle.DeclarationOf(Nested(Message(schema, "Customer"), "Address"));

        Assert.Equal("A block comment attached\nto the nested message.", address!.Documentation.Leading);
    }

    [Fact]
    public void ATrailingCommentBelongsToTheDeclarationItFollows()
    {
        var schema = Load(DocumentedSchema);

        var id = schema.Bundle.DeclarationOf(Field(Message(schema, "Customer"), "id"));

        Assert.Equal("Assigned by the ledger, not by us.", id!.Documentation.Trailing);
        Assert.Null(id.Documentation.Leading);
    }

    [Fact]
    public void ADetachedParagraphIsNotPartOfTheLeadingComment()
    {
        var schema = Load(DocumentedSchema);

        var customer = schema.Bundle.DeclarationOf(Message(schema, "Customer"));

        Assert.Equal(
            ["A paragraph about the section, detached from what follows."],
            customer!.Documentation.Detached);
        Assert.DoesNotContain("detached from what follows", customer.Documentation.Leading);
    }

    [Fact]
    public void ReturnedDetachedCommentsCannotChangeLaterDeclarationQueries()
    {
        var schema = Load(DocumentedSchema);
        var message = Message(schema, "Customer");
        var customer = schema.Bundle.DeclarationOf(message);
        const string Expected = "A paragraph about the section, detached from what follows.";

        Assert.NotNull(customer);
        Assert.Equal(Expected, Assert.Single(customer.Documentation.Detached));

        // A read-only interface can still expose a mutable list or array. A read-only wrapper
        // may offer IList but reject writes, which also protects the shared declaration.
        if (customer.Documentation.Detached is IList<string> writable)
        {
            try
            {
                writable[0] = "Replaced by a consumer";
            }
            catch (NotSupportedException)
            {
            }
        }

        var queriedAgain = schema.Bundle.DeclarationOf(message);
        Assert.NotNull(queriedAgain);
        Assert.Equal(Expected, Assert.Single(queriedAgain.Documentation.Detached));
    }

    [Fact]
    public void ASchemaWithNoCommentsAnswersEmptyRatherThanNull()
    {
        var schema = Load(DocumentedSchema);

        var standing = schema.Bundle.DeclarationOf(
            FileIn(schema.Bundle, SchemaName).EnumTypes.Single(type => type.Name == "Standing"));

        Assert.True(standing!.Documentation.IsEmpty, "nothing was written about Standing");
        Assert.NotNull(standing.Site);
    }

    // ------------------------------------------------------------------ columns

    /// <summary>
    /// protoc advances its column to the next multiple of eight for a tab, so a name one tab in is
    /// reported eight columns along. Taken literally, the selection lands past the declaration.
    /// </summary>
    [Fact]
    public void ATabIndentedDeclarationIsNotReportedEightColumnsToTheRight()
    {
        var schema = Load(TabIndentedSchema);

        var count = schema.Bundle.DeclarationOf(Field(Message(schema, "Tabbed"), "count"));

        Assert.Equal("count", Slice(schema.Text, count!.Site!.Name));
    }

    /// <summary>
    /// And it counts bytes rather than characters, so anything non-ASCII earlier on the line shifts
    /// everything after it -- by one column per extra byte.
    /// </summary>
    [Fact]
    public void ANonAsciiCommentDoesNotShiftTheDeclarationAfterIt()
    {
        var schema = Load(NonAsciiSchema);

        var count = schema.Bundle.DeclarationOf(Field(Message(schema, "Wide"), "count"));

        Assert.Equal("count", Slice(schema.Text, count!.Site!.Name));
    }

    [Fact]
    public void AUtf8BomDoesNotShiftDeclarationsOnTheFirstLine()
    {
        var schema = Load(
            "syntax = \"proto3\"; message Customer { string email = 1; }\n",
            utf8Bom: true);
        var path = schema.Bundle.PathFor(SchemaName);
        Assert.NotNull(path);
        Assert.Equal(new byte[] { 0xef, 0xbb, 0xbf }, File.ReadAllBytes(path)[..3]);

        foreach (var (name, declaration) in EverythingIn(schema))
        {
            Assert.NotNull(declaration);
            Assert.NotNull(declaration.Site);
            var span = declaration.Site.Name;

            Assert.Equal(name, Slice(schema.Text, span));
            Assert.Equal(1, span.Start.Line);
            Assert.Equal(schema.Text.IndexOf(name, StringComparison.Ordinal) + 1, span.Start.Column);
        }
    }

    [Theory]
    [InlineData("syntax = \"proto3\";\tmessage Customer { string email = 1; }\n")]
    [InlineData("\tsyntax = \"proto3\"; message Customer { string email = 1; }\n")]
    [InlineData("syntax = \"proto3\";\n\tmessage Customer { string email = 1; }\n")]
    public void AUtf8BomPreservesTabStopsWhenLocatingDeclarations(string source)
    {
        var schema = Load(source, utf8Bom: true);
        var lines = new LineMap(schema.Text);

        foreach (var (name, declaration) in EverythingIn(schema))
        {
            Assert.NotNull(declaration);
            Assert.NotNull(declaration.Site);
            var span = declaration.Site.Name;
            var offset = schema.Text.IndexOf(name, StringComparison.Ordinal);

            Assert.Equal(name, Slice(schema.Text, span));
            Assert.Equal(lines.PositionOf(offset), span.Start);
            Assert.Equal(lines.PositionOf(offset + name.Length), span.End);
        }
    }

    [Theory]
    [InlineData(0xE8)]
    [InlineData(0xFF)]
    public void InvalidUtf8InACommentCannotProduceAMisalignedSite(byte invalidByte)
    {
        var directory = TestPaths.CreateTempDirectory();
        var path = Path.Combine(directory, SchemaName);
        byte[] bytes = [
            .. Encoding.UTF8.GetBytes("syntax = \"proto3\";\nmessage /* caf"),
            invalidByte,
            .. Encoding.UTF8.GetBytes(" */ Customer { string email = 1; }\n")
        ];
        File.WriteAllBytes(path, bytes);
        var bundle = Loader().LoadBundle([SchemaName], [directory]);
        var message = Message(FileIn(bundle, SchemaName), "Customer");
        var declaration = bundle.DeclarationOf(message);

        Assert.NotNull(declaration);
        // Either decline undecodable text or preserve its byte-to-character mapping. Re-encoding
        // a replacement character as three bytes must not shift a one-byte input error's columns.
        if (declaration.Site is { } site)
        {
            Assert.Equal("Customer", Slice(File.ReadAllText(path), site.Name));
        }
    }

    // ------------------------------------------------------------------ what is missing

    [Theory]
    [InlineData("/* inserted */ message Customer { string email = 1; }")]
    [InlineData("// short")]
    public void ASchemaEditedBeforeItsFirstDeclarationQueryHasNoSite(string replacement)
    {
        const string Declaration = "message Customer { string email = 1; }";
        const string Source = "syntax = \"proto3\";\n// The original customer documentation.\n" + Declaration + "\n";
        var schema = Load(Source);
        var message = Message(schema, "Customer");
        var path = schema.Bundle.PathFor(SchemaName);
        Assert.NotNull(path);

        // Leave the index unqueried until after the edit. Its descriptor locations still refer
        // to Source, even when all their line numbers also exist in the replacement text.
        File.WriteAllText(path, Source.Replace(Declaration, replacement, StringComparison.Ordinal));

        var customer = schema.Bundle.DeclarationOf(message);
        Assert.NotNull(customer);
        Assert.Equal("The original customer documentation.", customer.Documentation.Leading);
        Assert.Null(customer.Site);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void APreviouslyLocatedSchemaLosesItsSiteWhenItsFileChanges(bool deleteFile)
    {
        const string Source = "syntax = \"proto3\";\n// Customer documentation.\nmessage Customer { string email = 1; }\n";
        var schema = Load(Source);
        var message = Message(schema, "Customer");
        var path = schema.Bundle.PathFor(SchemaName);
        Assert.NotNull(path);

        var original = schema.Bundle.DeclarationOf(message);
        Assert.NotNull(original);
        Assert.NotNull(original.Site);
        Assert.Equal("Customer", Slice(Source, original.Site.Name));

        // Warm the per-file index before changing the file, unlike the first-query regression.
        if (deleteFile)
        {
            File.Delete(path);
        }
        else
        {
            File.WriteAllText(path, Source.Replace("message Customer", "/* edited */ message Customer", StringComparison.Ordinal));
        }

        var unavailable = schema.Bundle.DeclarationOf(message);
        Assert.NotNull(unavailable);
        Assert.Equal("Customer documentation.", unavailable.Documentation.Leading);
        Assert.Null(unavailable.Site);

        File.WriteAllText(path, Source);
        var restored = schema.Bundle.DeclarationOf(message);
        Assert.NotNull(restored);
        Assert.NotNull(restored.Site);
        Assert.Equal("Customer", Slice(Source, restored.Site.Name));
    }

    /// <summary>
    /// A bundle whose source info was never asked for. The file is still known, so the answer is a
    /// declaration with nothing in it rather than no declaration at all -- the two mean different
    /// things to a caller deciding whether to say "no definition found".
    /// </summary>
    [Fact]
    public void ADescriptorSetWithNoSourceInfoAnswersWithoutASite()
    {
        var schema = Load(DocumentedSchema);

        var set = schema.Bundle.CloneSet();
        foreach (var file in set.File)
        {
            file.SourceCodeInfo = null;
        }

        var stripped = new DescriptorBundle(
            FileDescriptor.BuildFromByteStrings(set.File.Select(file => file.ToByteString()).ToList()),
            set,
            schema.Bundle.Closure);

        var customer = stripped.DeclarationOf(
            FileIn(stripped, SchemaName).MessageTypes.Single(message => message.Name == "Customer"));

        Assert.NotNull(customer);
        Assert.Null(customer.Site);
        Assert.True(customer.Documentation.IsEmpty, "there is no source info to have read a comment from");
    }

    [Theory]
    [InlineData(1000, 1008)]
    [InlineData(0, 1008)]
    public void ASourceInfoExtentBeyondTheLineHasNoSite(int startColumn, int endColumn)
    {
        var schema = Load("syntax = \"proto3\";\n// Customer documentation.\nmessage Customer {}\n");
        var set = schema.Bundle.CloneSet();
        var proto = Assert.Single(set.File);
        // External descriptor sets can carry invalid coordinates even when the file hash matches.
        var location = proto.SourceCodeInfo.Location.Single(item => item.Path.SequenceEqual(new[] { 4, 0 }));
        location.Span.Clear();
        location.Span.Add(new[] { 2, startColumn, endColumn });
        var bundle = new DescriptorBundle(
            FileDescriptor.BuildFromByteStrings(set.File.Select(file => file.ToByteString()).ToList()),
            set,
            schema.Bundle.Closure);

        var declaration = bundle.DeclarationOf(Message(FileIn(bundle, SchemaName), "Customer"));

        Assert.NotNull(declaration);
        Assert.Equal("Customer documentation.", declaration.Documentation.Leading);
        Assert.Null(declaration.Site);
    }

    /// <summary>
    /// Nothing on disk backs the schema, which is what a well-known type looks like under a protoc
    /// that resolves it from descriptors compiled into itself. Navigation and documentation are
    /// separate questions, and only the first of them has lost its answer.
    /// </summary>
    [Fact]
    public void ASchemaWithNoFileBehindItAnswersWithoutASiteButKeepsItsComments()
    {
        var schema = Load(DocumentedSchema);

        var detached = new DescriptorBundle(
            schema.Bundle.Descriptors,
            schema.Bundle.CloneSet(),
            [new SchemaFile(SchemaName, null, null)]);

        var email = detached.DeclarationOf(
            Field(FileIn(detached, SchemaName).MessageTypes.Single(message => message.Name == "Customer"), "email"));

        Assert.Null(email!.Site);
        Assert.Equal("Where receipts are sent.", email.Documentation.Leading);
    }

    /// <summary>
    /// No answer at all, which is the other thing a null can mean and the one a caller must be able
    /// to tell from an answer holding nothing: this bundle has never heard of the file.
    /// </summary>
    [Fact]
    public void ADescriptorFromAFileThisBundleDoesNotHoldIsNotAnswered()
    {
        var schema = Load(DocumentedSchema);
        var elsewhere = Loader().LoadBundle(["fixtures.proto"], [TestPaths.FixtureProtoDirectory]);

        var outer = Message(FileIn(elsewhere, "fixtures.proto"), "Outer");

        Assert.NotNull(elsewhere.DeclarationOf(outer));
        Assert.Null(schema.Bundle.DeclarationOf(outer));
    }

    /// <summary>
    /// A map field's entry type is generated by protoc rather than written by anyone, so it has a
    /// place in the descriptor tree and no place in the file. Asking about it is ordinary -- it is
    /// reachable from every walk of the nested types -- and must not be an error.
    /// </summary>
    [Fact]
    public void AMapFieldsSyntheticEntryTypeIsNotAnError()
    {
        var schema = Load(MappedSchema);
        var ledger = Message(schema, "Ledger");

        var totals = schema.Bundle.DeclarationOf(Field(ledger, "totals"));
        var entry = schema.Bundle.DeclarationOf(ledger.NestedTypes.Single());

        Assert.Equal("totals", Slice(schema.Text, totals!.Site!.Name));
        Assert.NotNull(entry);
        Assert.Null(entry.Site);
    }

    // ------------------------------------------------------------------ cost, and protoc's own schemas

    /// <summary>
    /// Source info is bulky and a hover is asked on a keystroke. The same answer object twice is what
    /// says the file was read and walked once, in the way <c>ProtocInvocations</c> says a load was not
    /// repeated.
    /// </summary>
    [Fact]
    public void AskingTwiceDoesNotRebuildTheAnswer()
    {
        var schema = Load(DocumentedSchema);
        var customer = Message(schema, "Customer");

        Assert.Same(schema.Bundle.DeclarationOf(customer), schema.Bundle.DeclarationOf(customer));
    }

    [Fact]
    public void UndoingASchemaEditRestoresSitesOnACachedBundle()
    {
        const string Source = "syntax = \"proto3\";\n// Customer documentation.\nmessage Customer { string email = 1; }\n";
        var directory = TestPaths.CreateTempDirectory();
        var path = Path.Combine(directory, SchemaName);
        File.WriteAllText(path, Source);

        var cache = new DescriptorCache();
        var loader = Loader(cache);
        var bundle = loader.LoadBundle([SchemaName], [directory]);
        var message = Message(FileIn(bundle, SchemaName), "Customer");

        // The first query happens while the source differs from the compiled descriptors.
        File.WriteAllText(path, Source.Replace("message Customer", "/* edited */ message Customer", StringComparison.Ordinal));
        var rejected = bundle.DeclarationOf(message);
        Assert.NotNull(rejected);
        Assert.Null(rejected.Site);
        Assert.Equal("Customer documentation.", rejected.Documentation.Leading);

        File.WriteAllText(path, Source);
        var reloaded = loader.LoadBundle([SchemaName], [directory]);

        // Undo restored the original bytes, so the descriptor cache can reuse the same bundle.
        Assert.Same(bundle, reloaded);
        Assert.Equal(1, cache.Statistics.Hits);
        Assert.Equal(1, loader.ProtocInvocations);

        var recovered = reloaded.DeclarationOf(Message(FileIn(reloaded, SchemaName), "Customer"));
        Assert.NotNull(recovered);
        Assert.NotNull(recovered.Site);
        Assert.Equal("Customer", Slice(Source, recovered.Site.Name));
        Assert.Equal("message Customer { string email = 1; }", Slice(Source, recovered.Site.Extent));
        Assert.Equal("Customer documentation.", recovered.Documentation.Leading);
    }

    /// <summary>
    /// No special case for <c>google/protobuf</c>. Where protoc supplies the schemas as files -- which
    /// the bundled one does -- they are answered like any other schema, comments included, and the
    /// path is wherever that protoc keeps them.
    /// </summary>
    [Fact]
    public void AWellKnownTypeIsAnsweredLikeAnyOtherSchema()
    {
        var loader = new DescriptorLoader(RequireBundledProtoc());
        var bundle = loader.LoadBundle(["google/protobuf/timestamp.proto"], [.. loader.ImplicitIncludePaths]);

        var declaration = bundle.DeclarationOf(
            FileIn(bundle, "google/protobuf/timestamp.proto").MessageTypes.Single(message => message.Name == "Timestamp"));

        Assert.NotNull(declaration!.Site);
        Assert.EndsWith("timestamp.proto", declaration.Site.Path, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Timestamp represents a point in time", declaration.Documentation.Leading ?? string.Empty);
    }

    // ------------------------------------------------------------------ helpers

    private const string SchemaName = "declarations.proto";

    /// <summary>A schema on disk, the bundle protoc made of it, and the text it was made from.</summary>
    private sealed record LoadedSchema(DescriptorBundle Bundle, string Text);

    private static LoadedSchema Load(string schema, bool utf8Bom = false)
    {
        var directory = TestPaths.CreateTempDirectory();
        var path = Path.Combine(directory, SchemaName);

        File.WriteAllText(path, schema, new UTF8Encoding(encoderShouldEmitUTF8Identifier: utf8Bom));

        return new LoadedSchema(Loader().LoadBundle([SchemaName], [directory]), File.ReadAllText(path));
    }

    private static DescriptorLoader Loader(DescriptorCache? cache = null)
    {
        var protoc = ProtocLocator.Locate();
        if (protoc is null)
        {
            Assert.Skip("No protoc on PATH and none in the NuGet cache. Restore the solution first.");
        }

        return new DescriptorLoader(protoc, new DescriptorLoaderOptions { Cache = cache });
    }

    private static string RequireBundledProtoc()
    {
        var protoc = ProtocLocator.FindBundledProtoc();
        if (protoc is null)
        {
            Assert.Skip("No Grpc.Tools protoc in the NuGet cache. Restore the solution first.");
        }

        return protoc;
    }

    private static string Slice(string text, SourceSpan span) => text.Substring(span.Start.Offset, span.Length);

    private static FileDescriptor FileIn(DescriptorBundle bundle, string name)
        => bundle.Descriptors.Single(file => file.Name == name);

    private static MessageDescriptor Message(LoadedSchema schema, string name)
        => Message(FileIn(schema.Bundle, SchemaName), name);

    private static MessageDescriptor Message(FileDescriptor file, string name)
        => file.MessageTypes.Single(message => message.Name == name);

    private static MessageDescriptor Nested(MessageDescriptor message, string name)
        => message.NestedTypes.Single(nested => nested.Name == name);

    private static FieldDescriptor Field(MessageDescriptor message, string name)
        => message.Fields.InDeclarationOrder().Single(field => field.Name == name);

    /// <summary>Every element the schema declares, paired with what the bundle says about it.</summary>
    private static IEnumerable<(string Name, SchemaDeclaration? Declaration)> EverythingIn(LoadedSchema schema)
        => EverythingIn(schema.Bundle, FileIn(schema.Bundle, SchemaName));

    private static IEnumerable<(string, SchemaDeclaration?)> EverythingIn(DescriptorBundle bundle, FileDescriptor file)
        => file.MessageTypes.SelectMany(message => EverythingIn(bundle, message))
            .Concat(file.EnumTypes.SelectMany(enumType => EverythingIn(bundle, enumType)));

    private static IEnumerable<(string, SchemaDeclaration?)> EverythingIn(DescriptorBundle bundle, MessageDescriptor message)
        => new[] { (message.Name, bundle.DeclarationOf(message)) }
            .Concat(message.Fields.InDeclarationOrder().Select(field => (field.Name, bundle.DeclarationOf(field))))
            .Concat(message.NestedTypes.SelectMany(nested => EverythingIn(bundle, nested)))
            .Concat(message.EnumTypes.SelectMany(enumType => EverythingIn(bundle, enumType)));

    private static IEnumerable<(string, SchemaDeclaration?)> EverythingIn(DescriptorBundle bundle, EnumDescriptor enumType)
        => new[] { (enumType.Name, bundle.DeclarationOf(enumType)) }
            .Concat(enumType.Values.Select(value => (value.Name, bundle.DeclarationOf(value))));
}
