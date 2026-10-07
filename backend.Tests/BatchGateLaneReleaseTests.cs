using AgentStudio.Pipeline;
using AgentStudio.Shared;
using Xunit;

namespace AgentStudio.Tests;

public sealed class BatchGateLaneReleaseTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddDays(20_000);

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
                Now, 1);
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

    [Fact]
    public void SupersededMarkerDoesNotBlockNewReviewAndIsClearedByIdentity()
    {
        var root = Path.Combine(Path.GetTempPath(), "batch-ownership-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new BatchGateStore(root);
            var subject = new BatchGateSubject(
                "task", "project", "repo", "develop", "full", "profile", "v1",
                "refs/heads/agent-studio/results/task", new string('a', 40),
                "old-run", 1, 1, new string('a', 40),
                true, true, true, true, false, null, true,
                Now, 1);
            var oldMarker = new BatchGateOwnership("old-review", subject);
            BatchGateOwnershipStore.Write(root, oldMarker);
            var reviewSubject = new ReviewSubjectDto("subject", "repo", new string('b', 40),
                "new-run", "requirements", "policy", [], Now.UtcDateTime);
            var newReview = new ReviewAttemptDto("new-review", "task", "repo", "new-run",
                null, reviewSubject, AttemptLifecycleState.Completed, null, 2, 2,
                Now.UtcDateTime, Now.UtcDateTime, ReviewTerminalOutcome.Pass,
                null, new string('b', 40), null, []);
            var projection = new AttemptAuthorityProjection("task", 2, null,
                reviewSubject, newReview, [], [newReview], false);

            Assert.Null(BatchGateOwnershipStore.ReleaseFailure(oldMarker, projection, store, root));
            Assert.Equal("batch-gate-evidence-missing",
                BatchGateOwnershipStore.ReleaseFailure(
                    oldMarker with { ReviewAttemptId = "new-review" },
                    projection, store, root));
            BatchGateOwnershipStore.ClearIfReviewAttempt(root, "new-review");
            Assert.Equal("old-review", BatchGateOwnershipStore.Read(root)?.ReviewAttemptId);
            BatchGateOwnershipStore.ClearIfReviewAttempt(root, "old-review");
            Assert.Null(BatchGateOwnershipStore.Read(root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
