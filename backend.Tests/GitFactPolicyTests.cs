using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3322 (Git facts S2): direct matrices for the pure decisions behind the
/// fact store: when <c>git config</c> runs, how <c>config_generation</c>
/// moves, when a restored generation is ready, the producer epoch fence, and
/// the repository health state.
/// </summary>
public sealed class GitFactPolicyTests
{
    [Theory]
    [InlineData(false, false, true, nameof(GitConfigCaptureReason.NoCapture))]
    [InlineData(false, true, false, nameof(GitConfigCaptureReason.NoCapture))]
    [InlineData(true, true, true, nameof(GitConfigCaptureReason.WatcherEvent))]
    [InlineData(true, true, false, nameof(GitConfigCaptureReason.WatcherEvent))]
    [InlineData(true, false, false, nameof(GitConfigCaptureReason.SourceChanged))]
    [InlineData(true, false, true, nameof(GitConfigCaptureReason.None))]
    public void ConfigCapture_RunsGitOnlyForAMissingCaptureAConfigEventOrAChangedSource(
        bool hasCapture, bool watcherEvent, bool sourcesUnchanged, string expected)
        => Assert.Equal(expected, GitConfigCapturePolicy.Decide(hasCapture, watcherEvent, sourcesUnchanged).ToString());

    [Theory]
    [InlineData(0L, null, "A", 1L)]
    [InlineData(1L, "A", "A", 1L)]
    [InlineData(1L, "A", "B", 2L)]
    [InlineData(7L, null, "A", 8L)]
    [InlineData(7L, "A", "A", 7L)]
    public void ConfigGeneration_AdvancesOnlyWhenGitResolvesADifferentConfiguration(
        long current, string? previous, string captured, long expected)
        => Assert.Equal(expected, GitConfigCapturePolicy.NextGeneration(current, previous, captured));

    private static readonly GitFactGenerationInput Stored = new("refs", 3, "settings", "tasks");

    [Theory]
    [InlineData("", true, nameof(GitFactRestoreVerdict.Ready))]
    [InlineData("", false, nameof(GitFactRestoreVerdict.UnreadableCards))]
    [InlineData("refs", true, nameof(GitFactRestoreVerdict.RefsMoved))]
    [InlineData("config", true, nameof(GitFactRestoreVerdict.ConfigChanged))]
    [InlineData("settings", true, nameof(GitFactRestoreVerdict.SettingsChanged))]
    [InlineData("tasks", true, nameof(GitFactRestoreVerdict.TasksChanged))]
    // The first moved input names the verdict.
    [InlineData("refs,config,settings,tasks", true, nameof(GitFactRestoreVerdict.RefsMoved))]
    [InlineData("settings,tasks", true, nameof(GitFactRestoreVerdict.SettingsChanged))]
    public void RestoredGeneration_IsReadyOnlyWhenEveryInputMatches(string moved, bool cardsReadable, string expected)
    {
        var changed = moved.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var observed = Stored with
        {
            RefFingerprint = changed.Contains("refs") ? "moved" : Stored.RefFingerprint,
            ConfigGeneration = changed.Contains("config") ? 4 : Stored.ConfigGeneration,
            SettingsVersion = changed.Contains("settings") ? "other" : Stored.SettingsVersion,
            TaskInputSignature = changed.Contains("tasks") ? "other" : Stored.TaskInputSignature,
        };
        Assert.Equal(expected, GitFactRestorePolicy.Confirm(Stored, observed, cardsReadable).ToString());
    }

    [Theory]
    [InlineData("/repo", "/jobs", "/repo", "/jobs", true)]
    [InlineData("/repo", "/jobs", "/moved", "/jobs", false)]
    [InlineData("/repo", "/jobs", "/repo", "/other-jobs", false)]
    [InlineData(null, "/jobs", "/repo", "/jobs", false)]
    [InlineData("/repo", null, "/repo", "/jobs", false)]
    public void RestoredGeneration_IsServedOnlyForTheSameRoots(string? storedRepo, string? storedJobs,
        string repo, string jobs, bool expected)
        => Assert.Equal(expected, GitFactRestorePolicy.CanServe(storedRepo, storedJobs, repo, jobs,
            (a, b) => string.Equals(a, b, StringComparison.Ordinal)));

    [Theory]
    [InlineData(1L, 0L, true)]
    [InlineData(2L, 2L, true)]
    [InlineData(3L, 2L, true)]
    [InlineData(1L, 2L, false)]
    [InlineData(0L, 0L, false)]
    public void PublishFence_RejectsAnEpochLowerThanTheStoredOne(long publishing, long stored, bool expected)
        => Assert.Equal(expected, GitFactPublishPolicy.Accepts(publishing, stored));

    [Theory]
    [InlineData(false, false, null, false, GitRepositoryHealthPolicy.Warming)]
    [InlineData(true, true, null, false, GitRepositoryHealthPolicy.Restored)]
    [InlineData(true, false, null, false, GitRepositoryHealthPolicy.Ready)]
    [InlineData(true, false, "repository-degraded", false, GitRepositoryHealthPolicy.Degraded)]
    [InlineData(true, true, "timeout", false, GitRepositoryHealthPolicy.Degraded)]
    [InlineData(false, false, "refresh-failed", false, GitRepositoryHealthPolicy.Failed)]
    [InlineData(true, false, null, true, GitRepositoryHealthPolicy.Fenced)]
    public void RepositoryHealth_KeepsServingTheLastGoodGenerationOnFailure(bool hasGeneration, bool restored,
        string? failure, bool fenced, string expected)
        => Assert.Equal(expected, GitRepositoryHealthPolicy.State(hasGeneration, restored, failure, fenced));
}
