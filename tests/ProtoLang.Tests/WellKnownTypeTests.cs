using ProtoLang.Backend;
using ProtoLang.Binding;
using ProtoLang.Diagnostics;
using ProtoLang.Tests.Harness;
using Xunit;

namespace ProtoLang.Tests;

/// <summary>
/// Covers schemas that draw on protobuf's own vocabulary -- Timestamp, Duration, and the rest of
/// google/protobuf.
/// </summary>
/// <remarks>
/// Every compilation here pins <see cref="ProtocLocator.FindBundledProtoc"/> rather than letting the
/// compiler choose. protoc resolves the well-known schemas from descriptors compiled into the binary
/// only from version 33 onwards, so on a machine with a recent protoc first on PATH these tests
/// would pass with the fix reverted and prove nothing. The bundled Grpc.Tools protoc is both the one
/// that needs help and the one a machine with nothing installed actually gets.
/// </remarks>
public class WellKnownTypeTests
{
    [Fact]
    public void TheBundledProtocShipsTheWellKnownSchemas()
    {
        var protoc = RequireBundledProtoc();

        var includes = ProtocLocator.FindWellKnownTypeIncludePaths(protoc);

        Assert.NotEmpty(includes);
        Assert.Contains(
            includes,
            include => File.Exists(Path.Combine(include, "google", "protobuf", "timestamp.proto")));
    }

    /// <summary>
    /// A second install, filed differently. Grpc.Tools is not the only protoc that needs the
    /// schemas handed to it, and every layout that ships them puts them somewhere else: beside the
    /// binary for a release archive, under the prefix for vcpkg and its kin.
    /// </summary>
    [Fact]
    public void AProtobufInstallLaidOutByPackageExposesItsSchemasToo()
    {
        var protobuf = Toolchain.LocateProtobufCpp();
        if (protobuf?.ProtocPath is null)
        {
            Assert.Skip(
                "No protobuf C++ install found. Run 'vcpkg install' or set "
                + "PROTOLANG_PROTOBUF_CPP_INCLUDE to the include directory.");
        }

        var includes = ProtocLocator.FindWellKnownTypeIncludePaths(protobuf.ProtocPath);

        Assert.Contains(
            includes,
            include => File.Exists(Path.Combine(include, "google", "protobuf", "timestamp.proto")));
    }

    /// <summary>
    /// A directory is only accepted once it is shown to hold the schemas, so a protoc somewhere with
    /// no include directory beside it contributes nothing instead of a --proto_path pointing at a
    /// path that does not exist.
    /// </summary>
    [Fact]
    public void AProtocWithNoSchemasBesideItContributesNoIncludePaths()
    {
        var directory = Path.Combine(Path.GetTempPath(), "protolang-bare-protoc", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var includes = ProtocLocator.FindWellKnownTypeIncludePaths(
            Path.Combine(directory, ProtocLocator.OverrideEnvironmentVariable));

        Assert.Empty(includes);
    }

    /// <summary>The case in the wild: a project's own schema importing a well-known type.</summary>
    [Fact]
    public void AnImportedWellKnownTypeResolvesWithoutBeingToldWhereItLives()
    {
        var path = TestPaths.WriteTempScript(
            """
            import proto "wkt_event.proto";

            extend Event {
                fn ends_at_seconds() -> int64 {
                    if not has starts_at or not has length {
                        return 0;
                    }

                    return starts_at.seconds + length.seconds;
                }
            }
            """);

        // Only the project's own proto root. Naming a directory for google/protobuf here would be
        // the very workaround this is meant to remove.
        var result = Compilation.Compile(path, [TestPaths.FixtureProtoDirectory], BundledLoader());

        Assert.True(result.Success, Describe(result));
    }

    /// <summary>
    /// Importing a well-known type by name, which is how you get one in scope to extend. Distinct
    /// from the case above because the compiler checks that every import exists before it runs
    /// protoc, and this one exists nowhere under the proto roots the user named.
    /// </summary>
    [Fact]
    public void AWellKnownTypeCanBeImportedDirectly()
    {
        var path = TestPaths.WriteTempScript(
            """
            import proto "google/protobuf/timestamp.proto";

            extend Timestamp {
                fn millis() -> int64 {
                    return seconds * 1000;
                }
            }
            """);

        var result = Compilation.Compile(path, [TestPaths.FixtureProtoDirectory], BundledLoader());

        Assert.True(result.Success, Describe(result));
        Assert.Contains(result.Module!.Methods, method => method.Signature.Name == "millis");
    }

    /// <summary>
    /// The well-known schemas have to become resolvable to protoc without becoming entries in the
    /// emitted build files: both runtimes ship them precompiled, so generating them again would
    /// define the same types twice.
    /// </summary>
    [Fact]
    public void ResolvingAWellKnownTypeDoesNotPutItInTheEmittedProject()
    {
        var path = TestPaths.WriteTempScript(
            """
            import proto "wkt_event.proto";

            extend Event {
                fn ends_at_seconds() -> int64 {
                    if not has starts_at or not has length {
                        return 0;
                    }

                    return starts_at.seconds + length.seconds;
                }
            }
            """);

        var result = Compilation.Compile(path, [TestPaths.FixtureProtoDirectory], BundledLoader());
        Assert.True(result.Success, Describe(result));

        var directory = Path.GetDirectoryName(path)!;
        var options = ScaffoldOptions.Create(
            path,
            [TestPaths.FixtureProtoDirectory],
            result.Descriptors,
            Path.Combine(directory, "generated", "csharp"),
            Path.Combine(directory, "generated", "tests", "csharp"),
            []);

        Assert.Contains(options.ProtoFiles, file => file.RelativePath == "wkt_event.proto");
        Assert.DoesNotContain(
            options.ProtoFiles,
            file => file.RelativePath.StartsWith("google/protobuf/", StringComparison.Ordinal));
    }

    /// <summary>
    /// Extending a well-known type is allowed and still compiles. The warning exists because the
    /// generated behavior has to ship as its own library, which is not something the source says.
    /// </summary>
    [Fact]
    public void ExtendingAWellKnownTypeWarnsWithoutRejectingIt()
    {
        var path = TestPaths.WriteTempScript(
            """
            import proto "google/protobuf/timestamp.proto";

            extend Timestamp {
                fn millis() -> int64 {
                    return seconds * 1000;
                }
            }
            """);

        var result = Compilation.Compile(path, [TestPaths.FixtureProtoDirectory], BundledLoader());

        Assert.True(result.Success, Describe(result));
        Assert.Contains(result.Module!.Methods, method => method.Signature.Name == "millis");

        var warning = Assert.Single(result.Diagnostics, d => d.Code == "PL0077");
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
    }

    /// <summary>
    /// Without this, a check that matched every extend would look exactly as correct as one that
    /// matched the right ones.
    /// </summary>
    [Fact]
    public void ExtendingAnOrdinaryMessageDoesNotWarn()
    {
        var path = TestPaths.WriteTempScript(
            """
            import proto "wkt_event.proto";

            extend Event {
                fn ends_at_seconds() -> int64 {
                    if not has starts_at or not has length {
                        return 0;
                    }

                    return starts_at.seconds + length.seconds;
                }
            }
            """);

        var result = Compilation.Compile(path, [TestPaths.FixtureProtoDirectory], BundledLoader());

        Assert.True(result.Success, Describe(result));

        // The receiver is the project's own message even though its fields are well-known types.
        // Reading one is not extending it.
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == "PL0077");
    }

    private static DescriptorLoader BundledLoader() => new(RequireBundledProtoc());

    private static string RequireBundledProtoc()
    {
        var protoc = ProtocLocator.FindBundledProtoc();
        if (protoc is null)
        {
            Assert.Skip("No Grpc.Tools protoc in the NuGet cache. Restore the solution first.");
        }

        return protoc;
    }

    private static string Describe(CompilationResult result)
        => string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.ToString()));
}
