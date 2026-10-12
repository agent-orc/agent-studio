extern alias Runner;

using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

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

    private AttemptAuthorityService NewAuthority()
    {
        Directory.CreateDirectory(_workspace);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _workspace,
        }).Build();
        return new AttemptAuthorityService(config, NullLogger<AttemptAuthorityService>.Instance);
    }

    private static void CreatePendingReviewAttempt(
        AttemptAuthorityService authority, string taskKey, string sha, Contract.ReviewPlanDto plan)
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
            "review-create-" + taskKey, RepositoryUrl: Website, Plan: plan));
        Assert.True(created.Accepted, created.Message);
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
