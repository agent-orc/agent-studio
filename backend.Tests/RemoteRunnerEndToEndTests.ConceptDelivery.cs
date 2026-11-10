extern alias Runner;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgentStudio.Pipeline;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Contract = AgentStudio.TaskServer.Contracts;
using RClient = Runner::AgentRunner.TaskServerClient;
using RClaim = Runner::AgentRunner.RunnerClaimRequest;
using RRemoteComplete = Runner::AgentRunner.RemoteRunCompletionRequest;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3696: a concept card executed by a Remote runner reaches the same
/// Dossier placement record, deterministic concept review, and sight-review
/// marker as the retired local post-steps, with ReviewDecisionOrchestrator
/// disabled (the factory default). The runner's real client claims the card,
/// a stub CLI authors the Dossier, the fenced result and completion go over the
/// runner wire protocol, a Remote Review passes, and the reviewed task-branch
/// merge integrates the Dossier before promote-concept creates the cards.
/// </summary>
public sealed partial class RemoteRunnerEndToEndTests
{
    private const string ConceptTopic = "remote-concept-flow";
    private const string ConceptEntrypoint = "docs/" + ConceptTopic + "/index.html";

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task Remote_concept_run_creates_dossier_parks_for_sight_review_and_promotes()
    {
        var flow = await RunRemoteConceptAsync(touchProductCode: false);
        using var factory = flow.Factory;
        using var http = flow.Http;
        var ct = CancellationToken.None;

        Assert.True(flow.ReportedState == TaskStates.HumanReview, flow.ReportDetail);
        var scanner = factory.Services.GetRequiredService<TaskScannerService>();
        var card = scanner.FindJob(flow.TaskKey, _watchPath)!;
        Assert.Equal(TaskStates.HumanReview, card.State);

        // The Dossier reached develop through the reviewed task-branch merge,
        // which lands on the Studio integration lane of the developer checkout.
        var developTip = (await GitAsync(flow.Repository, "rev-parse", "refs/agent-studio/integration/develop")).StdOut.Trim();
        var onDevelop = await GitAsync(flow.Repository, ["cat-file", "-e", $"{developTip}:{ConceptEntrypoint}"], allowFailure: true);
        Assert.Equal(0, onDevelop.ExitCode);

        // Placement record, deterministic concept review, sight-review marker.
        var record = ConceptWorkbenchStore.Read(card.FolderPath);
        Assert.NotNull(record);
        Assert.Equal("docs/" + ConceptTopic, record!.RepoRelativeDirectory);
        Assert.Equal(ConceptEntrypoint, record.RepoRelativeEntrypoint);
        Assert.Equal(developTip, record.CommitSha);
        var steps = factory.Services.GetRequiredService<PipelineExecutionLog>()
            .Read(card.FolderPath)!.Steps.ToDictionary(step => step.StepId);
        Assert.Equal(PipelineStepStatus.Passed, steps[PipelineCatalogue.ConceptWorkbenchPlacementStepId].Status);
        Assert.Equal(PipelineStepStatus.Passed, steps[PipelineCatalogue.ConceptReviewStepId].Status);
        Assert.Equal(PipelineStepStatus.Running, steps[PipelineCatalogue.ConceptSightReviewGateStepId].Status);
        Assert.Equal(SteerPendingKinds.ConceptSightReview, SteerPendingMarker.TryRead(card.FolderPath)?.Kind);
        Assert.DoesNotContain("failed", card.ParkedBlocker?.Reason ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        // The developer checkout still sits on main, so promotion reads the
        // Dossier copy that placement stored at the integrated commit.
        Assert.False(File.Exists(Path.Combine(flow.Repository, ConceptEntrypoint)));
        using var plan = await http.GetAsync($"/api/tasks/{flow.TaskKey}/promote-concept?project={ProjectName}", ct);
        Assert.True(plan.StatusCode == HttpStatusCode.OK, await plan.Content.ReadAsStringAsync(ct));
        using var promoted = await http.PostAsJsonAsync(
            $"/api/tasks/{flow.TaskKey}/promote-concept?project={ProjectName}",
            new PromoteConceptRequest(), ct);
        var promotedBody = await promoted.Content.ReadAsStringAsync(ct);
        Assert.True(promoted.StatusCode == HttpStatusCode.OK, promotedBody);
        using var created = JsonDocument.Parse(promotedBody);
        Assert.Equal(1, created.RootElement.GetProperty("created").GetArrayLength());
        Assert.Equal(TaskStates.Completed, scanner.FindJob(flow.TaskKey, _watchPath)!.State);
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task Remote_concept_run_that_touches_product_code_is_refused_by_dossier_placement()
    {
        var flow = await RunRemoteConceptAsync(touchProductCode: true);
        using var factory = flow.Factory;
        using var http = flow.Http;

        // Nothing was merged, so Human Review stays closed: the card waits in
        // Auto Review with a typed integration failure that names the step.
        Assert.True(flow.ReportedState == TaskStates.AutoReview, flow.ReportDetail);
        Assert.Equal(flow.BaseSha, (await GitAsync(flow.Repository, "rev-parse", "origin/develop")).StdOut.Trim());
        var scanner = factory.Services.GetRequiredService<TaskScannerService>();
        var integrating = scanner.FindJob(flow.TaskKey, _watchPath)!;
        var status = factory.Services.GetRequiredService<TaskIntegrationStatusService>()
            .BuildLookup([integrating]).Values.Single();
        Assert.Equal(AcceptedIntegrationFailureCodes.ConceptDossierRejected, status.Failure?.Code);
        Assert.Equal("Dossier placement failed", status.Failure?.Label);

        // The delivery-chain reconciler escalates it with that reason.
        await factory.Services.GetRequiredService<DeliveryChainReconciler>().RunOnceAsync();
        var card = scanner.FindJob(flow.TaskKey, _watchPath)!;
        Assert.Equal(TaskStates.Escalated, card.State);
        var parkReason = card.ParkedBlocker?.Reason ?? string.Empty;
        Assert.Contains(ConceptDeliveryPostSteps.PlacementParkPrefix, parkReason);
        Assert.Contains("README.md", parkReason);
        Assert.Null(ConceptWorkbenchStore.Read(card.FolderPath));
        var placement = factory.Services.GetRequiredService<PipelineExecutionLog>()
            .Read(card.FolderPath)!.Steps.Single(step => step.StepId == PipelineCatalogue.ConceptWorkbenchPlacementStepId);
        Assert.Equal(PipelineStepStatus.Failed, placement.Status);
        Assert.Equal(AcceptedIntegrationFailureCodes.ConceptDossierRejected, placement.FailureCode);
        Assert.Null(SteerPendingMarker.TryRead(card.FolderPath));
    }

    private sealed record RemoteConceptFlow(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Factory,
        HttpClient Http,
        string TaskKey,
        string Repository,
        string BaseSha,
        string? ReportedState,
        string ReportDetail);

    /// <summary>
    /// Create a concept card through the API, run it on the real Remote runner
    /// with a stub CLI that writes the Dossier, and settle a passing Remote
    /// Review with the concept-fit verdict.
    /// </summary>
    private async Task<RemoteConceptFlow> RunRemoteConceptAsync(bool touchProductCode)
    {
        var ct = CancellationToken.None;
        var origin = await SeedOriginAsync();
        await GitAsync(origin, "branch", "develop", "main");
        var repository = Path.Combine(_workspace, "integration-repository");
        await GitAsync(_workspace, "clone", origin, repository);
        await GitAsync(repository, "config", "user.name", "Remote Concept Test");
        await GitAsync(repository, "config", "user.email", "remote-concept@example.invalid");
        var baseSha = (await GitAsync(repository, "rev-parse", "origin/develop")).StdOut.Trim();

        var factory = BuildFactory(repositoryPath: repository);
        var http = factory.CreateClient();
        var api = factory.CreateClient();
        api.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);
        factory.Services.GetRequiredService<IConfiguration>()["AcceptanceRail:Enabled"] = "false";
        Assert.False(factory.Services.GetRequiredService<IConfiguration>()
            .GetValue("ReviewDecisionOrchestrator:Enabled", false));
        factory.Services.GetRequiredService<ProjectSettingsService>()
            .SetIntegrationBranch(ProjectName, "develop");

        // Create.
        using var create = await api.PostAsJsonAsync("/api/tasks", new
        {
            title = "Remote concept flow",
            project = "PROJ-001",
            mode = TaskModes.Concept,
            targetState = TaskStates.Ready,
            promptMarkdown = "Write the concept Dossier for the remote concept flow.",
        }, ct);
        var createdBody = await create.Content.ReadAsStringAsync(ct);
        Assert.True(create.IsSuccessStatusCode, createdBody);
        var scanner = factory.Services.GetRequiredService<TaskScannerService>();
        var card = scanner.ScanAllAutomationJobs().Single(job => TaskModes.IsConcept(job.Mode));
        var taskKey = card.Key ?? card.TaskKey;

        // The stub CLI copies a contract-valid Dossier into its worktree and
        // names it in its results, as a concept agent does.
        var staged = Path.Combine(_workspace, "staged-dossier");
        var dossier = ConceptWorkbenchContract.CreateScaffold(
            staged, ConceptTopic, "Remote concept flow", "How a remote concept is delivered.", card.Key ?? card.Id);
        AddImplementationTask(Path.Combine(dossier, ConceptWorkbenchContract.DescriptorFileName));
        var cli = WriteConceptStubCli(Path.Combine(_workspace, "cli"), dossier, touchProductCode);

        using var client = new RClient(http, RunnerId);
        await RegisterCodingRunnerAsync(client, http);
        await AssignRemoteAsync(http);
        await AddRepositoryUrlAsync(http, "https://github.com/example/remote-concept.git");
        var claim = await ClaimWithSuccessfulPreflightAsync(client, new RClaim(
            RunnerId, ProjectName, "concept-host", 4242, "remote-runner"));
        Assert.Equal(taskKey, claim.TaskKey);
        var lease = claim.Lease!;

        // The agent step. The standalone runner executes its CLI in a detached
        // worker process, which cannot load from this test host's output
        // folder (it carries the backend's Microsoft.Extensions 10 assemblies,
        // the worker needs 9). The test therefore runs the same stub CLI in the
        // delivery checkout itself and then performs the runner's wire steps:
        // fenced result push and out-of-band completion.
        await GitAsync(repository, "checkout", "-b", "concept-work", "origin/develop");
        await RunStubCliAsync(cli, repository, TaskPaths.ResultsDir(scanner.FindJob(taskKey, _watchPath)!.FolderPath));
        await GitAsync(repository, "add", "--all");
        await GitAsync(repository, "commit", "-m", "docs: remote concept Dossier");
        var resultSha = (await GitAsync(repository, "rev-parse", "HEAD")).StdOut.Trim();
        var immutableRef = Contract.FencedGitRefs.ImmutableResult(lease.AttemptId!, lease.FencingToken, resultSha);
        await GitAsync(repository, "push", "origin", $"HEAD:{immutableRef}");
        await GitAsync(repository, "checkout", "main");
        var completion = await client.CompleteRunAsync(new RRemoteComplete(
            taskKey,
            lease.LeaseId,
            lease.FencingToken,
            RunnerId,
            "Done",
            ResultSha: resultSha,
            AttemptChainId: lease.LeaseId,
            Repository: origin,
            AttemptId: lease.AttemptId,
            AuthorityEpoch: lease.AuthorityEpoch,
            IdempotencyKey: "remote-concept-completion",
            BaseSha: baseSha,
            ImmutableResultRef: immutableRef,
            ArtifactManifestDigest: new string('a', 64),
            IntegrationBranch: "refs/heads/develop"), ct);
        Assert.Equal(TaskStates.AutoReview, completion!.TargetState);
        Assert.Equal(TaskStates.AutoReview, scanner.FindJob(taskKey, _watchPath)!.State);

        // Remote Review: the concept plan carries the concept-fit aspect.
        const string reviewRunnerId = "review-runner-concept";
        const string reviewInstance = "review-host:concept";
        await RegisterReviewExecutorAsync(http, reviewRunnerId, reviewInstance);
        using var reviewClient = new RClient(http, reviewRunnerId, usesDurableTaskServer: true);
        var review = await reviewClient.ClaimReviewAsync(
            new Contract.ReviewClaimRequest(reviewRunnerId, reviewInstance, 120), ct);
        Assert.Equal("claimed", review.Status);
        Assert.Contains(review.Subject!.Plan.Commands, command => command.Aspect == "concept-fit");
        var report = PassingV1ReviewReport(review, "remote-concept-review") with
        {
            Summary = "The concept Dossier is complete.",
            Commands = SealedPlanEvidence(review),
            Verdicts = review.Subject.Plan.Commands
                .Select(command => command.Aspect)
                .Distinct(StringComparer.Ordinal)
                .Select(aspect => new Contract.ReviewVerdictDto(aspect, "pass", "Verified", $"{aspect} passed."))
                .ToArray(),
        };
        report = report with
        {
            Workspace = report.Workspace with
            {
                IntegrationRef = "refs/heads/develop",
                MergeBaseSha = baseSha,
                IntegrationTipSha = baseSha,
            },
        };
        var settled = await reviewClient.ReportReviewAsync(review.Attempt!.AttemptId, report, ct);
        http.Dispose();
        var settledCard = scanner.FindJob(taskKey, _watchPath)!;
        var pipelineFile = Path.Combine(settledCard.FolderPath, PipelineExecutionLog.FileName);
        var refs = (await GitAsync(repository, "for-each-ref", "--format=%(refname) %(objectname:short)")).StdOut;
        var worktrees = (await GitAsync(repository, "worktree", "list")).StdOut;
        var detail = $"{settled.Outcome} {settled.FailureClassification} {settled.Summary}\n{refs}\n{worktrees}\n"
            + (File.Exists(pipelineFile) ? File.ReadAllText(pipelineFile) : "no pipeline record");
        return new RemoteConceptFlow(factory, api, taskKey, repository, baseSha, settled.TaskState, detail);
    }

    private static void AddImplementationTask(string descriptorPath)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        var descriptor = JsonSerializer.Deserialize<ConceptWorkbenchDescriptor>(
            File.ReadAllText(descriptorPath), options)!;
        File.WriteAllText(descriptorPath, JsonSerializer.Serialize(descriptor with
        {
            ImplementationTasks =
            [
                new ConceptImplementationTask
                {
                    Title = "Implement the remote concept flow",
                    PromptMarkdown = "Add the approved remote concept flow endpoint.",
                },
            ],
        }, options));
    }

    private static async Task RunStubCliAsync(string cli, string workingDirectory, string resultsDirectory)
    {
        var start = new System.Diagnostics.ProcessStartInfo(cli)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.Environment["JOB_RESULTS_DIR"] = resultsDirectory;
        using var process = System.Diagnostics.Process.Start(start)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"stub CLI failed: {stderr}");
        Assert.Contains("[[TASK_DONE]]", stdout);
    }

    private static string WriteConceptStubCli(string directory, string dossier, bool touchProductCode)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The concept CLI stub is a Linux runner-host shell script.");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "concept-claude-stub.sh");
        var target = "docs/" + ConceptTopic;
        var body = new StringBuilder("#!/bin/sh\n")
            .Append("if [ \"$1\" = \"--version\" ]; then printf 'claude-test 1.0\\n'; exit 0; fi\n")
            .Append("mkdir -p ").Append(target).Append('\n')
            .Append("cp '").Append(dossier).Append("/workbench.json' ").Append(target).Append("/\n")
            .Append("cp '").Append(dossier).Append("/index.html' ").Append(target).Append("/\n");
        if (touchProductCode) body.Append("printf 'product change\\n' >> README.md\n");
        body.Append("mkdir -p \"$JOB_RESULTS_DIR\"\n")
            .Append("printf '# Deliverables\\n\\n- ").Append(ConceptEntrypoint).Append("\\n' > \"$JOB_RESULTS_DIR/deliverables.md\"\n")
            .Append("printf 'delivered ").Append(ConceptEntrypoint).Append("\\n[[TASK_DONE]]\\n'\n");
        File.WriteAllText(path, body.ToString(), new UTF8Encoding(false));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
}
