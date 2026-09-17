using AgentStudio.TestSupport;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// AGT-2858: a review attempt's verify commands must not pile their temp
/// residue up for the whole attempt, and the executor must never empty a temp
/// directory it does not own. <see cref="ReviewTempResidue.IsInside"/> is the
/// only branching decision in that cleanup, so it is covered as a matrix.
/// </summary>
public sealed class ReviewTempResidueTests : IDisposable
{
    private readonly TempWorkspace _workspace = new("review-temp-residue-tests");

    public void Dispose() => _workspace.Dispose();

    [Theory]
    // The attempt's own temp directory and anything below it: owned.
    [InlineData("attempt", "attempt/tmp", true)]
    [InlineData("attempt", "attempt/baseline-runtime-abc/tmp", true)]
    // The attempt root itself holds the repository and the artifacts. Emptying
    // it would delete the review's own evidence.
    [InlineData("attempt", "attempt", false)]
    // A sibling whose name merely starts with the root - exactly the shape of
    // the manual "-before-agt2787" cache copies the operator found.
    [InlineData("attempt", "attempt-before-agt2787/tmp", false)]
    // The host-shared root and a traversal back out of the attempt: not ours.
    [InlineData("attempt", "/tmp", false)]
    [InlineData("attempt", "attempt/tmp/../../elsewhere", false)]
    [InlineData("attempt", "", false)]
    [InlineData("attempt", null, false)]
    public void Only_a_path_below_the_attempt_root_is_purgeable(
        string relativeRoot,
        string? relativeCandidate,
        bool expected)
    {
        var root = _workspace.Combine(relativeRoot);
        var candidate = relativeCandidate switch
        {
            null => null,
            "" => string.Empty,
            var value when Path.IsPathRooted(value) => value,
            var value => _workspace.Combine(value.Replace('/', Path.DirectorySeparatorChar)),
        };

        Assert.Equal(expected, ReviewTempResidue.IsInside(candidate, root));
    }

    [Fact]
    public void Purge_empties_the_command_temp_directory_and_keeps_the_directory_itself()
    {
        var attemptRoot = _workspace.Directory("attempt");
        var temp = Path.Combine(attemptRoot, "tmp");
        Directory.CreateDirectory(Path.Combine(temp, "atp-crash-1", "logs"));
        File.WriteAllText(Path.Combine(temp, "atp-crash-1", "logs", "day.log"), "crash");
        File.WriteAllText(Path.Combine(temp, "MSBuild_pid-1234.failure.txt"), "node");
        var artifacts = Path.Combine(attemptRoot, "artifacts");
        Directory.CreateDirectory(artifacts);
        File.WriteAllText(Path.Combine(artifacts, "report.json"), "{}");

        var purge = ReviewTempResidue.Purge(temp, attemptRoot);

        Assert.Equal(2, purge.Removed);
        Assert.Equal(0, purge.Retained);
        Assert.True(purge.Observed);
        Assert.True(Directory.Exists(temp));
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp));
        Assert.True(File.Exists(Path.Combine(artifacts, "report.json")));
    }

    [Fact]
    public void Purge_of_an_unowned_or_missing_path_reports_nothing_and_touches_nothing()
    {
        var attemptRoot = _workspace.Directory("attempt");
        var foreign = _workspace.Directory("host-temp");
        File.WriteAllText(Path.Combine(foreign, "bus-bridge-fake-job-1"), "not ours");

        Assert.False(ReviewTempResidue.Purge(foreign, attemptRoot).Observed);
        Assert.False(ReviewTempResidue.Purge(Path.Combine(attemptRoot, "tmp"), attemptRoot).Observed);
        Assert.True(File.Exists(Path.Combine(foreign, "bus-bridge-fake-job-1")));
    }

    [SkippableFact]
    [Trait(PlatformGate.TraitName, PlatformGate.Linux)]
    public void Purge_removes_a_symlink_without_following_it()
    {
        PlatformGate.LinuxOnly("creating a directory symlink needs no elevation only here");

        var attemptRoot = _workspace.Directory("attempt");
        var temp = Path.Combine(attemptRoot, "tmp");
        Directory.CreateDirectory(temp);
        var target = _workspace.Directory("link-target");
        File.WriteAllText(Path.Combine(target, "keep.txt"), "must survive");
        Directory.CreateSymbolicLink(Path.Combine(temp, "escape"), target);

        var purge = ReviewTempResidue.Purge(temp, attemptRoot);

        Assert.Equal(1, purge.Removed);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp));
        Assert.True(File.Exists(Path.Combine(target, "keep.txt")));
    }
}
