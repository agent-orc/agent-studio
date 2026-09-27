using AgentStudio.Pipeline;
using AgentStudio.Shared;
using Xunit;

namespace AgentStudio.Tests;

public sealed class BatchGateLaneReleaseTests
{
    [Fact]
    public void PendingOrMissingBatchRecordBlocksLaneRelease()
    {
        var root = Path.Combine(Path.GetTempPath(), "batch-release-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new BatchGateStore(root);
            var subject = new BatchGateSubject(
                "task", "project", "repo", "develop", "full", "profile", "v1",
                "refs/heads/agent-studio/results/task", new string('a', 40),
                "run", 1, 1, new string('a', 40),
                true, true, true, true, false, null, true,
                DateTimeOffset.UtcNow, 1);
            var projection = new AttemptAuthorityProjection("task", 1,
                null, null, null, [], [], false);
            Assert.Equal("batch-gate-evidence-missing",
                BatchGateOwnershipStore.ReleaseFailure(
                    new BatchGateOwnership("review", subject), projection, store, root));
            Assert.Equal("batch-gate-evidence-missing",
                BatchGateOwnershipStore.ReleaseFailure(
                    new BatchGateOwnership("review", subject, "batch", "run"),
                    projection, store, root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
