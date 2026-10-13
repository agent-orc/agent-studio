extern alias Runner;

using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Xunit;

using Contract = AgentStudio.TaskServer.Contracts;
using RunnerOptions = Runner::AgentRunner.RunnerOptions;
using RunnerProbe = Runner::AgentRunner.RunnerCapabilityProbe;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3077: WEB-26/27/28 spent their whole infrastructure retry budget on a
/// clone that could never succeed, because the review host had no read access
/// to the private website repository. A private project now seals that fact
/// into the review plan, and the claim filter refuses the attempt up front on
/// every executor that holds no read credential for exactly that repository.
/// </summary>
[Collection(WebApplicationFactorySerialCollection.Name)]
public sealed class PrivateRepositoryReviewClaimTests : IDisposable
{
    private const string ProjectName = "agent-runner-01";
    private const string RunnerId = "review-runner-private";
    private const string Instance = "review-host:8485";
    private const string Website = "https://github.com/RobertMischke/agent-studio-for-software-website.git";
    private const string WebsiteKey =
        "review:repository-read:github.com/robertmischke/agent-studio-for-software-website";

    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(), "atp-private-review-" + Guid.NewGuid().ToString("N"));
    private readonly string _watchPath;

    public PrivateRepositoryReviewClaimTests()
    {
        _watchPath = Path.Combine(_workspace, "projects", ProjectName);
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("public", false)]
    [InlineData("private", true)]
    public void Plan_builder_seals_the_project_repository_visibility(string? visibility, bool privateRepository)
    {
        var config = new ConfigurationBuilder().Build();
        var aspects = new AspectRunnerService(
            new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance),
            NullLogger<AspectRunnerService>.Instance);

        var plan = new RemoteReviewPlanBuilder(aspects, config).Build(
            task: null, repositoryPath: null,
            new ProjectSettings { RepositoryVisibility = visibility }, "refs/heads/main");

        Assert.Equal(privateRepository, plan.PrivateRepository);
    }

    [Fact]
    public void Private_repository_is_refused_before_claim_and_named_with_its_read_key()
    {
        var authority = NewAuthority();
        CreatePendingReviewAttempt(authority, "WEB-28", new string('2', 40), Plan(privateRepository: true));
        var registered = Registration(credentialsFile: null);

        Assert.Equal(
            AttemptWriteStatus.NotFound,
            authority.ClaimNextReview(RunnerId, "review-host", Instance, 60, registered).Status);
        var unclaimable = Assert.Single(authority.ListUnclaimableReviews(registered));
        Assert.Equal("WEB-28", unclaimable.TaskKey);
        Assert.Equal([WebsiteKey], unclaimable.MissingCapabilities);
    }

    [Fact]
    public void Private_repository_is_claimable_by_an_executor_with_a_scoped_read_credential()
    {
        var authority = NewAuthority();
        CreatePendingReviewAttempt(authority, "WEB-28", new string('2', 40), Plan(privateRepository: true));
        var registered = Registration(WriteCredentials());

        Assert.Contains(WebsiteKey, registered);
        Assert.Empty(authority.ListUnclaimableReviews(registered));
        var claimed = authority.ClaimNextReview(RunnerId, "review-host", Instance, 60, registered);

        Assert.Equal(AttemptWriteStatus.Accepted, claimed.Status);
        Assert.Equal("WEB-28", claimed.ReviewAttempt!.TaskKey);
    }

    [Fact]
    public void Public_repository_stays_claimable_without_any_read_credential()
    {
        var authority = NewAuthority();
        CreatePendingReviewAttempt(authority, "AGT-1", new string('1', 40), Plan(privateRepository: false));
        var registered = Registration(credentialsFile: null);

        Assert.Empty(authority.ListUnclaimableReviews(registered));
        Assert.Equal(
            AttemptWriteStatus.Accepted,
            authority.ClaimNextReview(RunnerId, "review-host", Instance, 60, registered).Status);
    }

    [Fact]
    public async Task Claim_answers_the_typed_reason_and_names_the_missing_credential()
    {
        SeedTask("WEB-28");
        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        var registration = await http.PutAsJsonAsync(
            $"/api/v1/runners/{RunnerId}",
            new Contract.RegisterRunnerRequest(
                RunnerId, "review-host", Instance, "1.0.0", Contract.TaskServerProtocol.Current,
                [.. Registration(credentialsFile: null)]));
        registration.EnsureSuccessStatusCode();
        var authority = factory.Services.GetRequiredService<AttemptAuthorityService>();
        CreatePendingReviewAttempt(authority, "WEB-28", new string('2', 40), Plan(privateRepository: true));

        var response = await http.PostAsJsonAsync(
            $"/api/v1/runners/{RunnerId}/review-claims",
            new Contract.ReviewClaimRequest(RunnerId, Instance, 120, 1));
        response.EnsureSuccessStatusCode();
        var claim = (await response.Content.ReadFromJsonAsync<Contract.ReviewClaimResponse>())!;

        Assert.Equal("empty", claim.Status);
        Assert.Equal(Contract.ReviewClaimEmptyReasons.UnclaimablePlanRequirements, claim.Reason);
        Assert.Equal([WebsiteKey], claim.MissingCapabilities);
        Assert.Contains("RUNNER_REVIEW_GIT_CREDENTIALS_FILE", claim.Message!, StringComparison.Ordinal);
        // Refused, not consumed: the attempt stays pending for a credentialed host.
        Assert.Single(authority.ListPendingReviewAttempts());
    }

    [Fact]
    public async Task Repository_visibility_is_an_operator_project_setting()
    {
        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);

        var set = await http.PutAsJsonAsync(
            $"/api/projects/{ProjectName}/repository-visibility", new { visibility = "Private" });
        var invalid = await http.PutAsJsonAsync(
            $"/api/projects/{ProjectName}/repository-visibility", new { visibility = "internal" });

        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(
            Contract.ReviewRepositoryAccessPolicy.PrivateVisibility,
            factory.Services.GetRequiredService<AgentStudio.Projects.ProjectSettingsService>()
                .Get(ProjectName).RepositoryVisibility);
    }

    [Fact]
    public void Plan_less_private_attempt_is_skipped_without_a_lease_and_the_queue_moves_on()
    {
        // A lease-expiry successor or a settled-run recovery attempt carries no
        // plan; its visibility comes from the project at claim time. It used to
        // pass the queue filter, get leased and deferred, and hold the head of
        // the queue for every project on each poll.
        var now = new DateTime(2026, 10, 13, 9, 0, 0, DateTimeKind.Utc);
        var authority = NewAuthority(() => now);
        var website = CreatePendingReviewAttempt(authority, "WEB-28", new string('2', 40), plan: null);
        now = now.AddMinutes(1);
        CreatePendingReviewAttempt(authority, "AGT-1", new string('1', 40), Plan(privateRepository: false));
        var facts = new Dictionary<string, ReviewClaimRepositoryFact>(StringComparer.OrdinalIgnoreCase)
        {
            ["WEB-28"] = new(ProjectPrivate: true, RepositoryUrl: null),
            ["AGT-1"] = new(ProjectPrivate: false, RepositoryUrl: null),
        };
        var anonymous = Registration(credentialsFile: null);

        var claimed = authority.ClaimNextReview(
            RunnerId, "review-host", Instance, 60, anonymous, repositoryFacts: facts);
        var second = authority.ClaimNextReview(
            RunnerId, "review-host", Instance, 60, anonymous, repositoryFacts: facts);

        Assert.Equal(AttemptWriteStatus.Accepted, claimed.Status);
        Assert.Equal("AGT-1", claimed.ReviewAttempt!.TaskKey);
        Assert.Equal(AttemptWriteStatus.NotFound, second.Status);
        var skipped = authority.GetReview(website)!;
        Assert.Equal(AttemptLifecycleState.Pending, skipped.State);
        Assert.Null(skipped.Lease);
        Assert.Equal(0, skipped.LastFence);
        var unclaimable = Assert.Single(authority.ListUnclaimableReviews(anonymous, facts));
        Assert.Equal(website, unclaimable.AttemptId);
        Assert.Equal([WebsiteKey], unclaimable.MissingCapabilities);
    }

    [Fact]
    public void Plan_less_private_attempt_is_claimed_by_an_executor_with_the_read_key()
    {
        var now = new DateTime(2026, 10, 13, 9, 0, 0, DateTimeKind.Utc);
        var authority = NewAuthority(() => now);
        var website = CreatePendingReviewAttempt(authority, "WEB-28", new string('2', 40), plan: null);
        var facts = new Dictionary<string, ReviewClaimRepositoryFact>(StringComparer.OrdinalIgnoreCase)
        {
            ["WEB-28"] = new(ProjectPrivate: true, RepositoryUrl: null),
        };

        var claimed = authority.ClaimNextReview(
            RunnerId, "review-host", Instance, 60, Registration(WriteCredentials()), repositoryFacts: facts);

        Assert.Equal(AttemptWriteStatus.Accepted, claimed.Status);
        Assert.Equal(website, claimed.ReviewAttempt!.AttemptId);
    }

    [Theory]
    // sealed plan visibility, stored URL, project private, project URL, expected missing
    [InlineData(null, true, true, false, true)]
    [InlineData(null, false, true, true, true)]
    [InlineData(null, true, false, false, false)]
    [InlineData(null, false, true, false, false)]
    [InlineData(false, true, true, false, false)]
    [InlineData(true, true, false, false, true)]
    [InlineData(true, false, false, false, false)]
    public void Claim_time_access_follows_the_subject_the_executor_will_receive(
        bool? sealedPrivate, bool storedUrl, bool projectPrivate, bool projectUrl, bool missing)
    {
        var plan = sealedPrivate is { } value ? Plan(value) : null;
        var fact = new ReviewClaimRepositoryFact(projectPrivate, projectUrl ? Website : null);

        var result = ReviewClaimRepositoryAccessPolicy.MissingCapabilities(
            plan, storedUrl ? Website : null, fact, new HashSet<string>(StringComparer.Ordinal));

        Assert.Equal(missing ? [WebsiteKey] : Array.Empty<string>(), result);
        Assert.Empty(ReviewClaimRepositoryAccessPolicy.MissingCapabilities(
            plan, storedUrl ? Website : null, fact, new HashSet<string>(StringComparer.Ordinal) { WebsiteKey }));
    }

    [Fact]
    public async Task Plan_less_private_attempt_does_not_hold_the_claim_queue()
    {
        SeedTask("WEB-28");
        SeedTask("AGT-1");
        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        await RegisterAsync(http, Registration(credentialsFile: null));
        factory.Services.GetRequiredService<AgentStudio.Projects.ProjectSettingsService>()
            .SetRepositoryVisibility(ProjectName, Contract.ReviewRepositoryAccessPolicy.PrivateVisibility);
        var authority = factory.Services.GetRequiredService<AttemptAuthorityService>();
        var website = CreatePendingReviewAttempt(authority, "WEB-28", new string('2', 40), plan: null);
        CreatePendingReviewAttempt(authority, "AGT-1", new string('1', 40), Plan(privateRepository: false));

        var first = await ClaimAsync(http);
        var second = await ClaimAsync(http);

        // The older private attempt is skipped, not leased: the public review
        // behind it is handed out on the same poll.
        Assert.Equal("claimed", first.Status);
        Assert.Equal("AGT-1", first.Attempt!.TaskId);
        Assert.Equal("empty", second.Status);
        Assert.Equal(Contract.ReviewClaimEmptyReasons.UnclaimablePlanRequirements, second.Reason);
        Assert.Equal([WebsiteKey], second.MissingCapabilities);
        var skipped = authority.GetReview(website)!;
        Assert.Equal(AttemptLifecycleState.Pending, skipped.State);
        Assert.Equal(0, skipped.LastFence);
    }

    [Fact]
    public async Task Repository_visibility_change_replans_queued_review_attempts()
    {
        SeedTask("WEB-23");
        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);
        var authority = factory.Services.GetRequiredService<AttemptAuthorityService>();
        var queued = CreatePendingReviewAttempt(
            authority, "WEB-23", new string('3', 40), Plan(privateRepository: false));

        var flipped = await http.PutAsJsonAsync(
            $"/api/projects/{ProjectName}/repository-visibility", new { visibility = "private" });
        var replanned = authority.GetReview(queued)!.Subject.Plan!;
        var reverted = await http.PutAsJsonAsync(
            $"/api/projects/{ProjectName}/repository-visibility", new { visibility = "public" });

        Assert.Equal(HttpStatusCode.OK, flipped.StatusCode);
        Assert.True(replanned.PrivateRepository);
        Assert.Equal(HttpStatusCode.OK, reverted.StatusCode);
        Assert.False(authority.GetReview(queued)!.Subject.Plan!.PrivateRepository);
    }

    [Fact]
    public void Registered_read_key_is_admitted_only_with_a_fresh_ready_proof()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 13, 9, 0, 0, TimeSpan.Zero));
        var registry = new V1ReviewExecutorRegistry(clock);
        registry.Register(RunnerId, new Contract.RegisterRunnerRequest(
            RunnerId, "review-host", Instance, "1", Contract.TaskServerProtocol.Current,
            [Contract.ReviewCapabilities.ReviewExecutor, WebsiteKey]));
        void Advertise(string status, int generation) => registry.AdvertiseCapabilities(
            RunnerId,
            new Contract.CapabilityAdvertisementRequest(
                RunnerId, Instance, 2, clock.GetUtcNow().UtcDateTime, 180, generation,
                [
                    new Contract.AdvertisedCapabilityDto(Contract.CapabilityProtocol.ReviewExecutor, "executor"),
                    new Contract.AdvertisedCapabilityDto(WebsiteKey, "source", status),
                ]));

        var unadvertised = registry.AdmittedReviewCapabilities(RunnerId, Instance);
        Advertise("ready", 1);
        var proven = registry.AdmittedReviewCapabilities(RunnerId, Instance);
        clock.Advance(TimeSpan.FromMinutes(10));
        var stale = registry.AdmittedReviewCapabilities(RunnerId, Instance);
        Advertise("unavailable", 2);
        var revoked = registry.AdmittedReviewCapabilities(RunnerId, Instance);

        Assert.DoesNotContain(WebsiteKey, unadvertised);
        Assert.Contains(WebsiteKey, proven);
        Assert.DoesNotContain(WebsiteKey, stale);
        Assert.DoesNotContain(WebsiteKey, revoked);
        Assert.Contains(Contract.ReviewCapabilities.ReviewExecutor, revoked);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Coding_host_with_the_read_key_does_not_yield_a_private_review_to_a_keyless_host(
        bool privateRepository)
    {
        // AGT-3077 x AGT-3069: website cards are coded and reviewed on
        // agent-runner-01, the only host with the read key, while kori-review
        // idles without it. Yielding the private review to kori-review, whose
        // claim filter skips it, would leave it unreviewed. A public review
        // still yields, which proves the placement path is live here.
        SeedTask("WEB-28");
        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        await RegisterAsync(http, Registration(WriteCredentials()), host: "coding-host");
        await RegisterAsync(
            http, Registration(credentialsFile: null), runnerId: "kori-review", host: "kori-host",
            instance: "kori-host:8485");
        var clock = factory.Services.GetRequiredService<TimeProvider>();
        factory.Services.GetRequiredService<V1ReviewExecutorRegistry>().AdvertiseCapabilities(
            RunnerId,
            new Contract.CapabilityAdvertisementRequest(
                RunnerId, Instance, Contract.CapabilityProtocol.CurrentSchemaVersion,
                clock.GetUtcNow().UtcDateTime, 180, 1,
                [new Contract.AdvertisedCapabilityDto(WebsiteKey, "source", "ready")]));
        var authority = factory.Services.GetRequiredService<AttemptAuthorityService>();
        var website = CreatePendingReviewAttempt(
            authority, "WEB-28", new string('2', 40), Plan(privateRepository));

        var claim = await ClaimAsync(http);

        if (privateRepository)
        {
            Assert.Equal("claimed", claim.Status);
            Assert.Equal(website, claim.Attempt!.AttemptId);
            Assert.Equal("coding-host", claim.Attempt.HostId);
        }
        else
        {
            Assert.Equal("empty", claim.Status);
            Assert.Equal(Contract.ReviewClaimEmptyReasons.DifferentHostPreferred, claim.Reason);
        }
    }

    private static async Task RegisterAsync(
        HttpClient http,
        IReadOnlyCollection<string> capabilities,
        string runnerId = RunnerId,
        string host = "review-host",
        string instance = Instance)
    {
        var registration = await http.PutAsJsonAsync(
            $"/api/v1/runners/{runnerId}",
            new Contract.RegisterRunnerRequest(
                runnerId, host, instance, "1.0.0", Contract.TaskServerProtocol.Current,
                [.. capabilities]));
        registration.EnsureSuccessStatusCode();
    }

    private static async Task<Contract.ReviewClaimResponse> ClaimAsync(HttpClient http)
    {
        var response = await http.PostAsJsonAsync(
            $"/api/v1/runners/{RunnerId}/review-claims",
            new Contract.ReviewClaimRequest(RunnerId, Instance, 120, 1));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Contract.ReviewClaimResponse>())!;
    }

    private static Contract.ReviewPlanDto Plan(bool privateRepository)
        => new(
            [new Contract.ReviewCommandDto("verify-1", "build-tests", "git", ["status"], TimeoutSeconds: 120)],
            ["build-tests"],
            LibraryVersion: Contract.ReviewLibraryStepPolicy.Version,
            PrivateRepository: privateRepository);

    private HashSet<string> Registration(string? credentialsFile)
        => RunnerProbe.ReviewRegistrationCapabilities(new RunnerOptions
            {
                ServerUrl = "http://127.0.0.1:5031",
                RunnerId = RunnerId,
                RunnerName = RunnerId,
                Hostname = "review-host",
                BackendName = "test",
                Role = "review",
                WorkDir = Path.GetTempPath(),
                ReviewGitCredentialsFile = credentialsFile,
                BaseBranch = "main",
                ClaudeCliBin = "sh",
            }, onPath: _ => true, composeRenderVersion: () => null)
            .ToHashSet(StringComparer.Ordinal);

    private string WriteCredentials()
    {
        Directory.CreateDirectory(_workspace);
        var path = Path.Combine(_workspace, "review-git-credentials");
        // Built at runtime: no credential-shaped literal in source.
        var token = "fixture-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(path, $"https://x-access-token:{token}@github.com/RobertMischke/agent-studio-for-software-website.git\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return path;
    }

    private AttemptAuthorityService NewAuthority(Func<DateTime>? utcNow = null)
    {
        Directory.CreateDirectory(_workspace);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _workspace,
        }).Build();
        return new AttemptAuthorityService(config, NullLogger<AttemptAuthorityService>.Instance, utcNow);
    }

    private static string CreatePendingReviewAttempt(
        AttemptAuthorityService authority, string taskKey, string sha, Contract.ReviewPlanDto? plan,
        string? repositoryUrl = Website)
    {
        var run = authority.AcquireRun(
            taskKey, ProjectName, null, "coding-runner", "coding-host", 60, "acquire-" + taskKey).RunAttempt!;
        var envelope = new Contract.ImmutableResultEnvelope(
            run.RepositoryId, run.AttemptId, new string('0', 40), sha,
            "refs/agent-studio/results/" + run.AttemptId, null, new string('1', 64));
        authority.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(run.AttemptId, run.LastFence, run.AuthorityEpoch, "settle-" + taskKey),
            Outcome = "done",
            ResultSha = sha,
            ResultEnvelope = envelope,
            ResultEnvelopeDigest = Contract.ResultEnvelopeDigest.Compute(envelope),
        });
        var created = authority.CreateReviewAttempt(new CreateReviewAttemptRequest(
            taskKey, ProjectName, sha, run.AttemptId, "requirements-hash", "policy-hash", [],
            "review-create-" + taskKey, RepositoryUrl: repositoryUrl, Plan: plan));
        Assert.True(created.Accepted, created.Message);
        return created.ReviewAttempt!.AttemptId;
    }

    private void SeedTask(string taskKey)
    {
        var taskFolder = Path.Combine(_watchPath, TaskStates.AutoReview, taskKey);
        Directory.CreateDirectory(taskFolder);
        File.WriteAllText(
            Path.Combine(taskFolder, "task.json"),
            System.Text.Json.JsonSerializer.Serialize(new
            {
                id = taskKey,
                key = taskKey,
                title = "Private website review",
                state = TaskStates.AutoReview,
                order = 1,
                agent = "codex",
                kind = TaskKinds.Task,
            }));
        File.WriteAllText(Path.Combine(taskFolder, "prompt.md"), "Review the delivery.");
        File.WriteAllText(Path.Combine(taskFolder, "status.md"), "Result: pending.");
    }

    private WebApplicationFactory<Program> BuildFactory() =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["TaskRepository"] = _workspace,
                        ["WatchPaths:0:Name"] = ProjectName,
                        ["WatchPaths:0:Path"] = _watchPath,
                        ["WatchPaths:0:RootPath"] = _watchPath,
                        ["WatchPaths:0:RepositoryPath"] = _watchPath,
                        ["ReviewDecisionOrchestrator:Enabled"] = "false",
                    }));
            });
}
