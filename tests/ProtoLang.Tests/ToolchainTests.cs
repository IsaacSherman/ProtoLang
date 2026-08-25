using ProtoLang.Tests.Harness;
using Xunit;

namespace ProtoLang.Tests;

/// <summary>
/// Tests for the toolchain discovery the C++ suites depend on. What makes these worth writing is
/// that a bug here does not fail anything: it makes a test skip, which reads as "this machine is
/// not equipped" whether or not that is true.
/// </summary>
public class ToolchainTests
{
    /// <summary>
    /// Setting the documented override must not leave the toolchain looking less capable than
    /// leaving it unset. It once returned the headers alone, which silently downgraded the
    /// link-and-run and conformance suites to a skip on a fully equipped machine -- and a skip is
    /// exactly the failure mode nobody investigates.
    /// </summary>
    [Fact]
    public void TheIncludeOverrideKeepsTheRestOfTheInstall()
    {
        var discovered = Toolchain.LocateProtobufCpp();
        if (discovered is null || !discovered.CanLink)
        {
            Assert.Skip("No linkable protobuf C++ install found, so there is nothing to override with.");
        }

        var overridden = Toolchain.LocateProtobufCpp(discovered.IncludeDirectory);

        Assert.NotNull(overridden);
        Assert.True(
            overridden!.CanLink,
            "Pointing PROTOLANG_PROTOBUF_CPP_INCLUDE at an install that can link produced one that "
            + $"cannot: {overridden.DescribeMissingLinkInputs()}");

        // Records compare by value, so this asserts the override found the same protoc, libraries,
        // and runtime binaries, not merely that it found some.
        Assert.Equal(discovered, overridden);
    }

    /// <summary>
    /// An override that does not actually contain protobuf headers is ignored rather than believed,
    /// so a stale variable in someone's shell profile does not disable the C++ suites outright.
    /// </summary>
    [Fact]
    public void AnOverrideWithoutHeadersFallsBackToDiscovery()
    {
        var discovered = Toolchain.LocateProtobufCpp();

        Assert.Equal(discovered, Toolchain.LocateProtobufCpp(Path.GetTempPath()));
    }

    [Fact]
    public void MissingLinkInputsAreNamedIndividually()
    {
        var headersOnly = new ProtobufCppInstall("/somewhere/include", null, null, null);

        Assert.False(headersOnly.CanLink);

        var description = headersOnly.DescribeMissingLinkInputs();
        Assert.Contains("/somewhere/include", description, StringComparison.Ordinal);
        Assert.Contains("a matching protoc", description, StringComparison.Ordinal);
        Assert.Contains("import libraries", description, StringComparison.Ordinal);
        Assert.Contains("runtime binaries", description, StringComparison.Ordinal);
    }
}
