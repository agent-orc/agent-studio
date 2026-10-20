using AgentStudio.OrchestrationDecisions;
using AgentStudio.Registry;
using AgentStudio.Runner;
using AgentStudio.Tasks;
using AgentStudio.TaskServer.Contracts;
using System.Text.Json;
using AgentStudio.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class DecisionRuleVersionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "decision-rules-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 15, 10, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Activation_pin_and_rollback_are_durable()
    {
        var store = new DecisionRuleVersionStore(_root, _time);
        var first = store.Draft("workspace", null, new(null, "ignored", "initial rules", [Rule("queue") ]));
        Assert.Null(store.ActiveVersionId("workspace"));
        store.Activate("workspace", null, first.VersionId, new("operator", "enable"));
        var second = store.Draft("workspace", null, new(first.VersionId, "operator", "tune", [Rule("queue", "A2")]));
        Assert.Equal(first.VersionId, store.ActiveVersionId("workspace"));
        store.Activate("workspace", null, second.VersionId, new("operator", "enable tune"));
        var rollback = store.Rollback("workspace", null, first.VersionId, new("operator", "restore"));
        Assert.NotEqual(first.VersionId, rollback.VersionId);
        Assert.Equal(second.VersionId, rollback.ParentVersionId);
        var restarted = new DecisionRuleVersionStore(_root, _time);
        Assert.Equal(rollback.VersionId, restarted.ActiveVersionId("workspace"));
        Assert.Equal("A1", restarted.Effective("workspace")!.Rules.Single().AutonomyClass);
        Assert.Equal(3, restarted.View("workspace").Activations.Count);
    }

    [Fact]
    public void Dangerous_edits_are_refused_before_a_version_is_written()
    {
        var store = new DecisionRuleVersionStore(_root, _time);
        var bad = new[]
        {
            Rule("floor.fence"), Rule("raw") with { FactId = "raw-session.log" },
            Rule("weak") with { Model = "gpt-5.6-luna" },
            Rule("loop") with { FreshnessMinutes = int.MaxValue },
        };
        var error = Assert.Throws<ArgumentException>(() => store.Draft("workspace", null,
            new(null, "operator", "try dangerous edits", bad)));
        Assert.Contains("authority floors", error.Message);
        Assert.Contains("trusted correctness floor", error.Message);
        Assert.Empty(store.View("workspace").Versions);
        Assert.Contains("cannot govern host-wide", Assert.Throws<ArgumentException>(() =>
            store.Draft("workspace", "project", new(null, "operator", "host override",
                [Rule("host") with { Trigger = "capacity-check" }]))).Message);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<DecisionRuleV1>(
            """{"id":"unsafe","disableBreaker":true}"""));
    }

    [Fact]
    public void Active_rules_limit_candidates_and_preserve_platform_approval()
    {
        var at = _time.GetUtcNow().UtcDateTime;
        var facts = new[] { new GroundsFactV1("host.queue", "capacity", "Queue", "6", null,
            new("capacity-snapshot", "snapshot:host-1"), at, "complete", GroundsReliability.Reliable) };
        var offered = new[]
        {
            new GroundsAlternativeV1(DecisionActionKinds.Observe, "host", "reversible", "A0", true),
            new GroundsAlternativeV1(DecisionActionKinds.Adapt, "role-share:review", "reversible", "A2", true),
        };
        var rule = Rule("queue") with { CandidateActions = [DecisionActionKinds.Adapt],
            AutonomyClass = "A1" };
        var applied = DecisionRulePolicy.Apply([rule], "capacity-check", "capacity-check", facts,
            offered, at);
        Assert.Equal("rule-candidate-excluded", applied[0].RejectionReason);
        Assert.False(applied[0].Available);
        Assert.True(applied[1].Available);
        Assert.Equal("A2", applied[1].RequiredApproval);

        var humanOnly = DecisionRulePolicy.Apply([rule with { AutonomyClass = "A3" }],
            "capacity-check", "capacity-check", facts, offered, at);
        Assert.Equal("human-operator-required", humanOnly[1].RejectionReason);
    }

    [Fact]
    public void Missing_and_stale_evidence_apply_ask_or_hold()
    {
        var at = _time.GetUtcNow().UtcDateTime;
        var stale = new[] { new GroundsFactV1("host.queue", "capacity", "Queue", "6", null,
            new("capacity-snapshot", "snapshot:host-1"), at.AddMinutes(-31), "complete",
            GroundsReliability.Reliable) };
        var offered = new[] { new GroundsAlternativeV1(DecisionActionKinds.Adapt,
            "role-share:review", "reversible", "A1", true) };
        var ask = DecisionRulePolicy.Apply([Rule("queue")], "capacity-check", "capacity-check",
            stale, offered, at);
        Assert.Equal("rule-missing-evidence-ask", ask[0].RejectionReason);
        Assert.Equal(DecisionActionKinds.Ask, ask[1].Action);
        Assert.True(ask[1].Available);
        var hold = DecisionRulePolicy.Apply([Rule("queue") with { MissingDataBehavior = "hold" }],
            "capacity-check", "capacity-check", stale, offered, at);
        Assert.Single(hold);
        Assert.Equal("rule-missing-evidence-hold", hold[0].RejectionReason);
    }

    [Fact]
    public void Card_free_capacity_receipt_appears_in_host_history_and_workspace_spend()
    {
        var at = _time.GetUtcNow().UtcDateTime;
        var directory = Path.Combine(_root, "receipts");
        var store = new OrchestratorDecisionStore(directory, _time, TokenTestServices.Pricing());
        var fact = new GroundsFactV1("host.queue", "capacity", "Eligible queue", "6", "cards",
            new("capacity-snapshot", "snapshot:host-1"), at, "complete", GroundsReliability.Reliable, true);
        var grounds = new DecisionGroundsInputV1("capacity:host-1:2026-10-15T10:00Z",
            new("capacity-check", "capacity-check", at, "scheduled"),
            new("host", null, null, null, null, null, "host-1", "workspace"),
            null, null, "drv_pinned", new("codex", "gpt-5.6-sol", "high",
                DecisionModelCustody.RunnerHostJob), [fact],
            [new(DecisionActionKinds.Adapt, "role-share:review", "reversible", "A1", true),
                new(DecisionActionKinds.Defer, "intake:15m", "reversible", "A1", true)],
            [], new CardRunLedgerV1(0, 0, 0, DecisionTokenUsageV1.Zero, 0,
                DecisionTokenUsageV1.Zero, 0, 0, 0, [], DecisionPriceStatuses.NoUsage, "host", []),
            new("host.v1", "No card rounds", null, 0, 0, 0), []);
        store.FreezeInputs(new(grounds, DecisionActionKinds.Observe, "host-1", "host occurrence",
            DecisionReceiptPolicy.ActiveMode));
        var frozen = DecisionGroundsPolicy.Freeze(grounds);
        Assert.Equal("drv_pinned", frozen.PolicyVersion);
        Assert.Equal(DecisionGroundsPolicy.MaxCapacityPackTokens, frozen.Pack.MaxTokens);
        var job = store.CreateJob(new(grounds.OccurrenceId, "", "", 1, frozen.ContentDigest,
            DecisionReceiptPolicy.RenderPrompt(frozen), "codex", "gpt-5.6-sol", "high", 8_000, 1_000));
        var claim = store.Claim(new("runner-1", "runner-1")).Work!;
        Assert.True(store.Complete(new(claim.JobId, claim.ClaimToken, "runner-1", true,
            """{"chosenActions":[{"action":"adapt","target":"role-share:review"},{"action":"defer","target":"intake:15m"}],"confidence":0.8,"reasoning":"The queue is nonempty.","citedFacts":["host.queue"],"assessment":"Busy","report":"Review pressure is rising."}""",
            "gpt-5.6-sol", [new("call-1", "gpt-5.6-sol", 100, 0, 0, 20, false)],
            at, at.AddSeconds(2), null, false)));
        var receipt = DecisionReceiptPolicy.Build(new(frozen,
            new(DecisionActionKinds.Observe, "host-1", "none"), "host occurrence",
            store.GetJob(job.JobId), 1, false, null, DecisionReceiptPolicy.ActiveMode));
        store.PutReceipt(receipt);
        Assert.Equal(2, receipt.Chosen.Count);
        Assert.Equal("Review pressure is rising.", receipt.Report);
        Assert.Equal(DecisionReceiptPolicy.PeriodicDispatch, receipt.DispatchOutcome);
        var reloaded = new OrchestratorDecisionStore(directory, _time, TokenTestServices.Pricing());
        var host = Assert.Single(reloaded.ListReceipts(null, 10, hostId: "host-1"));
        Assert.Null(host.TaskId);
        Assert.Equal(120, host.TotalTokens);
        Assert.Equal(1, reloaded.SpendSummary(null, null, hostId: "host-1").Occurrences);
        Assert.Equal(1, reloaded.SpendSummary(null, null, workspaceId: "workspace").Occurrences);
        Assert.Empty(reloaded.ListReceipts(null, 10, workspaceId: "other"));
        var rules = new DecisionRuleVersionStore(Path.Combine(_root, "rules"), _time);
        var draft = rules.Draft("workspace", null, new(null, "operator", "check queue", [Rule("queue")]));
        var replay = rules.DryRun("workspace", null, draft.VersionId, [receipt]);
        var row = Assert.Single(replay.Occurrences);
        Assert.Equal("counterfactual-model-result", row.ModelResultLabel);
        Assert.Contains("queue: 6 (warning)", row.NewSignals);
        Assert.Empty(row.OldSignals);
    }

    [Fact]
    public void Empty_candidates_unknown_triggers_and_unreadable_hold_facts_are_refused()
    {
        var codex = DecisionRuleModelCatalogueSource.For("codex");
        var refusals = DecisionRulePolicy.Validate(
        [
            Rule("empty") with { CandidateActions = [] },
            Rule("review") with { Trigger = "review-decision" },
            Rule("batch") with { Trigger = "BatchPlan" },
            Rule("typo") with { Trigger = "capacity-chek" },
            Rule("hold") with { FactId = "host.nonexistent", MissingDataBehavior = "hold" },
            Rule("card-hold") with { Trigger = DecisionRuleTriggers.StepTransition, FactId = "host.queue",
                CandidateActions = ["prepare", "retry"], MissingDataBehavior = "hold" },
        ], false, codex).Refusals;
        Assert.Contains(refusals, value => value.Contains("empty needs at least one candidate action"));
        foreach (var trigger in new[] { "review-decision", "BatchPlan", "capacity-chek" })
            Assert.Contains(refusals, value => value.Contains($"trigger {trigger}, which no live decision path reads"));
        Assert.Contains(refusals, value => value.Contains("host.nonexistent, which is not a known fact of capacity-check"));
        Assert.Contains(refusals, value => value.Contains("host.queue, which is not a known fact of step-transition"));
        Assert.True(DecisionRulePolicy.Validate([Rule("known") with { MissingDataBehavior = "hold" }], false, codex).Accepted);
    }

    [Fact]
    public void Ask_and_hold_rules_read_only_facts_observed_at_the_occurrence()
    {
        var codex = DecisionRuleModelCatalogueSource.For("codex");
        var card = Rule("card") with { Trigger = DecisionRuleTriggers.CodingCompletion,
            FactId = "ledger.tokens", CandidateActions = ["prepare", "retry"] };
        var refusals = DecisionRulePolicy.Validate(
        [
            Rule("ask-unknown") with { FactId = "host.nonexistent", MissingDataBehavior = "ask" },
            card with { Id = "card-ask-unknown", FactId = "card.nonexistent", MissingDataBehavior = "ask" },
            card with { Id = "review-ask", FactId = "review.last-outcome", MissingDataBehavior = "ask" },
            card with { Id = "review-hold", FactId = "review.last-outcome", MissingDataBehavior = "hold" },
        ], false, codex).Refusals;
        Assert.Contains(refusals, value => value.Contains(
            "Ask rule ask-unknown reads host.nonexistent, which is not a known fact of capacity-check"));
        Assert.Contains(refusals, value => value.Contains(
            "Ask rule card-ask-unknown reads card.nonexistent, which is not a known fact of CodingCompletion"));
        Assert.Contains(refusals, value => value.Contains("Ask rule review-ask reads review.last-outcome")
            && value.Contains("usually stale"));
        Assert.Contains(refusals, value => value.Contains("Hold rule review-hold reads review.last-outcome")
            && value.Contains("usually stale"));
        // A signal rule may still observe the latest review outcome.
        Assert.True(DecisionRulePolicy.Validate(
            [card with { FactId = "review.last-outcome", MissingDataBehavior = "signal" }], false, codex).Accepted);
    }

    [Fact]
    public void Card_fact_vocabulary_matches_a_frozen_coding_completion_occurrence()
    {
        const string project = "agent-studio";
        const string card = "AGT-FACTS";
        var projectPath = Path.Combine(_root, "projects", project);
        var folder = Path.Combine(projectPath, TaskStates.Progress, card);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
        {
            id = card, title = "Coding completion facts", state = TaskStates.Progress,
            order = 1, agent = "codex", kind = TaskKinds.Task,
        }));
        File.WriteAllText(Path.Combine(folder, "prompt.md"), "Freeze the coding completion facts.");
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["TaskRepository"] = _root,
                    ["WatchPaths:0:Name"] = project,
                    ["WatchPaths:0:Path"] = projectPath,
                    ["WatchPaths:0:RootPath"] = projectPath,
                    ["ReviewDecisionOrchestrator:Enabled"] = "false",
                    ["Runner:RunLiveness:Enabled"] = "false",
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(_time);
            });
        });
        var services = factory.Services;
        var task = services.GetRequiredService<TaskScannerService>().FindJob(card, projectPath)!;
        var at = _time.GetUtcNow().UtcDateTime;
        var grounds = services.GetRequiredService<DecisionGroundsCollector>().CodingCompletion(new(
            "orch_facts", task, "attempt-1", 1, 1, "Done", TaskStates.Progress, new string('c', 40),
            new string('b', 40), "refs/agent-studio/results/attempt-1", true,
            new TransitionDecision(TransitionAction.CreateReview, "settled",
                new DecisionReceipt("facts", "", "", 0, 0, []), [], []), at));

        Assert.Equal(DecisionRuleTriggers.CodingCompletionFacts.Order(StringComparer.Ordinal),
            grounds.Facts.Select(fact => fact.Id).Order(StringComparer.Ordinal));
        // Every fact an ask or hold rule may read is observed at the occurrence, so it is fresh.
        Assert.All(grounds.Facts.Where(fact => DecisionRuleTriggers.EvidenceFacts(
                DecisionRuleTriggers.CodingCompletion).Contains(fact.Id)),
            fact => Assert.Equal(at, fact.ObservedAt));
    }

    [Fact]
    public void Card_rules_must_leave_a_non_ask_coding_completion_alternative()
    {
        var codex = DecisionRuleModelCatalogueSource.For("codex");
        var card = Rule("card") with { Trigger = DecisionRuleTriggers.CodingCompletion,
            FactId = "ledger.tokens", CandidateActions = ["prepare", "retry"] };
        Assert.True(DecisionRulePolicy.Validate([card], false, codex).Accepted);
        foreach (var blocking in new[]
                 {
                     card with { CandidateActions = ["observe"] },
                     card with { CandidateActions = ["ask"] },
                     card with { AutonomyClass = "A3" },
                     card with { AutonomyClass = "A2" },
                 })
            Assert.Contains(DecisionRulePolicy.Validate([blocking], false, codex).Refusals,
                value => value.Contains("no available non-ask CodingCompletion alternative"));

        var store = new DecisionRuleVersionStore(_root, _time);
        var workspace = store.Draft("workspace", null, new(null, "operator", "card rules", [card]));
        store.Activate("workspace", null, workspace.VersionId, new("operator", "enable"));
        // An overlay that replaces the only card rule with an unavailable action
        // would send every card of that project to ask.
        var error = Assert.Throws<ArgumentException>(() => store.Draft("workspace", "project",
            new(null, "operator", "overlay", [card with { CandidateActions = ["halt"] }])));
        Assert.Contains("Project overlay project", error.Message);
        // Checkpoint needs a verified tree, which a first delivery does not have.
        var checkpoint = Assert.Throws<ArgumentException>(() => store.Draft("workspace", "project",
            new(null, "operator", "overlay", [card with { CandidateActions = ["checkpoint"] }])));
        Assert.Contains("Project overlay project", checkpoint.Message);
        Assert.Contains("no verified tree", checkpoint.Message);
        // An overlay that adds a checkpoint rule keeps the workspace retry rule in the merged set.
        var overlay = store.Draft("workspace", "project", new(null, "operator", "overlay",
            [card with { Id = "card-checkpoint", CandidateActions = ["checkpoint"] }]));
        store.Activate("workspace", "project", overlay.VersionId, new("operator", "enable overlay"));
    }

    [Fact]
    public void Card_versions_that_need_a_settled_envelope_or_verified_tree_are_refused()
    {
        var codex = DecisionRuleModelCatalogueSource.For("codex");
        var card = Rule("card") with { Trigger = DecisionRuleTriggers.StepTransition,
            FactId = "run.outcome", CandidateActions = ["retry"] };
        Assert.True(DecisionRulePolicy.Validate([card], false, codex).Accepted);
        foreach (var only in new[] { "prepare", "checkpoint", "adapt" })
            Assert.Contains(DecisionRulePolicy.Validate([card with { CandidateActions = [only] }], false, codex).Refusals,
                value => value.Contains("no available non-ask CodingCompletion alternative"));
        Assert.Contains(DecisionRulePolicy.Validate([card with { CandidateActions = ["prepare"] }], false, codex)
            .Refusals, value => value.Contains("the envelope is not settled"));
        // Retry keeps a non-ask alternative even when the deterministic outcome asks.
        Assert.Null(DecisionRulePolicy.CardAlternativeRefusal([card with { CandidateActions = ["prepare", "retry"] }]));
    }

    [Fact]
    public void Rule_models_must_be_served_by_the_configured_cli_at_the_catalogue_floor()
    {
        var codex = DecisionRuleModelCatalogueSource.For("codex");
        Assert.Null(DecisionRulePolicy.ModelRefusal("gpt-6.1-sol", codex));
        Assert.Null(DecisionRulePolicy.ModelRefusal("gpt-6-sol", codex));
        Assert.Contains("cannot serve", DecisionRulePolicy.ModelRefusal("claude-opus-5", codex));
        Assert.Contains("below the trusted correctness floor", DecisionRulePolicy.ModelRefusal("gpt-6-luna", codex));
        Assert.Contains("does not know", DecisionRulePolicy.ModelRefusal("gpt-unknown-1", codex));
        var claude = DecisionRuleModelCatalogueSource.For("claude");
        Assert.Null(DecisionRulePolicy.ModelRefusal("claude-opus-5", claude));
        Assert.Contains("cannot serve", DecisionRulePolicy.ModelRefusal("gpt-6.1-sol", claude));

        // The route keeps the configured CLI, thinking level and alternate retry.
        var configured = new DecisionModelRouteV1("codex", "gpt-6-sol", "medium", DecisionModelCustody.RunnerHostJob,
            "claude", "claude-opus-5", "high");
        var route = DecisionGroundsCollector.RuleRoute(configured,
            [Rule("unserved") with { Model = "claude-opus-5" }, Rule("served") with { Model = "gpt-6.1-sol" }],
            codex, "capacity-check");
        Assert.Equal(configured with { Model = "gpt-6.1-sol" }, route);
    }

    [Fact]
    public void A_truncated_rule_file_is_quarantined_without_failing_the_store()
    {
        var store = new DecisionRuleVersionStore(_root, _time);
        var version = store.Draft("workspace", null, new(null, "operator", "initial rules", [Rule("queue")]));
        store.Activate("workspace", null, version.VersionId, new("operator", "enable"));
        var path = Path.Combine(_root, "version-" + version.VersionId + ".json");
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..(bytes.Length / 2)]);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));

        var restarted = new DecisionRuleVersionStore(_root, _time);
        var view = restarted.View("workspace");
        Assert.True(view.Degraded);
        Assert.Equal(["version-" + version.VersionId + ".json"], view.Quarantined);
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(Path.Combine(_root, DecisionRuleVersionStore.QuarantineFolder)));
        Assert.Empty(restarted.EffectiveRules("workspace"));
        Assert.False(new DecisionRuleVersionStore(_root, _time).View("workspace").Degraded);
    }

    [Fact]
    public void Card_prompt_stays_byte_identical_and_host_prompt_is_versioned_separately()
    {
        var at = _time.GetUtcNow().UtcDateTime;
        var host = DecisionGroundsPolicy.Freeze(new DecisionGroundsInputV1("capacity:host-1",
            new("capacity-check", "capacity-check", at, "scheduled"),
            new("host", null, null, null, null, null, "host-1", "workspace"), null, null, "platform.v1",
            new("codex", "gpt-6-sol", "medium", DecisionModelCustody.RunnerHostJob), [], [], [],
            new CardRunLedgerV1(0, 0, 0, DecisionTokenUsageV1.Zero, 0, DecisionTokenUsageV1.Zero, 0, 0, 0, [],
                DecisionPriceStatuses.NoUsage, "host", []), new("host.v1", "No card rounds", null, 0, 0, 0), []));
        var card = host with { Scope = new("card", "Project", "AGT-1", "attempt-1", 1, 1, null) };
        var cardPrompt = DecisionReceiptPolicy.RenderPrompt(card);
        Assert.StartsWith("You decide the next typed action of one orchestrator transition in Agent Studio.\n"
            + "Read only the frozen decision grounds below. Do not run tools, read files or query sessions.\n"
            + "Choose exactly one action from the alternatives whose \"available\" is true.", cardPrompt);
        Assert.Contains("\"uncertainty\":\"<missing evidence or none>\"}\n\nDecision grounds", cardPrompt);
        Assert.DoesNotContain("assessment", cardPrompt);
        Assert.Equal(DecisionReceiptPolicy.PromptVersion, DecisionReceiptPolicy.PromptVersionFor(card));
        Assert.Equal("od-s1.step-transition.v1", DecisionReceiptPolicy.PromptVersion);
        var hostPrompt = DecisionReceiptPolicy.RenderPrompt(host);
        Assert.StartsWith("You decide one bounded orchestrator occurrence in Agent Studio.", hostPrompt);
        Assert.Contains("\"report\":\"<periodic report, at most 600 characters>\"", hostPrompt);
        Assert.Equal(DecisionReceiptPolicy.HostPromptVersion, DecisionReceiptPolicy.PromptVersionFor(host));
    }

    [Fact]
    public void Host_occurrence_admission_reserves_waiting_occurrences_per_cadence()
    {
        var at = _time.GetUtcNow().UtcDateTime;
        var store = new OrchestratorDecisionStore((string?)null, _time, TokenTestServices.Pricing());
        DecisionShadowInputV1 Input(string id, string cadence, DateTime? observedAt = null) => new(new DecisionGroundsInputV1(id,
                new("capacity-check", "capacity-check", observedAt ?? at, cadence),
                new("host", null, null, null, null, null, "host-1", "workspace"), null, null, "platform.v1",
                new("codex", "gpt-6-sol", "medium", DecisionModelCustody.RunnerHostJob), [], [], [],
                new CardRunLedgerV1(0, 0, 0, DecisionTokenUsageV1.Zero, 0, DecisionTokenUsageV1.Zero, 0, 0, 0, [],
                    DecisionPriceStatuses.NoUsage, "host", []), new("host.v1", "No card rounds", null, 0, 0, 0), []),
            DecisionActionKinds.Observe, "host-1", "host occurrence", DecisionReceiptPolicy.ActiveMode);
        // Waiting occurrences count in the day the plane admitted them, not the day they were observed.
        for (var index = 0; index < DecisionCostCapPolicy.MaxEventCallsPerHostDay; index++)
            Assert.Equal(at, store.FreezeHostInputs(Input($"event-{index}", "event",
                index % 2 == 0 ? at.AddDays(-1) : null)).Input!.AdmittedAt);
        var refused = store.FreezeHostInputs(Input("event-over", "event"));
        Assert.Null(refused.Input);
        Assert.Contains("12 event decision calls", refused.CapReason);
        Assert.Null(store.GetInputs("event-over"));
        Assert.NotNull(store.FreezeHostInputs(Input("event-0", "event")).Input);
        // Waiting occurrences also reserve their full token envelope in the shared day contingent.
        var scheduled = 0;
        while (store.FreezeHostInputs(Input($"scheduled-{scheduled}", "scheduled")).Input is not null) scheduled++;
        Assert.Equal((int)(DecisionCostCapPolicy.MaxInputTokensPerHostDay / DecisionGroundsPolicy.MaxCallInputTokens)
            - DecisionCostCapPolicy.MaxEventCallsPerHostDay, scheduled);
        Assert.Contains("decision input tokens", store.FreezeHostInputs(Input("scheduled-over", "scheduled")).CapReason);
    }

    [Fact]
    public void Host_occurrence_boundary_requires_cadence_known_facts_and_a_fresh_observation()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var fact = new GroundsFactV1("host.queue", "capacity", "Queue", "6", null,
            new("capacity-snapshot", "snapshot:host-1"), now, "complete", GroundsReliability.Reliable);
        var valid = new HostDecisionOccurrenceRequestV1("capacity:host-1", "workspace", "runner_1",
            "capacity-check", "capacity-check", now, [fact], [], [], "scheduled");
        Assert.Null(OrchestratorDecisionEndpoints.HostOccurrenceRefusal(valid, now));
        Assert.Null(OrchestratorDecisionEndpoints.HostOccurrenceRefusal(valid with { ObservedAt = now.AddMinutes(-10) }, now));
        Assert.Contains("cadence", OrchestratorDecisionEndpoints.HostOccurrenceRefusal(valid with { Cadence = null }, now));
        Assert.Contains("within 10 minutes", OrchestratorDecisionEndpoints.HostOccurrenceRefusal(
            valid with { ObservedAt = now.AddMinutes(-11) }, now));
        Assert.Contains("trigger", OrchestratorDecisionEndpoints.HostOccurrenceRefusal(
            valid with { TriggerKind = "host-wide", Profile = "host-wide" }, now));
        Assert.Contains("vocabulary", OrchestratorDecisionEndpoints.HostOccurrenceRefusal(
            valid with { Facts = [fact with { Id = "filesystem.root" }] }, now));
    }

    [Fact]
    public void Dry_run_matches_receipt_project_names_to_overlay_project_ids()
    {
        var registry = Path.Combine(_root, "registry");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = registry,
        }).Build();
        var projects = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        var project = projects.EnsureProjectForStorage(Path.Combine(registry, "agent-studio"), "Agent Studio",
            "workspace", _time);
        Assert.Equal(project.Id, DecisionRuleEndpoints.ProjectIdOf("Agent Studio", projects));
        Assert.Equal(project.Id, DecisionRuleEndpoints.ProjectIdOf(project.Id, projects));
        Assert.Equal("Unregistered", DecisionRuleEndpoints.ProjectIdOf("Unregistered", projects));
        Assert.Null(DecisionRuleEndpoints.ProjectIdOf(null, projects));
    }

    private static DecisionRuleV1 Rule(string id, string autonomy = "A1")
        => new(id, "capacity-check", "host.queue", "5", ["observe", "adapt"], autonomy,
            "gpt-5.6-sol", "ask", 30);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
