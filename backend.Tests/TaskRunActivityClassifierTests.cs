
using System;
using AgentStudio.Shared;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Unit coverage for the pure <see cref="TaskRunActivityClassifier"/> (ASS-1751)
/// — the read-time mapper that disambiguates why a 3-progress card looks
/// "untouched": a live run, a failed run waiting out the rapid-crash backoff, an
/// orphan after a backend restart, or a failed-but-idle run. No runner needed;
/// the classifier is side-effect-free and takes its clock as a parameter.
/// </summary>
public sealed class TaskRunActivityClassifierTests
{
    private static readonly DateTime Now = new(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc);

    private static CliExecution Exec(string status, int pid = 0) =>
        new() { Status = status, ProcessId = pid };

    private static TaskOutcomeIssue Issue(string summary) =>
        new() { Kind = "watchdog-timeout", Summary = summary };

    [Fact]
    public void Active_when_slot_is_occupied_carries_pid()
    {
        var facts = new RunActivityFacts(SlotActive: true, BackoffUntil: null, ConsecutiveFailures: 0);

        var result = TaskRunActivityClassifier.Classify(facts, Exec("running", pid: 4242), null, Now);

        Assert.Equal(TaskRunActivityKinds.Active, result.Kind);
        Assert.Equal(4242, result.ProcessId);
        Assert.Null(result.BackoffUntil);
    }

    [Fact]
    public void Reattached_slot_is_distinctly_visible_as_continuing_after_restart()
    {
        var facts = new RunActivityFacts(
            SlotActive: true,
            BackoffUntil: null,
            ConsecutiveFailures: 0,
            ContinuingAfterRestart: true);

        var result = TaskRunActivityClassifier.Classify(facts, Exec("running", pid: 2780), null, Now);

        Assert.Equal(TaskRunActivityKinds.ContinuingAfterRestart, result.Kind);
        Assert.Equal(2780, result.ProcessId);
    }

    [Fact]
    public void Active_wins_even_when_a_backoff_is_also_armed()
    {
        // A live slot is the authoritative "occupies a slot / PID lives" signal
        // and must outrank a stale backoff deadline.
        var facts = new RunActivityFacts(true, Now.AddMinutes(5), ConsecutiveFailures: 2);

        var result = TaskRunActivityClassifier.Classify(facts, Exec("running", pid: 7), null, Now);

        Assert.Equal(TaskRunActivityKinds.Active, result.Kind);
    }

    [Fact]
    public void Active_when_execution_running_even_though_slot_registry_lost_it()
    {
        // ASS-1753: after a backend restart the in-memory slot registry can be
        // empty (SlotActive=false) while the CLI router still tracks a live
        // "running" execution. Trusting only the slot would paint the running
        // task as "no active run"; the live execution signal must promote it
        // back to Active and surface the pid.
        var facts = new RunActivityFacts(SlotActive: false, BackoffUntil: null, ConsecutiveFailures: 0);

        var result = TaskRunActivityClassifier.Classify(facts, Exec("running", pid: 5151), null, Now);

        Assert.Equal(TaskRunActivityKinds.Active, result.Kind);
        Assert.Equal(5151, result.ProcessId);
        Assert.Null(result.BackoffUntil);
    }

    [Fact]
    public void Active_from_running_execution_outranks_a_stale_backoff()
    {
        // A live "running" execution is authoritative even if a stale backoff
        // deadline is still armed from an earlier failed attempt.
        var facts = new RunActivityFacts(false, Now.AddMinutes(5), ConsecutiveFailures: 2);

        var result = TaskRunActivityClassifier.Classify(facts, Exec("running", pid: 8), null, Now);

        Assert.Equal(TaskRunActivityKinds.Active, result.Kind);
        Assert.Equal(8, result.ProcessId);
    }

    [Fact]
    public void FailedBackoff_when_backoff_in_future_and_no_slot()
    {
        var until = Now.AddSeconds(60);
        var facts = new RunActivityFacts(false, until, ConsecutiveFailures: 2);

        var result = TaskRunActivityClassifier.Classify(facts, Exec("failed"), Issue("git push rejected"), Now);

        Assert.Equal(TaskRunActivityKinds.FailedBackoff, result.Kind);
        Assert.Equal(until, result.BackoffUntil);
        Assert.Equal(2, result.Attempt);
        Assert.Equal("git push rejected", result.LastError);
        Assert.Null(result.ProcessId);
    }

    [Fact]
    public void NoActiveRun_when_backoff_has_expired_and_nothing_failed()
    {
        // Expired backoff with no failure evidence (e.g. the deadline elapsed
        // and the streak was reset) is just an idle, pick-able task.
        var facts = new RunActivityFacts(false, Now.AddSeconds(-1), ConsecutiveFailures: 0);

        var result = TaskRunActivityClassifier.Classify(facts, null, null, Now);

        Assert.Equal(TaskRunActivityKinds.NoActiveRun, result.Kind);
        Assert.Null(result.BackoffUntil);
    }

    [Fact]
    public void NoActiveRun_for_orphan_after_backend_restart()
    {
        // A restart clears every in-memory dict (no slot, no backoff, zero
        // failures) and the orphaned execution record is gone (null), so the
        // classifier reports an orphan awaiting re-pickup.
        var facts = new RunActivityFacts(false, null, ConsecutiveFailures: 0);

        var result = TaskRunActivityClassifier.Classify(facts, execution: null, outcomeIssue: null, Now);

        Assert.Equal(TaskRunActivityKinds.NoActiveRun, result.Kind);
        Assert.Equal(0, result.Attempt);
        Assert.Null(result.ProcessId);
        Assert.Null(result.BackoffUntil);
    }

    [Fact]
    public void FailedIdle_when_execution_failed_but_no_backoff_or_slot()
    {
        var facts = new RunActivityFacts(false, null, ConsecutiveFailures: 0);

        var result = TaskRunActivityClassifier.Classify(facts, Exec("failed"), Issue("missing sentinel"), Now);

        Assert.Equal(TaskRunActivityKinds.FailedIdle, result.Kind);
        Assert.Equal("missing sentinel", result.LastError);
    }

    [Fact]
    public void FailedIdle_when_failure_streak_recorded_without_live_execution()
    {
        var facts = new RunActivityFacts(false, null, ConsecutiveFailures: 1);

        var result = TaskRunActivityClassifier.Classify(facts, execution: null, outcomeIssue: null, Now);

        Assert.Equal(TaskRunActivityKinds.FailedIdle, result.Kind);
        Assert.Equal(1, result.Attempt);
    }

    [Fact]
    public void LastError_is_null_when_outcome_summary_is_blank()
    {
        var facts = new RunActivityFacts(false, null, ConsecutiveFailures: 1);

        var result = TaskRunActivityClassifier.Classify(facts, Exec("failed"), Issue("   "), Now);

        Assert.Null(result.LastError);
    }

    [Fact]
    public void ProcessId_is_omitted_for_non_active_kinds()
    {
        var facts = new RunActivityFacts(false, Now.AddSeconds(30), ConsecutiveFailures: 1);

        // Even if a stale execution carries a pid, only an active slot surfaces it.
        var result = TaskRunActivityClassifier.Classify(facts, Exec("failed", pid: 999), null, Now);

        Assert.Equal(TaskRunActivityKinds.FailedBackoff, result.Kind);
        Assert.Null(result.ProcessId);
    }
}
