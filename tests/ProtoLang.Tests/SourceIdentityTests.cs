using Xunit;

namespace ProtoLang.Tests;

/// <summary>
/// The one type that answers everything the compiler used to take from a source path. These are
/// pure -- no protoc, no compilation -- because the properties they pin are the ones every other
/// route in the compiler now trusts without re-deriving.
/// </summary>
public class SourceIdentityTests
{
    /// <summary>
    /// The label is taken from the path as written, not from its expanded form. Those differ for a
    /// trailing separator or a path ending in '.', and the label is what diagnostics print -- so
    /// normalizing it would move published CLI output.
    /// </summary>
    [Fact]
    public void NamesAFileByTheNameTheCallerWouldHaveSeen()
    {
        var written = Path.Combine("a", "b", "buffer.protolang");

        Assert.Equal(Path.GetFileName(written), SourceIdentity.FromPath(written).Name);
        Assert.Equal("buffer.protolang", SourceIdentity.FromPath(written).Name);
    }

    [Fact]
    public void ExpandsTheDirectoryEvenWhenThePathIsRelative()
    {
        var identity = SourceIdentity.FromPath(Path.Combine("a", "b", "buffer.protolang"));

        Assert.NotNull(identity.Directory);
        Assert.True(Path.IsPathFullyQualified(identity.Directory!));
        Assert.True(Path.IsPathFullyQualified(identity.Path!));
    }

    /// <summary>
    /// Path.GetFullPath throws on an empty string three frames deeper, where the caller cannot see
    /// which argument was wrong. The compiler used to reach for it in three separate places.
    /// </summary>
    [Fact]
    public void RefusesAnEmptyPathWhereTheCallerCanSeeIt()
    {
        Assert.Throws<ArgumentException>(() => SourceIdentity.FromPath(string.Empty));
    }

    [Fact]
    public void AnUnsavedBufferHasNoPathAndNoDirectory()
    {
        var identity = SourceIdentity.Unsaved();

        Assert.Null(identity.Path);
        Assert.Null(identity.Directory);
        Assert.Equal("<unsaved>", identity.Name);
        Assert.Equal(SourceIdentity.UnsavedName, identity.Name);
    }

    /// <summary>
    /// The common editor case: a new file inside an open project. It has no path yet, but it should
    /// still get the project's policy and the project's proto root.
    /// </summary>
    [Fact]
    public void AnUnsavedBufferCanStillBelongToADirectory()
    {
        var directory = TestPaths.CreateTempDirectory();
        var identity = SourceIdentity.Unsaved("draft.protolang", directory);

        Assert.Null(identity.Path);
        Assert.Equal("draft.protolang", identity.Name);
        Assert.Equal(directory, identity.Directory);
    }

    [Fact]
    public void ReadingAnUnsavedIdentityIsRefusedRatherThanGuessedAt()
    {
        Assert.Throws<ArgumentException>(() => SourceDocument.ReadFrom(SourceIdentity.Unsaved()));
    }
}
