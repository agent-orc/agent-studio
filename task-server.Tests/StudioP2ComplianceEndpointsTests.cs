using System.Net;
using System.Net.Http.Json;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace TaskServer.Tests;

/// <summary>
/// Covers the Studio P2 "security review, deployment, publish, and wiki
/// grading" bundle end to end over HTTP. The tests build a minimal standalone
/// host (<see cref="ComplianceHarness"/>) that maps only this bundle's routes,
/// while <see cref="TaskServerStore.InitializeAsync"/> applies the same complete
/// schema used by <c>Program</c>.
/// </summary>
public sealed class StudioP2ComplianceEndpointsTests
{
    [Fact]
    public async Task Security_audit_dispatch_surfaces_through_baseline_reviews_and_review_by_file()
    {
        using var temp = new TempDirectory();
        await using var harness = await ComplianceHarness.CreateAsync(temp.Path);
        var project = await harness.CreateProjectAsync("Security Project", "SEC");

        var emptyBaseline = await harness.Client.GetFromJsonAsync<SecurityBaselineDto>(
            $"/api/v1/studio/projects/{project.ProjectId}/security/baseline");
        Assert.Null(emptyBaseline!.OperationId);

        var emptyReviews = await harness.Client.GetFromJsonAsync<SecurityReviewListResponse>(
            $"/api/v1/studio/projects/{project.ProjectId}/security/reviews");
        Assert.Empty(emptyReviews!.Reviews);

        var accept = await harness.Client.PostAsync(
            $"/api/v1/studio/projects/{project.ProjectId}/security/audit", null);
        Assert.Equal(HttpStatusCode.Accepted, accept.StatusCode);
        var accepted = (await accept.Content.ReadFromJsonAsync<StudioOperationAcceptedResponse>())!;
        Assert.Equal(StudioOperationKinds.SecurityAudit, accepted.Kind);
        Assert.Equal(StudioOperationStatuses.Pending, accepted.Status);

        await harness.CompleteLatestOperationAsync(
            accepted.OperationId,
            """{"summary":"1 finding","reviews":[{"fileName":"app.py","severity":"high","summary":"SQL injection risk"}]}""");

        var baseline = await harness.Client.GetFromJsonAsync<SecurityBaselineDto>(
            $"/api/v1/studio/projects/{project.ProjectId}/security/baseline");
        Assert.Equal(accepted.OperationId, baseline!.OperationId);
        Assert.Contains("1 finding", baseline.ResultJson);

        var reviews = await harness.Client.GetFromJsonAsync<SecurityReviewListResponse>(
            $"/api/v1/studio/projects/{project.ProjectId}/security/reviews");
        var review = Assert.Single(reviews!.Reviews);
        Assert.Equal(accepted.OperationId, review.Id);

        var detail = await harness.Client.GetFromJsonAsync<SecurityReviewDetailDto>(
            $"/api/v1/studio/projects/{project.ProjectId}/security/reviews/app.py");
        Assert.Equal("app.py", detail!.FileName);
        Assert.Contains("SQL injection", detail.DetailJson);

        var missing = await harness.Client.GetAsync(
            $"/api/v1/studio/projects/{project.ProjectId}/security/reviews/does-not-exist.py");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Deployment_compile_dispatch_surfaces_through_summary()
    {
        using var temp = new TempDirectory();
        await using var harness = await ComplianceHarness.CreateAsync(temp.Path);
        var project = await harness.CreateProjectAsync("Deployment Project", "DEP");

        var emptySummary = await harness.Client.GetFromJsonAsync<DeploymentSummaryDto>(
            $"/api/v1/studio/projects/{project.ProjectId}/deployment/summary");
        Assert.Null(emptySummary!.OperationId);

        var accept = await harness.Client.PostAsync(
            $"/api/v1/studio/projects/{project.ProjectId}/deployment/compile", null);
        Assert.Equal(HttpStatusCode.Accepted, accept.StatusCode);
        var accepted = (await accept.Content.ReadFromJsonAsync<StudioOperationAcceptedResponse>())!;
        Assert.Equal(StudioOperationKinds.DeploymentCompile, accepted.Kind);

        await harness.CompleteLatestOperationAsync(accepted.OperationId, """{"status":"ok","warnings":0}""");

        var summary = await harness.Client.GetFromJsonAsync<DeploymentSummaryDto>(
            $"/api/v1/studio/projects/{project.ProjectId}/deployment/summary");
        Assert.Equal(accepted.OperationId, summary!.OperationId);
        Assert.Contains("\"status\":\"ok\"", summary.ResultJson);
    }

    [Fact]
    public async Task Publish_automation_toggle_is_durable_and_package_website_dispatch_studio_operations()
    {
        using var temp = new TempDirectory();
        await using var harness = await ComplianceHarness.CreateAsync(temp.Path);
        var project = await harness.CreateProjectAsync("Publish Project", "PUB");

        var enable = await harness.Client.PutAsJsonAsync(
            $"/api/v1/studio/projects/{project.ProjectId}/publish/automation", new SetPublishAutomationRequest(true));
        enable.EnsureSuccessStatusCode();
        var enabled = (await enable.Content.ReadFromJsonAsync<PublishAutomationSettingDto>())!;
        Assert.True(enabled.Enabled);
        Assert.Equal(project.ProjectId, enabled.ProjectId);

        var disable = await harness.Client.PutAsJsonAsync(
            $"/api/v1/studio/projects/{project.ProjectId}/publish/automation", new SetPublishAutomationRequest(false));
        disable.EnsureSuccessStatusCode();
        Assert.False((await disable.Content.ReadFromJsonAsync<PublishAutomationSettingDto>())!.Enabled);

        var package = await harness.Client.PostAsync(
            $"/api/v1/studio/projects/{project.ProjectId}/publish/package", null);
        Assert.Equal(HttpStatusCode.Accepted, package.StatusCode);
        Assert.Equal(
            StudioOperationKinds.PublishPackage,
            (await package.Content.ReadFromJsonAsync<StudioOperationAcceptedResponse>())!.Kind);

        var website = await harness.Client.PostAsync(
            $"/api/v1/studio/projects/{project.ProjectId}/publish/website", null);
        Assert.Equal(HttpStatusCode.Accepted, website.StatusCode);
        Assert.Equal(
            StudioOperationKinds.PublishWebsite,
            (await website.Content.ReadFromJsonAsync<StudioOperationAcceptedResponse>())!.Kind);
    }

    [Fact]
    public async Task Wiki_grading_run_dispatches_and_abort_cancels_the_in_flight_operation()
    {
        using var temp = new TempDirectory();
        await using var harness = await ComplianceHarness.CreateAsync(temp.Path);
        var project = await harness.CreateProjectAsync("Wiki Project", "WIK");

        var noOpAbort = await harness.Client.PostAsync(
            $"/api/v1/studio/projects/{project.ProjectId}/wiki/grading/abort", null);
        noOpAbort.EnsureSuccessStatusCode();
        var noOpResult = (await noOpAbort.Content.ReadFromJsonAsync<WikiGradingAbortResponse>())!;
        Assert.False(noOpResult.Aborted);
        Assert.Null(noOpResult.OperationId);

        var run = await harness.Client.PostAsync(
            $"/api/v1/studio/projects/{project.ProjectId}/wiki/grading/run", null);
        Assert.Equal(HttpStatusCode.Accepted, run.StatusCode);
        var accepted = (await run.Content.ReadFromJsonAsync<StudioOperationAcceptedResponse>())!;
        Assert.Equal(StudioOperationKinds.WikiGradingRun, accepted.Kind);
        Assert.Equal(StudioOperationStatuses.Pending, accepted.Status);

        var abort = await harness.Client.PostAsync(
            $"/api/v1/studio/projects/{project.ProjectId}/wiki/grading/abort", null);
        abort.EnsureSuccessStatusCode();
        var abortResult = (await abort.Content.ReadFromJsonAsync<WikiGradingAbortResponse>())!;
        Assert.True(abortResult.Aborted);
        Assert.Equal(accepted.OperationId, abortResult.OperationId);

        var operation = await harness.Store.GetStudioOperationAsync(accepted.OperationId, default);
        Assert.Equal(StudioOperationStatuses.Canceled, operation!.Status);

        var secondAbort = await harness.Client.PostAsync(
            $"/api/v1/studio/projects/{project.ProjectId}/wiki/grading/abort", null);
        secondAbort.EnsureSuccessStatusCode();
        Assert.False((await secondAbort.Content.ReadFromJsonAsync<WikiGradingAbortResponse>())!.Aborted);
    }

    [Fact]
    public async Task Unknown_project_identity_returns_not_found()
    {
        using var temp = new TempDirectory();
        await using var harness = await ComplianceHarness.CreateAsync(temp.Path);

        var response = await harness.Client.GetAsync("/api/v1/studio/projects/does-not-exist/security/baseline");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// A minimal standalone host that maps only this bundle's routes while
    /// relying on the product store's canonical migration sequence.
    /// </summary>
    private sealed class ComplianceHarness : IAsyncDisposable
    {
        private ComplianceHarness(WebApplication app, TaskServerStore store, HttpClient client)
        {
            App = app;
            Store = store;
            Client = client;
        }

        public WebApplication App { get; }
        public TaskServerStore Store { get; }
        public HttpClient Client { get; }

        public static async Task<ComplianceHarness> CreateAsync(string dataDirectory)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
            builder.Services.AddSingleton<IResultFinalizationSummaryGenerator, ApplicationResultFinalizationSummaryGenerator>();
            builder.Services.Configure<TaskServerOptions>(options =>
            {
                options.DataDirectory = dataDirectory;
                options.BackupDirectory = Path.Combine(dataDirectory, "backups");
            });
            builder.Services.AddSingleton<TaskServerStore>();
            builder.Services.AddStudioP2ComplianceServices();

            var app = builder.Build();
            var store = app.Services.GetRequiredService<TaskServerStore>();
            await store.InitializeAsync();

            app.UseRouting();
            app.MapStudioP2ComplianceEndpoints();
            await app.StartAsync();

            return new ComplianceHarness(app, store, app.GetTestClient());
        }

        public async Task<ProjectDto> CreateProjectAsync(string name, string prefix)
        {
            var workspace = await Store.CreateWorkspaceAsync(new CreateWorkspaceRequest($"{name} workspace"), "test", default);
            return await Store.CreateProjectAsync(
                new CreateProjectRequest(workspace.WorkspaceId, name, prefix), "test", default);
        }

        /// <summary>
        /// Simulates a Runner claiming and completing the given studio
        /// operation, the same fenced claim/report cycle
        /// <c>StudioOperationsEndpoints</c> exposes over HTTP. That surface
        /// is out of this bundle's scope, so the harness drives it directly
        /// through the store.
        /// </summary>
        public async Task CompleteLatestOperationAsync(string operationId, string resultJson)
        {
            var claim = await Store.ClaimStudioOperationAsync(
                new ClaimStudioOperationRequest("harness-runner", "harness-instance"), default);
            Assert.Equal("claimed", claim.Status);
            var operation = claim.Operation!;
            Assert.Equal(operationId, operation.OperationId);
            await Store.CompleteStudioOperationAsync(
                operationId,
                new CompleteStudioOperationRequest(
                    "harness-runner",
                    "harness-instance",
                    operation.LeaseId!,
                    operation.Fence,
                    "succeeded",
                    ResultJson: resultJson),
                default);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.DisposeAsync();
        }
    }
}
