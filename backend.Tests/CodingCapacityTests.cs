using AgentStudio.Cli;
using AgentStudio.Operator;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Shared;
using AgentStudio.Tasks;
using Xunit;

namespace AgentStudio.Tests;

public sealed class CodingCapacityTests
{
    private static readonly DateTime Now = new(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);
    private static TaskInfo Card(string key, string state = TaskStates.Ready, params string[] dependencies)
        => new() { Id = key, Key = key, ProjectName = "studio", State = state,
            References = new() { DependsOn = dependencies.Select(key => new TaskDependencyReference(key)).ToList() } };
    private static CodingCapacityFact Project(TaskInfo[] cards, Func<TaskInfo, IReadOnlyList<CapacityBlock>>? blocks = null)
        => CodingCapacityProjection.Project("studio", cards, TaskReferenceIndex.Build(cards), 12, 0,
            blocks ?? (_ => []));

    [Theory]
    [InlineData("A")]
    [InlineData("a")]
    public void Duplicate_card_keys_keep_the_first_card_without_inflating_capacity(string duplicateKey)
    {
        var first = Card("A");
        var stale = Card(duplicateKey, TaskStates.Ready, "MISSING") with { Id = "stale-copy" };
        var evaluated = new List<TaskInfo>();
        var fact = Project([first, stale, Card("B", TaskStates.Ready, "A")], task =>
        {
            evaluated.Add(task);
            return [];
        });

        Assert.Equal((2, 1, 1, 1), (fact.Ready, fact.ClaimableNow, fact.DependencyWidth, fact.MaxConcurrentCards));
        Assert.Equal(new[] { "A", "B" }, fact.Cards.Select(card => card.CardKey));
        Assert.Same(first, evaluated[0]);
        Assert.Equal(2, evaluated.Count);
        Assert.Equal("A", Assert.Single(fact.Heads).CardKey);
        Assert.Equal(1, Assert.Single(fact.Heads).CardsBehind);
    }

    [Fact]
    public void Two_days_of_dispatch_refusals_do_not_look_like_a_healthy_ready_queue()
    {
        var cards = Enumerable.Range(1, 19).Select(i => Card($"AGT-{i}")).ToArray();
        var fact = Project(cards, _ => [new("dispatch-rule", "dispatch-authority-unavailable")]);
        Assert.Equal((12, 19, 0, 19), (fact.FreeSlots, fact.Ready, fact.ClaimableNow, fact.DependencyWidth));
        Assert.All(fact.Cards, card => Assert.Equal("dispatch-rule", Assert.Single(card.Blocks).Class));
        Assert.Contains("dispatch", fact.Cause);
        var tracker = new CapacityIdleTracker();
        Assert.Null(tracker.Observe(fact, [], Now.AddDays(-2)));
        Assert.Contains("dispatch", tracker.Observe(fact, [], Now)!.Reason);
    }

    [Fact]
    public void Nineteen_ready_cards_in_three_chains_prioritize_escalated_and_parked_heads()
    {
        var cards = new List<TaskInfo> { Card("AGT-4011", TaskStates.Escalated),
            Card("AGT-4015", TaskStates.HumanReview) with { ParkedBlocker = new("operator-decision", "manual", "Await owner", Now, 0, "Parked", "blocked", Now, "Await owner") } };
        for (var chain = 0; chain < 3; chain++)
            for (var i = 0; i < (chain == 2 ? 7 : 6); i++)
                cards.Add(Card($"CHAIN-{chain}-{i}", TaskStates.Ready,
                    i > 0 ? [$"CHAIN-{chain}-{i - 1}"] : chain == 0 ? ["AGT-4011"] : chain == 1 ? ["AGT-4015"] : []));
        var fact = Project(cards.ToArray());
        Assert.Equal((19, 1, 3, 3), (fact.Ready, fact.ClaimableNow, fact.DependencyWidth, fact.MaxConcurrentCards));
        Assert.Equal("AGT-4011", fact.Heads[0].CardKey);
        Assert.Equal(6, fact.Heads[0].CardsBehind);
        Assert.Contains("AGT-4011 is escalated and holds 6 cards", fact.Cause);
        Assert.Equal("parked", fact.Heads[1].State);
        Assert.Contains(fact.Cards, card => card.Blocks.Any(block => block.Class == "parked"));
        var tracker = new CapacityIdleTracker();
        Assert.Null(tracker.Observe(fact, [], Now));
        Assert.Contains("AGT-4015 is parked", tracker.Observe(fact, [], Now.AddMinutes(21))!.Reason);
        Assert.Contains(fact.Cards, card => card.Blocks.Any(block => block.Class == "dependency" && block.Dependency == "AGT-4011"));
    }

    [Fact]
    public void Evening_three_cards_and_then_empty_ready_both_expose_unused_slots()
    {
        var fact = Project([Card("A"), Card("B"), Card("C")]);
        Assert.Equal((3, 3), (fact.Ready, fact.ClaimableNow));
        Assert.Contains("3 claimable", fact.Cause);
        var tracker = new CapacityIdleTracker();
        Assert.Null(tracker.Observe(fact, [], Now));
        Assert.Contains("3 claimable", tracker.Observe(fact, [], Now.AddMinutes(21))!.Reason);
        Assert.Equal("Ready is empty; plan more cards.", Project([]).Cause);
    }

    [Fact]
    public void Fork_and_join_width_is_two_even_though_graph_has_one_root()
    {
        var fact = Project([Card("A"), Card("B", TaskStates.Ready, "A"),
            Card("C", TaskStates.Ready, "A"), Card("D", TaskStates.Ready, "B", "C")]);
        Assert.Equal(2, fact.DependencyWidth);
        Assert.Equal(1, fact.ClaimableNow);
    }

    [Fact]
    public void Shared_external_ancestors_are_evaluated_once_per_projection()
    {
        var head = Card("HEAD", TaskStates.Escalated) with { ProjectName = "other" };
        var middle = Card("MIDDLE", TaskStates.HumanReview, "HEAD") with { ProjectName = "other" };
        var ready = Enumerable.Range(0, 20).Select(i => Card($"READY-{i}", TaskStates.Ready, "MIDDLE")).ToArray();
        var references = TaskReferenceIndex.Build(ready.Concat([head, middle]));
        var evaluations = new Dictionary<string, int>();
        var fact = CodingCapacityProjection.Project("studio", ready, references, 12, 0, _ => [], task =>
        {
            evaluations[task.Key!] = evaluations.GetValueOrDefault(task.Key!) + 1;
            return references.EvaluateWaitsOn(task);
        });

        Assert.Equal(20, fact.DependencyWidth);
        Assert.Equal(20, Assert.Single(fact.Heads).CardsBehind);
        Assert.All(evaluations.Values, count => Assert.Equal(1, count));
    }

    [Fact]
    public void Every_applicable_reason_is_preserved()
    {
        var fact = Project([Card("A", TaskStates.Ready, "MISSING")],
            _ => [new("parked", "parked"), new("quota", "waiting for reset"), new("dispatch-rule", "held")]);
        Assert.Equal(new[] { "dependency", "parked", "quota", "dispatch-rule" }, fact.Cards[0].Blocks.Select(block => block.Class));
        Assert.Equal(0, fact.ClaimableNow);
    }

    [Theory]
    [InlineData(QuotaAdmissionOutcome.Wait)]
    [InlineData(QuotaAdmissionOutcome.Throttle)]
    public void Quota_admission_wait_and_throttle_are_quota_blocks(QuotaAdmissionOutcome outcome)
    {
        var card = Card("A");
        var forecast = new CapacityForecast(new SelectionRoute("codex", "gpt-6-sol", "high"),
            new QuotaAdmissionPlan(outcome, "codex", "gpt-6-sol", "high", false, "weekly cap", Now.AddDays(1), null));
        var blocks = CapacityClaimability.Blocks(card, new() { PickupMode = "auto", ExecutionLocation = "runner" },
            [new("runner", "runner", "host", 12, 0, new HashSet<string>())], TaskReferenceIndex.Build([card]),
            new Dictionary<string, TaskIntegrationStatus>(), forecast, new(true, 0, null), null);
        Assert.Contains(blocks, block => block.Class == "quota" && block.Reason == "weekly cap");
    }

    [Fact]
    public void A_dependency_wait_is_not_mislabelled_as_a_dispatch_rule()
    {
        var card = Card("A", TaskStates.Ready, "MISSING");
        var forecast = new CapacityForecast(new SelectionRoute("codex", "gpt-6-sol", "high"),
            new QuotaAdmissionPlan(QuotaAdmissionOutcome.LaunchPrimary, "codex", "gpt-6-sol", "high", false, "healthy", null, null));
        var references = TaskReferenceIndex.Build([card]);
        var blocks = CapacityClaimability.Blocks(card, new() { PickupMode = "auto", ExecutionLocation = "runner" },
            [new("runner", "runner", "host", 12, 0, new HashSet<string>())], references,
            new Dictionary<string, TaskIntegrationStatus>(), forecast, new(true, 0, null), null);
        Assert.Empty(blocks);
        Assert.Equal("dependency", Assert.Single(Project([card], _ => blocks).Cards[0].Blocks).Class);
    }

    [Fact]
    public void A_current_dispatch_admission_supersedes_an_old_rejection_but_manual_pickup_still_blocks()
    {
        var card = Card("A") with { RemoteDispatchRejection = new() { RejectedAtUtc = Now, Code = "old-refusal" } };
        var forecast = new CapacityForecast(new SelectionRoute("codex", "gpt-6-sol", "high"),
            new QuotaAdmissionPlan(QuotaAdmissionOutcome.LaunchPrimary, "codex", "gpt-6-sol", "high", false, "healthy", null, null));
        var blocks = CapacityClaimability.Blocks(card, new() { PickupMode = "manual", ExecutionLocation = "runner" },
            [], TaskReferenceIndex.Build([card]), new Dictionary<string, TaskIntegrationStatus>(), forecast,
            new(true, 0, null), new(true, "admitted"));
        Assert.DoesNotContain(blocks, block => block.Reason.Contains("old-refusal"));
        Assert.Contains(blocks, block => block.Class == "dispatch-rule" && block.Reason.Contains("manual"));
    }

    [Fact]
    public void Weekly_thirty_percent_used_with_one_day_left_projects_sixty_five_percent_left()
    {
        var snapshot = new QuotaSnapshot { CliType = "codex", SubscriptionKey = "subscription-a", FetchedAt = Now,
            Windows = [new() { Label = "Weekly", UsedPct = 30, ResetAt = Now.AddDays(1) }] };
        var row = Assert.Single(CodingCapacityProjection.Subscriptions(new() { Snapshots = [snapshot], TtlSeconds = 600 }, Now));
        Assert.Equal(600d / 7, row.ElapsedPercent!.Value, 4);
        Assert.Equal(65, row.LeftAtResetPercent!.Value, 4);
        Assert.Equal("subscription-a", row.SubscriptionKey);
    }

    [Fact]
    public void Missing_reset_stale_suspicious_or_expired_windows_never_invent_a_projection()
    {
        foreach (var window in new QuotaWindow[] { new() { Label = "Weekly", UsedPct = 30 },
                     new() { Label = "Weekly", UsedPct = 30, ResetAt = Now.AddDays(-1) },
                     new() { Label = "Weekly", UsedPct = 30, ResetAt = Now.AddDays(1), ProjectionSuspiciousReason = "shifted" } })
        {
            var row = Assert.Single(CodingCapacityProjection.Subscriptions(new() { TtlSeconds = 600,
                Snapshots = [new() { FetchedAt = Now, Windows = [window] }] }, Now));
            Assert.Null(row.LeftAtResetPercent);
            if (window.ResetAt is null) Assert.Equal("reset unknown", row.Status);
        }
    }

    [Fact]
    public void Stale_or_failed_subscription_readings_and_early_windows_do_not_raise_leftover_alerts()
    {
        var snapshot = new QuotaSnapshot { CliType = "codex", FetchedAt = Now,
            Windows = [new() { Label = "Weekly", UsedPct = 30, ResetAt = Now.AddDays(1) }] };
        foreach (var excluded in new[] { snapshot with { FetchedAt = Now.AddHours(-1) },
                     snapshot with { Error = "probe failed" }, snapshot with { ProbeFailedAt = Now },
                     snapshot with { Suspicious = true }, snapshot with { Windows = [new() { Label = "Weekly", UsedPct = 0, ResetAt = Now.AddDays(7) }] } })
            Assert.Null(Assert.Single(CodingCapacityProjection.Subscriptions(
                new() { TtlSeconds = 600, Snapshots = [excluded] }, Now)).LeftAtResetPercent);
    }

    [Fact]
    public void Cycles_are_dependency_blocks_and_do_not_invent_runnable_chains()
    {
        var fact = Project([Card("A", TaskStates.Ready, "B"), Card("B", TaskStates.Ready, "A")]);
        Assert.Equal(0, fact.ClaimableNow);
        Assert.Equal(0, fact.DependencyWidth);
        Assert.All(fact.Cards, card => Assert.Contains(card.Blocks, block => block.Reason == "dependency cycle"));
    }

    [Fact]
    public void Host_inventory_excludes_stale_draining_read_only_and_retired_clients()
    {
        ClientIdentity Host(string id) => new() { Id = id, DisplayName = id, RunnerDaemonState = "running",
            LastSeenAt = Now, RunnerDesiredMaxParallelism = 12, RunnerActiveSlots = 5 };
        var hosts = CodingCapacityService.Hosts(Now,
            [Host("live"), Host("stale") with { LastSeenAt = Now.AddMinutes(-11) },
                Host("draining") with { DrainRequestedAt = Now }, Host("read-only") with { RunnerGitStatus = "read-only" },
                Host("retired") with { Kind = ClientIdentityKind.Retired }], []);
        var host = Assert.Single(hosts);
        Assert.Equal(("live", 12, 5), (host.Id, host.Slots, host.Used));
        Assert.True(CodingCapacityService.Assigned(new() { ExecutionLocation = "live" }, host));
        Assert.False(CodingCapacityService.Assigned(new() { ExecutionLocation = "other" }, host));
    }

    [Fact]
    public void Twelve_coding_slots_and_five_active_runs_exclude_review_and_gate_roles_and_duplicate_identity()
    {
        RunnerCapabilitySnapshotDto Role(string id, string capability) => new(id, id, id, "instance", "1", 1,
            "active", Now, Now, new(id, "open", null, null, null, null),
            [new(capability, "runtime", "ready", "healthy", null, Now, Now.AddMinutes(10), true,
                null, null, null, null, 0, null, null, null, [], [])],
            new(Now, null, null, null, null, null, null, null, null, null, null, 16, 5), RoleMaxParallelism: 12);
        var coding = Role("coding", CapabilityProtocol.CodingExecutor);
        var hosts = CodingCapacityService.Hosts(Now,
            [new() { Id = "coding", LastSeenAt = Now, RunnerDaemonState = "running", RunnerDesiredMaxParallelism = 12 }],
            [coding, Role("review", CapabilityProtocol.ReviewExecutor), Role("gate", "gate-executor"),
                coding with { RunnerId = "stale", LastSeenAt = Now.AddMinutes(-11) },
                coding with { RunnerId = "closed", HostAdmission = new("closed", "draining", null, null, null, null) }]);
        var host = Assert.Single(hosts);
        Assert.Equal((12, 5, 7), (host.Slots, host.Used, host.Slots - host.Used));
    }

    [Fact]
    public void Fulfilled_delivery_edges_do_not_reduce_width_and_cross_project_heads_remain_visible()
    {
        var head = Card("HEAD", TaskStates.Escalated) with { ProjectName = "other" };
        var card = Card("A", TaskStates.Ready, "HEAD");
        var references = TaskReferenceIndex.Build([head, card]);
        var fact = CodingCapacityProjection.Project("studio", [card], references, 12, 0, _ => []);
        Assert.Equal("HEAD", Assert.Single(fact.Heads).CardKey);
        Assert.Equal(0, fact.ClaimableNow);
        var delivered = CodingCapacityProjection.Project("studio", [head with { State = TaskStates.Ready }, card],
            references, 12, 0, _ => [], _ => new() { Blocked = false });
        Assert.Equal(2, delivered.DependencyWidth);
        Assert.Equal(2, delivered.ClaimableNow);
    }

    [Fact]
    public void Attention_waits_more_than_twenty_minutes_and_clears_when_full()
    {
        var tracker = new CapacityIdleTracker();
        var fact = Project([]);
        Assert.Null(tracker.Observe(fact, [], Now));
        Assert.Null(tracker.Observe(fact, [], Now.AddMinutes(20)));
        var item = tracker.Observe(fact, [], Now.AddMinutes(21));
        Assert.Equal(Now, item!.ObservedAtUtc);
        Assert.Equal("Ready is empty; plan more cards.", item.Reason);
        Assert.Null(tracker.Observe(fact with { FreeSlots = 0 }, [], Now.AddMinutes(22)));
        Assert.Null(tracker.Observe(fact, [], Now.AddMinutes(23)));
    }

    [Fact]
    public void Quota_leftover_triggers_even_with_enough_claimable_cards()
    {
        var tracker = new CapacityIdleTracker();
        var fact = Project(Enumerable.Range(1, 12).Select(i => Card($"A-{i}")).ToArray());
        CapacitySubscription[] quota = [new("codex", "sub", "Weekly", 30, 85, 65, Now.AddDays(1), "projected")];
        Assert.Null(tracker.Observe(fact, quota, Now));
        var item = tracker.Observe(fact, quota, Now.AddMinutes(21));
        Assert.Contains("65%", item!.Reason);
        Assert.Contains("20%", item.Reason);
    }
}
