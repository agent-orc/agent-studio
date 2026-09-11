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

public sealed class StudioP2DesignEndpointsTests
{
    [Fact]
    public async Task Design_action_dispatch_projects_council_overview_and_references()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioApiFactory(temp.Path);
        using var client = Client(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Design WS"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Design Project", "DSN"), "test", default);

        var emptyCouncil = await client.GetFromJsonAsync<DesignCouncilListResponse>(
            $"/api/v1/studio/projects/{project.ProjectId}/design/council");
        Assert.Empty(emptyCouncil!.Council);
        var emptyOverview = await client.GetFromJsonAsync<DesignOverviewResponse>(
            $"/api/v1/studio/projects/{project.ProjectId}/design/overview");
        Assert.Null(emptyOverview!.Overview);

        var dispatch = await client.PostAsJsonAsync(
            $"/api/v1/studio/projects/{project.ProjectId}/design/actions/regenerate-overview", new { });
        Assert.Equal(HttpStatusCode.Accepted, dispatch.StatusCode);
        var accepted = (await dispatch.Content.ReadFromJsonAsync<StudioOperationAcceptedResponse>())!;
        Assert.Equal(StudioOperationKinds.DesignAction, accepted.Kind);

        var resultJson = JsonSerializer.Serialize(new
        {
            council = new[]
            {
                new { fileName = "note-1.md", title = "First note" },
                new { fileName = "note-2.md", title = "Second note" },
            },
            overview = new { status = "in-progress", referencesCount = 3 },
            references = new[] { new { fileName = "ref-1.md" } },
        });
        var operation = await CompleteAndProjectAsync(store, StudioOperationKinds.DesignAction, project.ProjectId, resultJson, "regenerate-overview");

        var council = await client.GetFromJsonAsync<DesignCouncilListResponse>(
            $"/api/v1/studio/projects/{project.ProjectId}/design/council");
        Assert.Equal(operation.OperationId, council!.SourceOperationId);
        Assert.Equal(2, council.Council.Count);

        var entry = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/studio/projects/{project.ProjectId}/design/council/note-2.md");
        Assert.Equal("Second note", entry.GetProperty("title").GetString());

        var missingEntry = await client.GetAsync(
            $"/api/v1/studio/projects/{project.ProjectId}/design/council/does-not-exist.md");
        Assert.Equal(HttpStatusCode.NotFound, missingEntry.StatusCode);

        var accept = await client.PostAsJsonAsync(
            $"/api/v1/studio/projects/{project.ProjectId}/design/council/note-2.md/accept", new { });
        accept.EnsureSuccessStatusCode();
        var acceptance = (await accept.Content.ReadFromJsonAsync<DesignCouncilAcceptanceDto>())!;
        Assert.Equal("note-2.md", acceptance.FileName);
        Assert.Equal("studio-p2-design-test", acceptance.AcceptedBy);

        var overview = await client.GetFromJsonAsync<DesignOverviewResponse>(
            $"/api/v1/studio/projects/{project.ProjectId}/design/overview");
        Assert.Equal("in-progress", overview!.Overview!.Value.GetProperty("status").GetString());

        var references = await client.GetFromJsonAsync<DesignReferencesResponse>(
            $"/api/v1/studio/projects/{project.ProjectId}/design/references");
        Assert.Single(references!.References);

        var missingProject = await client.GetAsync("/api/v1/studio/projects/does-not-exist/design/overview");
        Assert.Equal(HttpStatusCode.NotFound, missingProject.StatusCode);
    }

    [Fact]
    public async Task Proposals_generate_dispatch_completes_projects_rows_and_supports_decision_and_delete()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioApiFactory(temp.Path);
        using var client = Client(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Proposals WS"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Proposals Project", "PRP"), "test", default);

        // 1. Dispatch via the endpoint.
        var generate = await client.PostAsJsonAsync(
            $"/api/v1/studio/projects/{project.ProjectId}/proposals/generate", new { });
        Assert.Equal(HttpStatusCode.Accepted, generate.StatusCode);
        var accepted = (await generate.Content.ReadFromJsonAsync<StudioOperationAcceptedResponse>())!;
        Assert.Equal(StudioOperationKinds.ProposalsGenerate, accepted.Kind);

        // 2. Complete the dispatched operation (claim + complete, mirroring a Runner) and
        //    run the projector this bundle registers for ProposalsGenerate/ProposalsRefineFeedback.
        var firstResult = JsonSerializer.Serialize(new
        {
            proposals = new[]
            {
                new { title = "Proposal A", body = "Do A" },
                new { title = "Proposal B", body = "Do B" },
            },
        });
        await CompleteAndProjectAsync(store, StudioOperationKinds.ProposalsGenerate, project.ProjectId, firstResult, "generate");

        // 3. Assert the rows materialized via the store's own proposals read path.
        var afterFirst = await store.ListStudioProposalsAsync(project.ProjectId, default);
        Assert.Equal(2, afterFirst.Proposals.Count);
        Assert.All(afterFirst.Proposals, proposal =>
        {
            Assert.Equal(1, proposal.Generation);
            Assert.Equal(StudioProposalStatuses.Pending, proposal.Status);
        });
        var proposalA = afterFirst.Proposals.Single(p => p.Payload.GetProperty("title").GetString() == "Proposal A");

        // Decision route flips status and stamps decidedAt.
        var decide = await client.PostAsJsonAsync(
            $"/api/v1/studio/projects/{project.ProjectId}/proposals/{proposalA.Id}/decision",
            new DecideProposalRequest(StudioProposalStatuses.Accepted));
        decide.EnsureSuccessStatusCode();
        var decided = (await decide.Content.ReadFromJsonAsync<StudioProposalDto>())!;
        Assert.Equal(StudioProposalStatuses.Accepted, decided.Status);
        Assert.NotNull(decided.DecidedAt);

        var invalidDecision = await client.PostAsJsonAsync(
            $"/api/v1/studio/projects/{project.ProjectId}/proposals/{proposalA.Id}/decision",
            new DecideProposalRequest("maybe"));
        Assert.Equal(HttpStatusCode.BadRequest, invalidDecision.StatusCode);

        var decisionMissing = await client.PostAsJsonAsync(
            $"/api/v1/studio/projects/{project.ProjectId}/proposals/does-not-exist/decision",
            new DecideProposalRequest(StudioProposalStatuses.Accepted));
        Assert.Equal(HttpStatusCode.NotFound, decisionMissing.StatusCode);

        // 4. A second generation via refine-feedback.
        var refine = await client.PostAsJsonAsync(
            $"/api/v1/studio/projects/{project.ProjectId}/proposals/refine-feedback",
            new RefineProposalsFeedbackRequest("Make it punchier"));
        Assert.Equal(HttpStatusCode.Accepted, refine.StatusCode);
        Assert.Equal(
            StudioOperationKinds.ProposalsRefineFeedback,
            (await refine.Content.ReadFromJsonAsync<StudioOperationAcceptedResponse>())!.Kind);

        var emptyFeedback = await client.PostAsJsonAsync(
            $"/api/v1/studio/projects/{project.ProjectId}/proposals/refine-feedback",
            new RefineProposalsFeedbackRequest(""));
        Assert.Equal(HttpStatusCode.BadRequest, emptyFeedback.StatusCode);

        // Refine-feedback appends to the latest existing generation rather than starting a new one.
        var refineResult = JsonSerializer.Serialize(new { proposals = new[] { new { title = "Proposal C" } } });
        await CompleteAndProjectAsync(store, StudioOperationKinds.ProposalsRefineFeedback, project.ProjectId, refineResult, "refine-feedback");
        var afterRefine = await store.ListStudioProposalsAsync(project.ProjectId, default);
        Assert.Equal(3, afterRefine.Proposals.Count);
        Assert.All(afterRefine.Proposals, proposal => Assert.Equal(1, proposal.Generation));

        // 5. A brand-new generate operation starts generation 2.
        var regenerate = await client.PostAsJsonAsync(
            $"/api/v1/studio/projects/{project.ProjectId}/proposals/generate", new { });
        Assert.Equal(HttpStatusCode.Accepted, regenerate.StatusCode);
        var secondResult = JsonSerializer.Serialize(new { proposals = new[] { new { title = "Proposal D" } } });
        await CompleteAndProjectAsync(store, StudioOperationKinds.ProposalsGenerate, project.ProjectId, secondResult, "generate");
        var afterSecondGeneration = await store.ListStudioProposalsAsync(project.ProjectId, default);
        Assert.Equal(4, afterSecondGeneration.Proposals.Count);
        Assert.Equal(3, afterSecondGeneration.Proposals.Count(p => p.Generation == 1));
        Assert.Single(afterSecondGeneration.Proposals, p => p.Generation == 2);

        // 6. Bulk delete keeping only generation 2.
        var bulkDelete = await client.DeleteAsync(
            $"/api/v1/studio/projects/{project.ProjectId}/proposals?keepGeneration=2");
        bulkDelete.EnsureSuccessStatusCode();
        var afterKeep = await store.ListStudioProposalsAsync(project.ProjectId, default);
        Assert.Single(afterKeep.Proposals);
        Assert.Equal(2, afterKeep.Proposals[0].Generation);

        // 7. Delete a single proposal by id, then confirm 404 on repeat and on unknown ids.
        var lastId = afterKeep.Proposals[0].Id;
        var deleteOne = await client.DeleteAsync($"/api/v1/studio/projects/{project.ProjectId}/proposals/{lastId}");
        deleteOne.EnsureSuccessStatusCode();
        var deleteOneAgain = await client.DeleteAsync($"/api/v1/studio/projects/{project.ProjectId}/proposals/{lastId}");
        Assert.Equal(HttpStatusCode.NotFound, deleteOneAgain.StatusCode);
        Assert.Empty((await store.ListStudioProposalsAsync(project.ProjectId, default)).Proposals);

        // 8. Bulk delete with no query param on an already-empty project is a no-op, not an error.
        var bulkDeleteEmpty = await client.DeleteAsync($"/api/v1/studio/projects/{project.ProjectId}/proposals");
        bulkDeleteEmpty.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Skill_readiness_fix_task_dispatches_a_studio_operation()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioApiFactory(temp.Path);
        using var client = Client(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Skill WS"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Skill Project", "SKR"), "test", default);
        var task = await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("Flaky task"), "test", default);

        var fix = await client.PostAsJsonAsync(
            $"/api/v1/studio/projects/{project.ProjectId}/skill-readiness/fix-task",
            new FixSkillReadinessTaskRequest(task.TaskId));
        Assert.Equal(HttpStatusCode.Accepted, fix.StatusCode);
        var accepted = (await fix.Content.ReadFromJsonAsync<StudioOperationAcceptedResponse>())!;
        Assert.Equal(StudioOperationKinds.SkillReadinessFixTask, accepted.Kind);

        var missingTaskId = await client.PostAsJsonAsync(
            $"/api/v1/studio/projects/{project.ProjectId}/skill-readiness/fix-task",
            new FixSkillReadinessTaskRequest(""));
        Assert.Equal(HttpStatusCode.BadRequest, missingTaskId.StatusCode);
    }

    [Fact]
    public async Task Snapshot_reports_project_fields_and_queue_counts_by_lane()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioApiFactory(temp.Path);
        using var client = Client(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Snapshot WS"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Snapshot Project", "SNP"), "test", default);
        await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("Backlog task"), "test", default);
        await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("Ready 1", State: "2-ready"), "test", default);
        await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("Ready 2", State: "2-ready"), "test", default);
        await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("Completed", State: "6-completed"), "test", default);

        var snapshot = await client.GetFromJsonAsync<StudioProjectSnapshotResponse>(
            $"/api/v1/studio/projects/{project.ProjectId}/snapshot");
        Assert.Equal(project.ProjectId, snapshot!.Project.ProjectId);
        Assert.Equal(1, snapshot.Queue.Backlog);
        Assert.Equal(2, snapshot.Queue.Ready);
        Assert.Equal(1, snapshot.Queue.Completed);
        Assert.Equal(4, snapshot.Queue.Total);

        var missingProject = await client.GetAsync("/api/v1/studio/projects/does-not-exist/snapshot");
        Assert.Equal(HttpStatusCode.NotFound, missingProject.StatusCode);
    }

    [Fact]
    public async Task Visual_evidence_lists_newest_first_and_acknowledges_items()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioApiFactory(temp.Path);
        using var client = Client(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Evidence WS"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Evidence Project", "EVD"), "test", default);

        var first = await store.RecordVisualEvidenceAsync(
            project.ProjectId, JsonSerializer.SerializeToElement(new { note = "first" }), default);
        var second = await store.RecordVisualEvidenceAsync(
            project.ProjectId, JsonSerializer.SerializeToElement(new { note = "second" }), default);

        var list = await client.GetFromJsonAsync<StudioVisualEvidenceListResponse>(
            $"/api/v1/studio/projects/{project.ProjectId}/visual-evidence?refresh=true");
        Assert.Equal([second.Id, first.Id], list!.Items.Select(item => item.Id));
        Assert.All(list.Items, item => Assert.Null(item.AcknowledgedAt));

        var acknowledge = await client.PostAsync(
            $"/api/v1/studio/projects/{project.ProjectId}/visual-evidence/{first.Id}/acknowledge", null);
        acknowledge.EnsureSuccessStatusCode();
        var acknowledged = (await acknowledge.Content.ReadFromJsonAsync<StudioVisualEvidenceItemDto>())!;
        Assert.NotNull(acknowledged.AcknowledgedAt);

        var missing = await client.PostAsync(
            $"/api/v1/studio/projects/{project.ProjectId}/visual-evidence/does-not-exist/acknowledge", null);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    /// <summary>
    /// Drives a studio operation through the same claim -> complete lifecycle
    /// a Runner uses, then runs the same completion projector the app
    /// registers via <see cref="StudioP2DesignServiceCollectionExtensions.AddStudioP2DesignServices"/>
    /// (the HTTP completion route invokes it after a successful outcome; this
    /// helper does the same without needing a fully-scoped test client).
    /// </summary>
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
        var projector = new ProposalsCompletionProjector();
        if (projector.Handles(completed.Kind))
            await projector.OnCompletedAsync(completed, store, default);
        return completed;
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TaskServerProtocol.HeaderName, TaskServerProtocol.Current.ToString());
        client.DefaultRequestHeaders.Add("X-Client-Id", "studio-p2-design-test");
        return client;
    }

    private sealed class StudioApiFactory(string dataDirectory) : WebApplicationFactory<Program>
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
