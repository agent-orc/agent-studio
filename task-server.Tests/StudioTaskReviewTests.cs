using System.Net;
using System.Net.Http.Json;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace TaskServer.Tests;

/// <summary>
/// P1 "G8_TaskReview" route tests: task-level code review findings, the
/// task's pipeline view / step-run requests, the regression radar, review
/// evidence acknowledge/follow-up, and durable task summaries.
/// <c>Program.cs</c> maps <c>MapStudioTaskReviewEndpoints</c> and runs
/// <c>ApplyStudioTaskReviewMigrationAsync</c> as part of the shared
/// migration sequence, so each test host here is the same
/// <see cref="StudioTestApiFactory"/> the other P1 groups use.
/// </summary>
public sealed class StudioTaskReviewTests
{
    [Fact]
    public async Task Code_review_finding_round_trips_through_submit_list_and_file_lookup()
    {
        using var temp = new TempDirectory();
        await using var factory = await CreateReadyFactoryAsync(temp.Path);
        using var client = StudioTestClient.Create(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var (_, project, task) = await SeedReadyTaskAsync(store);

        var submit = await client.PostAsJsonAsync(
            $"/api/v1/projects/{project.ProjectId}/tasks/{task.TaskId}/code-review",
            new SubmitCodeReviewFindingRequest("Widget.cs", "Off-by-one in the loop bound.", "concerns"));
        Assert.Equal(HttpStatusCode.Created, submit.StatusCode);
        var finding = await submit.Content.ReadFromJsonAsync<CodeReviewFindingDto>();
        Assert.NotNull(finding);
        Assert.Equal("Widget.cs", finding!.FileName);
        Assert.Equal(1, finding.Version);

        var second = await client.PostAsJsonAsync(
            $"/api/v1/projects/{project.ProjectId}/tasks/{task.TaskId}/code-review",
            new SubmitCodeReviewFindingRequest("Other.cs", "Missing null check.", "block"));
        second.EnsureSuccessStatusCode();

        var list = await client.GetFromJsonAsync<CodeReviewFindingListResponse>(
            $"/api/v1/projects/{project.ProjectId}/tasks/{task.TaskId}/code-review/list");
        Assert.NotNull(list);
        Assert.Equal(2, list!.Findings.Count);
        Assert.Contains(list.Findings, item => item.FileName == "Widget.cs" && item.Severity == "concerns");

        var byFile = await client.GetFromJsonAsync<CodeReviewFileFindingsResponse>(
            $"/api/v1/projects/{project.ProjectId}/tasks/{task.TaskId}/code-review/Widget.cs");
        Assert.NotNull(byFile);
        var onlyFinding = Assert.Single(byFile!.Findings);
        Assert.Equal("Off-by-one in the loop bound.", onlyFinding.Body);
        Assert.Equal(finding.FindingId, onlyFinding.FindingId);
    }

    [Fact]
    public async Task Code_review_defaults_is_global_and_not_task_scoped()
    {
        using var temp = new TempDirectory();
        await using var factory = await CreateReadyFactoryAsync(temp.Path);
        using var client = StudioTestClient.Create(factory);

        var defaults = await client.GetFromJsonAsync<CodeReviewDefaultsDto>("/api/v1/studio/tasks/code-review/defaults");
        Assert.NotNull(defaults);
        Assert.NotEmpty(defaults!.SeverityLevels);
        Assert.Contains(defaults.DefaultSeverity, defaults.SeverityLevels);
    }

    [Fact]
    public async Task Pipeline_view_layers_step_status_and_run_request_only_records_a_request()
    {
        using var temp = new TempDirectory();
        await using var factory = await CreateReadyFactoryAsync(temp.Path);
        using var client = StudioTestClient.Create(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var (_, project, task) = await SeedReadyTaskAsync(store);

        var flow = await store.GetFlowDefinitionAsync(project.ProjectId, default);
        Assert.NotNull(flow);
        var firstStepId = flow!.Stages[0].ToString();

        var beforeRun = await client.GetFromJsonAsync<TaskPipelineViewDto>(
            $"/api/v1/projects/{project.ProjectId}/tasks/{task.TaskId}/pipeline");
        Assert.NotNull(beforeRun);
        Assert.Equal(flow.Stages.Count, beforeRun!.Steps.Count);
        Assert.All(beforeRun.Steps, step => Assert.Null(step.LatestStatus));

        var run = await client.PostAsJsonAsync(
            $"/api/v1/projects/{project.ProjectId}/tasks/{task.TaskId}/pipeline/steps/{firstStepId}/run",
            new RunPipelineStepRequest("kick off"));
        Assert.Equal(HttpStatusCode.Created, run.StatusCode);
        var stepRun = await run.Content.ReadFromJsonAsync<TaskPipelineStepRunDto>();
        Assert.NotNull(stepRun);
        // This route only ever records a REQUEST; the Task Server cannot
        // execute the step itself, so the status must never claim completion.
        Assert.Equal("requested", stepRun!.Status);
        Assert.Null(stepRun.CompletedAt);

        var afterRun = await client.GetFromJsonAsync<TaskPipelineViewDto>(
            $"/api/v1/projects/{project.ProjectId}/tasks/{task.TaskId}/pipeline");
        Assert.NotNull(afterRun);
        var layered = Assert.Single(afterRun!.Steps, step => step.StepId == firstStepId);
        Assert.Equal("requested", layered.LatestStatus);
        Assert.Null(layered.LatestCompletedAt);
    }

    [Fact]
    public async Task Regression_radar_flags_a_non_infrastructure_non_success_outcome()
    {
        using var temp = new TempDirectory();
        await using var factory = await CreateReadyFactoryAsync(temp.Path);
        using var client = StudioTestClient.Create(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var (_, project, task) = await SeedReadyTaskAsync(store);

        await store.RegisterRunnerAsync("runner-a", Runner("instance-a"), "test", default);
        var claim = await store.ClaimAsync(new ClaimRequest("runner-a", "instance-a"), "test", default);
        var run = claim.Run!;
        var lease = claim.Lease!;

        var facts = new ExecutionRawFacts(
            run.RunId,
            ExecutionAttemptKind.Coding,
            FinalAssistantOutput: "[[TASK BLOCKED: need a decision on the API contract]]",
            ExitCode: 0);
        var decision = ExecutionOutcomeAdapter.Classify(facts);
        Assert.False(decision.IsInfrastructureOutcome);
        Assert.Equal(ExecutionOutcomeKind.ExplicitAgentBlocker, decision.Outcome);

        await store.CompleteRunAsync(
            run.RunId,
            new CompleteRunRequest(
                "runner-a",
                "instance-a",
                lease.LeaseId,
                lease.Fence,
                decision.Outcome.ToString(),
                IdempotencyKey: $"completion:{run.RunId}:blocked",
                Sequence: 1,
                OutcomeDecision: decision),
            "runner-a",
            default);

        var radar = await client.GetFromJsonAsync<RegressionRadarDto>(
            $"/api/v1/projects/{project.ProjectId}/tasks/{task.TaskId}/regression-radar");
        Assert.NotNull(radar);
        Assert.True(radar!.HasRegressions);
        Assert.Equal(1, radar.TotalAttempts);
        var entry = Assert.Single(radar.FlaggedAttempts);
        Assert.Equal(run.RunId, entry.RunId);
        Assert.Equal(ExecutionOutcomeKind.ExplicitAgentBlocker, entry.Outcome);
    }

    [Fact]
    public async Task Review_evidence_acknowledge_is_idempotent_and_follow_up_is_not()
    {
        using var temp = new TempDirectory();
        await using var factory = await CreateReadyFactoryAsync(temp.Path);
        using var client = StudioTestClient.Create(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var (_, project, task) = await SeedReadyTaskAsync(store);

        var basePath = $"/api/v1/projects/{project.ProjectId}/tasks/{task.TaskId}/review-evidence/ev-1";

        var firstAck = await client.PostAsJsonAsync($"{basePath}/acknowledge", new ReviewEvidenceActionRequest("looks fine"));
        firstAck.EnsureSuccessStatusCode();
        var firstAckDto = await firstAck.Content.ReadFromJsonAsync<ReviewEvidenceActionDto>();

        var secondAck = await client.PostAsJsonAsync($"{basePath}/acknowledge", new ReviewEvidenceActionRequest("looks fine again"));
        secondAck.EnsureSuccessStatusCode();
        var secondAckDto = await secondAck.Content.ReadFromJsonAsync<ReviewEvidenceActionDto>();
        // Idempotent: the second acknowledge of the same evidence returns
        // the SAME row rather than creating a duplicate.
        Assert.Equal(firstAckDto!.Id, secondAckDto!.Id);
        Assert.Equal(firstAckDto.Note, secondAckDto.Note);

        var firstFollowUp = await client.PostAsJsonAsync($"{basePath}/follow-up", new ReviewEvidenceActionRequest("still investigating"));
        firstFollowUp.EnsureSuccessStatusCode();
        var firstFollowUpDto = await firstFollowUp.Content.ReadFromJsonAsync<ReviewEvidenceActionDto>();

        var secondFollowUp = await client.PostAsJsonAsync($"{basePath}/follow-up", new ReviewEvidenceActionRequest("fixed in abc123"));
        secondFollowUp.EnsureSuccessStatusCode();
        var secondFollowUpDto = await secondFollowUp.Content.ReadFromJsonAsync<ReviewEvidenceActionDto>();
        // Not idempotent: each follow-up is a discrete note and appends a new row.
        Assert.NotEqual(firstFollowUpDto!.Id, secondFollowUpDto!.Id);
        Assert.Equal("still investigating", firstFollowUpDto.Note);
        Assert.Equal("fixed in abc123", secondFollowUpDto.Note);
    }

    [Fact]
    public async Task Interim_summary_then_regenerate_bumps_the_request_count_without_corrupting_markdown()
    {
        using var temp = new TempDirectory();
        await using var factory = await CreateReadyFactoryAsync(temp.Path);
        using var client = StudioTestClient.Create(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var (_, project, task) = await SeedReadyTaskAsync(store);

        var interimPath = $"/api/v1/projects/{project.ProjectId}/tasks/{task.TaskId}/summary/interim";
        var regeneratePath = $"/api/v1/projects/{project.ProjectId}/tasks/{task.TaskId}/summary/regenerate";

        var interim = await client.PostAsJsonAsync(interimPath, new InterimSummaryRequest("## Progress\nDone the thing."));
        interim.EnsureSuccessStatusCode();
        var interimDto = await interim.Content.ReadFromJsonAsync<TaskSummaryDto>();
        Assert.Equal("## Progress\nDone the thing.", interimDto!.SummaryMarkdown);
        Assert.Equal(0, interimDto.RegenerationRequestedCount);
        Assert.Equal(1, interimDto.Version);

        var regenerate = await client.PostAsJsonAsync(regeneratePath, new RegenerateSummaryRequest("stale after refactor"));
        regenerate.EnsureSuccessStatusCode();
        var regenerateDto = await regenerate.Content.ReadFromJsonAsync<TaskSummaryDto>();
        // The Task Server cannot itself run an LLM: regenerate only bumps the
        // request count. It must not fabricate new content or lose the
        // existing markdown.
        Assert.Equal("## Progress\nDone the thing.", regenerateDto!.SummaryMarkdown);
        Assert.Equal(1, regenerateDto.RegenerationRequestedCount);
        Assert.Equal(interimDto.Version, regenerateDto.Version);

        var secondRegenerate = await client.PostAsJsonAsync(regeneratePath, new RegenerateSummaryRequest());
        secondRegenerate.EnsureSuccessStatusCode();
        var secondRegenerateDto = await secondRegenerate.Content.ReadFromJsonAsync<TaskSummaryDto>();
        Assert.Equal(2, secondRegenerateDto!.RegenerationRequestedCount);
        Assert.Equal("## Progress\nDone the thing.", secondRegenerateDto.SummaryMarkdown);
    }

    [Fact]
    public async Task Regenerate_before_any_interim_summary_records_the_request_without_inventing_content()
    {
        using var temp = new TempDirectory();
        await using var factory = await CreateReadyFactoryAsync(temp.Path);
        using var client = StudioTestClient.Create(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var (_, project, task) = await SeedReadyTaskAsync(store);

        var regenerate = await client.PostAsJsonAsync(
            $"/api/v1/projects/{project.ProjectId}/tasks/{task.TaskId}/summary/regenerate",
            new RegenerateSummaryRequest());
        regenerate.EnsureSuccessStatusCode();
        var dto = await regenerate.Content.ReadFromJsonAsync<TaskSummaryDto>();
        Assert.Null(dto!.SummaryMarkdown);
        Assert.Equal(1, dto.RegenerationRequestedCount);
    }

    // ---- shared test plumbing ------------------------------------------------

    private static Task<WebApplicationFactory<Program>> CreateReadyFactoryAsync(string dataDirectory)
        => Task.FromResult<WebApplicationFactory<Program>>(new StudioTestApiFactory(dataDirectory));

    private static RegisterRunnerRequest Runner(string instance)
        => new("runner", "host-a", instance, "1.0.0", TaskServerProtocol.Current, [ReviewCapabilities.CodingExecutor]);

    private static async Task<(WorkspaceDto Workspace, ProjectDto Project, TaskDto Task)> SeedReadyTaskAsync(TaskServerStore store)
    {
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("G8 Workspace"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "G8 Project", "G8"), "test", default);
        var task = await store.CreateTaskAsync(
            project.ProjectId, new CreateTaskRequest("G8 Task", "Body", "2-ready"), "test", default);
        return (workspace, project, task);
    }
}
