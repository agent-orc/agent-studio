using AgentStudio.Runner;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2870, server side. Two defects the 18.09.2026 incident exposed: an
/// operator could not stop a remotely executed run (<c>POST
/// /api/tasks/{key}/stop</c> answered 404 because <c>TaskRunnerService.StopJob</c>
/// only knows local processes), and a card released to Ready after a lost worker
/// carried no continuation, so the next round started the whole task again from
/// the integration branch.
/// </summary>
public sealed class RemoteRunStopAndLostWorkerContinuationTests
{
    private static readonly DateTime Now = new(2026, 9, 18, 7, 45, 0, DateTimeKind.Utc);

    // ---- stop dispatch: the 202 that used to be a 404 -------------------------

    /// <summary>
    /// The observed case: nothing runs locally, a remote runner holds the fenced
    /// lease. The endpoint records the request and answers 202 instead of 404.
    /// </summary>
    [Fact]
    public void Stop_on_a_remotely_executed_run_is_recorded_rather_than_refused()
        => Assert.Equal(
            RunStopDispatch.RemoteStopRequested,
            RemoteRunStopPolicy.Decide(new RunStopFacts(
                LocalProcessStopped: false,
                RemoteLeaseHeld: true)));

    /// <summary>A local run keeps the pre-AGT-2870 behaviour: signalled here, 200.</summary>
    [Fact]
    public void A_local_run_is_still_stopped_directly()
        => Assert.Equal(
            RunStopDispatch.StoppedLocally,
            RemoteRunStopPolicy.Decide(new RunStopFacts(
                LocalProcessStopped: true,
                RemoteLeaseHeld: false)));

    /// <summary>
    /// Without a local process and without a held lease there is genuinely
    /// nothing to stop, which stays a 404. The endpoint must not answer 202 for
    /// a card that is not running.
    /// </summary>
    [Fact]
    public void A_card_that_is_not_running_anywhere_is_still_not_found()
        => Assert.Equal(
            RunStopDispatch.NoRunningAttempt,
            RemoteRunStopPolicy.Decide(new RunStopFacts(
                LocalProcessStopped: false,
                RemoteLeaseHeld: false)));

    // ---- the request the runner collects on its next renewal ------------------

    [Fact]
    public void A_recorded_stop_waits_for_its_runner_and_is_not_consumed_by_peeking()
    {
        var store = new RemoteRunStopRequestStore(() => Now);

        var recorded = store.Record(
            "AGT-2869",
            RemoteRunStopReasons.Followup,
            attemptId: "attempt-1",
            requestedBy: "operator@example.invalid");

        Assert.Equal("AGT-2869", recorded.TaskKey);
        Assert.Equal(Now, recorded.RequestedAtUtc);
        // A runner may miss a heartbeat; the request stays valid until the
        // attempt it belongs to actually hands back.
        Assert.Equal(recorded, store.Peek("AGT-2869"));
        Assert.Equal(recorded, store.Peek("agt-2869"));
        Assert.Equal(recorded, store.Clear("AGT-2869"));
        Assert.Null(store.Peek("AGT-2869"));
    }

    /// <summary>
    /// Pause and Send is what lets a queued <c>/continue</c> start the next round
    /// immediately, so the reason has to survive the round trip verbatim.
    /// </summary>
    [Fact]
    public void Pause_and_send_records_a_followup_stop()
    {
        Assert.Equal(RemoteRunStopReasons.Followup, RemoteRunStopReasons.From(RunStopReason.FollowupPause));
        Assert.True(RemoteRunStopReasons.IsFollowup(RemoteRunStopReasons.Followup));
        Assert.False(RemoteRunStopReasons.IsFollowup(RemoteRunStopReasons.User));
    }

    /// <summary>An unrecognised reason is a plain operator stop, never a rejection.</summary>
    [Fact]
    public void An_unknown_reason_is_recorded_as_a_user_stop()
    {
        Assert.Equal(RemoteRunStopReasons.User, RemoteRunStopReasons.From(RunStopReason.UserStop));
        Assert.Equal(
            RemoteRunStopReasons.User,
            new RemoteRunStopRequestStore(() => Now).Record("AGT-1", "   ").Reason);
    }

    // ---- continuation after a lost worker -------------------------------------

    /// <summary>
    /// AGT-2869's card went back to Ready with an empty <c>pendingIntent</c> and
    /// the next round restarted the task. A lost worker with a salvage is the
    /// same situation a run timeout is - work exists, the run did not finish -
    /// so it gets the same single automatic round.
    /// </summary>
    [Fact]
    public void A_lost_worker_with_a_salvage_gets_one_automatic_continuation()
        => Assert.Equal(
            RunTimeoutSalvageAction.StartContinuation,
            LostWorkerContinuationPolicy.Decide(
                LostWorkerContinuationPolicy.ReleaseOutcome,
                hasSalvageCommit: true,
                automaticContinuationRoundsUsed: 0));

    /// <summary>
    /// The bound is per delivery generation, exactly as AGT-2861 set it: a second
    /// loss escalates with the ref rather than spending another round.
    /// </summary>
    [Fact]
    public void A_second_loss_in_the_same_generation_escalates_instead_of_looping()
        => Assert.Equal(
            RunTimeoutSalvageAction.Escalate,
            LostWorkerContinuationPolicy.Decide(
                LostWorkerContinuationPolicy.ReleaseOutcome,
                hasSalvageCommit: true,
                automaticContinuationRoundsUsed:
                    RunTimeoutSalvageContinuationPolicy.MaxAutomaticContinuationRounds));

    /// <summary>
    /// Without a salvage there is nothing to continue from, so the card follows
    /// its ordinary release path rather than opening an empty round.
    /// </summary>
    [Fact]
    public void A_lost_worker_without_a_salvage_opens_no_continuation()
        => Assert.Equal(
            RunTimeoutSalvageAction.None,
            LostWorkerContinuationPolicy.Decide(
                LostWorkerContinuationPolicy.ReleaseOutcome,
                hasSalvageCommit: false,
                automaticContinuationRoundsUsed: 0));

    /// <summary>Other release outcomes are not this policy's business.</summary>
    [Theory]
    [InlineData("runner-process-missing")]
    [InlineData("authority-deadline-exhausted")]
    [InlineData(null)]
    public void Only_a_worker_lost_release_is_continued(string? outcome)
        => Assert.Equal(
            RunTimeoutSalvageAction.None,
            LostWorkerContinuationPolicy.Decide(
                outcome,
                hasSalvageCommit: true,
                automaticContinuationRoundsUsed: 0));

    /// <summary>
    /// The escalation has to make the manual recovery possible without reading
    /// the host journal: the ref, the rounds already spent, and the worker's own
    /// last line - the observed one being the PAL_SEHException at the task
    /// ceiling.
    /// </summary>
    [Fact]
    public void The_escalation_names_the_salvage_the_rounds_and_the_crash_line()
    {
        var reason = LostWorkerContinuationPolicy.ComposeEscalationReason(
            "terminate called after throwing an instance of 'PAL_SEHException'",
            new RunSalvageReference(
                "agent-studio/salvage/agent-runner-01/AGT-2869/attempt-1/fence-2/8c3c943",
                "8c3c943be0000000000000000000000000000000"),
            automaticContinuationRoundsUsed: 1);

        Assert.Contains("worker was lost before it recorded a result", reason, StringComparison.Ordinal);
        Assert.Contains(
            "agent-studio/salvage/agent-runner-01/AGT-2869/attempt-1/fence-2/8c3c943",
            reason,
            StringComparison.Ordinal);
        Assert.Contains("1 automatic continuation round was already spent", reason, StringComparison.Ordinal);
        Assert.Contains("PAL_SEHException", reason, StringComparison.Ordinal);
    }

    /// <summary>A worker that logged nothing still produces a usable escalation.</summary>
    [Fact]
    public void An_escalation_without_a_crash_line_still_names_the_salvage()
    {
        var reason = LostWorkerContinuationPolicy.ComposeEscalationReason(
            crashLine: null,
            new RunSalvageReference("agent-studio/salvage/r/AGT-1/a/fence-1/abc", "abc"),
            automaticContinuationRoundsUsed: 0);

        Assert.Contains("salvaged as agent-studio/salvage/r/AGT-1/a/fence-1/abc at abc", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("last worker line", reason, StringComparison.Ordinal);
    }

    // ---- the base the next round starts from ---------------------------------

    /// <summary>
    /// The next round's worktree must start from the salvage, not the
    /// integration branch, and exactly once: a later round of the same card must
    /// not silently start on a stale generation's salvage.
    /// </summary>
    [Fact]
    public void The_continuation_base_is_handed_to_the_next_claim_exactly_once()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"agt2870-continuation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var record = new ContinuationBaseRecord(
                "refs/heads/agent-studio/salvage/agent-runner-01/AGT-2869/attempt-1/fence-2/8c3c943",
                "8c3c943be0000000000000000000000000000000",
                LostWorkerContinuationPolicy.ContinuationReason,
                "attempt-1",
                Now);

            Assert.True(ContinuationBaseStore.Save(folder, record));
            Assert.Equal(record.Ref, ContinuationBaseStore.Read(folder)?.Ref);
            Assert.Equal(record.Ref, ContinuationBaseStore.Consume(folder)?.Ref);
            Assert.Null(ContinuationBaseStore.Read(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
