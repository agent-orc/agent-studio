using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace TaskServer.Tests;

/// <summary>
/// Covers the Studio P3 "administration and long tail" bundle end to end
/// over HTTP: every route reads a table a P0-P2 bundle already owns (this
/// bundle added no migration), so most tests write through the existing P2
/// route and read back through the new P3 route.
/// </summary>
public sealed class StudioP3AdministrationEndpointsTests
{
    [Fact]
    public async Task Admin_config_and_prompt_overrides_are_readable_after_being_written()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioP3ApiFactory(temp.Path);
        using var client = Client(factory);

        var beforeConfig = await client.GetFromJsonAsync<OrchestratorConfigDto>("/api/v1/studio/admin/config/orchestrator");
        Assert.Equal("{}", beforeConfig!.ConfigJson);

        var putConfig = await client.PutAsJsonAsync(
            "/api/v1/studio/admin/config/orchestrator", new UpdateOrchestratorConfigRequest("""{"a":1}"""));
        putConfig.EnsureSuccessStatusCode();
        var afterConfig = await client.GetFromJsonAsync<OrchestratorConfigDto>("/api/v1/studio/admin/config/orchestrator");
        Assert.Equal("""{"a":1}""", afterConfig!.ConfigJson);

        var emptyList = await client.GetFromJsonAsync<PromptOverrideListResponse>("/api/v1/studio/admin/prompts");
        Assert.Empty(emptyList!.Items);
        var emptyCoverage = await client.GetFromJsonAsync<PromptOverrideCoverageResponse>("/api/v1/studio/admin/prompts/coverage");
        Assert.Equal(0, emptyCoverage!.TotalOverrides);

        var putOverride = await client.PutAsJsonAsync(
            "/api/v1/studio/admin/prompts/commit-message.md", new UpdatePromptOverrideRequest("Custom commit prompt"));
        putOverride.EnsureSuccessStatusCode();

        var list = await client.GetFromJsonAsync<PromptOverrideListResponse>("/api/v1/studio/admin/prompts");
        var stored = Assert.Single(list!.Items);
        Assert.Equal("commit-message.md", stored.Name);
        Assert.Equal("Custom commit prompt", stored.OverrideContent);

        var detail = await client.GetFromJsonAsync<PromptOverrideDto>("/api/v1/studio/admin/prompts/commit-message.md");
        Assert.Equal("Custom commit prompt", detail!.OverrideContent);

        var missing = await client.GetAsync("/api/v1/studio/admin/prompts/does-not-exist.md");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var coverage = await client.GetFromJsonAsync<PromptOverrideCoverageResponse>("/api/v1/studio/admin/prompts/coverage");
        Assert.Equal(1, coverage!.TotalOverrides);
        Assert.Equal(0, coverage.ReviewedOverrides);
        Assert.Equal(1, coverage.PendingReviewOverrides);

        var review = await client.PostAsync("/api/v1/studio/admin/prompts/commit-message.md/review", content: null);
        review.EnsureSuccessStatusCode();
        var afterReview = await client.GetFromJsonAsync<PromptOverrideCoverageResponse>("/api/v1/studio/admin/prompts/coverage");
        Assert.Equal(1, afterReview!.ReviewedOverrides);
        Assert.Equal(0, afterReview.PendingReviewOverrides);
    }

    [Fact]
    public async Task Auto_review_status_lists_tasks_currently_in_the_auto_review_lane()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioP3ApiFactory(temp.Path);
        using var client = Client(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();

        var empty = await client.GetFromJsonAsync<AutoReviewStatusResponse>("/api/v1/studio/auto-review/status");
        Assert.Equal(0, empty!.ActiveCount);

        var (_, project, task) = await SeedTaskAsync(store, "Auto Review Task");
        await store.MoveTaskAsync(project.ProjectId, task.TaskId, new MoveTaskRequest(StudioTaskLanes.AutoReview), "test", default);

        var status = await client.GetFromJsonAsync<AutoReviewStatusResponse>("/api/v1/studio/auto-review/status");
        Assert.Equal(1, status!.ActiveCount);
        var active = Assert.Single(status.Active);
        Assert.Equal(task.TaskId, active.TaskId);
        Assert.Equal(project.ProjectId, active.ProjectId);
    }

    [Fact]
    public async Task Cli_quota_and_model_routing_reads_reflect_the_p2_singleton_and_the_static_policy_document()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioP3ApiFactory(temp.Path);
        using var client = Client(factory);

        (await client.PutAsJsonAsync("/api/v1/studio/cli/quota/caps", new SetQuotaCapsRequest("""{"claude":95}"""))).EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync("/api/v1/studio/cli/quota/model-routes", new SetQuotaModelRoutesRequest("""{"claude":"opus"}"""))).EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync("/api/v1/studio/cli/quota/wait-policy", new SetCliQuotaWaitPolicyRequest("""{"mode":"wait"}"""))).EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync("/api/v1/studio/cli/model-routing/economy-mode", new SetEconomyModeRequest(true))).EnsureSuccessStatusCode();

        var caps = await client.GetFromJsonAsync<CliSettingsDto>("/api/v1/studio/cli/quota/caps");
        Assert.Equal("""{"claude":95}""", caps!.QuotaCapsJson);
        var routes = await client.GetFromJsonAsync<CliSettingsDto>("/api/v1/studio/cli/quota/model-routes");
        Assert.Equal("""{"claude":"opus"}""", routes!.QuotaModelRoutesJson);
        var waitPolicy = await client.GetFromJsonAsync<CliSettingsDto>("/api/v1/studio/cli/quota/wait-policy");
        Assert.Equal("""{"mode":"wait"}""", waitPolicy!.QuotaWaitPolicyJson);

        var policy = await client.GetFromJsonAsync<ModelRoutingPolicyDto>("/api/v1/studio/cli/model-routing/policy");
        Assert.NotEmpty(policy!.Tiers);
        Assert.Contains(policy.TaskTypeDefaults, item => item.TaskType == "chore");
        Assert.True(policy.EconomyMode);

        var recommendation = await client.GetFromJsonAsync<ModelRoutingRecommendationDto>(
            "/api/v1/studio/cli/model-routing/recommendation?taskType=feature");
        Assert.Equal("feature", recommendation!.TaskType);
        Assert.NotEmpty(recommendation.Model);
    }

    [Fact]
    public async Task Project_scoped_resolved_settings_reflect_p2_writes_and_report_the_available_choices()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioP3ApiFactory(temp.Path);
        using var client = Client(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var project = await SeedProjectAsync(store, "Resolved Settings Project", "RSP");
        var basePath = $"/api/v1/studio/projects/{project.ProjectId}";

        (await client.PutAsJsonAsync($"{basePath}/cli-mode", new SetCliModeRequest("yolo"))).EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync($"{basePath}/cli-context-mode", new SetCliContextModeRequest("clean"))).EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync($"{basePath}/lane-sort-strategy", new SetLaneSortStrategyRequest("manual"))).EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync($"{basePath}/quota-wait-policy", new SetQuotaWaitPolicyRequest("""{"mode":"wait"}"""))).EnsureSuccessStatusCode();

        var cliMode = await client.GetFromJsonAsync<ResolvedOptionListDto>($"{basePath}/cli-modes");
        Assert.Equal("yolo", cliMode!.Resolved);
        Assert.Contains("yolo", cliMode.Available);

        var cliContextMode = await client.GetFromJsonAsync<ResolvedOptionListDto>($"{basePath}/cli-context-modes");
        Assert.Equal("clean", cliContextMode!.Resolved);
        Assert.Contains("shared", cliContextMode.Available);

        var laneSort = await client.GetFromJsonAsync<ResolvedOptionListDto>($"{basePath}/lane-sort-strategies");
        Assert.Equal("manual", laneSort!.Resolved);
        Assert.Contains("lane-entry", laneSort.Available);

        var quotaWaitPolicy = await client.GetFromJsonAsync<ProjectQuotaWaitPolicyDto>($"{basePath}/quota-wait-policy");
        Assert.Equal("""{"mode":"wait"}""", quotaWaitPolicy!.PolicyJson);

        var missing = await client.GetAsync("/api/v1/studio/projects/does-not-exist/cli-modes");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task All_project_settings_lists_every_project_keyed_by_id()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioP3ApiFactory(temp.Path);
        using var client = Client(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var first = await SeedProjectAsync(store, "First Project", "FST");
        var second = await SeedProjectAsync(store, "Second Project", "SND");
        await store.SetOrchestratorModelAsync(first.ProjectId, new SetOrchestratorModelRequest("gpt-5"), "test", default);

        var all = await client.GetFromJsonAsync<AllProjectSettingsResponse>("/api/v1/studio/projects/settings");
        Assert.Equal(2, all!.Projects.Count);
        Assert.Equal("gpt-5", all.Projects[first.ProjectId].OrchestratorModel);
        Assert.Equal("default", all.Projects[second.ProjectId].OrchestratorModel);
    }

    [Fact]
    public async Task Proposals_list_and_evidence_are_readable_once_generated()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioP3ApiFactory(temp.Path);
        using var client = Client(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var project = await SeedProjectAsync(store, "Proposals Project", "PRP");

        var generate = await client.PostAsJsonAsync(
            $"/api/v1/studio/projects/{project.ProjectId}/proposals/generate", new { });
        Assert.Equal(HttpStatusCode.Accepted, generate.StatusCode);
        var resultJson = JsonSerializer.Serialize(new
        {
            proposals = new[] { new { title = "Proposal A", evidenceScreenshot = "generation-1/evidence-a.png" } },
        });
        await CompleteAndProjectAsync(store, StudioOperationKinds.ProposalsGenerate, project.ProjectId, resultJson, "generate");

        var list = await client.GetFromJsonAsync<StudioProposalListResponse>($"/api/v1/studio/projects/{project.ProjectId}/proposals");
        var proposal = Assert.Single(list!.Proposals);
        Assert.Equal("Proposal A", proposal.Payload.GetProperty("title").GetString());

        var evidence = await client.GetFromJsonAsync<ProposalEvidenceDto>(
            $"/api/v1/studio/projects/{project.ProjectId}/proposals/evidence/generation-1/evidence-a.png");
        Assert.Equal(proposal.Id, evidence!.ProposalId);
        Assert.Equal("generation-1/evidence-a.png", evidence.RelPath);

        var missingEvidence = await client.GetAsync(
            $"/api/v1/studio/projects/{project.ProjectId}/proposals/evidence/generation-1/does-not-exist.png");
        Assert.Equal(HttpStatusCode.NotFound, missingEvidence.StatusCode);
    }

    [Fact]
    public async Task Publish_panel_and_run_report_automation_and_operation_status()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioP3ApiFactory(temp.Path);
        using var client = Client(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var project = await SeedProjectAsync(store, "Publish Project", "PUB");
        var basePath = $"/api/v1/studio/projects/{project.ProjectId}/publish";

        (await client.PutAsJsonAsync($"{basePath}/automation", new SetPublishAutomationRequest(true))).EnsureSuccessStatusCode();

        var neverRun = await client.GetAsync($"{basePath}/website/run");
        Assert.Equal(HttpStatusCode.NotFound, neverRun.StatusCode);

        var dispatch = await client.PostAsJsonAsync($"{basePath}/package", new { });
        dispatch.EnsureSuccessStatusCode();
        await CompleteAndProjectAsync(
            store, StudioOperationKinds.PublishPackage, project.ProjectId,
            JsonSerializer.Serialize(new { artifactPath = "dist/package.zip" }), "package");

        var panel = await client.GetFromJsonAsync<PublishPanelDto>($"{basePath}/package/panel");
        Assert.True(panel!.AutomationEnabled);
        Assert.NotNull(panel.LatestOperationId);
        Assert.Equal("succeeded", panel.LatestStatus);

        var run = await client.GetFromJsonAsync<PublishRunStatusDto>($"{basePath}/package/run");
        Assert.Equal("succeeded", run!.State);
        Assert.Contains("dist/package.zip", run.ResultJson);

        var badTarget = await client.GetAsync($"{basePath}/not-a-target/panel");
        Assert.Equal(HttpStatusCode.BadRequest, badTarget.StatusCode);
    }

    [Fact]
    public async Task Wiki_grading_status_reports_never_run_then_succeeded()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioP3ApiFactory(temp.Path);
        using var client = Client(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var project = await SeedProjectAsync(store, "Wiki Grading Project", "WGP");
        var statusPath = $"/api/v1/studio/projects/{project.ProjectId}/wiki/grading/status";

        var neverRun = await client.GetFromJsonAsync<WikiGradingStatusDto>(statusPath);
        Assert.Equal("never-run", neverRun!.State);

        var dispatch = await client.PostAsJsonAsync(
            $"/api/v1/studio/projects/{project.ProjectId}/wiki/grading/run", new { });
        dispatch.EnsureSuccessStatusCode();

        var whileRunning = await client.GetFromJsonAsync<WikiGradingStatusDto>(statusPath);
        Assert.True(whileRunning!.State is "pending" or "claimed");

        await CompleteAndProjectAsync(
            store, StudioOperationKinds.WikiGradingRun, project.ProjectId,
            JsonSerializer.Serialize(new { graded = 4 }), "run");

        var completed = await client.GetFromJsonAsync<WikiGradingStatusDto>(statusPath);
        Assert.Equal("succeeded", completed!.State);
        Assert.Contains("\"graded\":4", completed.ResultJson);
    }

    [Fact]
    public async Task Review_decisions_pending_lists_tasks_whose_latest_completion_needed_input()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioP3ApiFactory(temp.Path);
        using var client = Client(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var (_, project, task) = await SeedTaskAsync(store, "Needs Input Task");

        var empty = await client.GetFromJsonAsync<ReviewDecisionsPendingResponse>(
            $"/api/v1/studio/projects/{project.ProjectId}/review-decisions-pending");
        Assert.Empty(empty!.Items);

        await store.RegisterRunnerAsync("runner-a", Runner("instance-a"), "test", default);
        var claim = await store.ClaimAsync(new ClaimRequest("runner-a", "instance-a"), "test", default);
        var run = claim.Run!;
        var lease = claim.Lease!;
        const string question = "Which strategy should I implement?";
        await store.CompleteRunAsync(run.RunId, new CompleteRunRequest(
            "runner-a", "instance-a", lease.LeaseId, lease.Fence, "explicit-agent-blocker",
            IdempotencyKey: $"completion:{run.RunId}", Sequence: 1, NeedsInputMessage: question), "runner-a", default);

        var pending = await client.GetFromJsonAsync<ReviewDecisionsPendingResponse>(
            $"/api/v1/studio/projects/{project.ProjectId}/review-decisions-pending");
        var item = Assert.Single(pending!.Items);
        Assert.Equal(task.TaskId, item.TaskId);
        Assert.Equal(question, item.Reason);
    }

    [Fact]
    public async Task Pipeline_catalogue_returns_the_static_per_type_step_list_and_rejects_unknown_types()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioP3ApiFactory(temp.Path);
        using var client = Client(factory);

        var all = await client.GetFromJsonAsync<PipelineCatalogueResponse>("/api/v1/studio/projects/pipeline-catalogue");
        Assert.Equal(4, all!.Types.Count);

        var bug = await client.GetFromJsonAsync<PipelineCatalogueResponse>("/api/v1/studio/projects/pipeline-catalogue?pipelineType=bug");
        var bugType = Assert.Single(bug!.Types);
        Assert.Equal("bug", bugType.PipelineType);
        Assert.Contains(bugType.Steps, step => step.Id == "core-agent-run");

        var unknown = await client.GetAsync("/api/v1/studio/projects/pipeline-catalogue?pipelineType=not-a-type");
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    [Fact]
    public async Task Watch_paths_list_reflects_created_and_deleted_entries()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioP3ApiFactory(temp.Path);
        using var client = Client(factory);

        var empty = await client.GetFromJsonAsync<WatchPathListResponse>("/api/v1/studio/watch-paths");
        Assert.Empty(empty!.Items);

        (await client.PostAsJsonAsync("/api/v1/studio/watch-paths", new CreateWatchPathRequest("demo", null, "**/*.md"))).EnsureSuccessStatusCode();
        var afterCreate = await client.GetFromJsonAsync<WatchPathListResponse>("/api/v1/studio/watch-paths");
        var entry = Assert.Single(afterCreate!.Items);
        Assert.Equal("demo", entry.Name);
        Assert.Equal("**/*.md", entry.Pattern);

        (await client.DeleteAsync("/api/v1/studio/watch-paths/demo")).EnsureSuccessStatusCode();
        var afterDelete = await client.GetFromJsonAsync<WatchPathListResponse>("/api/v1/studio/watch-paths");
        Assert.Empty(afterDelete!.Items);
    }

    [Fact]
    public async Task Search_finds_tasks_by_title_or_key_optionally_scoped_to_one_project()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioP3ApiFactory(temp.Path);
        using var client = Client(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var (_, projectA, _) = await SeedTaskAsync(store, "Renovate the deployment pipeline", "SRA");
        var (_, projectB, _) = await SeedTaskAsync(store, "Renovate the search index", "SRB");

        var tooShort = await client.GetFromJsonAsync<StudioSearchResponse>("/api/v1/studio/search?q=r");
        Assert.Empty(tooShort!.Tasks);

        var unscoped = await client.GetFromJsonAsync<StudioSearchResponse>("/api/v1/studio/search?q=Renovate");
        Assert.Equal(2, unscoped!.Tasks.Count);

        var scoped = await client.GetFromJsonAsync<StudioSearchResponse>(
            $"/api/v1/studio/search?q=Renovate&project={projectA.ProjectId}");
        var match = Assert.Single(scoped!.Tasks);
        Assert.Equal(projectA.ProjectId, match.ProjectId);
        Assert.DoesNotContain(scoped.Tasks, item => item.ProjectId == projectB.ProjectId);
    }

    private static async Task<StudioOperationDto> CompleteAndProjectAsync(
        TaskServerStore store, string kind, string projectId, string resultJson, string trigger)
    {
        var claim = await store.ClaimStudioOperationAsync(
            new ClaimStudioOperationRequest($"runner-{Guid.NewGuid():N}", "instance-1"), default);
        Assert.Equal("claimed", claim.Status);
        var operation = claim.Operation!;
        Assert.Equal(kind, operation.Kind);
        Assert.Equal(projectId, operation.ProjectId);
        Assert.Equal(trigger, operation.Trigger);
        var completed = await store.CompleteStudioOperationAsync(
            operation.OperationId,
            new CompleteStudioOperationRequest(
                operation.RunnerId!, "instance-1", operation.LeaseId!, operation.Fence, "succeeded", resultJson),
            default);
        if (string.Equals(kind, StudioOperationKinds.ProposalsGenerate, StringComparison.Ordinal)
            || string.Equals(kind, StudioOperationKinds.ProposalsRefineFeedback, StringComparison.Ordinal))
        {
            await store.ApplyGeneratedProposalsAsync(completed, default);
        }
        return completed;
    }

    private static RegisterRunnerRequest Runner(string instance)
        => new("runner", "host-a", instance, "1.0.0", TaskServerProtocol.Current, [ReviewCapabilities.CodingExecutor]);

    private static async Task<ProjectDto> SeedProjectAsync(TaskServerStore store, string name, string prefix)
    {
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest($"{name} WS"), "test", default);
        return await store.CreateProjectAsync(new CreateProjectRequest(workspace.WorkspaceId, name, prefix), "test", default);
    }

    private static async Task<(WorkspaceDto Workspace, ProjectDto Project, TaskDto Task)> SeedTaskAsync(
        TaskServerStore store, string title, string prefix = "TSK")
    {
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest($"{title} WS"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, $"{title} Project", prefix), "test", default);
        var task = await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest(title, "Body", "2-ready"), "test", default);
        return (workspace, project, task);
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TaskServerProtocol.HeaderName, TaskServerProtocol.Current.ToString());
        client.DefaultRequestHeaders.Add("X-Client-Id", "studio-p3-administration-test");
        return client;
    }

    private sealed class StudioP3ApiFactory(string dataDirectory) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["TaskServer:DataDirectory"] = dataDirectory,
                    ["TaskServer:ListenUrl"] = string.Empty,
                    ["TaskServer:RetentionSchedulerEnabled"] = "false",
                }));
        }
    }
}
