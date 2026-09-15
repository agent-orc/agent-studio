using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Direct matrix over <see cref="CommitCandidateAssetPolicy"/>, the pure half of
/// the WEB-21 fix: which binary candidates are declared evidence the platform
/// may commit unattended, and which stay a surprise that needs a person.
///
/// <para>The rule is conjunctive on purpose (declared path AND evidence
/// extension AND inside the size limit), so each clause gets its own negative
/// row. A regression that drops one clause is a regression that lets an
/// arbitrary binary ride into git without review.</para>
/// </summary>
public sealed class CommitCandidateAssetPolicyTests
{
    private const long Small = 4096;

    [Theory]
    // The WEB-21 shape: a screenshot under the platform's default asset paths.
    [InlineData("results/WEB-21/board-overview.png", true)]
    [InlineData("results/WEB-21/nested/deep/detail.jpeg", true)]
    [InlineData("results/report.PDF", true)]
    // Right extension, wrong place: a screenshot dropped anywhere else is still
    // an unexplained binary. A documentation asset tree is a plausible home for
    // evidence, but the platform does not nominate directories on a project's
    // behalf - the project declares them.
    [InlineData("frontend/src/app/logo.png", false)]
    [InlineData("docs/assets/images/pinned.png", false)]
    [InlineData("shot.png", false)]
    // Right place, wrong kind: an asset directory is not a licence to commit
    // archives, binaries, or videos unattended.
    [InlineData("results/WEB-21/trace.zip", false)]
    [InlineData("results/WEB-21/capture.mp4", false)]
    [InlineData("results/WEB-21/app.exe", false)]
    // The directory itself is not a candidate, and a prefix match must not leak
    // into a sibling directory that merely starts with the same letters.
    [InlineData("results", false)]
    [InlineData("results-scratch/shot.png", false)]
    public void IsEvidenceAsset_DefaultPaths(string path, bool expected)
        => Assert.Equal(expected, CommitCandidateAssetPolicy.IsEvidenceAsset(path, Small, null));

    [Theory]
    [InlineData(1, true)]
    [InlineData(CommitCandidateAssetPolicy.MaxAssetBytes, true)]
    [InlineData(CommitCandidateAssetPolicy.MaxAssetBytes + 1, false)]
    // A zero-byte "screenshot" is a failed capture, not evidence.
    [InlineData(0, false)]
    public void IsEvidenceAsset_HonoursTheSizeLimit(long size, bool expected)
        => Assert.Equal(
            expected,
            CommitCandidateAssetPolicy.IsEvidenceAsset("results/WEB-21/shot.png", size, null));

    [Fact]
    public void DeclaredPaths_ReplaceThePlatformDefaultRatherThanExtendIt()
    {
        string[] declared = ["evidence/screenshots"];

        Assert.True(CommitCandidateAssetPolicy.IsEvidenceAsset(
            "evidence/screenshots/board.png", Small, declared));
        // The project said where its evidence lives. Silently keeping the
        // built-in paths as well would commit binaries from directories the
        // project never declared.
        Assert.False(CommitCandidateAssetPolicy.IsEvidenceAsset(
            "results/WEB-21/board.png", Small, declared));
    }

    [Theory]
    [InlineData("  /Evidence\\Shots/  ", "Evidence/Shots")]
    [InlineData("results/", "results")]
    public void ResolveDeclaredPaths_NormalizesSeparatorsAndEdges(string declared, string expected)
        => Assert.Equal([expected], CommitCandidateAssetPolicy.ResolveDeclaredPaths([declared]));

    [Fact]
    public void ResolveDeclaredPaths_BlankDeclarationStaysAbsent()
    {
        // Null means "inherit the default". Persisting today's default instead
        // would freeze it into every project's settings file.
        Assert.Null(CommitCandidateAssetPolicy.ResolveDeclaredPaths(null));
        Assert.Null(CommitCandidateAssetPolicy.ResolveDeclaredPaths(["", "   ", "/"]));
        Assert.Equal(
            CommitCandidateAssetPolicy.DefaultAssetPaths,
            CommitCandidateAssetPolicy.ResolveAssetPaths([" "]));
    }

    [Fact]
    public void ResolveDeclaredPaths_DropsDuplicatesAfterNormalization()
        => Assert.Equal(
            ["results"],
            CommitCandidateAssetPolicy.ResolveDeclaredPaths(["results", "/results/", "results\\"]));
}
