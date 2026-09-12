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

public sealed class StudioP2AnalysisDriftEndpointsTests
{
    [Fact]
    public async Task Analysis_reports_list_create_get_and_filter_round_trip()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioApiFactory(temp.Path);
        using var client = Client(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Analysis WS"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Analysis Project", "ANL"), "test", default);

        var emptyList = await client.GetFromJsonAsync<AnalysisReportListResponse>(
            $"/api/v1/studio/analysis/{project.ProjectId}/reports");
        Assert.Empty(emptyList!.Reports);

        var summary = JsonSerializer.SerializeToElement(new { headline = "Queue is healthy", score = 92 });
        var create = await client.PostAsJsonAsync(
            $"/api/v1/studio/analysis/{project.ProjectId}/reports",
            new SubmitAnalysisReportRequest("manual", "info", "queue-health", summary));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = (await create.Content.ReadFromJsonAsync<AnalysisReportDto>())!;
        Assert.Equal(project.ProjectId, created.ProjectId);
        Assert.Equal("queue-health", created.Topic);
        Assert.Equal("Queue is healthy", created.Summary.GetProperty("headline").GetString());

        var missing = await client.GetAsync($"/api/v1/studio/analysis/{project.ProjectId}/reports/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var fetched = await client.GetFromJsonAsync<AnalysisReportDto>(
            $"/api/v1/studio/analysis/{project.ProjectId}/reports/{created.Id}");
        Assert.Equal(created.Id, fetched!.Id);

        var listed = await client.GetFromJsonAsync<AnalysisReportListResponse>(
            $"/api/v1/studio/analysis/{project.ProjectId}/reports");
        Assert.Single(listed!.Reports);

        var filteredMiss = await client.GetFromJsonAsync<AnalysisReportListResponse>(
            $"/api/v1/studio/analysis/{project.ProjectId}/reports?topic=other-topic");
        Assert.Empty(filteredMiss!.Reports);

        var filteredHit = await client.GetFromJsonAsync<AnalysisReportListResponse>(
            $"/api/v1/studio/analysis/{project.ProjectId}/reports?topic=queue-health&severity=info&trigger=manual");
        Assert.Single(filteredHit!.Reports);

        var missingProject = await client.GetAsync("/api/v1/studio/analysis/does-not-exist/reports");
        Assert.Equal(HttpStatusCode.NotFound, missingProject.StatusCode);
    }

    [Fact]
    public async Task Analysis_schedule_defaults_then_upserts()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioApiFactory(temp.Path);
        using var client = Client(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Schedule WS"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Schedule Project", "SCH"), "test", default);

        var initial = await client.GetFromJsonAsync<AnalysisScheduleDto>(
            $"/api/v1/studio/analysis/{project.ProjectId}/schedule");
        Assert.Equal(project.ProjectId, initial!.ProjectId);
        Assert.Null(initial.Schedule);
        Assert.Null(initial.UpdatedAt);

        var schedule = JsonSerializer.SerializeToElement(new { cadence = "daily", topics = new[] { "drift", "analysis" } });
        var upsert = await client.PutAsJsonAsync(
            $"/api/v1/studio/analysis/{project.ProjectId}/schedule", new UpdateAnalysisScheduleRequest(schedule));
        upsert.EnsureSuccessStatusCode();
        var upserted = (await upsert.Content.ReadFromJsonAsync<AnalysisScheduleDto>())!;
        Assert.Equal("daily", upserted.Schedule!.Value.GetProperty("cadence").GetString());
        Assert.NotNull(upserted.UpdatedAt);

        var refetched = await client.GetFromJsonAsync<AnalysisScheduleDto>(
            $"/api/v1/studio/analysis/{project.ProjectId}/schedule");
        Assert.Equal("daily", refetched!.Schedule!.Value.GetProperty("cadence").GetString());
    }

    [Fact]
    public async Task Drift_actions_dispatch_studio_operations_and_expose_prompt_and_rules()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioApiFactory(temp.Path);
        using var client = Client(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Drift WS"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Drift Project", "DFT"), "test", default);

        var adr = await client.PostAsync($"/api/v1/studio/drift/{project.ProjectId}/actions/adr-code-drift", null);
        Assert.Equal(HttpStatusCode.Accepted, adr.StatusCode);
        var adrAccepted = (await adr.Content.ReadFromJsonAsync<StudioOperationAcceptedResponse>())!;
        Assert.Equal(StudioOperationKinds.DriftAdrCodeDrift, adrAccepted.Kind);
        Assert.Equal(StudioOperationStatuses.Pending, adrAccepted.Status);

        var docs = await client.PostAsync($"/api/v1/studio/drift/{project.ProjectId}/actions/docs-marketing-drift", null);
        Assert.Equal(HttpStatusCode.Accepted, docs.StatusCode);
        Assert.Equal(
            StudioOperationKinds.DriftDocsMarketingDrift,
            (await docs.Content.ReadFromJsonAsync<StudioOperationAcceptedResponse>())!.Kind);

        var arch = await client.PostAsync($"/api/v1/studio/drift/{project.ProjectId}/actions/software-architecture-drift", null);
        Assert.Equal(HttpStatusCode.Accepted, arch.StatusCode);
        Assert.Equal(
            StudioOperationKinds.DriftSoftwareArchitectureDrift,
            (await arch.Content.ReadFromJsonAsync<StudioOperationAcceptedResponse>())!.Kind);

        var prompt = await client.GetFromJsonAsync<DriftActionPromptResponse>(
            $"/api/v1/studio/drift/{project.ProjectId}/actions/software-architecture-drift/prompt");
        Assert.Equal(StudioOperationKinds.DriftSoftwareArchitectureDrift, prompt!.Kind);
        Assert.False(string.IsNullOrWhiteSpace(prompt.Description));

        var missingProjectPrompt = await client.GetAsync(
            "/api/v1/studio/drift/does-not-exist/actions/software-architecture-drift/prompt");
        Assert.Equal(HttpStatusCode.NotFound, missingProjectPrompt.StatusCode);

        var rules = await client.GetFromJsonAsync<List<DriftRuleDescriptor>>(
            "/api/v1/studio/drift/actions/code-pattern-drift/rules");
        Assert.NotEmpty(rules!);

        var codePattern = await client.PostAsJsonAsync(
            "/api/v1/studio/drift/actions/code-pattern-drift", new TriggerCodePatternDriftRequest(project.ProjectId));
        Assert.Equal(HttpStatusCode.Accepted, codePattern.StatusCode);
        Assert.Equal(
            StudioOperationKinds.DriftCodePatternDrift,
            (await codePattern.Content.ReadFromJsonAsync<StudioOperationAcceptedResponse>())!.Kind);

        var codePatternMissingProject = await client.PostAsJsonAsync(
            "/api/v1/studio/drift/actions/code-pattern-drift", new TriggerCodePatternDriftRequest("does-not-exist"));
        Assert.Equal(HttpStatusCode.NotFound, codePatternMissingProject.StatusCode);
    }

    [Fact]
    public async Task Drift_architecture_reflects_completed_scan_and_element_status_overrides()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioApiFactory(temp.Path);
        using var client = Client(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Arch WS"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Arch Project", "ARC"), "test", default);

        var beforeScan = await client.GetFromJsonAsync<DriftArchitectureResponse>(
            $"/api/v1/studio/drift/{project.ProjectId}/architecture");
        Assert.Null(beforeScan!.SourceOperationId);
        Assert.Null(beforeScan.Model);
        Assert.Empty(beforeScan.ElementStatusOverrides);

        var resultJson = JsonSerializer.Serialize(new { modelId = "model-1", elements = new[] { new { id = "element-1" } } });
        var operation = await CompleteOperationAsync(
            store, StudioOperationKinds.DriftSoftwareArchitectureDrift, project.ProjectId, resultJson);

        var afterScan = await client.GetFromJsonAsync<DriftArchitectureResponse>(
            $"/api/v1/studio/drift/{project.ProjectId}/architecture");
        Assert.Equal(operation.OperationId, afterScan!.SourceOperationId);
        Assert.NotNull(afterScan.Model);
        Assert.Equal("model-1", afterScan.Model!.Value.GetProperty("modelId").GetString());
        Assert.Empty(afterScan.ElementStatusOverrides);

        var setStatus = await client.PostAsJsonAsync(
            $"/api/v1/studio/drift/{project.ProjectId}/architecture/model-1/elements/element-1/status",
            new UpdateDriftElementStatusRequest("Accepted"));
        setStatus.EnsureSuccessStatusCode();
        var statusDto = (await setStatus.Content.ReadFromJsonAsync<DriftElementStatusDto>())!;
        Assert.Equal("Accepted", statusDto.Status);

        var afterOverride = await client.GetFromJsonAsync<DriftArchitectureResponse>(
            $"/api/v1/studio/drift/{project.ProjectId}/architecture");
        var overrideEntry = Assert.Single(afterOverride!.ElementStatusOverrides);
        Assert.Equal("model-1", overrideEntry.ModelId);
        Assert.Equal("element-1", overrideEntry.ElementId);
        Assert.Equal("Accepted", overrideEntry.Status);

        // Overriding again updates in place rather than duplicating the row.
        var updateStatus = await client.PostAsJsonAsync(
            $"/api/v1/studio/drift/{project.ProjectId}/architecture/model-1/elements/element-1/status",
            new UpdateDriftElementStatusRequest("Resolved"));
        updateStatus.EnsureSuccessStatusCode();
        var afterUpdate = await client.GetFromJsonAsync<DriftArchitectureResponse>(
            $"/api/v1/studio/drift/{project.ProjectId}/architecture");
        var updatedEntry = Assert.Single(afterUpdate!.ElementStatusOverrides);
        Assert.Equal("Resolved", updatedEntry.Status);
    }

    [Fact]
    public async Task Drift_reports_merge_across_kinds_and_resolve_individually()
    {
        using var temp = new TempDirectory();
        await using var factory = new StudioApiFactory(temp.Path);
        using var client = Client(factory);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Reports WS"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Reports Project", "RPT"), "test", default);
        var otherProject = await store.CreateProjectAsync(
            new CreateProjectRequest(workspace.WorkspaceId, "Other Project", "OTH"), "test", default);

        var emptyReports = await client.GetFromJsonAsync<DriftReportListResponse>(
            $"/api/v1/studio/drift/{project.ProjectId}/reports");
        Assert.Empty(emptyReports!.Reports);

        var adr = await CompleteOperationAsync(
            store, StudioOperationKinds.DriftAdrCodeDrift, project.ProjectId,
            JsonSerializer.Serialize(new { verdict = "clean" }), trigger: "adr-code-drift", severity: "low", topic: "adr");
        var docs = await CompleteOperationAsync(
            store, StudioOperationKinds.DriftDocsMarketingDrift, project.ProjectId,
            JsonSerializer.Serialize(new { verdict = "stale" }), trigger: "docs-marketing-drift", severity: "high", topic: "docs");
        // Belongs to a different project: must never leak into project's report list.
        await CompleteOperationAsync(
            store, StudioOperationKinds.DriftAdrCodeDrift, otherProject.ProjectId, JsonSerializer.Serialize(new { verdict = "n/a" }));

        var reports = await client.GetFromJsonAsync<DriftReportListResponse>(
            $"/api/v1/studio/drift/{project.ProjectId}/reports");
        Assert.Equal(2, reports!.Reports.Count);
        Assert.All(reports.Reports, report => Assert.Equal(project.ProjectId, report.ProjectId));

        var filtered = await client.GetFromJsonAsync<DriftReportListResponse>(
            $"/api/v1/studio/drift/{project.ProjectId}/reports?severity=high");
        var onlyHigh = Assert.Single(filtered!.Reports);
        Assert.Equal(docs.OperationId, onlyHigh.Id);

        var single = await client.GetFromJsonAsync<DriftReportDto>(
            $"/api/v1/studio/drift/{project.ProjectId}/reports/{adr.OperationId}");
        Assert.Equal(adr.OperationId, single!.Id);
        Assert.Equal("clean", single.Summary!.Value.GetProperty("verdict").GetString());

        var wrongProject = await client.GetAsync(
            $"/api/v1/studio/drift/{otherProject.ProjectId}/reports/{adr.OperationId}");
        Assert.Equal(HttpStatusCode.NotFound, wrongProject.StatusCode);

        var missingReport = await client.GetAsync($"/api/v1/studio/drift/{project.ProjectId}/reports/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, missingReport.StatusCode);
    }

    /// <summary>
    /// Drives a studio operation through the same claim → complete lifecycle
    /// a Runner uses, so tests can exercise the P2 GET routes' "durable
    /// projection" reads without a real Runner executor.
    /// </summary>
    private static async Task<StudioOperationDto> CompleteOperationAsync(
        TaskServerStore store,
        string kind,
        string projectId,
        string resultJson,
        string? trigger = null,
        string? severity = null,
        string? topic = null)
    {
        await store.CreateStudioOperationAsync(kind, projectId, null, new { }, trigger, severity, topic, default);
        var claim = await store.ClaimStudioOperationAsync(
            new ClaimStudioOperationRequest($"runner-{Guid.NewGuid():N}", "instance-1"), default);
        Assert.Equal("claimed", claim.Status);
        var operation = claim.Operation!;
        return await store.CompleteStudioOperationAsync(
            operation.OperationId,
            new CompleteStudioOperationRequest(
                operation.RunnerId!, "instance-1", operation.LeaseId!, operation.Fence, "succeeded", resultJson),
            default);
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TaskServerProtocol.HeaderName, TaskServerProtocol.Current.ToString());
        client.DefaultRequestHeaders.Add("X-Client-Id", "studio-p2-analysis-drift-test");
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
