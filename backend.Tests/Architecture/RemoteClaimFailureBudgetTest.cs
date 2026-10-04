using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Architecture-level breaker for the loop-inventory entry
/// <c>remote-claim.environment-preparation-per-task</c>.
/// </summary>
public sealed class RemoteClaimFailureBudgetTest
{
    [Fact]
    public void Persistent_budget_allows_two_requeues_then_escalates()
    {
        var folder = Path.Combine(
            Path.GetTempPath(),
            "remote-claim-budget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), "{}");
        var task = new TaskInfo { Id = "AGT-1", FolderPath = folder };

        try
        {
            Assert.Equal(3, RemoteClaimFailureBudget.MaxAttempts);

            var first = NewBudget().Record(task, "clone failed: 403 agent-orc/website");
            var second = NewBudget().Record(task, "clone failed: 403 agent-orc/website");
            var third = NewBudget().Record(task, "clone failed: 403 agent-orc/website");

            Assert.Equal(1, first.Attempt);
            Assert.False(first.Escalate);
            Assert.Equal(2, second.Attempt);
            Assert.False(second.Escalate);
            Assert.Equal(3, third.Attempt);
            Assert.True(third.Escalate);

            NewBudget().PrepareForClaim(task);
            var afterOperatorRequeue = NewBudget().Record(task, "clone failed again");
            Assert.Equal(1, afterOperatorRequeue.Attempt);
            Assert.False(afterOperatorRequeue.Escalate);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Different_fingerprint_resets_consecutive_count_and_preserves_host()
    {
        var folder = Path.Combine(Path.GetTempPath(), "remote-claim-budget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), "{}");
        var task = new TaskInfo { Id = "AGT-1", FolderPath = folder };
        try
        {
            var budget = NewBudget();
            budget.Record(task, "fatal: not a git repository", "runner-environment-preparation-failed", "host-a");
            budget.Record(task, "fatal: not a git repository", "runner-environment-preparation-failed", "host-a");
            var changed = budget.Record(task, "permission denied", "runner-results-handling-failed", "host-b");
            Assert.Equal(1, changed.Attempt);
            var state = budget.GetState(task)!;
            Assert.Equal("host-b", state.Host);
            Assert.Equal(RemoteClaimFailureBudget.Fingerprint("runner-results-handling-failed", "permission denied"), state.Fingerprint);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void Escalation_under_a_lower_budget_resets_after_operator_requeues_with_a_higher_budget()
    {
        var folder = Path.Combine(Path.GetTempPath(), "remote-claim-budget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), "{}");
        var task = new TaskInfo { Id = "AGT-1", FolderPath = folder };
        try
        {
            var twoAttemptBudget = new RemoteClaimFailureBudget(NullLogger<RemoteClaimFailureBudget>.Instance, 2);
            Assert.False(twoAttemptBudget.Record(task, "fatal: not a git repository").Escalate);
            Assert.True(twoAttemptBudget.Record(task, "fatal: not a git repository").Escalate);

            var threeAttemptBudget = NewBudget();
            threeAttemptBudget.PrepareForClaim(task);
            var firstAfterRequeue = threeAttemptBudget.Record(task, "fatal: not a git repository");
            Assert.Equal(1, firstAfterRequeue.Attempt);
            Assert.False(firstAfterRequeue.Escalate);
        }
        finally { Directory.Delete(folder, true); }
    }

    private static RemoteClaimFailureBudget NewBudget()
        => new(NullLogger<RemoteClaimFailureBudget>.Instance);
}
