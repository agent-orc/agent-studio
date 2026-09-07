using AgentStudio.Retention;

namespace AgentStudio.Retention.Tests;

public sealed class ArtifactClassifierTests
{
    private readonly ArtifactClassifier _classifier = new();

    [Theory]
    [InlineData("task.json", ArtifactClass.Authority)]
    [InlineData("logs/timeline.jsonl", ArtifactClass.Authority)]
    [InlineData("leases/current.json", ArtifactClass.Authority)]
    [InlineData("fences/run.json", ArtifactClass.Authority)]
    [InlineData("review-attempts/1.json", ArtifactClass.Authority)]
    [InlineData("audit/events.jsonl", ArtifactClass.Authority)]
    [InlineData("orchestrator-chat-turns.jsonl", ArtifactClass.Authority)]
    [InlineData("status.md", ArtifactClass.Evidence)]
    [InlineData("prompt.md", ArtifactClass.Evidence)]
    [InlineData("prompt-history/1.md", ArtifactClass.Evidence)]
    [InlineData("review-grade.json", ArtifactClass.Evidence)]
    [InlineData("reports/summary.md", ArtifactClass.Evidence)]
    [InlineData("integration-records.json", ArtifactClass.Evidence)]
    [InlineData("commits.json", ArtifactClass.Evidence)]
    [InlineData("logs/session-events.jsonl", ArtifactClass.Evidence)]
    [InlineData("enrichment-report.json", ArtifactClass.Evidence)]
    [InlineData("post-steps/test.log", ArtifactClass.Evidence)]
    [InlineData("logs/cli-output.log", ArtifactClass.HeavyWorkingData)]
    [InlineData("logs/cli-output.log.1", ArtifactClass.HeavyWorkingData)]
    [InlineData("review/code-review-stdout.log", ArtifactClass.HeavyWorkingData)]
    [InlineData("results/trace.zip", ArtifactClass.HeavyWorkingData)]
    [InlineData("attachments/screenshot.png", ArtifactClass.HeavyWorkingData)]
    [InlineData(".metadata/attempt-authority.json", ArtifactClass.Runtime)]
    [InlineData(".metadata/attempt-authority.archive-2026-01-01.json", ArtifactClass.Runtime)]
    [InlineData("logs/bus/project/2026-01-01.jsonl", ArtifactClass.Runtime)]
    [InlineData(".runtime/session/cache.bin", ArtifactClass.Runtime)]
    [InlineData("scratch.tmp", ArtifactClass.Runtime)]
    public void ClassifiesDossierPathFamilies(string path, ArtifactClass expected)
        => Assert.Equal(expected, _classifier.Classify(path).ArtifactClass);

    [Fact]
    public void CommitRefusalAppliesOnlyToHeavyFilesOver50MiB()
    {
        Assert.True(_classifier.IsCommitRefused("results/trace.zip", 50L * 1024 * 1024 + 1));
        Assert.False(_classifier.IsCommitRefused("results/trace.zip", 50L * 1024 * 1024));
        Assert.False(_classifier.IsCommitRefused("status.md", 60L * 1024 * 1024));
    }
}
