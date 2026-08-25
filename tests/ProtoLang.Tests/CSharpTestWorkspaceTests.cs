using ProtoLang.Tests.Harness;
using Xunit;

namespace ProtoLang.Tests;

public class CSharpTestWorkspaceTests
{
    /// <summary>
    /// The generated project must not be able to reach the network. Every package it references is
    /// one the repository already references at the same version, so restore has nothing to fetch,
    /// and clearing the sources turns that from a hope into a guarantee.
    /// </summary>
    /// <remarks>
    /// This is pinned by a test rather than left to the comment because the failure it prevents does
    /// not look like a configuration problem. With <c>TreatWarningsAsErrors</c> set, an unreachable
    /// source makes restore fail with "NU1900: Warning As Error: Error occurred while getting
    /// package vulnerability data", and the conformance run reads as a branch failure rather than as
    /// a sandbox with no outbound network.
    /// </remarks>
    [Fact]
    public void TheGeneratedProjectRestoresWithoutAnyPackageSource()
    {
        var workspace = CSharpTestWorkspace.Create("workspace-shape");
        workspace.WriteProjectFiles();

        var config = Path.Combine(workspace.Directory, "nuget.config");
        Assert.True(File.Exists(config), $"no nuget.config was written to {workspace.Directory}");

        var contents = File.ReadAllText(config);
        Assert.Contains("<packageSources>", contents, StringComparison.Ordinal);
        Assert.Contains("<clear />", contents, StringComparison.Ordinal);

        // A source added after the clear would put the network back in the loop.
        Assert.DoesNotContain("<add ", contents, StringComparison.Ordinal);
    }
}
