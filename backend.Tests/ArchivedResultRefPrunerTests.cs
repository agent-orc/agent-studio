using Xunit;

namespace AgentStudio.Tests;

public sealed class ArchivedResultRefPrunerTests
{
    [Theory]
    [InlineData("agent-studio/results/run-1/fence-1/abc", "refs/remotes/origin/agent-studio/results/run-1/fence-1/abc")]
    [InlineData("origin/agent-studio/quarantine/runner/task/run", "refs/remotes/origin/agent-studio/quarantine/runner/task/run")]
    [InlineData("refs/heads/agent-studio/results/run-1/fence-1/abc", "refs/remotes/origin/agent-studio/results/run-1/fence-1/abc")]
    public void Candidate_IsAlwaysALocalRemoteTrackingRef(string value, string expected)
        => Assert.Equal(expected, ArchivedResultRefPruner.ToLocalRemoteTrackingRef(value));

    [Theory]
    [InlineData("main")]
    [InlineData("refs/heads/main")]
    [InlineData("agent-studio/salvage/runner/task/run")]
    [InlineData("refs/remotes/upstream/agent-studio/results/run")]
    [InlineData("agent-studio/results/../main")]
    public void Candidate_RejectsEveryRefOutsideSafeLocalNamespaces(string value)
        => Assert.Null(ArchivedResultRefPruner.ToLocalRemoteTrackingRef(value));

    [Fact]
    public void CandidateRefs_UsesOnlyArchivedCardsAndIndexedSafeRefs()
    {
        var root = Path.Combine(Path.GetTempPath(), "archived-ref-prune-" + Guid.NewGuid().ToString("N"));
        var archivedFolder = Path.Combine(root, "archive", "archived");
        var activeFolder = Path.Combine(root, "progress", "active");
        try
        {
            WriteSubject(archivedFolder, "ARCH-1", "agent-studio/results/run-1/fence-1/result");
            WriteSubject(activeFolder, "LIVE-1", "agent-studio/quarantine/runner/live/run-1");
            var cards = new[]
            {
                new TaskInfo
                {
                    TaskKey = "ARCH-1", State = TaskStates.Archive, FolderPath = archivedFolder,
                },
                new TaskInfo
                {
                    TaskKey = "LIVE-1", State = TaskStates.Progress, FolderPath = activeFolder,
                },
            };

            var indexed = new[]
            {
                new AttemptIndexedDeliveryRef(
                    "ARCH-1",
                    "agent-studio/quarantine/runner/arch/run-1",
                    new string('2', 40),
                    DateTime.UtcNow),
                new AttemptIndexedDeliveryRef(
                    "LIVE-1",
                    "agent-studio/results/run-live/fence-1/result",
                    new string('3', 40),
                    DateTime.UtcNow),
            };

            var candidates = ArchivedResultRefPruner.CandidateRefs(cards, indexed);

            Assert.Equal(
                [
                    "refs/remotes/origin/agent-studio/quarantine/runner/arch/run-1",
                    "refs/remotes/origin/agent-studio/results/run-1/fence-1/result",
                ],
                candidates);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void WriteSubject(string folder, string taskKey, string resultRef)
        => ReviewSubjectStore.Write(folder, new ReviewSubjectRecord
        {
            TaskKey = taskKey,
            RunAttemptId = "run-1",
            AttemptChainId = "chain-1",
            ResultSha = new string('1', 40),
            ImmutableResultRef = resultRef,
            ResultRef = "main",
            CompletedAtUtc = DateTimeOffset.UtcNow,
        });
}
