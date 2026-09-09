extern alias Runner;

using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

using Contract = AgentStudio.TaskServer.Contracts;
using RClient = Runner::AgentRunner.TaskServerClient;
using RClaim = Runner::AgentRunner.RunnerClaimRequest;
using RClaimStatus = Runner::AgentRunner.RunnerClaimStatus;
using ROptions = Runner::AgentRunner.RunnerOptions;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2751: cross-CLI quota-aware admission, exercised end-to-end through the
/// remote execution paths that sit outside the shared local one-shot boundary:
/// the remote-claim candidate loop
/// (<c>/api/runner/claim</c>) and the review-attempt claim
/// (<c>/api/v1/runners/{runnerId}/review-claims</c>). Both paths must:
/// (a) route a quota-exhausted card to the other equivalence-catalogue family
/// derived by <see cref="ModelEquivalenceCatalog"/> with no
/// <c>cli-model-routing.json</c> profile configured, (b) persist a durable
/// <c>quota-fallback.json</c> marker for the owning task, and (c) leave the
/// frozen source-of-truth (the task's own configured cli/model for a claim,
/// the immutable ReviewAttempt's persisted plan for a review claim) untouched.
/// </summary>
[Collection(WebApplicationFactorySerialCollection.Name)]
public sealed class QuotaFallbackCrossPathTests : IDisposable
{
    private const string ProjectName = "quota-fallback-cross-path";
    private const string RunnerId = "quota-fallback-runner";

    private readonly string _workspace;
    private readonly string _watchPath;

    public QuotaFallbackCrossPathTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "atp-quota-fallback-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "projects", ProjectName);
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
        SeedOrchestratorSessions();
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>
    /// Seeds a cached quota snapshot showing <paramref name="cliType"/> over its
    /// configured cap, written to disk BEFORE the host is ever started so that
    /// <c>QuotaService</c>'s constructor-time hydration (the only place it reads
    /// the cache from disk) picks it up. <c>WebApplicationFactory</c> builds the
    /// DI container lazily (on the first client/service resolution), so writing
    /// the file in the test body before <c>BuildFactory().CreateClient()</c> is
    /// enough - no special test hook is required.
    /// </summary>
    private void SeedExhaustedQuota(string cliType, double usedPct, TimeSpan resetIn)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _workspace })
            .Build();
        var store = new QuotaCacheStore(config, NullLogger<QuotaCacheStore>.Instance);
        store.Write([
            new QuotaSnapshot
            {
                CliType = cliType,
                FetchedAt = DateTime.UtcNow,
                Windows =
                [
                    new QuotaWindow
                    {
                        Label = "5-hour",
                        UsedPct = usedPct,
                        ResetAt = DateTime.UtcNow.Add(resetIn),
                    },
                ],
            },
        ]);
    }

    // ── Test A: remote-claim (/api/runner/claim) picks the equivalence fallback ──
    [Theory]
    [InlineData(CliTypes.Codex, ModelIds.Gpt56Sol, "high", CliTypes.Claude, ModelIds.ClaudeOpus5, "high")]
    [InlineData(CliTypes.Claude, ModelIds.ClaudeOpus5, "high", CliTypes.Codex, ModelIds.Gpt56Sol, "high")]
    public async Task Remote_claim_routes_a_quota_exhausted_card_to_the_equivalent_family(
        string configuredCli,
        string configuredModel,
        string configuredThinking,
        string expectedCli,
        string expectedModel,
        string expectedThinking)
    {
        var taskKey = $"AGT-QUOTA-CLAIM-{configuredCli.ToUpperInvariant()}";
        SeedTask(
            TaskStates.Ready, taskKey, "Card queued during quota incident", "Do the configured thing.",
            cliType: configuredCli, model: configuredModel, thinkingLevel: configuredThinking);

        // The configured family is at 98% of its 5-hour window (cap default
        // 95%), reset a few hours out. No cli-model-routing.json profile is
        // written: the catalogue alone must derive the equal-strength route.
        SeedExhaustedQuota(configuredCli, 98, TimeSpan.FromHours(3));

        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        using var client = new RClient(http, RunnerId);

        // The runner advertises BOTH codex and claude capability/provider-auth
        // (RegisterCodingRunnerAsync's default). Capability admission runs
        // against the RESOLVED (post-quota) cli, so a runner that only ever
        // spoke codex for this card can still claim it once quota routes it to
        // claude - but this fixture keeps both ready to avoid conflating the
        // capability check with the quota-routing behavior under test.
        await RegisterCodingRunnerAsync(client, http);
        await AssignRemoteAsync(http);
        await AddRepositoryUrlAsync(
            http,
            $"https://github.com/example/quota-claim-fallback-{configuredCli}.git");

        var claimRequest = new RClaim(
            RunnerId,
            ProjectName,
            "host",
            1,
            "remote-runner",
            IdempotencyKey: $"quota-fallback-{configuredCli}");
        var claim = await ClaimWithSuccessfulPreflightAsync(client, claimRequest);

        Assert.Equal(RClaimStatus.Claimed, claim.Status);
        Assert.False(string.IsNullOrWhiteSpace(claim.TaskKey));
        Assert.NotNull(claim.RunSpec);
        Assert.Equal(expectedCli, claim.RunSpec!.CliType);
        Assert.Equal(expectedModel, claim.RunSpec.Model);
        Assert.Equal(expectedThinking, claim.RunSpec.ThinkingLevel);

        // An idempotent re-delivery is the same launch, so it must replay the
        // already-resolved spec rather than rebuilding the capped card route.
        var replay = await client.ClaimAsync(claimRequest, CancellationToken.None);
        Assert.Equal(RClaimStatus.Claimed, replay.Status);
        Assert.Equal(expectedCli, replay.RunSpec?.CliType);
        Assert.Equal(expectedModel, replay.RunSpec?.Model);
        Assert.Equal(expectedThinking, replay.RunSpec?.ThinkingLevel);

        // The move keeps the card's on-disk folder name (its "id"), while the
        // wire TaskKey the claim returns is the project's own assigned key
        // ("QFC-1"-style) - a separate, auto-backfilled field. Locate the
        // moved folder by the id this fixture seeded the card under.
        var claimedFolder = Path.Combine(_watchPath, TaskStates.Progress, taskKey);
        Assert.True(Directory.Exists(claimedFolder), "claimed card did not move to 2-progress");

        var timeline = factory.Services.GetRequiredService<TimelineLog>().ReadAll(claimedFolder);
        var fallbackEvent = Assert.Single(
            timeline, e => e.Kind == TimelineEventKinds.QuotaFallbackActivated);
        Assert.Equal(TimelineActors.System, fallbackEvent.Actor);
        Assert.Equal(expectedCli, fallbackEvent.Details!["fallbackCli"]);
        Assert.Equal(expectedModel, fallbackEvent.Details["fallbackModel"]);
        Assert.Equal(configuredCli, fallbackEvent.Details["primaryCli"]);

        var marker = QuotaFallbackMarker.TryRead(claimedFolder);
        Assert.NotNull(marker);
        Assert.Equal(expectedCli, marker!.CliType);
        Assert.Equal(expectedModel, marker.Model);
        Assert.Equal(expectedThinking, marker.ThinkingLevel);
    }

    // ── Test B: review-claim (/api/v1/runners/{id}/review-claims) re-resolves ──
    [Theory]
    [InlineData(CliTypes.Codex, ModelIds.Gpt54Mini, "high", CliTypes.Claude, ModelIds.ClaudeSonnet5, "medium")]
    [InlineData(CliTypes.Claude, ModelIds.ClaudeSonnet5, "medium", CliTypes.Codex, ModelIds.Gpt54Mini, "high")]
    public async Task Review_claim_re_resolves_a_pinned_aspect_without_mutating_the_frozen_plan(
        string configuredCli,
        string configuredModel,
        string configuredThinking,
        string expectedCli,
        string expectedModel,
        string expectedThinking)
    {
        var taskKey = $"AGT-QUOTA-REVIEW-{configuredCli.ToUpperInvariant()}";
        const string reviewRunnerId = "quota-fallback-review-runner";
        var repositoryUrl = "https://example.invalid/quota-fallback-review.git";
        var repositoryId = Contract.RepositoryIdentityContract.FromUrl(repositoryUrl)!;
        const string resultSha = "abcabcabcabcabcabcabcabcabcabcabcabcabca";

        SeedTask(
            TaskStates.AutoReview, taskKey, "Review with a codex-pinned aspect",
            "Prompt body for the aspect under review.");

        SeedExhaustedQuota(configuredCli, 98, TimeSpan.FromHours(2));

        using var factory = BuildFactory();
        using var http = factory.CreateClient();

        var authority = factory.Services.GetRequiredService<AttemptAuthorityService>();
        var run = authority.AcquireRun(
            taskKey, repositoryId, null, RunnerId, "coding-host", 120,
            "quota-review-seed-run").RunAttempt!;
        var settled = authority.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(run.AttemptId, run.LastFence, run.AuthorityEpoch, "quota-review-seed-complete"),
            Outcome = "done",
            ResultSha = resultSha,
            ResultEnvelope = new Contract.ImmutableResultEnvelope(
                repositoryId,
                run.AttemptId,
                resultSha,
                resultSha,
                "refs/heads/main",
                null,
                new string('a', 64),
                RepositoryUrl: repositoryUrl),
        });
        Assert.True(settled.Accepted);

        // The immutable ReviewAttempt is minted with an agent-aspect command
        // already pinned to codex - the "already-open attempt" incident shape:
        // the plan was frozen before codex hit its cap.
        var frozenPlan = new Contract.ReviewPlanDto(
            Commands:
            [
                new Contract.ReviewCommandDto(
                    "aspect-code-quality", "code-quality", configuredCli, Array.Empty<string>(),
                    ExecutionKind: Contract.ReviewCommandKinds.AgentAspect,
                    CliType: configuredCli, Model: configuredModel, ThinkingLevel: configuredThinking),
            ],
            RequiredAspects: ["code-quality"]);
        var created = authority.CreateReviewAttempt(new CreateReviewAttemptRequest(
            taskKey, repositoryId, resultSha, run.AttemptId, "requirements", "policy",
            [], "quota-review-attempt-create",
            RepositoryUrl: repositoryUrl, ResultRef: "refs/heads/main", Plan: frozenPlan));
        Assert.True(created.Accepted);
        var attemptId = created.ReviewAttempt!.AttemptId;

        var options = new ROptions
        {
            ServerUrl = "http://in-process",
            RunnerId = reviewRunnerId,
            RunnerName = reviewRunnerId,
            Hostname = "review-host",
            BackendName = "remote-review",
            Role = "review",
            WorkDir = Path.Combine(_workspace, "coding-work-not-used"),
            ReviewWorkDir = Path.Combine(_workspace, "review-work"),
            StateDir = Path.Combine(_workspace, "review-state"),
            BaseBranch = "main",
            CliBin = "unused",
            CliArgs = string.Empty,
            CodexCliBin = "codex",
            TtlSeconds = 120,
            HeartbeatSeconds = 1,
            RunTimeoutSeconds = 30,
            HostMaxParallelism = 1,
            PollSeconds = 1,
        };
        var instanceId = "review-host:quota-fallback";
        using var client = new RClient(
            http, reviewRunnerId, usesDurableTaskServer: true, options: options, runnerInstanceId: instanceId);
        await client.EnsureCompatibleAsync(CancellationToken.None);
        await client.RegisterAsync("quota fallback review host", "review-executor", CancellationToken.None);

        var claim = await client.ClaimReviewAsync(
            new Contract.ReviewClaimRequest(reviewRunnerId, instanceId, 120, AvailableSlots: 1),
            CancellationToken.None);

        Assert.Equal("claimed", claim.Status);
        Assert.NotNull(claim.Subject);
        var returnedAspect = Assert.Single(
            claim.Subject!.Plan.Commands, c => Contract.ReviewCommandKinds.IsAgent(c.ExecutionKind));
        Assert.Equal(expectedCli, returnedAspect.CliType);
        Assert.Equal(expectedModel, returnedAspect.Model);
        Assert.Equal(expectedThinking, returnedAspect.ThinkingLevel);
        Assert.Equal(expectedCli, returnedAspect.FileName);

        // The durable, persisted ReviewAttempt plan is untouched: a second,
        // independent read of the frozen source-of-truth still shows codex.
        var storedAfterClaim = authority.GetReview(attemptId)!;
        var storedAspect = Assert.Single(
            storedAfterClaim.Subject.Plan!.Commands, c => Contract.ReviewCommandKinds.IsAgent(c.ExecutionKind));
        Assert.Equal(configuredCli, storedAspect.CliType);
        Assert.Equal(configuredModel, storedAspect.Model);
        Assert.Equal(configuredThinking, storedAspect.ThinkingLevel);

        var taskFolder = Path.Combine(_watchPath, TaskStates.AutoReview, taskKey);
        var marker = QuotaFallbackMarker.TryRead(taskFolder);
        Assert.NotNull(marker);
        Assert.Equal(expectedCli, marker!.CliType);
        Assert.Equal(expectedModel, marker.Model);
        Assert.Equal(expectedThinking, marker.ThinkingLevel);
    }

    // ── helpers, mirroring RemoteRunnerEndToEndTests' conventions ───────────────

    private static async Task<Runner::AgentRunner.RunnerClaimResponse> ClaimWithSuccessfulPreflightAsync(
        RClient client,
        RClaim request)
    {
        var offered = await client.ClaimAsync(request, CancellationToken.None);
        Assert.Equal(RClaimStatus.PreflightRequired, offered.Status);
        Assert.False(string.IsNullOrWhiteSpace(offered.ProjectId));
        Assert.False(string.IsNullOrWhiteSpace(offered.RepositoryUrl));
        Assert.False(string.IsNullOrWhiteSpace(offered.RegistrationFingerprint));

        return await client.ClaimAsync(request with
        {
            ProjectPreflight = new Runner::AgentRunner.RunnerProjectPreflightReport(
                offered.ProjectId!, offered.RegistrationFingerprint!, true,
                "clone/fetch URLs match registration; write probe succeeded",
                DateTime.UtcNow, offered.RepositoryUrl!, offered.RepositoryUrl!),
        }, CancellationToken.None);
    }

    private static async Task<string> RegisterCodingRunnerAsync(RClient client, HttpClient http)
    {
        var clientId = await client.RegisterAsync(ProjectName, "service", CancellationToken.None);
        var instanceId = $"{Environment.MachineName}:{Environment.ProcessId}";
        var registration = await http.PutAsJsonAsync(
            $"/api/v1/runners/{RunnerId}",
            new Contract.RegisterRunnerRequest(
                ProjectName, "test-host", instanceId, "1.0.0", Contract.TaskServerProtocol.Current,
                [Contract.ReviewCapabilities.CodingExecutor]));
        registration.EnsureSuccessStatusCode();

        var capabilities = new List<Contract.AdvertisedCapabilityDto>
        {
            new(Contract.CapabilityProtocol.CodingExecutor, "executor"),
            new(Contract.CapabilityProtocol.GitFetch, "source"),
            new(Contract.CapabilityProtocol.GitPush, "source"),
            new(Contract.CapabilityProtocol.RepositoryAccess, "source"),
            new(Contract.CapabilityProtocol.Disk, "foundation"),
            new(Contract.CapabilityProtocol.TaskServerConnectivity, "foundation"),
        };
        foreach (var cliType in new[] { CliTypes.Claude, CliTypes.Codex })
        {
            capabilities.Add(new Contract.AdvertisedCapabilityDto(
                Contract.CapabilityProtocol.CliExecution(cliType), "cli-execution", "ready"));
            capabilities.Add(new Contract.AdvertisedCapabilityDto(
                Contract.CapabilityProtocol.ProviderAuthentication(cliType), "provider-auth", "ready"));
        }
        var advertised = await http.PutAsJsonAsync(
            $"/api/v1/runners/{RunnerId}/capabilities",
            new Contract.CapabilityAdvertisementRequest(
                RunnerId, instanceId, Contract.CapabilityProtocol.CurrentSchemaVersion,
                DateTime.UtcNow, 180, DateTime.UtcNow.Ticks, capabilities));
        advertised.EnsureSuccessStatusCode();
        return clientId;
    }

    private static async Task AssignRemoteAsync(HttpClient http)
    {
        var assignment = await http.PutAsJsonAsync(
            $"/api/projects/{ProjectName}/execution-runner",
            new { executionRunner = ProjectName, remoteExecutionEnabled = true });
        assignment.EnsureSuccessStatusCode();
    }

    private static async Task AddRepositoryUrlAsync(
        HttpClient http, string repositoryUrl, string projectId = "PROJ-001")
    {
        var response = await http.PostAsJsonAsync(
            $"/api/projects/{projectId}/urls", new { label = "repo", url = repositoryUrl });
        response.EnsureSuccessStatusCode();
    }

    private void SeedTask(
        string state, string key, string title, string promptBody,
        string? cliType = null, string? model = null, string? thinkingLevel = null)
    {
        var dir = Path.Combine(_watchPath, state, key);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "task.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            id = key,
            title,
            state,
            order = 1,
            agent = cliType ?? "claude",
            kind = TaskKinds.Task,
            cliType,
            model,
            thinkingLevel,
        }));
        File.WriteAllText(Path.Combine(dir, "prompt.md"), promptBody);
        File.WriteAllText(Path.Combine(dir, "status.md"), "Result: pending.");
    }

    private void SeedOrchestratorSessions()
    {
        var projectSession = Path.Combine(
            _watchPath, ".orchestrator", "orchestrator-session.json");
        var globalSession = Path.Combine(
            _workspace, ".runtime", "global-orchestrator-session.json");
        Directory.CreateDirectory(Path.GetDirectoryName(projectSession)!);
        Directory.CreateDirectory(Path.GetDirectoryName(globalSession)!);
        const string session = "{\"sessionId\":\"quota-test-session\",\"model\":\"test\"}";
        File.WriteAllText(projectSession, session);
        File.WriteAllText(globalSession, session);
    }

    private WebApplicationFactory<Program> BuildFactory() =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Test");
                b.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["TaskRepository"] = _workspace,
                        ["WatchPaths:0:Name"] = ProjectName,
                        ["WatchPaths:0:Path"] = _watchPath,
                        ["WatchPaths:0:RootPath"] = _watchPath,
                        ["WatchPaths:0:RepositoryPath"] = _watchPath,
                        ["ReviewDecisionOrchestrator:Enabled"] = "false",
                    });
                });
            });
}
