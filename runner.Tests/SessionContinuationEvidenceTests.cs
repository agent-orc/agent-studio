using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// The continuation ledger entry names the attempt the Task Server fenced.
/// On the legacy runner plane the slot's own attempt id is the lease id, while
/// the completion request carries the lease's attempt id; the server rejects
/// evidence whose attempt id differs from the completion's (observed on
/// 2026-09-27 with Stable 0.9.3: every production completion answered 400).
/// </summary>
public class SessionContinuationEvidenceTests
{
    [Fact]
    public void Fenced_attempt_id_is_the_lease_attempt_id_on_the_legacy_plane()
    {
        var root = NewRoot();
        try
        {
            var lease = Lease("AGT-1", "6e2c1a2b3c4d5e6f7a8b9c0d1e2f3a4b", "run_8475a45c066e4f4a81d69dd2a245dacf");
            var slot = new RunnerStateStore(root).Create(lease.TaskKey, lease, Path.Combine(root, "worktree"));

            Assert.Equal("run_8475a45c066e4f4a81d69dd2a245dacf", SessionContinuationEvidence.FencedAttemptId(slot));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // AGT-2985: the slot's attempt id and the lease's attempt id are one concept.
    // No consumer of slot.AttemptId may drift from the id the server fenced.
    [Fact]
    public void Slot_attempt_id_equals_the_lease_attempt_id_after_create_on_the_legacy_plane()
    {
        var root = NewRoot();
        try
        {
            var lease = Lease("AGT-4", "6e2c1a2b3c4d5e6f7a8b9c0d1e2f3a4b", "run_8475a45c066e4f4a81d69dd2a245dacf");
            var store = new RunnerStateStore(root);
            var slot = store.Create(lease.TaskKey, lease, Path.Combine(root, "worktree"));

            Assert.Equal(lease.AttemptId, slot.AttemptId);
            Assert.Null(slot.RunId);
            Assert.Equal(lease.AttemptId, Assert.Single(store.LoadAll()).AttemptId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Slot_attempt_id_equals_the_lease_attempt_id_after_create_on_the_v1_plane()
    {
        var root = NewRoot();
        try
        {
            var lease = Lease("AGT-5", "lease-v1", "run_v1");
            var slot = new RunnerStateStore(root).Create(lease.TaskKey, lease, Path.Combine(root, "worktree"), runId: "run_v1");

            Assert.Equal(lease.AttemptId, slot.AttemptId);
            Assert.Equal(slot.RunId, slot.AttemptId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Fenced_attempt_id_still_prefers_the_lease_for_a_slot_persisted_by_an_older_runner()
    {
        var root = NewRoot();
        try
        {
            var lease = Lease("AGT-6", "6e2c1a2b3c4d5e6f7a8b9c0d1e2f3a4b", "run_older_runner");
            var slot = new RunnerStateStore(root).Create(lease.TaskKey, lease, Path.Combine(root, "worktree"))
                with { AttemptId = lease.LeaseId };

            Assert.Equal("run_older_runner", SessionContinuationEvidence.FencedAttemptId(slot));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Fenced_attempt_id_falls_back_to_the_slot_id_when_the_lease_has_none()
    {
        var root = NewRoot();
        try
        {
            var lease = Lease("AGT-2", "0f1e2d3c4b5a69788796a5b4c3d2e1f0", attemptId: null);
            var slot = new RunnerStateStore(root).Create(lease.TaskKey, lease, Path.Combine(root, "worktree"));

            Assert.Equal(slot.AttemptId, SessionContinuationEvidence.FencedAttemptId(slot));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Fenced_attempt_id_keeps_the_run_id_on_the_v1_plane()
    {
        var root = NewRoot();
        try
        {
            var lease = Lease("AGT-3", "lease-v1", "run_v1");
            var slot = new RunnerStateStore(root).Create(lease.TaskKey, lease, Path.Combine(root, "worktree"), runId: "run_v1");

            Assert.Equal("run_v1", slot.AttemptId);
            Assert.Equal("run_v1", SessionContinuationEvidence.FencedAttemptId(slot));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static RunLeaseInfoDto Lease(string taskKey, string leaseId, string? attemptId) => new(
        taskKey,
        "runner-a",
        "runner a",
        "host-a",
        Environment.ProcessId,
        "backend",
        leaseId,
        4,
        DateTime.UtcNow,
        DateTime.UtcNow.AddMinutes(2),
        attemptId);

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "agent-runner-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
