using System.Text;
using System.Text.Json;
using AgentStudio.Runner;
using Contract = AgentStudio.TaskServer.Contracts;

using Xunit;

namespace AgentStudio.Tests;

public sealed class RemotePipelineExecutionProjectionTests
{
    [Theory]
    [InlineData("pass", PipelineStepStatus.Passed)]
    [InlineData("concerns", PipelineStepStatus.Passed)]
    [InlineData("blocked", PipelineStepStatus.Failed)]
    [InlineData("failed", PipelineStepStatus.Failed)]
    [InlineData("infrastructure", PipelineStepStatus.Failed)]
    public void Project_SettledRemoteReview_ShowsAspectsGradeUsageAndSkippedReason(
        string verdictStatus, PipelineStepStatus expectedStatus)
    {
        var folder = Path.Combine(Path.GetTempPath(), "remote-settlement-pipeline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var report = ReviewReport(verdictStatus);
            var entry = new RemoteReviewSettlementEntry
            {
                AttemptId = "review_2", TaskKey = "AGT-3457", IdempotencyKey = report.IdempotencyKey,
                Report = report, ReportSha256 = RemoteReviewSettlementJournal.Hash(report),
                ReceivedAtUtc = Utc(8, 20),
            };
            RemoteReviewSettlementJournal.Write(folder, entry with
            {
                AttemptId = "review_1", ReceivedAtUtc = Utc(8, 12),
            });
            RemoteReviewSettlementJournal.Write(folder, entry);
            var usage = new TaskTokenSummary
            {
                TotalTokens = 100, InputTokens = 90, OutputTokens = 10,
                Entries = [Call(Utc(8, 16),
                    "support:remote-review:review_2:step:aspect-code-quality:usage:review_2:0",
                    90, 10, 0.01m)],
            };
            var result = RemotePipelineExecutionProjection.Project(null, PipelineCatalogue.Standard,
                RemoteTask(folder), RemoteSessions(), RemoteCompletion(), usage);
            var aspect = Step(result.Execution!, "aspect-code-quality");
            Assert.Equal(expectedStatus, aspect.Status);
            Assert.Equal(verdictStatus, aspect.Verdict);
            Assert.Equal("The review checked the diff.", aspect.VerdictSummary);
            Assert.Equal("gpt-6", aspect.Model);
            Assert.Equal("codex", aspect.ReviewCli);
            Assert.Equal(90, aspect.InputTokens);
            Assert.Equal(10, aspect.OutputTokens);
            Assert.Equal(60_000, aspect.DurationMs);
            Assert.Equal("review_2", aspect.ExecutionAttemptId);
            Assert.Equal(2, aspect.ReviewAttemptNumber);
            Assert.Equal("remote-review-grade-review_2.md", aspect.EvidenceRef);
            Assert.Equal("No requirement review was needed.",
                Step(result.Execution!, "aspect-requirement-fit").Reason);
            var grade = Step(result.Execution!, PipelineCatalogue.OrchestratorDecisionStepId);
            Assert.Equal(PipelineStepStatus.Passed, grade.Status);
            Assert.Equal("review_2", grade.ExecutionAttemptId);
            Assert.Equal("Review completed.", grade.VerdictSummary);
            Assert.Equal("codex", grade.ReviewCli);
            Assert.Equal("gpt-6", grade.Model);
            Assert.Equal(60_000, grade.DurationMs);
            var codeReviewGrade = Step(result.Execution!, PipelineCatalogue.CodeReviewGradeStepId);
            Assert.Equal(PipelineStepStatus.Passed, codeReviewGrade.Status);
            Assert.Equal("review_2", codeReviewGrade.ExecutionAttemptId);
            Assert.Equal("codex", codeReviewGrade.ReviewCli);
            Assert.Equal("gpt-6", codeReviewGrade.Model);
            Assert.Equal("This Remote Review did not run build or test commands.",
                Step(result.Execution!, PipelineCatalogue.BuildTestGateStepId).Reason);
            Assert.Equal(100, new PipelineCostCalculator(TokenTestServices.Pricing())
                .SummarizeWithLedger(result.Execution, result.LedgerCalls).TotalTokens);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void Project_PublishedBatch_ShowsGateIdentityVerdictAndPublishedSha()
    {
        var folder = Path.Combine(Path.GetTempPath(), "remote-batch-pipeline-" + Guid.NewGuid().ToString("N"));
        var gateRoot = Path.Combine(folder, "gate-store");
        Directory.CreateDirectory(folder);
        try
        {
            var report = ReviewReport("pass") with
            {
                Verdicts = [new Contract.ReviewVerdictDto("build-tests", "pass", "DeferredToBatch",
                    "Build and tests deferred to the batch.")],
            };
            RemoteReviewSettlementJournal.Write(folder, new RemoteReviewSettlementEntry
            {
                AttemptId = "review_2", TaskKey = "AGT-3457", IdempotencyKey = report.IdempotencyKey,
                Report = report, ReportSha256 = RemoteReviewSettlementJournal.Hash(report),
                ReceivedAtUtc = Utc(8, 20),
            });
            var store = new BatchGateStore(gateRoot);
            var batch = store.BatchDirectory("batch-1");
            Directory.CreateDirectory(Path.Combine(batch, "state"));
            Directory.CreateDirectory(Path.Combine(batch, "runs"));
            WriteJson(Path.Combine(batch, "manifest.json"), new
            {
                batchId = "batch-1", scope = new { project = "Agent Taskboard", repository = "repo",
                    integrationBranch = "develop", gateProfile = "full", gateProfileDigest = "profile",
                    platformVersion = "1" }, baseSha = "base", membershipDigest = "digest",
                closedAtUtc = Utc(8, 21), members = new[] { new { taskKey = "AGT-3457" } },
                exclusions = Array.Empty<object>(), eligibleKeys = new[] { "AGT-3457" },
            });
            WriteJson(Path.Combine(batch, "state", "00000001.json"), new
            {
                batchId = "batch-1", membershipDigest = "digest", phase = 6,
                candidateSha = "candidate", coordinatorFence = 1, recordedAtUtc = Utc(8, 30),
                batchRunId = "run-1",
            });
            var runName = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes("run-1")));
            WriteJson(Path.Combine(batch, "runs", runName + ".verdict.json"), new
            {
                batchRunId = "run-1", batchId = "batch-1", membershipDigest = "digest",
                testedCandidateSha = "candidate", gateProfileDigest = "profile",
                outcome = "pass", classification = "green", evidencePath = "gate.log",
                completedAtUtc = Utc(8, 29),
            });
            WriteJson(Path.Combine(batch, "publication.json"), new
            {
                batchId = "batch-1", membershipDigest = "digest", batchRunId = "run-1",
                preTipSha = "base", testedCandidateSha = "candidate",
                verifiedRemoteSha = new string('a', 40), coordinatorFence = 1,
                refMutationFence = 1, verifiedAtUtc = Utc(8, 30),
            });
            var taskName = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes("AGT-3457")));
            var members = Directory.CreateDirectory(Path.Combine(batch, "members", taskName)).FullName;
            WriteJson(Path.Combine(members, runName + ".json"), new BatchGateMemberRecord(
                "AGT-3457", "run", 1, new string('a', 40), [new string('a', 40)],
                "batch-1", "digest", "base", "candidate", "profile", "run-1",
                "pass", "gate.log", Utc(8, 30)));
            var result = RemotePipelineExecutionProjection.Project(null, PipelineCatalogue.Standard,
                RemoteTask(folder), RemoteSessions(), RemoteCompletion(), null,
                store);
            var gate = Step(result.Execution!, PipelineCatalogue.BuildTestGateStepId);
            Assert.Equal(PipelineStepStatus.Passed, gate.Status);
            Assert.Equal("batch-1", gate.BatchGate?.BatchId);
            Assert.Equal("published", gate.BatchGate?.Phase);
            Assert.Equal("pass", gate.BatchGate?.Verdict);
            Assert.Equal(new string('a', 40), gate.BatchGate?.PublishedSha);
        }
        finally { Directory.Delete(folder, true); }
    }

    private static void WriteJson(string path, object value)
        => File.WriteAllText(path, JsonSerializer.Serialize(value));

    private static TaskInfo RemoteTask(string folder) => new()
    {
        Id = "AGT-3457", TaskKey = "AGT-3457", FolderPath = folder,
        CreatedAt = Utc(8, 0), State = TaskStates.HumanReview,
    };

    private static SessionEvent[] RemoteSessions() =>
        [new() { Ts = Utc(8, 0), Kind = "start", Cli = "remote-runner" }];

    private static TimelineEvent[] RemoteCompletion() =>
        [new() { Ts = Utc(8, 10), Kind = TimelineEventKinds.AgentRunFinished,
            Summary = "remote run done", Details = new Dictionary<string, string>
            { ["cli"] = "remote-runner", ["status"] = "done" } }];

    private static Contract.ReviewReportRequest ReviewReport(string status)
    {
        var sha = new string('a', 40);
        return new Contract.ReviewReportRequest(
            "executor", "instance", "lease", 1, "report-key", "Pass", null,
            "Review completed.",
            new Contract.ReviewWorkspaceProofDto("repository", sha, sha, sha, false, false,
                "workspace", "review_2"),
            new Contract.ReviewEnvironmentDto("host", "executor", "instance", "linux", "x64", "10",
                new Dictionary<string, string>(), new Dictionary<string, string>()),
            [new Contract.ReviewCommandEvidenceDto("aspect-code-quality", "code-quality", "codex", [],
                sha, sha, sha, Utc(8, 15), Utc(8, 16), 0, null, sha, sha,
                ExecutionKind: Contract.ReviewCommandKinds.AgentAspect, Model: "gpt-6",
                InputTokens: 90, OutputTokens: 10)],
            [],
            [new Contract.ReviewVerdictDto("code-quality", status, "RemoteAspectVerdict",
                "The review checked the diff.")],
            SkippedAspects: [new Contract.ReviewSkippedAspectDto("requirement-fit",
                "No requirement review was needed.")]);
    }
    [Fact]
    public void Project_LegacyRemoteFailedConcern_RepairsReadModelWithoutMigration()
    {
        var folder = Path.Combine(Path.GetTempPath(), "remote-concern-projection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "remote-review-grade-review_agt2794.md"), """
                ---
                attemptId: "review_agt2794"
                receivedAt: 2026-09-18T08:20:00Z
                outcome: "Pass"
                ---

                **Outcome:** Pass

                ## Aspect verdicts

                | Aspect | Status | Classification | Evidence checked | Missing | Summary |
                | --- | --- | --- | --- | --- | --- |
                | [code-quality](aspect-code-quality.md) | concerns | RemoteAspectVerdict | diff | none | Clean diff with one dead no-op assertion. |
                """);
            var pipeline = PipelineCatalogue.Standard;
            var local = new PipelineExecutionRecord
            {
                PipelineId = pipeline.Id,
                PipelineVersion = pipeline.Version,
                JobId = "AGT-2794",
                StartedAt = Utc(8, 0),
                Steps = pipeline.AllSteps.Select(step => new PipelineStepExecution
                {
                    StepId = step.Id,
                    Kind = step.Kind,
                    Status = step.Id == "aspect-code-quality"
                        ? PipelineStepStatus.Failed
                        : PipelineStepStatus.Pending,
                    ExecutionLocation = step.Id == "aspect-code-quality" ? "remote" : null,
                    ExecutionAttemptId = step.Id == "aspect-code-quality" ? "review_agt2794" : null,
                }).ToList(),
            };
            var projected = RemotePipelineExecutionProjection.Project(
                local,
                pipeline,
                new TaskInfo { Id = "AGT-2794", FolderPath = folder, CreatedAt = Utc(8, 0), State = TaskStates.HumanReview },
                [new SessionEvent { Ts = Utc(8, 0), Kind = "start", Cli = "remote-runner" }],
                [new TimelineEvent { Ts = Utc(8, 10), Kind = TimelineEventKinds.AgentRunFinished, Summary = "remote run", Details = new Dictionary<string, string> { ["cli"] = "remote-runner" } }],
                null);

            var aspect = Step(projected.Execution!, "aspect-code-quality");
            Assert.Equal(PipelineStepStatus.Passed, aspect.Status);
            Assert.Equal("concerns", aspect.Verdict);
            Assert.Equal("Clean diff with one dead no-op assertion.", aspect.VerdictSummary);
            Assert.Equal(PipelineStepStatus.Failed, Step(local, "aspect-code-quality").Status);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Project_RemoteNeedsInput_RecordsCoreRunAsNeedsDecisionNotFailed()
    {
        // AGT-3045 regression (AGT-2798): a remote run that parked on two open
        // operator decisions must not read as "Pipeline failure".
        var folder = Path.Combine(Path.GetTempPath(), "remote-needsinput-projection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var pipeline = PipelineCatalogue.Standard;
            var projected = RemotePipelineExecutionProjection.Project(
                null,
                pipeline,
                new TaskInfo { Id = "AGT-2798", FolderPath = folder, CreatedAt = Utc(8, 0), State = TaskStates.Completed },
                [new SessionEvent { Ts = Utc(8, 0), Kind = "start", Cli = "remote-runner" }],
                [new TimelineEvent
                {
                    Ts = Utc(8, 8),
                    Kind = TimelineEventKinds.AgentRunFinished,
                    Summary = "remote run needsinput on agent-runner-01",
                    Details = new Dictionary<string, string> { ["cli"] = "remote-runner", ["status"] = "needsinput" },
                }],
                null);

            var core = Step(projected.Execution!, PipelineCatalogue.CoreAgentRunStepId);
            Assert.Equal(PipelineStepStatus.NeedsDecision, core.Status);
            Assert.Equal("needsinput", core.Verdict);
            Assert.DoesNotContain("remote run needsinput", core.Reason);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Project_RemoteLifecycle_MapsCoreReviewSkipsAndLedgerWithoutWritingParallelState()
    {
        var folder = Path.Combine(
            Path.GetTempPath(),
            "remote-pipeline-projection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(
                Path.Combine(folder, "remote-review-grade-rat-2427.md"),
                """
                ---
                type: remote-review-grade
                attemptId: "rat-2427"
                subjectId: "rsub-2427"
                receivedAt: 2026-07-30T08:20:00.0000000Z
                outcome: "Pass"
                expectedResultSha: "1111111111111111111111111111111111111111"
                actualHead: "1111111111111111111111111111111111111111"
                reportSha256: "abc"
                ---

                # Remote Review Grade

                **Outcome:** Pass

                Review Plane grade passed on the immutable result.

                ## Aspect verdicts
                """,
                Encoding.UTF8);

            var pipeline = PipelineCatalogue.Standard;
            var local = new PipelineExecutionRecord
            {
                PipelineId = pipeline.Id,
                PipelineVersion = pipeline.Version,
                Project = "Agent Taskboard",
                JobId = "agt-2427",
                StartedAt = Utc(8, 18),
                Attempt = 1,
                Steps = pipeline.AllSteps.Select(step => new PipelineStepExecution
                {
                    StepId = step.Id,
                    Kind = step.Kind,
                    Attempt = 1,
                    Status = step.Stub ? PipelineStepStatus.Planned : PipelineStepStatus.Pending,
                }).Select(step => step.StepId == PipelineCatalogue.BuildTestGateStepId
                    ? step with
                    {
                        Status = PipelineStepStatus.Passed,
                        StartedAt = Utc(8, 20),
                        CompletedAt = Utc(8, 43),
                        DurationMs = 23 * 60 * 1000,
                        Reason = "Transactional integration gate passed.",
                    }
                    : step).ToList(),
            };
            var task = new TaskInfo
            {
                Id = "agt-2427",
                TaskKey = "AGT-2427",
                ProjectName = "Agent Taskboard",
                FolderPath = folder,
                CreatedAt = Utc(7, 59),
                State = TaskStates.Completed,
                Model = "gpt-5.4",
                ThinkingLevel = "high",
            };
            var sessions = new[]
            {
                new SessionEvent { Ts = Utc(8, 0), Kind = "start", Cli = "remote-runner" },
            };
            var timeline = new[]
            {
                new TimelineEvent
                {
                    Ts = Utc(8, 10),
                    Kind = TimelineEventKinds.AgentRunFinished,
                    Actor = TimelineActors.Agent,
                    RunId = "run-2427",
                    Summary = "remote run done on agent-runner-01",
                    Details = new Dictionary<string, string>
                    {
                        ["cli"] = "remote-runner",
                        ["status"] = "done",
                    },
                },
            };
            var summary = new TaskTokenSummary
            {
                Calls = 3,
                InputTokens = 600,
                OutputTokens = 60,
                TotalTokens = 660,
                EstimatedApiCostUsd = 0.66m,
                AllModelsPriced = true,
                LastModel = "gpt-5.4",
                LastUpdate = Utc(8, 19),
                Entries =
                [
                    Call(Utc(8, 5), "agent:codex", 100, 10, 0.11m),
                    Call(Utc(8, 9), "agent:codex", 200, 20, 0.22m),
                    Call(Utc(8, 19), "orchestrator:Agent Taskboard", 300, 30, 0.33m),
                ],
            };

            var projected = RemotePipelineExecutionProjection.Project(
                local,
                pipeline,
                task,
                sessions,
                timeline,
                summary);

            Assert.NotNull(projected.Execution);
            Assert.Equal(Utc(8, 0), projected.Execution!.StartedAt);
            Assert.Equal(Utc(8, 43), projected.Execution.CompletedAt);

            var core = Step(projected.Execution, PipelineCatalogue.CoreAgentRunStepId);
            Assert.Equal(PipelineStepStatus.Passed, core.Status);
            Assert.Equal(10 * 60 * 1000, core.DurationMs);
            Assert.Equal(300, core.InputTokens);
            Assert.Equal(30, core.OutputTokens);
            Assert.Equal("high", core.ThinkingLevel);
            Assert.Contains("Remote token ledger · 2 calls", core.TokenUsageSource);

            var decision = Step(projected.Execution, PipelineCatalogue.OrchestratorDecisionStepId);
            Assert.Equal(PipelineStepStatus.Passed, decision.Status);
            Assert.Equal("pass", decision.Verdict);
            Assert.Equal(300, decision.InputTokens);
            Assert.Contains("Review Plane grade passed", decision.Reason);

            var aspect = Step(projected.Execution, PipelineCatalogue.AspectStepIds[0]);
            Assert.Equal(PipelineStepStatus.Skipped, aspect.Status);
            Assert.Equal(RemotePipelineExecutionProjection.NotApplicableReason, aspect.Reason);
            Assert.Equal(
                PipelineStepStatus.Skipped,
                Step(projected.Execution, PipelineCatalogue.LoopGuardStepId).Status);

            var gate = Step(projected.Execution, PipelineCatalogue.BuildTestGateStepId);
            Assert.Equal(PipelineStepStatus.Passed, gate.Status);
            Assert.Equal(23 * 60 * 1000, gate.DurationMs);

            // A remote lifecycle that never ran this tool remains pending. The
            // frontend may therefore label it NOT RUN truthfully.
            Assert.Equal(
                PipelineStepStatus.Pending,
                Step(projected.Execution, PipelineCatalogue.RegressionRadarStepId).Status);

            var cost = new PipelineCostCalculator(TokenTestServices.Pricing()).SummarizeWithLedger(
                projected.Execution,
                projected.LedgerCalls);
            Assert.Equal(660, cost.TotalTokens);
            Assert.Equal(0.66m, cost.TotalCostUsd);
            Assert.Equal(
                0.33m,
                cost.Steps.Single(step =>
                    step.StepId == PipelineCatalogue.CoreAgentRunStepId).CostUsd);
            Assert.Equal(
                0.33m,
                cost.Steps.Single(step =>
                    step.StepId == PipelineCatalogue.OrchestratorDecisionStepId).CostUsd);

            // Projection only reads the grade and returns an in-memory record.
            Assert.False(File.Exists(Path.Combine(folder, PipelineExecutionLog.FileName)));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Project_LocalLifecycle_IsUnchanged()
    {
        var record = new PipelineExecutionRecord
        {
            PipelineId = PipelineCatalogue.Standard.Id,
            PipelineVersion = PipelineCatalogue.Standard.Version,
            JobId = "local",
            StartedAt = Utc(8, 0),
            Steps = [],
        };

        var projected = RemotePipelineExecutionProjection.Project(
            record,
            PipelineCatalogue.Standard,
            new TaskInfo { Id = "local", CreatedAt = Utc(8, 0) },
            [],
            [],
            null);

        Assert.Same(record, projected.Execution);
        Assert.Empty(projected.LedgerCalls);
    }

    [Fact]
    public void Project_RemoteLifecycleWithoutGrade_SkipsLocalReviewsButLeavesDecisionUnreached()
    {
        var pipeline = PipelineCatalogue.Standard;
        var task = new TaskInfo
        {
            Id = "remote-without-grade",
            CreatedAt = Utc(8, 0),
            State = TaskStates.Completed,
        };

        var projected = RemotePipelineExecutionProjection.Project(
            null,
            pipeline,
            task,
            [new SessionEvent { Ts = Utc(8, 0), Kind = "start", Cli = "remote-runner" }],
            [
                new TimelineEvent
                {
                    Ts = Utc(8, 10),
                    Kind = TimelineEventKinds.AgentRunFinished,
                    Summary = "remote run done",
                    Details = new Dictionary<string, string>
                    {
                        ["cli"] = "remote-runner",
                        ["status"] = "done",
                    },
                },
            ],
            null);

        Assert.NotNull(projected.Execution);
        Assert.Equal(
            PipelineStepStatus.Skipped,
            Step(projected.Execution!, PipelineCatalogue.AspectStepIds[0]).Status);
        Assert.Equal(
            PipelineStepStatus.Pending,
            Step(projected.Execution!, PipelineCatalogue.OrchestratorDecisionStepId).Status);
    }

    [Fact]
    public void Project_LaterLocalRun_DoesNotReuseEarlierRemoteEvidence()
    {
        var record = new PipelineExecutionRecord
        {
            PipelineId = PipelineCatalogue.Standard.Id,
            PipelineVersion = PipelineCatalogue.Standard.Version,
            JobId = "remote-then-local",
            StartedAt = Utc(9, 0),
            Steps = [],
        };

        var projected = RemotePipelineExecutionProjection.Project(
            record,
            PipelineCatalogue.Standard,
            new TaskInfo { Id = "remote-then-local", CreatedAt = Utc(8, 0) },
            [
                new SessionEvent { Ts = Utc(8, 0), Kind = "start", Cli = "remote-runner" },
                new SessionEvent { Ts = Utc(9, 0), Kind = "start", Cli = "codex" },
            ],
            [
                new TimelineEvent
                {
                    Ts = Utc(8, 10),
                    Kind = TimelineEventKinds.AgentRunFinished,
                    Summary = "remote run done",
                    Details = new Dictionary<string, string> { ["cli"] = "remote-runner" },
                },
                new TimelineEvent
                {
                    Ts = Utc(9, 10),
                    Kind = TimelineEventKinds.AgentRunFinished,
                    Summary = "local run done",
                    Details = new Dictionary<string, string> { ["cli"] = "codex" },
                },
            ],
            null);

        Assert.Same(record, projected.Execution);
        Assert.Empty(projected.LedgerCalls);
    }

    private static PipelineStepExecution Step(PipelineExecutionRecord record, string id) =>
        record.Steps.Single(step => string.Equals(
            step.StepId,
            id,
            StringComparison.OrdinalIgnoreCase));

    private static TaskTokenCall Call(
        DateTime at,
        string participant,
        long input,
        long output,
        decimal cost) =>
        new()
        {
            Ts = at,
            Model = "gpt-5.4",
            ParticipantId = participant,
            InputTokens = input,
            OutputTokens = output,
            EstimatedApiCostUsd = cost,
            ModelPriced = true,
        };

    private static DateTime Utc(int hour, int minute) =>
        new(2026, 7, 30, hour, minute, 0, DateTimeKind.Utc);
}
