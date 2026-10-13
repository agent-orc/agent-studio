using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ReviewPriorityEndpointTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "review-priority-endpoint-" + Guid.NewGuid().ToString("N"));
    private string WatchPath => Path.Combine(_root, "projects", "reviews");
    private DateTime _now = new(2026, 10, 11, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Operator_route_uses_reference_key_and_changes_claim_order()
    {
        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);
        var authority = factory.Services.GetRequiredService<AttemptAuthorityService>();
        var older = CreateReview(factory, authority, "AGT-OLD", 'a');
        _now = _now.AddMinutes(1);
        var newer = CreateReview(factory, authority, "AGT-NEW", 'b');
        factory.Services.GetRequiredService<TaskIndexCache>().ForceRefresh();
        var card = factory.Services.GetRequiredService<TaskScannerService>().FindJob("AGT-NEW", WatchPath)!;
        Assert.Equal("AGT-NEW", card.Key);
        Assert.NotEqual(card.Key, card.TaskKey);

        var response = await http.PostAsJsonAsync(
            $"/api/tasks/AGT-NEW/review-priority?watchPath={Uri.EscapeDataString(WatchPath)}",
            new { priority = "urgent" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("urgent", authority.ReadReviewQueue()["AGT-NEW"].Priority);
        using var board = JsonDocument.Parse(await http.GetStringAsync("/api/tasks?includeFixtures=true"));
        var boardCard = board.RootElement.EnumerateArray().Single(item =>
            item.GetProperty("id").GetString() == "AGT-NEW");
        Assert.Equal("urgent", boardCard.GetProperty("reviewPriority").GetString());

        // These fixture runs have no result envelope, so advance beyond the
        // compatibility grace before asking the claim queue to select them.
        _now = _now.AddMinutes(16);
        Assert.Equal(1, authority.ReadReviewQueue()["AGT-NEW"].Position);
        using var claimableBoard = JsonDocument.Parse(await http.GetStringAsync("/api/tasks?includeFixtures=true"));
        var claimableCard = claimableBoard.RootElement.EnumerateArray().Single(item =>
            item.GetProperty("id").GetString() == "AGT-NEW");
        Assert.Equal(1, claimableCard.GetProperty("reviewQueuePosition").GetInt32());
        Assert.Equal(newer.AttemptId,
            authority.ClaimNextReview("reviewer", "host", "instance", 60).ReviewAttempt!.AttemptId);
        Assert.Equal(older.AttemptId,
            authority.ClaimNextReview("reviewer", "host", "instance", 60).ReviewAttempt!.AttemptId);
    }

    [Fact]
    public void Lane_exit_clears_priority_in_durable_authority()
    {
        using var factory = BuildFactory();
        using var client = factory.CreateClient();
        var authority = factory.Services.GetRequiredService<AttemptAuthorityService>();
        CreateReview(factory, authority, "AGT-EXIT", 'c');
        factory.Services.GetRequiredService<TaskIndexCache>().ForceRefresh();
        Assert.True(authority.SetReviewPriority("AGT-EXIT", "urgent"));

        var moved = factory.Services.GetRequiredService<TaskStateMachine>()
            .MoveJob("AGT-EXIT", TaskStates.Backlog, WatchPath);

        Assert.Equal(MoveJobStatus.Success, moved.Status);
        Assert.Equal("normal", authority.ReadReviewQueue()["AGT-EXIT"].Priority);
        Assert.Equal("normal", new AttemptAuthorityService(
            factory.Services.GetRequiredService<IConfiguration>(),
            factory.Services.GetRequiredService<ILogger<AttemptAuthorityService>>(),
            () => _now).ReadReviewQueue()["AGT-EXIT"].Priority);
    }

    private ReviewAttemptDto CreateReview(WebApplicationFactory<Program> factory,
        AttemptAuthorityService authority, string key, char digit)
    {
        var folder = Path.Combine(WatchPath, TaskStates.AutoReview, key);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
        {
            id = key, key, title = key, state = TaskStates.AutoReview, order = 1, agent = "codex",
        }));
        File.WriteAllText(Path.Combine(folder, "prompt.md"), "Review fixture");
        File.WriteAllText(Path.Combine(folder, "status.md"), "Result: ready for review.");
        var run = authority.AcquireRun(key, "reviews", null, "runner", "host", 60,
            "run-" + key).RunAttempt!;
        var sha = new string(digit, 40);
        authority.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(run.AttemptId, run.LastFence, run.AuthorityEpoch, "settle-" + key),
            Outcome = "done", ResultSha = sha,
        });
        return authority.CreateReviewAttempt(new CreateReviewAttemptRequest(
            key, "reviews", sha, run.AttemptId, "requirements", "policy", [],
            "review-" + key)).ReviewAttempt!;
    }

    private WebApplicationFactory<Program> BuildFactory()
    {
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(WatchPath, state));
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TaskRepository"] = _root,
                    ["WatchPaths:0:Name"] = "reviews",
                    ["WatchPaths:0:Path"] = WatchPath,
                    ["WatchPaths:0:RootPath"] = WatchPath,
                    ["ReviewDecisionOrchestrator:Enabled"] = "false",
                    ["DeliveryChain:Guarded"] = "false",
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<AttemptAuthorityService>();
                services.AddSingleton(provider => new AttemptAuthorityService(
                    provider.GetRequiredService<IConfiguration>(),
                    provider.GetRequiredService<ILogger<AttemptAuthorityService>>(),
                    () => _now,
                    provider.GetRequiredService<IAtomicJsonFileWriter>()));
            });
        });
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
