// clock-independent: every date here is a pure policy input; the policies under test read no clock.
using AgentStudio.Operator;
using AgentStudio.Pipeline;
using AgentStudio.Runner;
using AgentStudio.Shared;
using AgentStudio.Tasks;
using AgentStudio.Watcher;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Direct matrix tests of the operator decision stream policies (operator
/// agent level OP-2): which occurrences a sweep publishes, which events a
/// reader sees, how the watcher lease decides, and how existing records map
/// to the eleven event types.
/// </summary>
public sealed class OperatorDecisionPolicyTests
{
    private static readonly DateTime At = new(2026, 11, 4, 9, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan LeaseTimeout = TimeSpan.FromSeconds(90);

    [Fact]
    public void Diff_publishes_a_new_occurrence_once_and_stays_quiet_while_it_lasts()
    {
        var fact = Fact(OperatorDecisionTypes.EscalationOpened, "escalation.opened|alpha|A-1|t0");
        var observed = Types(OperatorDecisionTypes.EscalationOpened);

        var first = OperatorDecisionPolicy.Diff(Keys(), [fact, fact], observed);
        var second = OperatorDecisionPolicy.Diff(first.Published, [fact], observed);

        Assert.Single(first.Publish);
        Assert.Empty(second.Publish);
        Assert.Equal([fact.Key], second.Published);
    }

    [Fact]
    public void Diff_forgets_a_fact_that_ended_so_its_return_is_published_again()
    {
        var fact = Fact(OperatorDecisionTypes.HumanReviewReady, "human-review.ready|alpha|A-1|t0");
        var observed = Types(OperatorDecisionTypes.HumanReviewReady);

        var ended = OperatorDecisionPolicy.Diff(Keys(fact.Key), [], observed);
        var back = OperatorDecisionPolicy.Diff(ended.Published, [fact], observed);

        Assert.Empty(ended.Published);
        Assert.Single(back.Publish);
    }

    [Fact]
    public void Diff_keeps_published_keys_of_a_type_whose_source_failed()
    {
        var red = "batch.red|b1|t0";
        var escalated = "escalation.opened|alpha|A-1|t0";

        // The batch source failed: its type is not observed, so its key survives.
        var diff = OperatorDecisionPolicy.Diff(Keys(red, escalated), [], Types(OperatorDecisionTypes.EscalationOpened));

        Assert.Equal([red], diff.Published);
        Assert.Empty(diff.Publish);
    }

    [Theory]
    [InlineData(true, "alpha", true)]
    [InlineData(true, null, true)]
    [InlineData(false, "alpha", true)]
    [InlineData(false, "beta", false)]
    [InlineData(false, null, false)]
    public void Readers_see_only_events_of_their_projects(bool unrestricted, string? project, bool shown)
    {
        var reader = new OperatorDecisionReader(unrestricted, name => name == "alpha");
        Assert.Equal(shown, OperatorDecisionPolicy.Shows(Event(OperatorDecisionTypes.BatchRed, project), reader));
    }

    [Fact]
    public void Every_reader_sees_the_installation_watcher_heartbeat()
    {
        var scoped = new OperatorDecisionReader(false, _ => false);
        Assert.True(OperatorDecisionPolicy.Shows(Event(OperatorDecisionTypes.WatcherHeartbeat, null), scoped));
    }

    [Fact]
    public void Lease_is_granted_when_none_is_active_and_conflicts_while_one_is()
    {
        var holder = Lease("wl_1", "service-account:a", At);

        Assert.Equal(WatcherLeaseOutcome.Granted, WatcherLeasePolicy.Request(null, false, At, LeaseTimeout).Outcome);
        var conflict = WatcherLeasePolicy.Request(holder, false, At.AddSeconds(30), LeaseTimeout);
        Assert.Equal(WatcherLeaseOutcome.Conflict, conflict.Outcome);
        Assert.Same(holder, conflict.Current);
        Assert.Equal(WatcherLeaseOutcome.TakenOver, WatcherLeasePolicy.Request(holder, true, At.AddSeconds(30), LeaseTimeout).Outcome);
    }

    [Fact]
    public void An_expired_lease_is_granted_again_without_a_take_over()
    {
        var stale = Lease("wl_1", "service-account:a", At);
        Assert.Equal(WatcherLeaseOutcome.Granted,
            WatcherLeasePolicy.Request(stale, false, At + LeaseTimeout, LeaseTimeout).Outcome);
    }

    [Theory]
    [InlineData("wl_1", "service-account:a", WatcherHeartbeatOutcome.Renewed)]
    [InlineData("wl_1", "service-account:b", WatcherHeartbeatOutcome.Lost)]
    [InlineData("wl_2", "service-account:a", WatcherHeartbeatOutcome.Lost)]
    public void Only_the_holder_renews_its_lease(string leaseId, string actor, WatcherHeartbeatOutcome expected)
        => Assert.Equal(expected, WatcherLeasePolicy.Heartbeat(Lease("wl_1", "service-account:a", At), leaseId, actor));

    [Fact]
    public void Task_records_map_to_lane_decision_and_quota_events()
    {
        var tasks = new[]
        {
            Card("A-1", TaskStates.Escalated) with { ParkedBlocker = Blocker("gate failed twice") },
            Card("A-2", TaskStates.HumanReview),
            Card("A-3", TaskStates.Preparation) with
            {
                Kind = TaskKinds.Decision,
                Decision = new DecisionContent { Question = "Which store?", Status = DecisionStatuses.Pending },
            },
            Card("A-4", TaskStates.Preparation) with
            {
                Kind = TaskKinds.Decision,
                Decision = new DecisionContent { Question = "Settled", Status = DecisionStatuses.Decided },
            },
            Card("A-5", TaskStates.Ready) with { QuotaWait = new QuotaWaitStatus("claude", At, At.AddHours(2), 30, "weekly limit") },
            Card("A-6", TaskStates.Progress),
            Card("A-7", TaskStates.Ready),
        };

        var facts = OperatorDecisionFacts.FromTasks(tasks,
            task => task.Id == "A-6" ? new QuotaFallbackStatus("codex", "fallback-model", "primary exhausted", At) : null);

        Assert.Equal(
            [
                (OperatorDecisionTypes.EscalationOpened, "A-1"), (OperatorDecisionTypes.HumanReviewReady, "A-2"),
                (OperatorDecisionTypes.DecisionCardOpened, "A-3"), (OperatorDecisionTypes.QuotaExhausted, "A-5"),
                (OperatorDecisionTypes.QuotaFallbackActive, "A-6"),
            ],
            facts.Select(fact => (fact.Type, fact.CardKey!)).ToArray());
        Assert.All(facts, fact => Assert.Equal("alpha", fact.Project));
        Assert.Equal("gate failed twice", facts[0].Reason);
        Assert.Equal("/api/tasks/A-1", facts[0].Evidence[0].Href);
    }

    [Fact]
    public void A_card_that_escalates_again_is_a_new_occurrence()
    {
        var first = OperatorDecisionFacts.FromTasks([Card("A-1", TaskStates.Escalated)], _ => null).Single();
        var again = OperatorDecisionFacts.FromTasks(
            [Card("A-1", TaskStates.Escalated) with { EnteredLaneAt = At.AddHours(3) }], _ => null).Single();
        Assert.NotEqual(first.Key, again.Key);
    }

    [Fact]
    public void Review_attention_carries_title_grade_concerns_and_changed_brief()
    {
        var folder = Path.Combine(Path.GetTempPath(), "attention-grade-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "code-review-grade-2026-11-04T09-00-00-000Z.md"),
                "## Reviewer reply\n[[CODE_REVIEW_FINDING: text=Security gap ]]\n"
                + "[[CODE_REVIEW_FINDING: text=Missing test ]]\n");
        var graded = Card("A-2", TaskStates.HumanReview) with
        {
            Title = "Repair account flow",
            FolderPath = folder, Tags = ["code-review:grade-b"],
        };
        var changed = Card("A-3", TaskStates.HumanReview) with
        {
            Title = "Updated brief",
            OlderBriefDelivery = new("attempt", "old", "new", null, null, null, null, At),
        };
        var facts = OperatorDecisionFacts.FromTasks([graded, changed], _ => null);

        Assert.Equal("Repair account flow", facts[0].Title);
        Assert.Equal("Grade B · 2 concerns.", facts[0].Reason);
        Assert.Equal("Cannot be accepted: brief changed.", facts[1].Reason);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData("host-offline", OperatorDecisionTypes.BackendStalled)]
    [InlineData("claim-refusal", OperatorDecisionTypes.BackendStalled)]
    [InlineData("batch-gate-stuck", OperatorDecisionTypes.BackendStalled)]
    [InlineData("idle-capacity", OperatorDecisionTypes.ProgressIdleWithReady)]
    [InlineData("fleet-claim-stall", OperatorDecisionTypes.ProgressIdleWithReady)]
    [InlineData("lane-stall", null)]
    public void Open_watcher_stall_cases_map_by_their_stall_kind(string kind, string? type)
    {
        var facts = OperatorDecisionFacts.FromWatcherCases([Case(kind, "alpha", WatcherCaseStates.Open)]);
        Assert.Equal(type, facts.SingleOrDefault()?.Type);
    }

    [Fact]
    public void Resolved_cases_are_not_published_and_workspace_cases_name_no_project()
    {
        Assert.Empty(OperatorDecisionFacts.FromWatcherCases([Case("host-offline", "alpha", WatcherCaseStates.Resolved)]));
        var workspace = OperatorDecisionFacts.FromWatcherCases(
            [Case("fleet-claim-stall", QuotaProbeSignalProbe.WorkspaceProject, WatcherCaseStates.DecisionRequired)]).Single();
        Assert.Null(workspace.Project);
    }

    [Fact]
    public void A_red_batch_is_isolating_while_a_diagnostic_child_runs()
    {
        var parent = Manifest("parent", null, "A-1", "A-2");
        var running = Manifest("child-1", "parent", "A-1");
        var done = Manifest("child-2", "parent", "A-2");

        var isolating = OperatorDecisionFacts.FromBatches(
            [(parent, State("parent", BatchPhase.Red)), (running, State("child-1", BatchPhase.Running)), (done, State("child-2", BatchPhase.Green))]);
        var finished = OperatorDecisionFacts.FromBatches(
            [(parent, State("parent", BatchPhase.Red)), (running, State("child-1", BatchPhase.Red))]);

        Assert.Equal([OperatorDecisionTypes.BatchRed, OperatorDecisionTypes.BatchIsolating], isolating.Select(fact => fact.Type));
        Assert.Equal(["A-1", "A-2"], isolating[0].Cards!);
        Assert.Equal("/api/projects/alpha/batch-gate/state", isolating[0].Evidence[0].Href);
        // The finished child is red itself, so it reports as a red batch; nothing isolates any more.
        Assert.Equal([OperatorDecisionTypes.BatchRed, OperatorDecisionTypes.BatchRed], finished.Select(fact => fact.Type));
    }

    [Fact]
    public void Children_of_a_batch_that_is_not_red_do_not_isolate()
    {
        var parent = Manifest("parent", null, "A-1");
        var successor = Manifest("successor", "parent", "A-1");
        Assert.Empty(OperatorDecisionFacts.FromBatches(
            [(parent, State("parent", BatchPhase.Abandoned)), (successor, State("successor", BatchPhase.Running))]));
    }

    [Fact]
    public void Only_pending_pushes_past_the_gate_age_alert_are_overdue()
    {
        var since = new DateTimeOffset(At);
        var facts = OperatorDecisionFacts.FromPendingPushes("alpha",
            [
                new PendingIntegrationPushWait("waiting", "job-1", "abc", since, AgeAlert: true, AgeMinutes: 12.5),
                new PendingIntegrationPushWait("waiting", "job-2", "def", since, AgeAlert: false, AgeMinutes: 1),
            ],
            jobId => jobId == "job-1" ? "A-1" : null);

        var fact = Assert.Single(facts);
        Assert.Equal(OperatorDecisionTypes.PushPendingOverdue, fact.Type);
        Assert.Equal("A-1", fact.CardKey);
        Assert.Contains("12 minutes", fact.Reason);
    }

    [Fact]
    public void Provider_limits_are_installation_wide_and_one_occurrence_per_cli()
    {
        var first = OperatorDecisionFacts.FromProviderLimits([new ProviderLimitStatus("claude", At, At.AddHours(1), "429", true)]).Single();
        var later = OperatorDecisionFacts.FromProviderLimits([new ProviderLimitStatus("claude", At.AddMinutes(5), At.AddHours(2), "429", true)]).Single();

        Assert.Null(first.Project);
        Assert.Equal(first.Key, later.Key);
    }

    [Fact]
    public void Every_type_names_suggested_operations()
        => Assert.All(OperatorDecisionTypes.All, type => Assert.NotEmpty(OperatorOperations.For(type)));

    private static OperatorDecisionFact Fact(string type, string key)
        => new(type, key, "alpha", "A-1", "reason", At, []);

    private static OperatorDecisionEvent Event(string type, string? project)
        => new("opd_1", "1", type, At, project, null, [], "reason", [], [], type + "|x", At);

    private static HashSet<string> Keys(params string[] keys) => new(keys, StringComparer.Ordinal);

    private static HashSet<string> Types(params string[] types) => new(types, StringComparer.Ordinal);

    private static WatcherLease Lease(string id, string actor, DateTime heartbeat) => new(id, actor, "laptop", heartbeat, heartbeat);

    private static TaskInfo Card(string key, string state) => new()
    {
        Id = key, Key = key, ProjectName = "alpha", State = state, EnteredLaneAt = At, CreatedAt = At,
    };

    private static ParkedBlockerStatus Blocker(string reason)
        => new("gate", "manual", "needs a look", At, 0, reason, "blocked", null, "");

    private static WatcherCase Case(string kind, string project, string state) => new()
    {
        Id = "WCH-000001", Fingerprint = "fp-" + kind, DetectorClass = WatcherDetectorClasses.Silence, Project = project,
        State = state, Subject = "host:h1", SinceUtc = At, LastSummary = kind + " observed",
        LastDetails = new Dictionary<string, string> { ["stallKind"] = kind }, AffectedCards = ["A-1"],
    };

    private static BatchGateManifest Manifest(string id, string? parent, params string[] keys)
    {
        var scope = new BatchGateScope("alpha", "repo", "develop", "full", "digest", "v1");
        var members = keys.Select((key, index) => new BatchGateSubject(key, "alpha", "repo", "develop", "full", "digest", "v1",
            "refs/heads/result", new string('a', 40), "run-1", 1, 1, new string('a', 40), true, true, true, true, false, null,
            false, new DateTimeOffset(At), index + 1)).ToArray();
        return new BatchGateManifest(id, scope, new string('a', 40), "digest", new DateTimeOffset(At), members, [], keys, parent);
    }

    private static BatchGateState State(string id, BatchPhase phase)
        => new(id, "digest", phase, null, 1, new DateTimeOffset(At), phase == BatchPhase.Red ? "deterministic suite red" : null);
}
