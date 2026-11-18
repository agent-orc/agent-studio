using System.Text.Json;
using AgentStudio.Pipeline;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// A missing server checkout leaves the Git state index without a projection.
/// Board, detail and Git-detail reads must retain the recorded
/// <c>server-checkout-missing</c> code so the card can show its remedy.
/// </summary>
public sealed class ServerCheckoutFailureProjectionEndpointTests : IDisposable
{
    private const string Id = "TLY-1";
    private static readonly DateTime FailedAt = new(2026, 11, 15, 21, 41, 24, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "server-checkout-projection-" + Guid.NewGuid().ToString("N"));
    private string Jobs => Path.Combine(_root, "jobs");

    [Theory]
    [InlineData(null)]
    [InlineData("missing")]
    [InlineData("nongit")]
    public async Task EscalatedCard_WithoutUsableServerCheckout_CarriesTheRecordedFailureCodeOnEveryRead(string? checkout)
    {
        SeedEscalatedCardWithCheckoutFailure();
        if (checkout == "nongit") Directory.CreateDirectory(Path.Combine(_root, checkout));
        await using var factory = Factory(checkout);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        var project = factory.Services.GetRequiredService<ProjectRegistry>().FindByStorageLocation(Jobs)!;
        await WarmIndexAsync(factory);

        // Full detail (Human Review acceptance and the legacy detail read).
        using var detail = await client.GetAsync($"/api/tasks/{Id}?project={project.Id}");
        detail.EnsureSuccessStatusCode();
        using var detailBody = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        AssertCheckoutFailure(detailBody.RootElement.GetProperty("info").GetProperty("integration"));

        // Git detail resource.
        using var coreResponse = await client.GetAsync($"/api/tasks/{Id}/core?project={project.Id}");
        coreResponse.EnsureSuccessStatusCode();
        using var core = JsonDocument.Parse(await coreResponse.Content.ReadAsStringAsync());
        var generation = core.RootElement.GetProperty("coreVersion").GetString();
        using var git = await client.GetAsync($"/api/tasks/{Id}/details/git?project={project.Id}&generation={generation}");
        git.EnsureSuccessStatusCode();
        using var gitBody = JsonDocument.Parse(await git.Content.ReadAsStringAsync());
        AssertCheckoutFailure(gitBody.RootElement.GetProperty("data").GetProperty("integration"));

        // Board list, which seeds the open card's detail.
        using var board = await client.GetAsync($"/api/tasks?project={project.Id}");
        board.EnsureSuccessStatusCode();
        using var boardBody = JsonDocument.Parse(await board.Content.ReadAsStringAsync());
        var boardCard = Assert.Single(boardBody.RootElement.EnumerateArray());
        AssertCheckoutFailure(boardCard.GetProperty("integration"));

        // The grouped board and persisted-board writer use the same projector.
        var card = factory.Services.GetRequiredService<TaskIndexCache>().GetDetailFacts(Id, Jobs).Info!;
        var projected = Assert.Single(factory.Services.GetRequiredService<BoardCardProjector>().Project([card]));
        Assert.Equal(AcceptedIntegrationFailureCodes.ServerCheckoutMissing, projected.Integration?.Failure?.Code);
        Assert.False(projected.Integration!.Failure!.ReplayAvailable);
    }

    private static void AssertCheckoutFailure(JsonElement integration)
    {
        Assert.Equal(JsonValueKind.Object, integration.ValueKind);
        Assert.Equal(IntegrationStatuses.ConflictSkipped, integration.GetProperty("status").GetString());
        var failure = integration.GetProperty("failure");
        Assert.Equal(AcceptedIntegrationFailureCodes.ServerCheckoutMissing, failure.GetProperty("code").GetString());
        Assert.False(failure.TryGetProperty("replayAvailable", out var replay) && replay.GetBoolean());
    }

    private void SeedEscalatedCardWithCheckoutFailure()
    {
        var folder = Path.Combine(Jobs, TaskStates.Escalated, Id);
        Directory.CreateDirectory(Path.Combine(folder, "logs"));
        const string sha = "95b1273a72c912748114e171a67e4de592f06d2b";
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
        {
            id = Id, key = Id, title = "Add mean() helper", state = TaskStates.Escalated, order = 10,
            ownerClientId = "local-default",
            enteredLaneAt = FailedAt,
            mode = "coding", modelExplicit = true, thinkingLevelExplicit = true,
            commit = new { sha, shortSha = sha[..8], message = "delivery", filesChanged = 1, at = FailedAt },
            commits = new[] { new { sha, shortSha = sha[..8], message = "delivery", filesChanged = 1, at = FailedAt } },
        }));
        File.WriteAllText(Path.Combine(folder, "prompt.md"), "Add a mean() helper.");
        File.WriteAllText(Path.Combine(folder, "status.md"), "Delivered.");
        var pipeline = new PipelineExecutionLog(TokenTestServices.Pricing(), NullLogger<PipelineExecutionLog>.Instance);
        pipeline.Begin(folder, PipelineCatalogue.Standard, "Tally Library", Id);
        pipeline.RecordStep(folder, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.MergeIntoDevelopStepId,
            Kind = StepKind.Tool,
            Status = PipelineStepStatus.Failed,
            StartedAt = FailedAt,
            CompletedAt = FailedAt,
            Verdict = AcceptedIntegrationFailureCodes.ServerCheckoutMissing,
            Reason = "This project needs a server checkout before delivery can integrate. Integration worktree "
                     + "preparation cannot continue. Open Project Settings, configure a checkout on the Studio "
                     + "server, then retry integration.",
            FailureCode = AcceptedIntegrationFailureCodes.ServerCheckoutMissing,
        });
    }

    private async Task WarmIndexAsync(WebApplicationFactory<Program> factory)
    {
        var index = factory.Services.GetRequiredService<TaskIndexCache>();
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (index.GetCore(Id, Jobs).Warming)
        {
            Assert.True(waited.Elapsed < TimeSpan.FromSeconds(30), $"Task index did not warm for {Id}.");
            if (index.RefreshInFlight is { } refresh)
                await refresh.WaitAsync(TimeSpan.FromSeconds(30));
            else
                await Task.Delay(TimeSpan.FromMilliseconds(10));
        }
    }

    private WebApplicationFactory<Program> Factory(string? checkout) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = Path.Combine(_root, "workspace"),
                ["ErrorHandling:IncludeExceptionDetails"] = "true",
                ["WatchPaths:0:Name"] = "Tally Library",
                ["WatchPaths:0:Path"] = Jobs,
                ["WatchPaths:0:RepositoryPath"] = checkout is null ? null : Path.Combine(_root, checkout),
            }));
            builder.ConfigureTestServices(services => services.RemoveAll<IHostedService>());
        });

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException ex) { SilentCatch.Note(ex, "Watcher disposal may complete after the test."); }
    }
}
