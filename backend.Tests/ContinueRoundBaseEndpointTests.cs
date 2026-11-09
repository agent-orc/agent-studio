using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Pipeline;
using AgentStudio.Runner;
using AgentStudio.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3483 (operator-agent-level Dossier, OP-3) at the HTTP boundary:
/// <c>POST /api/tasks/{id}/continue</c> starts from the card's latest result
/// ref, refuses a <c>baseRef</c> the card does not own, and refuses continue,
/// steer and requeue for a member of an isolating batch while eject stays
/// allowed. The cases use only the wire shape, so they also describe the
/// behaviour before the change: there the ref was ignored and every round was
/// accepted.
/// </summary>
public sealed class ContinueRoundBaseEndpointTests : IDisposable
{
    private const string Project = "base-ref";
    private static readonly DateTimeOffset Reviewed = new(2026, 11, 4, 8, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "continue-base-" + Guid.NewGuid().ToString("N"));
    private string WatchPath => Path.Combine(_root, "projects", Project);

    [Fact]
    public async Task Continue_without_a_ref_starts_from_the_cards_latest_result_ref()
    {
        var (resultRef, sha) = SeedReviewedCard("AGT-LATEST", TaskStates.HumanReview);
        await using var factory = BuildFactory();
        using var client = Client(factory);

        var response = await client.PostAsJsonAsync(Route("AGT-LATEST", "continue"),
            new { prompt = "Tighten the wording of the summary." });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("queued", body.GetProperty("status").GetString());
        Assert.True(body.TryGetProperty("base", out var resolved) && resolved.ValueKind == JsonValueKind.Object,
            "the response names the resolved base: " + body);
        Assert.Equal(resultRef, resolved.GetProperty("ref").GetString());
        Assert.Equal(sha, resolved.GetProperty("sha").GetString());
        Assert.Equal("latest-result", resolved.GetProperty("source").GetString());

        // The claim reads this record and hands it to the runner as the run base.
        var readyFolder = Path.Combine(WatchPath, TaskStates.Ready, "AGT-LATEST");
        var claimed = ContinuationBaseStore.ConsumeForClaim(readyFolder);
        Assert.Equal(resultRef, claimed?.Ref);
        Assert.Equal(sha, claimed?.CommitSha);
        var recorded = Assert.Single(factory.Services.GetRequiredService<TimelineLog>().ReadAll(readyFolder),
            row => row.Kind == TimelineEventKinds.ContinuationBaseRecorded);
        Assert.Equal(sha, recorded.Details!["sha"]);
    }

    [Fact]
    public async Task Continue_with_a_base_ref_the_card_does_not_own_returns_400()
    {
        SeedReviewedCard("AGT-FOREIGN", TaskStates.HumanReview);
        await using var factory = BuildFactory();
        using var client = Client(factory);

        var response = await client.PostAsJsonAsync(Route("AGT-FOREIGN", "continue"), new
        {
            prompt = "Continue from the other delivery.",
            baseRef = "refs/heads/agent-studio/results/run_other/fence-1/" + new string('e', 40),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("base-ref-not-on-card", body.GetProperty("error").GetString());
        var folder = Path.Combine(WatchPath, TaskStates.HumanReview, "AGT-FOREIGN");
        Assert.True(Directory.Exists(folder), "a refused round leaves the card in its lane");
        Assert.False(File.Exists(Path.Combine(folder, "pending-intent.json")));
        Assert.Null(ContinuationBaseStore.Read(folder));
    }

    [Fact]
    public async Task Continue_with_a_base_ref_of_the_card_starts_from_it()
    {
        var (_, sha) = SeedReviewedCard("AGT-NAMED", TaskStates.HumanReview, legacyRef: "agent-studio/salvage/AGT-NAMED");
        await using var factory = BuildFactory();
        using var client = Client(factory);

        var response = await client.PostAsJsonAsync(Route("AGT-NAMED", "continue"),
            new { prompt = "Continue on the salvage branch.", mode = "extend", baseRef = "agent-studio/salvage/AGT-NAMED" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var resolved = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("base");
        Assert.Equal("refs/heads/agent-studio/salvage/AGT-NAMED", resolved.GetProperty("ref").GetString());
        Assert.Equal(sha, resolved.GetProperty("sha").GetString());
        Assert.Equal("explicit", resolved.GetProperty("source").GetString());
    }

    [Fact]
    public async Task Member_of_an_isolating_batch_refuses_continue_steer_and_requeue_while_eject_succeeds()
    {
        SeedReviewedCard("AGT-ISO", TaskStates.AutoReview);
        await using var factory = BuildFactory();
        using var client = Client(factory);
        var flight = SeedIsolatingFlight(factory, "AGT-ISO");

        foreach (var mode in new[] { "continue", "steer", "extend" })
        {
            var round = await client.PostAsJsonAsync(Route("AGT-ISO", "continue"),
                new { prompt = "Fix the failing test.", mode });
            await AssertIsolationRefusalAsync(round, flight.BatchId);
        }
        var requeue = await client.PostAsJsonAsync(Route("AGT-ISO", "move"),
            new { targetState = TaskStates.Ready, reason = "Run it again" });
        await AssertIsolationRefusalAsync(requeue, flight.BatchId);
        var batchMove = await factory.Services.GetRequiredService<IBatchMoveItemExecutor>().ExecuteAsync(
            new BatchMoveItem { JobId = "AGT-ISO", WatchPath = WatchPath, TargetState = TaskStates.Ready },
            default, "human:operator");
        Assert.Equal("rejected", batchMove.Status);
        Assert.StartsWith("batch-isolation-active", batchMove.Message, StringComparison.Ordinal);

        var folder = Path.Combine(WatchPath, TaskStates.AutoReview, "AGT-ISO");
        Assert.True(Directory.Exists(folder), "a refused round leaves the member in its lane");
        Assert.False(File.Exists(Path.Combine(folder, "pending-intent.json")));

        var eject = await client.PostAsync(
            $"/api/projects/{Project}/batch-gate/members/AGT-ISO/eject", content: null);
        Assert.Equal(HttpStatusCode.Accepted, eject.StatusCode);
        Assert.True(factory.Services.GetRequiredService<BatchGateStore>().HasEject(Project, "AGT-ISO"));

        // When isolation ends without a verdict the batch pauses; the member takes rounds again.
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        BatchGateStates.Reach(store, new BatchGateState(flight.BatchId, flight.MembershipDigest, BatchPhase.Paused,
            new string('b', 40), 1, Reviewed.AddMinutes(30), "unresolved-cohort:AGT-ISO"));
        var after = await client.PostAsJsonAsync(Route("AGT-ISO", "continue"),
            new { prompt = "Fix the failing test." });
        Assert.Equal(HttpStatusCode.Accepted, after.StatusCode);
    }

    private static async Task AssertIsolationRefusalAsync(HttpResponseMessage response, string flight)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("batch-isolation-active", body.GetProperty("error").GetString());
        Assert.Equal(flight, body.GetProperty("flight").GetString());
    }

    private (string Ref, string Sha) SeedReviewedCard(string id, string state, string? legacyRef = null)
    {
        var folder = Path.Combine(WatchPath, state, id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = id,
            ["title"] = id,
            ["state"] = state,
            ["order"] = 1,
            ["agent"] = "claude",
        }));
        File.WriteAllText(Path.Combine(folder, "prompt.md"), $"# {id}\n\nOriginal task.\n");
        var sha = new string('b', 40);
        var resultRef = AgentStudio.TaskServer.Contracts.FencedGitRefs.ImmutableResult("run-" + id, 3, sha);
        ReviewSubjectStore.Write(folder, new ReviewSubjectRecord
        {
            TaskKey = id,
            RunAttemptId = "run-" + id,
            AttemptChainId = "chain-" + id,
            Project = Project,
            ResultSha = sha,
            ImmutableResultRef = resultRef,
            ResultRef = legacyRef,
            CompletedAtUtc = Reviewed,
        });
        return (resultRef, sha);
    }

    private BatchGateManifest SeedIsolatingFlight(WebApplicationFactory<Program> factory, string id)
    {
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var folder = Path.Combine(WatchPath, TaskStates.AutoReview, id);
        var baseSha = new string('a', 40);
        var resultSha = new string('b', 40);
        var scope = new BatchGateScope(Project, "repo", "develop", "full", "digest", "v1");
        var subject = new BatchGateSubject(id, Project, "repo", "develop", "full", "digest", "v1",
            AgentStudio.TaskServer.Contracts.FencedGitRefs.ImmutableResult("run-" + id, 3, resultSha),
            resultSha, "run-" + id, 1, 3, resultSha, true, true, true, true, false, null, false, Reviewed, 1);
        var manifest = new BatchGateManifest("flight-" + id.ToLowerInvariant(), scope, baseSha,
            BatchGatePolicy.MembershipDigest(baseSha, scope, [subject]), Reviewed, [subject], [], [id]);
        store.CloseManifest(manifest);
        store.Enqueue(new BatchGatePendingRecord("rev-" + id, subject, "repo", WatchPath, folder,
            "merge", "default", Reviewed));
        BatchGateOwnershipStore.Write(folder, new BatchGateOwnership("rev-" + id, subject, BatchId: manifest.BatchId));
        BatchGateStates.Reach(store, new BatchGateState(manifest.BatchId, manifest.MembershipDigest, BatchPhase.Red,
            resultSha, 1, Reviewed.AddMinutes(10), "deterministic suite red"));
        return manifest;
    }

    private string Route(string id, string action)
        => $"/api/tasks/{id}/{action}?watchPath={Uri.EscapeDataString(WatchPath)}";

    private WebApplicationFactory<Program> BuildFactory()
    {
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(WatchPath, state));
        var factory = new TestHostFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TaskRepository"] = _root,
                    ["WatchPaths:0:Name"] = Project,
                    ["WatchPaths:0:Path"] = WatchPath,
                    ["WatchPaths:0:RootPath"] = WatchPath,
                }));
            // No periodic worker may move the seeded cards between the requests,
            // and the batch store lives in this fixture's root, not in the
            // machine's local state directory.
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<BatchGateStore>();
                services.AddSingleton(new BatchGateStore(Path.Combine(_root, "batch-gates")));
            });
        });
        // Hosted services are removed, so activate the project's runner as the
        // host start would. A persisted orchestrator session keeps its boot a
        // no-op, so no CLI is reached.
        factory.Services.GetRequiredService<OrchestratorSessionStore>().Write(WatchPath, new OrchestratorSession(
            SessionId: "test-session", Model: "test-model", BootedAt: Reviewed.UtcDateTime,
            BootPromptPreview: string.Empty, BootReplyPreview: string.Empty,
            CumulativeInputTokens: 0, CumulativeOutputTokens: 0, CumulativeCacheReadTokens: 0,
            CumulativeCacheCreationTokens: 0, Calls: 1, LastUsedAt: Reviewed.UtcDateTime, LastError: null));
        var scanner = factory.Services.GetRequiredService<TaskScannerService>();
        Assert.True(factory.Services.GetRequiredService<TaskRunnerService>()
            .EnsureRunner(scanner.GetWatchPaths().First(entry => entry.Name == Project)), "the project runner must activate");
        return factory;
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);
        return client;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) { SilentCatch.Note(ex, "test teardown is best-effort"); }
    }
}
