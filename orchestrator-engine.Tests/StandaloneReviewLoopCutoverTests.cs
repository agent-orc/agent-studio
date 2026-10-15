extern alias TaskServerAssembly;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.OrchestratorEngine;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using AgentStudio.TestSupport;
using AgentRunner;
using Xunit;

namespace OrchestratorEngine.Tests;

/// <summary>
/// Cutover gate scenario for AGT-W65 I11 (AGT-3061): the whole review loop on
/// the standalone Task Server with the Orchestrator Engine as the only
/// decider. Coding, review and integration hosts speak their store-level
/// protocol; the engine runs its real service loop over authenticated HTTP.
/// After setup no operator call happens: every lane move comes from a runner
/// fact or an engine receipt.
/// </summary>
public sealed class StandaloneReviewLoopCutoverTests
{
    private const string RepositoryUrl = "https://example.invalid/cutover/product.git";
    private const string IntegrationRef = "develop";
    private const string TreeSha = "0123456789abcdef0123456789abcdef01234567";
    private static readonly string FirstResultSha = new('1', 40);
    private static readonly string FixResultSha = new('2', 40);
    private static readonly string IntegratedSha = new('3', 40);

    [Fact]
    public async Task Standalone_server_runs_coding_rejection_fix_round_pass_integration_to_human_review_with_engine_receipts()
    {
        using var workspace = new TempWorkspace("standalone-loop");
        await using var factory = new CutoverFactory(workspace.Path);
        using var bootstrap = factory.CreateClient();
        var store = factory.Services.GetRequiredService<TaskServerStore>();

        var (projectId, task) = await SetupAsync(store);
        var loopStart = (await store.ListAuditAsync(0, default)).Max(record => record.Sequence);

        using var transport = EngineTransport(factory);
        using var engineClient = new EngineTaskServerClient(transport);
        using var engine = Engine(engineClient);
        await engine.StartAsync(default);
        try
        {
            // 1. Coding: completion creates the review subject from the fenced result.
            var first = await CodeAsync(store, null, FirstResultSha);
            Assert.Null(first.Claim.ContinuationBaseRef);
            Assert.Equal("4-auto-review", (await TaskAsync(store, projectId, task.TaskId)).State);

            // 2. Rejected review; the engine opens a fix round.
            var rejected = await ReviewAsync(store, FirstResultSha, reject: true);
            Assert.Equal(first.Run.RunId, rejected.SourceRunId);
            await WaitForAsync(async () => (await TaskAsync(store, projectId, task.TaskId)).State == "2-ready");

            // 3. Fix round on the reviewed delivery (D17) with the findings as prompt.
            var fix = await CodeAsync(store, first.Envelope, FixResultSha);
            Assert.Equal(first.Envelope.ImmutableRemoteRef, fix.Claim.ContinuationBaseRef);
            Assert.Equal(FirstResultSha, fix.Claim.ContinuationBaseSha);
            Assert.Equal(ContinuationBaseReasons.ReviewFixRound, fix.Claim.ContinuationBaseReason);
            var followUp = Assert.IsType<FollowUpDeliveryDto>(fix.Claim.FollowUp);
            Assert.Contains(ReviewFindingRoundPrompt.Directive, followUp.Prompt, StringComparison.Ordinal);
            Assert.Contains("The build and test command failed on this delivery.", followUp.Prompt,
                StringComparison.Ordinal);
            Assert.Contains(ReviewFindingRoundPrompt.BaseSentence(true), followUp.Prompt, StringComparison.Ordinal);

            // 4. Passed review; the engine accepts the delivery for integration.
            await ReviewAsync(store, FixResultSha, reject: false);
            IntegrationCommandDto? command = null;
            await WaitForAsync(async () =>
                (command = (await store.ListIntegrationsAsync(task.TaskId, default)).SingleOrDefault()) is not null);
            Assert.Equal("4-auto-review", (await TaskAsync(store, projectId, task.TaskId)).State);
            Assert.Equal(FixResultSha, command!.ResultSha);
            Assert.Equal(fix.Envelope.ImmutableRemoteRef, command.ResultRef);
            Assert.Equal(IntegrationRef, command.IntegrationRef);

            // 5. Integration under the publication lease, then Human Review.
            var claim = await store.ClaimIntegrationAsync(
                new IntegrationClaimRequest("integration-a", "integration-instance"), "integration-a", default);
            Assert.Equal("claimed", claim.Status);
            Assert.Equal(command.IntegrationId, claim.Command!.IntegrationId);
            // Always the registered project repository, never the coding host's remote.
            Assert.Equal(RepositoryUrl, claim.Command.RepositoryUrl);
            // A long publication renews its lease under the same fence.
            var renewed = await store.RenewPublicationLeaseAsync(command.IntegrationId,
                new IntegrationLeaseRenewRequest("integration-a", "integration-instance", claim.Lease!.LeaseId,
                    claim.Lease.Fence, 900), "integration-a", default);
            Assert.Equal(claim.Lease.Fence, renewed.Fence);
            Assert.True(renewed.ExpiresAt > claim.Lease.ExpiresAt);
            var stale = await Assert.ThrowsAsync<TaskServerConflictException>(() =>
                store.RenewPublicationLeaseAsync(command.IntegrationId,
                    new IntegrationLeaseRenewRequest("integration-a", "integration-instance", claim.Lease.LeaseId,
                        claim.Lease.Fence + 1), "integration-a", default));
            Assert.Equal("stale-fence", stale.Code);
            var report = await store.ReportIntegrationAsync(command.IntegrationId,
                new IntegrationReportRequest("integration-a", "integration-instance", claim.Lease!.LeaseId,
                    claim.Lease.Fence, $"integration:{claim.Lease.Fence}", IntegrationOutcomeKinds.Integrated,
                    IntegratedSha), "integration-a", default);
            Assert.Equal("5-human-review", report.TaskState);
            Assert.Equal("5-human-review", (await TaskAsync(store, projectId, task.TaskId)).State);
        }
        finally
        {
            await engine.StopAsync(default);
        }

        // Engine receipts only: every settlement applied a typed engine
        // decision, and nobody outside the hosts and the engine acted.
        var audit = await store.ListAuditAsync(loopStart, default);
        var settlements = audit.Where(record => record.Action == "orchestration.stage-completed").ToArray();
        Assert.Equal(6, settlements.Length);
        Assert.All(settlements, record =>
            Assert.DoesNotContain("task-server-compatibility", record.DetailJson, StringComparison.Ordinal));
        // ReviewDecision of both runs and Council of the passing run carry typed decisions.
        Assert.Equal(3, settlements.Count(record =>
            record.DetailJson.Contains("\"decider\":\"engine\"", StringComparison.Ordinal)));
        Assert.All(settlements, record => Assert.Equal("bootstrap-engine", record.ActorId));
        Assert.All(audit, record => Assert.Contains(record.ActorId,
            new[] { "coding-a", "review-a", "integration-a", "bootstrap-engine" }));
        Assert.Contains(audit, record => record.Action == "continuation.accepted"
            && record.DetailJson.Contains("review-finding-round", StringComparison.Ordinal));
        var runs = await store.ListOrchestrationRunsAsync(projectId, null, default);
        Assert.Equal(["integrating", "reissued"], runs.Select(run => run.Status).Order().ToArray());
        var decisions = runs.SelectMany(run => run.StageResults ?? [])
            .Where(result => result.OutputJson.Contains("\"transition\"", StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, decisions.Length);
        Assert.All(decisions, result =>
            Assert.Contains("orchestration-decisions:", result.OutputJson, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_passed_delivery_pushed_to_another_remote_escalates_instead_of_publishing()
    {
        using var workspace = new TempWorkspace("standalone-loop");
        await using var factory = new CutoverFactory(workspace.Path);
        using var bootstrap = factory.CreateClient();
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var (projectId, task) = await SetupAsync(store);
        var loopStart = (await store.ListAuditAsync(0, default)).Max(record => record.Sequence);

        await RunEngineAsync(factory, async () =>
        {
            await CodeAsync(store, null, FirstResultSha, "https://example.invalid/fork/product.git");
            await ReviewAsync(store, FirstResultSha, reject: false);
            await WaitForAsync(async () => (await TaskAsync(store, projectId, task.TaskId)).State == "5e-escalated");
        });

        // The settlement escalated once instead of queueing an integration
        // that would publish to the wrong remote or roll back forever.
        await AssertEscalatedOnceWithoutIntegrationAsync(store, projectId, task.TaskId, loopStart,
            "another remote than the registered project repository");
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task A_passed_source_bundle_only_delivery_escalates_once_without_queueing_an_integration()
    {
        using var workspace = new TempWorkspace("standalone-loop");
        var (resultSha, bundle) = await SourceBundleAsync(workspace.Path);
        await using var factory = new CutoverFactory(workspace.Path);
        using var bootstrap = factory.CreateClient();
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var (projectId, task) = await SetupAsync(store);
        var loopStart = (await store.ListAuditAsync(0, default)).Max(record => record.Sequence);

        await RunEngineAsync(factory, async () =>
        {
            // A valid result envelope carrying a source bundle and no fenced ref.
            var coded = await CodeAsync(store, null, resultSha, sourceBundle: bundle);
            Assert.Null(coded.Envelope.ImmutableRemoteRef);
            Assert.Equal("4-auto-review", (await TaskAsync(store, projectId, task.TaskId)).State);
            var subject = await ReviewAsync(store, resultSha, reject: false, async claim =>
            {
                using var transport = EngineTransport(factory);
                transport.DefaultRequestHeaders.Add(TaskServerProtocol.HeaderName,
                    TaskServerProtocol.Current.ToString());
                using var client = new TaskServerClient(transport, "review-a");
                var options = new RunnerOptions
                {
                    ServerUrl = "http://localhost", RunnerId = "review-a", RunnerName = "review-a",
                    Hostname = "review-host", BackendName = "review", Role = "review",
                    WorkDir = workspace.Combine("coding"), ReviewWorkDir = workspace.Combine("review"),
                    BaseBranch = "main",
                };
                var worker = new RemoteReviewWorkspace(options, claim.Subject!, claim.Lease!, _ => { });
                var proof = await worker.PrepareAsync(client, default);
                Assert.Equal(resultSha, proof.ActualHead);
            });
            Assert.Null(subject.ResultRef);
            Assert.NotNull(subject.SourceBundleArtifactId);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bundle)).ToLowerInvariant(), subject.SourceBundleSha256);
            await WaitForAsync(async () => (await TaskAsync(store, projectId, task.TaskId)).State == "5e-escalated");
        });

        await AssertEscalatedOnceWithoutIntegrationAsync(store, projectId, task.TaskId, loopStart,
            "no fenced result ref and SHA to publish");
    }

    /// <summary>
    /// The engine accepted the passed delivery, the Task Server classified it
    /// as unpublishable and escalated it in exactly one settlement: no
    /// integration command, no rolled-back or repeated settlement.
    /// </summary>
    private static async Task AssertEscalatedOnceWithoutIntegrationAsync(
        TaskServerStore store, string projectId, string taskId, long loopStart, string reason)
    {
        Assert.Equal("5e-escalated", (await TaskAsync(store, projectId, taskId)).State);
        Assert.Empty(await store.ListIntegrationsAsync(taskId, default));
        var run = Assert.Single(await store.ListOrchestrationRunsAsync(projectId, null, default));
        Assert.Equal("escalated", run.Status);
        var settlements = (await store.ListAuditAsync(loopStart, default))
            .Where(record => record.Action == "orchestration.stage-completed").ToArray();
        var escalation = Assert.Single(settlements, record =>
            record.DetailJson.Contains("\"nextStatus\":\"escalated\"", StringComparison.Ordinal));
        Assert.Contains(reason, escalation.DetailJson, StringComparison.Ordinal);
        // The later stage applies the transition the engine accepted at ReviewDecision.
        Assert.Contains("\"decider\":\"engine-stage-action\"", escalation.DetailJson, StringComparison.Ordinal);
        Assert.Equal("bootstrap-engine", escalation.ActorId);
        Assert.All(settlements, record =>
            Assert.DoesNotContain("task-server-compatibility", record.DetailJson, StringComparison.Ordinal));
        Assert.Same(escalation, settlements[^1]);
    }

    private static async Task RunEngineAsync(CutoverFactory factory, Func<Task> loop)
    {
        using var transport = EngineTransport(factory);
        using var engineClient = new EngineTaskServerClient(transport);
        using var engine = Engine(engineClient);
        await engine.StartAsync(default);
        try
        {
            await loop();
        }
        finally
        {
            await engine.StopAsync(default);
        }
    }

    /// <summary>Operator setup before the loop: project, repository, flow, review plan, hosts.</summary>
    private static async Task<(string ProjectId, TaskDto Task)> SetupAsync(TaskServerStore store)
    {
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Cutover"), "setup", default);
        var (repository, _) = await store.RegisterProjectRepositoryAsync(new RegisterProjectRepositoryRequest(
            "prj-cutover", workspace.WorkspaceId, "Cutover", "CUT", RepositoryUrl, IntegrationRef),
            "setup", default);
        var projectId = repository.ProjectId;
        await store.UpsertFlowDefinitionAsync(projectId, new UpsertFlowDefinitionRequest(null,
        [
            OrchestrationStage.ReviewDecision, OrchestrationStage.Council, OrchestrationStage.PostProcessing,
            OrchestrationStage.GateDispatch, OrchestrationStage.CompletionJudge,
        ]), "setup", default);
        await store.UpsertProjectReviewPlanAsync(projectId,
            new UpsertProjectReviewPlanRequest(null, "cutover-policy-v1", ReviewPlan()), "setup", default);
        var task = await store.CreateTaskAsync(projectId,
            new CreateTaskRequest("Standalone loop", "Ship the feature.", "2-ready"), "setup", default);
        await store.RegisterRunnerAsync("coding-a", new RegisterRunnerRequest(
            "coding-a", "coding-host", "coding-instance", "1.0.0", TaskServerProtocol.Current,
            [ReviewCapabilities.CodingExecutor]), "setup", default);
        await store.RecordProjectRepositoryProbeAsync(projectId, "coding-a",
            new ProjectRepositoryProbeRequest(RepositoryUrl, RepositoryUrl, true, true, false), default);
        await store.RegisterRunnerAsync("review-a", new RegisterRunnerRequest(
            "review-a", "review-host", "review-instance", "1.0.0", TaskServerProtocol.Current,
            [
                ReviewCapabilities.ReviewExecutor, ReviewCapabilities.GitMaterialization,
                ReviewCapabilities.SemanticReview, ReviewCapabilities.BaselineComparison,
                ReviewCapabilities.DependencyPreparation, ReviewCapabilities.LibraryStepV1,
                ReviewCapabilities.SourceBundleMaterialization,
            ], BootstrapMaxParallelism: 2), "setup", default);
        await store.RegisterRunnerAsync("integration-a", new RegisterRunnerRequest(
            "integration-a", "coding-host", "integration-instance", "1.0.0", TaskServerProtocol.Current,
            [IntegrationCapabilities.Executor]), "setup", default);
        return (projectId, task);
    }

    private static HttpClient EngineTransport(CutoverFactory factory)
    {
        var transport = factory.CreateClient();
        transport.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", CutoverFactory.EngineToken);
        // The engine's own HTTP client names itself the same way.
        transport.DefaultRequestHeaders.Add("X-Client-Id", "bootstrap-engine");
        return transport;
    }

    private static OrchestratorEngineService Engine(EngineTaskServerClient engineClient)
    {
        var engineOptions = EngineOptions.Parse(key => key switch
        {
            "SERVER_URL" => "http://localhost",
            "CLIENT_ID" => "bootstrap-engine",
            "POLL_SECONDS" => "1",
            _ => null,
        });
        return new OrchestratorEngineService(engineOptions, engineClient,
            [
                new ReviewDecisionOrchestratorLoop(), new CouncilLoop(), new PostProcessingLoop(),
                new GateDispatchLoop(), new CompletionJudgeLoop(), new OperatorSweepLoop(),
            ],
            NullLogger<OrchestratorEngineService>.Instance);
    }

    private static ReviewPlanDto ReviewPlan()
    {
        string[] aspects = ["build-tests", "requirements"];
        return new ReviewPlanDto(
            aspects.Select(aspect => new ReviewCommandDto($"step-{aspect}", aspect, "review-tool", [aspect])).ToArray(),
            aspects);
    }

    private static async Task<(string ResultSha, byte[] Bundle)> SourceBundleAsync(string root)
    {
        var repository = Path.Combine(root, "bundle-source");
        Directory.CreateDirectory(repository);
        async Task<string> Git(params string[] args)
        {
            var result = await ProcessRunner.RunAsync("git", args, repository);
            Assert.True(result.Success, $"git {string.Join(' ', args)} failed: {result.StdErr}");
            return result.StdOut.Trim();
        }
        await Git("init", "-q");
        await File.WriteAllTextAsync(Path.Combine(repository, "product.txt"), "bundle delivery\n");
        await Git("add", "product.txt");
        await Git("-c", "user.name=Test", "-c", "user.email=test@example.invalid",
            "commit", "-q", "-m", "bundle delivery");
        var sha = await Git("rev-parse", "HEAD");
        await Git("bundle", "create", "source.bundle", "--all");
        return (sha, await File.ReadAllBytesAsync(Path.Combine(repository, "source.bundle")));
    }

    private sealed record CodingRound(ClaimResponse Claim, RunDto Run, ImmutableResultEnvelope Envelope);

    private static async Task<CodingRound> CodeAsync(
        TaskServerStore store, ImmutableResultEnvelope? continuesOn, string resultSha,
        string repositoryUrl = RepositoryUrl, byte[]? sourceBundle = null)
    {
        var claim = await store.ClaimAsync(new ClaimRequest("coding-a", "coding-instance"), "coding-a", default);
        Assert.Equal("claimed", claim.Status);
        var run = claim.Run!;
        var lease = claim.Lease!;
        if (claim.FollowUp is { } followUp)
            await store.RenewLeaseAsync(run.RunId, new LeaseRenewRequest("coding-a", "coding-instance",
                lease.LeaseId, lease.Fence, StartedPromptSha256: followUp.PromptSha256), "coding-a", default);
        var sourceBundleOnly = sourceBundle is not null;
        var bundleDigest = sourceBundleOnly
            ? Convert.ToHexString(SHA256.HashData(sourceBundle!)).ToLowerInvariant() : null;
        if (sourceBundleOnly)
            await store.IngestArtifactAsync(run.RunId, new AgentStudio.TaskServer.Contracts.ArtifactIngestRequest(
                $"bundle-{run.RunId}", "source.bundle", "application/x-git-bundle",
                Convert.ToBase64String(sourceBundle!), bundleDigest!, $"bundle:{run.RunId}", lease.Fence,
                "coding-a", "coding-instance", lease.LeaseId, 1), "coding-a", default);
        var envelope = new ImmutableResultEnvelope(
            RepositoryIdentityContract.FromUrl(RepositoryUrl)!, run.RunId,
            continuesOn?.ResultSha ?? new string('0', 40), resultSha,
            sourceBundleOnly ? null : FencedGitRefs.ImmutableResult(run.RunId, lease.Fence, resultSha),
            bundleDigest, new string('a', 64),
            RepositoryUrl: repositoryUrl);
        var digest = ResultEnvelopeDigest.Compute(envelope);
        await store.AcknowledgeResultHandoffAsync(run.RunId, new ResultHandoffRequest(
            "coding-a", "coding-instance", lease.LeaseId, lease.Fence, sourceBundleOnly ? 2 : 1,
            $"handoff:{run.RunId}:{digest}", digest, envelope), "coding-a", default);
        var completed = await store.CompleteRunAsync(run.RunId, new CompleteRunRequest(
            "coding-a", "coding-instance", lease.LeaseId, lease.Fence, "success", "done", digest,
            $"completion:{run.RunId}:{digest}", sourceBundleOnly ? 3 : 2), "coding-a", default);
        return new CodingRound(claim, completed, envelope);
    }

    private static async Task<ReviewSubjectDto> ReviewAsync(TaskServerStore store, string resultSha,
        bool reject, Func<ReviewClaimResponse, Task>? beforeReport = null)
    {
        var claim = await store.ClaimReviewAsync(
            new ReviewClaimRequest("review-a", "review-instance"), "review-a", default);
        Assert.Equal("claimed", claim.Status);
        var subject = claim.Subject!;
        Assert.Equal(resultSha, subject.ExpectedResultSha);
        Assert.Equal("cutover-policy-v1", subject.ReviewPolicyHash);
        if (beforeReport is not null) await beforeReport(claim);
        var report = Report(claim, resultSha, reject);
        var settled = await store.ReportReviewAsync(claim.Attempt!.AttemptId, report, "review-a", default);
        Assert.Equal(reject ? "ProductFailure" : "Pass", settled.Outcome);
        await store.CleanupReviewAsync(claim.Attempt.AttemptId, new ReviewCleanupRequest(
            "review-a", "review-instance", claim.Lease!.LeaseId, claim.Lease.Fence,
            $"cleanup-{claim.Attempt.AttemptId}", true), "review-a", default);
        return subject;
    }

    private static ReviewReportRequest Report(ReviewClaimResponse claim, string resultSha, bool reject)
    {
        var subject = claim.Subject!;
        var lease = claim.Lease!;
        var workspacePath = $"/review/{lease.ResourceNamespace}";
        var commands = subject.Plan.Commands.Select(command =>
        {
            var evidence = new ReviewCommandEvidenceDto(
                command.StepId, command.Aspect, command.FileName, command.Arguments,
                resultSha, resultSha, TreeSha,
                lease.AcquiredAt, lease.AcquiredAt, 0, null,
                new string('a', 64), new string('b', 64),
                ExecutionKind: command.ExecutionKind,
                ExecutionLocation: "remote",
                ExecutorId: lease.ExecutorId,
                HostId: lease.HostId,
                AttemptId: claim.Attempt!.AttemptId,
                LibraryStep: command.LibraryStep);
            return reject && command.Aspect == "build-tests"
                ? evidence with
                {
                    ExitCode = 1,
                    BaselineSha = new string('c', 40),
                    BaselineExitCode = 0,
                    Diagnosis = new DeliveryFailureDiagnosisResult(
                        DeliveryFailureDiagnosis.Product, 1, ["clean repeat reproduced the failure"]),
                }
                : evidence;
        }).ToList();
        if (reject)
            commands.Add(commands.Single(command => command.Aspect == "build-tests") with
            {
                Phase = "clean-repeat",
                WorkspaceRole = "clean-repeat",
                BaselineSha = null,
                BaselineExitCode = null,
                Diagnosis = null,
            });
        var verdicts = subject.Plan.RequiredAspects.Select(aspect =>
            reject && aspect == "build-tests"
                ? new ReviewVerdictDto(aspect, "block", "ReviewCommandFailed",
                    "The build and test command failed on this delivery.")
                : new ReviewVerdictDto(aspect, "pass", "Verified", $"{aspect} passed")).ToArray();
        var toolchain = new Dictionary<string, string>
        {
            ["runtime"] = ".NET 10",
            ["git"] = "git;sha256=" + new string('d', 64),
        };
        foreach (var command in subject.Plan.Commands)
            toolchain[$"command:{command.StepId}"] = command.FileName + ";sha256=" + new string('e', 64);
        var artifacts = commands.SelectMany(command => new[]
        {
            new ReviewArtifactEvidenceDto($"{command.StepId}.stdout.log", "text/plain", command.StdoutSha256, 1),
            new ReviewArtifactEvidenceDto($"{command.StepId}.stderr.log", "text/plain", command.StderrSha256, 1),
        }).DistinctBy(artifact => artifact.Name).ToArray();
        return new ReviewReportRequest(
            lease.ExecutorId, lease.InstanceId, lease.LeaseId, lease.Fence,
            // The server derives ProductFailure from the command evidence.
            $"report-{claim.Attempt!.AttemptId}", "Pass", null,
            reject ? "build-tests failed" : "all review aspects passed",
            new ReviewWorkspaceProofDto(
                subject.RepositoryId, resultSha, resultSha, TreeSha, false, false,
                Hash(workspacePath), lease.ResourceNamespace),
            new ReviewEnvironmentDto(
                lease.HostId, lease.ExecutorId, lease.InstanceId, "linux", "x64", "10.0", toolchain,
                new Dictionary<string, string>
                {
                    ["workspace"] = workspacePath,
                    ["cache"] = $"{workspacePath}/cache",
                    ["temp"] = $"{workspacePath}/tmp",
                    ["ports"] = $"{lease.PortBase}-{lease.PortBase + 7}",
                    ["containers"] = lease.ResourceNamespace,
                    ["databases"] = lease.ResourceNamespace,
                    ["credentials"] = "review-read-only",
                }),
            commands,
            artifacts,
            verdicts);
    }

    private static async Task<TaskDto> TaskAsync(TaskServerStore store, string projectId, string taskId)
        => (await store.GetTaskAsync(projectId, taskId, default))!;

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (!await condition())
        {
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(60), "The engine did not settle the loop within 60 seconds.");
            await Task.Delay(100);
        }
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class CutoverFactory(string directory) : WebApplicationFactory<TaskServerAssembly::Program>
    {
        public const string EngineToken = "engine-cutover-test-token-0000000000000000001";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AUTH"] = "bearer",
                    ["TaskServer:DataDirectory"] = directory,
                    ["TaskServer:ListenUrl"] = string.Empty,
                    ["ENGINE_AUTH_TOKEN"] = EngineToken,
                }));
        }
    }
}
