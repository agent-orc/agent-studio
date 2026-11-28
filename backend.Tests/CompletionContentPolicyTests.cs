using AgentStudio.Tasks;
using AgentStudio.Shared;
using Xunit;
using AgentStudio.Runner;
using Microsoft.Extensions.Configuration;

namespace AgentStudio.Tests;

public sealed class CompletionContentPolicyTests
{
    private const string Current = "brief-v2";

    [Fact]
    public void ConceptWithOnlyBuildAndLintCannotComplete()
    {
        var decision = CompletionContentPolicy.Decide(new CompletionContentFacts(
            "concept", Current, Current, Current, "concept-fit", null,
            ["build-tests", "lint"], []));
        Assert.False(decision.Accepted);
        Assert.Contains("no content verdict", decision.Message);
    }

    [Fact]
    public void OlderDeliveryCannotCompleteEvenWithPassedContentReview()
    {
        var decision = CompletionContentPolicy.Decide(new CompletionContentFacts(
            "coding", Current, "brief-v1", Current, "requirement-fit", "pass",
            ["requirement-fit"], []));
        Assert.False(decision.Accepted);
        Assert.Contains("delivered against brief", decision.Message);
    }

    [Fact]
    public void OlderContentVerdictCannotCompleteCurrentDelivery()
    {
        var decision = CompletionContentPolicy.Decide(new CompletionContentFacts(
            "coding", Current, Current, "brief-v1", "requirement-fit", "pass",
            ["requirement-fit"], []));
        Assert.False(decision.Accepted);
        Assert.Contains("content verdict was for brief", decision.Message);
    }

    [Fact]
    public void WrittenOverrideRecordsTheGapAndBothBriefVersions()
    {
        var decision = CompletionContentPolicy.Decide(new CompletionContentFacts(
            "concept", Current, "brief-v1", null, "concept-fit", null,
            ["build-tests", "lint"], [], true, "Accepted after manual review."));
        Assert.True(decision.Accepted);
        Assert.True(decision.Overridden);
        Assert.Contains("brief-v1", decision.Message);
        Assert.Contains(Current, decision.Message);
    }

    [Fact]
    public void CurrentDeliveryAndPassedCurrentContentCanComplete()
    {
        var decision = CompletionContentPolicy.Decide(new CompletionContentFacts(
            "coding", Current, Current, Current, "requirement-fit", "pass",
            ["build-tests", "requirement-fit"], []));
        Assert.True(decision.Accepted);
        Assert.False(decision.Overridden);
    }

    [Fact]
    public void AdmittedStatusCannotHideAnUnfulfilledContentAdmissionObligation()
    {
        var decision = CompletionContentPolicy.Decide(new CompletionContentFacts(
            "coding", Current, Current, Current, "requirement-fit", "pass",
            ["requirement-fit"], [], AdmissionProblem: "Review local-review-1 still owes its linked follow-up."));
        Assert.False(decision.Accepted);
        Assert.Contains("local-review-1 still owes its linked follow-up", decision.Message);
    }

    [Fact]
    public void CodingCardCannotUseConceptFitAsItsContentVerdict()
    {
        var decision = CompletionContentPolicy.Decide(new CompletionContentFacts(
            "coding", Current, Current, Current, "concept-fit", "pass",
            ["concept-fit"], []));
        Assert.False(decision.Accepted);
        Assert.Contains("requirement-fit", decision.Message);
    }

    [Fact]
    public void CurrentConceptDeliveryWithPassedConceptFitCanComplete()
    {
        var decision = CompletionContentPolicy.Decide(new CompletionContentFacts(
            "concept", Current, Current, Current, "concept-fit", "pass",
            ["build-tests", "lint", "concept-fit"], []));
        Assert.True(decision.Accepted);
    }

    [Fact]
    public void HistoricalReportFlagsLegacyClaimsAndLeavesBoundPassesOut()
    {
        Assert.True(CompletionContentPolicy.NeedsHistoricalReview(null));
        Assert.True(CompletionContentPolicy.NeedsHistoricalReview(new TaskCompletionClaim
        {
            CurrentBriefVersion = Current,
            DeliveryBriefVersion = Current,
            ReviewBriefVersion = Current,
            ContentStatus = null,
        }));
        Assert.False(CompletionContentPolicy.NeedsHistoricalReview(new TaskCompletionClaim
        {
            CurrentBriefVersion = Current,
            DeliveryBriefVersion = Current,
            ReviewBriefVersion = Current,
            ContentStatus = "pass",
            ContentAspect = "requirement-fit",
        }));
        Assert.True(CompletionContentPolicy.NeedsHistoricalReview(new TaskCompletionClaim
        {
            CurrentBriefVersion = Current,
            DeliveryBriefVersion = Current,
            ReviewBriefVersion = Current,
            ContentStatus = "pass",
            ContentAspect = "requirement-fit",
        }, "concept"));
    }

    [Fact]
    public void LocalContentArtifactKeepsItsReviewBriefWhenTheCardChanges()
    {
        var folder = Path.Combine(Path.GetTempPath(), "completion-content-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var oldVersion = BriefVersionStore.Record(folder, "First brief.");
            CompletionContentEvidence.StampLocalRun(folder);
            Assert.Equal(oldVersion, CompletionContentEvidence.ReadLocalRunVersion(folder));
            File.WriteAllText(Path.Combine(folder, CompletionContentEvidence.LocalContextFile),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    briefVersion = CompletionContentEvidence.ReadLocalRunVersion(folder),
                }));
            File.WriteAllText(Path.Combine(folder, "aspect-requirement-fit.json"),
                "{\"status\":\"pass\",\"summary\":\"Matches the first brief.\"}");
            var task = new TaskInfo { FolderPath = folder, Mode = "coding" };
            Assert.True(CompletionContentPolicy.Decide(CompletionContentEvidence.Read(task, null)).Accepted);

            BriefVersionStore.Record(folder, "Second brief.");
            var stale = CompletionContentPolicy.Decide(CompletionContentEvidence.Read(task, null));
            Assert.False(stale.Accepted);
            Assert.Contains("delivered against brief", stale.Message);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void BriefEditKeepsTheBeforeAndAfterTextForOwnerComparison()
    {
        var folder = Path.Combine(Path.GetTempPath(), "brief-difference-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            const string before = "First brief.";
            const string after = "Second brief with a changed requirement.";
            var from = BriefVersionStore.Record(folder, before);
            var to = BriefVersionStore.Record(folder, after, before);
            var difference = BriefVersionStore.ReadDifference(folder);
            Assert.NotNull(difference);
            Assert.Equal((from, to, before, after),
                (difference.FromVersion, difference.ToVersion, difference.Before, difference.After));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}

// Exercises the policy, fenced authority, immutable settlement, durable transfer,
// completion evidence and historical claim projection with the same delivery.
public sealed class CompletionContentAdmissionTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "content-admission-" + Guid.NewGuid().ToString("N"));
    private readonly TaskScannerService _scanner;
    private readonly TaskMutationService _mutations;
    private readonly AttemptAuthorityService _authority;
    private readonly TaskInfo _task;
    private readonly string _brief;
    private int _delivered;
    private static readonly DateTime Now = new(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);

    public CompletionContentAdmissionTests()
    {
        var watch = Path.Combine(_workspace, "projects", "demo");
        Directory.CreateDirectory(watch);
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> {
                ["TaskRepository"] = _workspace, ["WatchPaths:0:Name"] = "demo",
                ["WatchPaths:0:Path"] = watch, ["WatchPaths:0:RootPath"] = watch,
            }).Build();
        _scanner = new(config, Microsoft.Extensions.Logging.Abstractions.NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(Microsoft.Extensions.Logging.Abstractions.NullLogger<SummaryGenerationService>.Instance, config));
        _mutations = new(_scanner,
            new ClientIdentityStore(config, Microsoft.Extensions.Logging.Abstractions.NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(config, Microsoft.Extensions.Logging.Abstractions.NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(Microsoft.Extensions.Logging.Abstractions.NullLogger<TaskChangeNotifier>.Instance),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TaskMutationService>.Instance);
        var id = _mutations.CreateJob(new CreateTaskRequest {
            Id = "source", Title = "Source delivery", WatchPath = watch,
            TargetState = TaskStates.AutoReview, PromptMarkdown = "Deliver the scoped implementation.",
        });
        _task = Assert.IsType<TaskInfo>(_scanner.FindJob(Assert.IsType<string>(id), watch));
        _brief = BriefVersionStore.ReadCurrent(_task.FolderPath);
        _authority = new(config, Microsoft.Extensions.Logging.Abstractions.NullLogger<AttemptAuthorityService>.Instance, () => Now);
    }

    [Theory]
    [InlineData("Consider clearer wording.", 0, 1, "advisory")]
    [InlineData("backend/Feature.cs fails to name the optional diagnostic.", 1, 1, "concern-budget-accepted")]
    public void Accepted_review_integrated_and_verified_agrees_with_completion_and_history(
        string concern, int used, int maximum, string disposition)
    {
        var settled = Settle("concerns", concern, used, maximum);
        Assert.Equal(AgentStudio.TaskServer.Contracts.ReviewFollowUpAction.Accept, settled.Decision.FollowUp.Action);
        Assert.Equal(ReviewTerminalOutcome.Pass, _authority.GetReview(settled.AttemptId)!.Outcome);
        var facts = CompletionContentEvidence.Read(_task, _authority);
        Assert.True(CompletionContractPolicy.Decide(new(true, true, true, IntegrationStatuses.Integrated,
            ContainmentCommitSha: settled.Report.Workspace.ActualHead)).Accepted);
        var decision = CompletionContentPolicy.Decide(facts);
        Assert.True(decision.Accepted, decision.Message);
        Assert.False(decision.Overridden);
        var visible = AcceptanceReadinessPolicy.Classify(new(
            CompletionAdmissionPreview.Admitted(decision.Message, facts,
                new TaskIntegrationVerification { State = IntegrationVerificationStates.Verified,
                    Sha = settled.Report.Workspace.ActualHead }), "coding", null));
        Assert.Equal(AcceptanceReadinessClasses.Ready, visible.Readiness);
        Assert.Equal(facts.ContentStatus, visible.ContentStatus);
        var held = AcceptanceReadinessPolicy.Classify(new(
            CompletionAdmissionPreview.Admitted(decision.Message, facts, null), "coding",
            new ParkedBlockerStatus("operator-decision", "decision", "Choose the release scope.", Now,
                0, "A person must choose the scope.", "blocked", Now, "Decision remains open.")
                { RequiresDecisionCard = true }));
        Assert.Equal(AcceptanceReadinessClasses.OwnerDecision, held.Readiness);
        Assert.Contains(concern, facts.ContentSummary);
        Assert.Contains(disposition, facts.ContentSummary);
        Assert.Contains("original concerns", facts.ContentSummary);
        Assert.Equal("concerns", RemoteReviewSettlementJournal.Read(_task.FolderPath, settled.AttemptId)
            .Entry!.Report.Verdicts.Single().Status);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(_task.FolderPath, "logs"), "remote-review-settlement-*.json"));
        Assert.False(CompletionContentPolicy.NeedsHistoricalReview(new TaskCompletionClaim {
            CurrentBriefVersion = facts.CurrentBriefVersion, DeliveryBriefVersion = facts.DeliveryBriefVersion,
            ReviewBriefVersion = facts.ReviewBriefVersion, ContentStatus = facts.ContentStatus,
            ContentAspect = facts.RequiredAspect, ContentSummary = facts.ContentSummary,
        }, "coding"));
    }

    [Theory]
    [InlineData("concerns")]
    [InlineData("pass")]
    public void Degraded_quality_block_waits_for_real_linked_follow_up_and_restart_restores_it_once(string status)
    {
        var settled = Settle(status, "A wording suggestion.", qualityBlock: true);
        Assert.True(settled.Decision.ShouldDegrade);
        var pending = CompletionContentPolicy.Decide(CompletionContentEvidence.Read(_task, _authority));
        Assert.False(pending.Accepted);
        Assert.Contains("still owes its linked follow-up", pending.Message);
        var entry = RemoteReviewSettlementJournal.Read(_task.FolderPath, settled.AttemptId).Entry!;
        Assert.True(RemoteReviewSettlementPolicy.RestoreReviewBudgetSideEffects(_task, entry, _mutations, _scanner));
        Assert.True(RemoteReviewSettlementPolicy.RestoreReviewBudgetSideEffects(_task, entry, _mutations, _scanner));
        var followUp = Assert.Single(_scanner.ScanAllJobs(), task => task.CreationSource == "review-budget-follow-up");
        Assert.Contains(_task.Key ?? _task.Id, followUp.References.FollowUpOf);
        Assert.Equal(_task.Key ?? _task.Id, Assert.Single(followUp.References.DependsOn).Key);
        var facts = CompletionContentEvidence.Read(_task, _authority);
        Assert.True(CompletionContentPolicy.Decide(facts).Accepted);
        Assert.Contains(followUp.Key ?? followUp.Id, facts.ContentSummary);
        // Recovery can reconstruct admission from the hashed budget journal.
        File.Delete(CompletionContentEvidence.AdmissionPath(_task.FolderPath, settled.AttemptId));
        Assert.True(CompletionContentPolicy.Decide(CompletionContentEvidence.Read(_task, _authority)).Accepted);
    }

    [Fact]
    public void Actionable_content_concern_transfers_when_card_budget_prevents_another_fix_round()
    {
        var settled = Settle("concerns", "backend/Feature.cs fails to name the optional diagnostic.", 0, 1);
        Assert.True(settled.Decision.ShouldDegrade);
        var entry = RemoteReviewSettlementJournal.Read(_task.FolderPath, settled.AttemptId).Entry!;
        Assert.False(CompletionContentPolicy.Decide(CompletionContentEvidence.Read(_task, _authority)).Accepted);
        Assert.True(RemoteReviewSettlementPolicy.RestoreReviewBudgetSideEffects(_task, entry, _mutations, _scanner));
        var facts = CompletionContentEvidence.Read(_task, _authority);
        Assert.True(CompletionContentPolicy.Decide(facts).Accepted);
        Assert.Contains("transferred", facts.ContentSummary);
    }

    [Fact]
    public void Failed_follow_up_creation_leaves_durable_machine_owned_obligation_and_cannot_complete()
    {
        var settled = Settle("concerns", "A wording suggestion.", qualityBlock: true);
        var entry = RemoteReviewSettlementJournal.Read(_task.FolderPath, settled.AttemptId).Entry!;
        var unavailableProject = _task with { WatchPath = Path.Combine(_workspace, "unavailable-project") };
        Assert.False(RemoteReviewSettlementPolicy.RestoreReviewBudgetSideEffects(unavailableProject, entry, _mutations, _scanner));
        Assert.Null(Assert.Single(ReviewRoundBudgetStore.Read(_task.FolderPath).Rounds).FollowUpTaskKey);
        Assert.NotNull(RemoteReviewSettlementJournal.Read(_task.FolderPath, settled.AttemptId).Entry!.ReviewBudgetDecision);
        var refusal = CompletionContentPolicy.Decide(CompletionContentEvidence.Read(_task, _authority));
        Assert.False(refusal.Accepted);
        Assert.Contains("retry settlement recovery", refusal.Message);
        Assert.True(RemoteReviewSettlementPolicy.RestoreReviewBudgetSideEffects(_task, entry, _mutations, _scanner));
        Assert.True(CompletionContentPolicy.Decide(CompletionContentEvidence.Read(_task, _authority)).Accepted);
    }

    [Theory]
    [InlineData("requirement-fit")]
    [InlineData("concept-fit")]
    public void Genuine_content_block_never_degrades_when_every_budget_is_spent(string aspect)
    {
        var settled = Settle("block", "backend/Feature.cs fails the required behavior.", 1, 1, aspect: aspect);
        Assert.False(settled.Decision.ShouldDegrade);
        Assert.True(settled.Decision.Escalate);
        Assert.Equal(AgentStudio.TaskServer.Contracts.ReviewFollowUpAction.ReviewFindingRound, settled.Decision.FollowUp.Action);
        var card = _task with { Mode = aspect == "concept-fit" ? TaskModes.Concept : TaskModes.Coding };
        var refusal = CompletionContentPolicy.Decide(CompletionContentEvidence.Read(card, _authority));
        Assert.False(refusal.Accepted);
        Assert.Contains(aspect + " verdict was block", refusal.Message);
        Assert.Equal("block", settled.Report.Verdicts.Single().Status);
    }

    [Fact]
    public void Repaired_delivery_uses_its_new_settled_pass_and_retains_earlier_finding()
    {
        var blocked = Settle("block", "backend/Feature.cs fails the required behavior.");
        var repaired = Settle("pass", "The repaired implementation meets the brief.");
        Assert.NotEqual(blocked.AttemptId, repaired.AttemptId);
        Assert.NotEqual(blocked.Report.Workspace.ExpectedResultSha, repaired.Report.Workspace.ExpectedResultSha);
        var facts = CompletionContentEvidence.Read(_task, _authority);
        Assert.True(CompletionContentPolicy.Decide(facts).Accepted);
        Assert.Equal(repaired.AttemptId, facts.ReviewAttemptId);
        Assert.Equal("block", RemoteReviewSettlementJournal.Read(_task.FolderPath, blocked.AttemptId).Entry!.Report.Verdicts.Single().Status);
    }

    [Fact]
    public void Stale_brief_wrong_aspect_and_missing_research_report_remain_actionable_refusals()
    {
        Settle("concerns", "A wording suggestion.");
        var facts = CompletionContentEvidence.Read(_task, _authority);
        var missing = CompletionContentPolicy.Decide(CompletionContentEvidence.Read(_task with { Mode = "research" }, _authority));
        Assert.False(missing.Accepted);
        Assert.Contains("required report is missing", missing.Message);
        var wrong = CompletionContentPolicy.Decide(facts with { RequiredAspect = "concept-fit" });
        Assert.False(wrong.Accepted);
        Assert.Contains("content aspect must be requirement-fit", wrong.Message);
        BriefVersionStore.Record(_task.FolderPath, "Changed requirement.");
        var stale = CompletionContentPolicy.Decide(CompletionContentEvidence.Read(_task, _authority));
        Assert.False(stale.Accepted);
        Assert.Contains("delivered against brief", stale.Message);
        Assert.Contains("content verdict was for brief", stale.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Historical_material_concern_requires_review_recovery_only_for_snapshot_review(bool checkoutVerdict)
    {
        var legacyPlan = new AgentStudio.TaskServer.Contracts.ReviewPlanDto(
            [new("aspect-concept-fit", "concept-fit", "codex", [], ExecutionKind: "agent-aspect",
                Prompt: "End with `[[TASK_DONE]]` on its own line. Do not edit files or run tools.")], ["concept-fit"]);
        Settle("concerns", "The published Dossier snapshot is incomplete.", aspect: "concept-fit",
            plan: legacyPlan, checkoutVerdict: checkoutVerdict);
        var card = _task with { Mode = TaskModes.Concept };
        var facts = CompletionContentEvidence.Read(card, _authority);
        var refusal = CompletionContentPolicy.Decide(facts);

        if (checkoutVerdict)
        {
            Assert.Contains("The published Dossier snapshot is incomplete.", facts.ContentSummary);
            Assert.Null(facts.AdmissionProblem);
            return;
        }
        Assert.Null(facts.ContentStatus);
        Assert.False(refusal.Accepted);
        Assert.Contains("review infrastructure", refusal.Message);
        Assert.DoesNotContain("verdict was concerns", refusal.Message);
    }

    [Fact]
    public void Wrong_content_aspect_in_settled_report_is_not_admitted()
    {
        Settle("concerns", "A wording suggestion.", aspect: "concept-fit");
        var refusal = CompletionContentPolicy.Decide(CompletionContentEvidence.Read(_task, _authority));
        Assert.False(refusal.Accepted);
        Assert.Contains("no content verdict (requirement-fit)", refusal.Message);
        Assert.Contains("concept-fit", refusal.Message);
    }

    [Fact]
    public void Receipt_for_wrong_result_or_settlement_cannot_admit_content()
    {
        var settled = Settle("concerns", "A wording suggestion.");
        var path = CompletionContentEvidence.AdmissionPath(_task.FolderPath, settled.AttemptId);
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        json["resultSha"] = "other-result";
        File.WriteAllText(path, json.ToJsonString());
        var decision = CompletionContentPolicy.Decide(CompletionContentEvidence.Read(_task, _authority));
        Assert.False(decision.Accepted);
        Assert.Contains("stale delivery, brief or settlement identity", decision.Message);
    }

    [Fact]
    public void Conflicting_report_replay_cannot_replace_the_frozen_admission()
    {
        var settled = Settle("concerns", "A wording suggestion.");
        var before = File.ReadAllText(CompletionContentEvidence.AdmissionPath(_task.FolderPath, settled.AttemptId));
        var original = RemoteReviewSettlementJournal.Read(_task.FolderPath, settled.AttemptId).Entry!;
        var changed = settled.Report with { Summary = "Different payload under the accepted key." };
        var invoked = false;
        var conflict = RemoteReviewSettlementJournal.PrepareAndSettle(_task.FolderPath,
            original with { Report = changed, ReportSha256 = RemoteReviewSettlementJournal.Hash(changed) },
            () => original.IdempotencyKey,
            () => { invoked = true; throw new InvalidOperationException("Conflict must not reach authority."); });
        Assert.True(RemoteReviewSettlementJournal.IsIdempotencyConflict(conflict));
        Assert.False(invoked);
        Assert.Equal(before, File.ReadAllText(CompletionContentEvidence.AdmissionPath(_task.FolderPath, settled.AttemptId)));
        Assert.True(CompletionContentPolicy.Decide(CompletionContentEvidence.Read(_task, _authority)).Accepted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Incomplete_receipt_cannot_bypass_a_pending_transfer_of_passed_content(bool emptyDocument)
    {
        var settled = Settle("pass", "The content meets the brief.", qualityBlock: true);
        var path = CompletionContentEvidence.AdmissionPath(_task.FolderPath, settled.AttemptId);
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        json["dispositions"] = new System.Text.Json.Nodes.JsonArray();
        File.WriteAllText(path, emptyDocument ? "null" : json.ToJsonString());
        var decision = CompletionContentPolicy.Decide(CompletionContentEvidence.Read(_task, _authority));
        Assert.False(decision.Accepted);
        Assert.Contains("repair the settled review receipt", decision.Message);
    }

    [Fact]
    public void Engine_transfer_requires_the_routing_receipt_of_its_frozen_occurrence()
    {
        Settle("concerns", "A wording suggestion.", qualityBlock: true, orchestrationRunId: "engine-current");
        using var timeline = new TimelineLog(Microsoft.Extensions.Logging.Abstractions.NullLogger<TimelineLog>.Instance);
        Assert.True(timeline.Append(_task.FolderPath, TimelineEventKinds.ReviewFollowUpRouted, TimelineActors.System,
            "Earlier routing receipt.", details: new Dictionary<string, string> {
                ["orchestrationRunId"] = "engine-old", ["followUpTaskKey"] = "old-follow-up",
            }));
        Assert.False(CompletionContentPolicy.Decide(CompletionContentEvidence.Read(_task, _authority)).Accepted);
        Assert.True(timeline.Append(_task.FolderPath, TimelineEventKinds.ReviewFollowUpRouted, TimelineActors.System,
            "Current routing receipt.", details: new Dictionary<string, string> {
                ["orchestrationRunId"] = "engine-current", ["followUpTaskKey"] = "current-follow-up",
            }));
        var facts = CompletionContentEvidence.Read(_task, _authority);
        Assert.True(CompletionContentPolicy.Decide(facts).Accepted);
        Assert.Contains("current-follow-up", facts.ContentSummary);
        Assert.DoesNotContain("old-follow-up", facts.ContentSummary);
    }

    private (string AttemptId, AgentStudio.TaskServer.Contracts.ReviewReportRequest Report,
        AgentStudio.OrchestrationDecisions.ReviewBudgetDecision Decision) Settle(string status, string summary,
        int used = 0, int maximum = 0, bool qualityBlock = false, string aspect = "requirement-fit",
        string? orchestrationRunId = null, AgentStudio.TaskServer.Contracts.ReviewPlanDto? plan = null,
        bool checkoutVerdict = false)
    {
        var key = Guid.NewGuid().ToString("N");
        var taskKey = TaskIdentity.AuthorityKey(_task);
        var sha = new string("abcdef0123456789"[_delivered++], 40);
        var run = _authority.AcquireRun(taskKey, "demo", null, "runner", "host", 60, "run-" + key, briefVersion: _brief).RunAttempt!;
        Assert.True(_authority.SettleRun(new SettleRunAttemptRequest {
            Write = new(run.AttemptId, run.LastFence, run.AuthorityEpoch, "settle-" + key), Outcome = "done", ResultSha = sha,
        }).Accepted);
        var created = _authority.CreateReviewAttempt(new(taskKey, "demo", sha, run.AttemptId,
            _brief, AttemptAuthorityService.Hash("policy"), [], "review-" + key, Plan: plan));
        var review = _authority.ClaimReview(created.AttemptId, "reviewer", "host", 60, "claim-" + key).ReviewAttempt!;
        AgentStudio.TaskServer.Contracts.ReviewVerdictDto[] verdicts = qualityBlock
            ? [new(aspect, status, "Product", summary), new("code-quality", "block", "Product", "backend/Quality.cs fails an optional diagnostic.")]
            : [new(aspect, status, "Product", summary) { UsesDossierCheckout = checkoutVerdict }];
        var outcome = qualityBlock || status == "block" ? "ProductFailure" : "Pass";
        var budget = AgentStudio.OrchestrationDecisions.ReviewBudgetPolicy.Decide(new(outcome,
            verdicts.Select(item => new AgentStudio.TaskServer.Contracts.ReviewFollowUpFinding(item.Aspect,
                item.Status, item.Summary)).ToArray(), used, maximum, new(4, 4),
            AgentStudio.OrchestrationDecisions.ReviewRoundBudgetLedger.Empty, review.AttemptId, 1, 2, 2, 2));
        var original = new AgentStudio.TaskServer.Contracts.ReviewReportRequest("reviewer", "instance", review.Lease!.LeaseId,
            review.LastFence, "report-" + key, outcome, null, "Settled review.",
            new("demo", sha, sha, "tree", false, false, "workspace", "namespace"),
            new("host", "reviewer", "instance", "os", "arch", "runtime", new Dictionary<string, string>(), new Dictionary<string, string>()),
            [], [], verdicts, AuthorityEpoch: review.AuthorityEpoch, BriefSha256: _brief);
        var report = budget.ShouldDegrade
            ? AgentStudio.OrchestrationDecisions.ReviewRoundBudgetPolicy.ApplyRemoteReport(original, budget.RoundBudget) : original;
        var hash = RemoteReviewSettlementJournal.Hash(report);
        var entry = new RemoteReviewSettlementEntry {
            AttemptId = review.AttemptId, TaskKey = taskKey, IdempotencyKey = report.IdempotencyKey, ReportSha256 = hash,
            Report = report, ReceivedAtUtc = Now, ReviewBudgetDecision = budget.ShouldDegrade ? budget.RoundBudget : null,
            ReviewBudgetOriginalVerdicts = budget.ShouldDegrade ? verdicts : null,
        };
        Assert.True(RemoteReviewSettlementJournal.PrepareAndSettle(_task.FolderPath, entry,
            () => _authority.GetReview(review.AttemptId)!.Reports.LastOrDefault(item => item.AuthorityStatus == AttemptWriteStatus.Accepted)?.IdempotencyKey,
            () => {
                CompletionContentEvidence.WriteAdmission(_task.FolderPath, review.AttemptId, report, hash,
                    budget.RoundBudget.ContentAdmissions, orchestrationRunId);
                return _authority.SettleReview(new(new(review.AttemptId, review.LastFence, review.AuthorityEpoch, report.IdempotencyKey),
                    sha, report.Outcome == "Pass" ? ReviewTerminalOutcome.Pass : ReviewTerminalOutcome.ProductFailure));
            }).Accepted);
        return (review.AttemptId, report, budget);
    }

    public void Dispose() => Directory.Delete(_workspace, recursive: true);
}
