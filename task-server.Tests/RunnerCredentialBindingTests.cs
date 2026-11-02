using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TaskServer.Contracts.GitHubApp;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

/// <summary>
/// SM-9: a runner credential is bound to its host, its role and the projects of
/// its host. Each test drives one route family with a real authenticated runner.
/// </summary>
public sealed class RunnerCredentialBindingTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
    private const string ResultSha = "589c462f589c462f589c462f589c462f589c462f";
    private const string RepositoryId = "repo_sm9binding0001";

    [Fact]
    public async Task A_new_runner_credential_expires_after_thirty_days_and_names_its_end_on_every_answer()
    {
        using var temp = new TempDirectory();
        await using var server = new BoundServer(temp.Path);
        var created = await server.CreateRunnerAsync("coder", role: null);
        var enrolled = await server.EnrolRunnerAsync("reviewer", RunnerCredentialRoles.Review);

        Assert.Equal(Start.UtcDateTime.AddDays(30), created.ExpiresAt);
        Assert.Equal(Start.UtcDateTime.AddDays(30), enrolled.ExpiresAt);
        var authenticated = await server.Store.AuthenticatePrincipalAsync(enrolled.Credential!, default);
        Assert.Equal(Start.UtcDateTime.AddDays(30), authenticated!.CredentialExpiresAt);

        using var runner = server.Client(created.Credential);
        var response = await runner.GetAsync("/api/v1/artifact-limits");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Start.UtcDateTime.AddDays(30),
            DateTime.Parse(response.Headers.GetValues(RunnerRouteAuthorization.CredentialExpiresHeader).Single(),
                null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal));
    }

    [Fact]
    public async Task An_existing_runner_credential_keeps_working_without_expiry_and_gets_a_migration_notice()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(Start);
        var store = new TaskServerStore(Options.Create(new TaskServerOptions { DataDirectory = temp.Path }), clock);
        await store.InitializeAsync();
        var credential = "legacy-" + RandomNumberGenerator.GetHexString(48).ToLowerInvariant();
        await store.EnsureBootstrapPrincipalAsync("runner:legacy", TaskServerPrincipalKinds.Runner, credential, "legacy", default);
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Workspace"), "test", default);
        await store.CreateProjectAsync(new CreateProjectRequest(workspace.WorkspaceId, "Alpha", "ALP"), "test", default);
        await store.RegisterRunnerAsync("legacy", Coding("legacy", "legacy-host"), "test", default);
        // An installation from before SM-9: no binding migration has run yet.
        await using (var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM meta WHERE key = 'sm9_runner_binding_migration';";
            await command.ExecuteNonQueryAsync();
        }

        var upgraded = new TaskServerStore(Options.Create(new TaskServerOptions { DataDirectory = temp.Path }), clock);
        await upgraded.InitializeAsync();
        clock.Advance(TimeSpan.FromDays(400));

        var principal = await upgraded.AuthenticatePrincipalAsync(credential, default);
        Assert.NotNull(principal);
        Assert.Null(principal!.CredentialExpiresAt);
        var audit = await upgraded.ListAuditAsync(0, default);
        var notice = Assert.Single(audit, record => record.Action == "principal.expiry-migration-notice");
        Assert.Contains(principal.CredentialId!, notice.DetailJson, StringComparison.Ordinal);
        // The host that ran before keeps its projects, now as an explicit policy an Owner can narrow.
        var policy = await upgraded.GetHostProjectPolicyAsync("legacy-host", default);
        Assert.True(policy!.AllowAllProjects);
        Assert.Contains(audit, record => record.Action == "host-project-policy.migrated" && record.TargetId == "legacy-host");
        var inventory = await upgraded.GetCredentialInventoryAsync(StudioUserRoles.Owner, default);
        Assert.Contains(inventory.Items, item => item.Detail == TaskServerStore.RunnerCredentialWithoutExpiryNotice);

        // The migration runs once: a restart adds no second notice.
        var restarted = new TaskServerStore(Options.Create(new TaskServerOptions { DataDirectory = temp.Path }), clock);
        await restarted.InitializeAsync();
        Assert.Single(await restarted.ListAuditAsync(0, default), record => record.Action == "principal.expiry-migration-notice");
    }

    [Fact]
    public async Task A_host_without_a_project_policy_claims_nothing()
    {
        using var temp = new TempDirectory();
        await using var server = new BoundServer(temp.Path);
        var (alpha, _) = await server.SeedProjectsAsync();
        await server.CreateTaskAsync(alpha.ProjectId, "Alpha card");
        var coder = await server.CreateRunnerAsync("coder", RunnerCredentialRoles.Coding);
        using var runner = server.Client(coder.Credential);
        await RegisterAsync(runner, Coding("coder", "unbound-host"));

        var claim = await ClaimAsync(runner, "coder");

        Assert.Equal("empty", claim.Status);
        Assert.Equal(TaskServerStore.HostProjectPolicyMissingReason, claim.PlacementReason);
    }

    [Fact]
    public async Task A_coding_claim_stays_inside_the_projects_of_the_bound_host()
    {
        using var temp = new TempDirectory();
        await using var server = new BoundServer(temp.Path);
        var (alpha, beta) = await server.SeedProjectsAsync();
        await server.CreateTaskAsync(beta.ProjectId, "Beta card");
        var alphaTask = await server.CreateTaskAsync(alpha.ProjectId, "Alpha card");
        await server.SetPolicyAsync("host-a", alpha.ProjectId);
        var coder = await server.CreateRunnerAsync("coder", RunnerCredentialRoles.Coding);
        using var runner = server.Client(coder.Credential);
        await RegisterAsync(runner, Coding("coder", "host-a"));

        var claim = await ClaimAsync(runner, "coder");
        var second = await ClaimAsync(runner, "coder");

        Assert.Equal(alphaTask.TaskId, claim.Task!.TaskId);
        Assert.Equal("empty", second.Status);
    }

    [Fact]
    public async Task A_copied_credential_cannot_register_from_another_host()
    {
        using var temp = new TempDirectory();
        await using var server = new BoundServer(temp.Path);
        var (alpha, _) = await server.SeedProjectsAsync();
        await server.SetPolicyAsync("host-a", alpha.ProjectId);
        await server.SetPolicyAsync("host-b", alpha.ProjectId);
        var coder = await server.CreateRunnerAsync("coder", RunnerCredentialRoles.Coding);
        using var runner = server.Client(coder.Credential);
        await RegisterAsync(runner, Coding("coder", "host-a"));

        var moved = await runner.PutAsJsonAsync("/api/v1/runners/coder", Coding("coder", "host-b"));

        await AssertRefusedAsync(moved, "runner-host-mismatch");
        Assert.Contains(await server.Store.ListAuditAsync(0, default),
            record => record.Action == "principal.host-bound" && record.DetailJson.Contains("host-a", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_runner_reads_a_card_only_while_it_holds_the_lease()
    {
        using var temp = new TempDirectory();
        await using var server = new BoundServer(temp.Path);
        var (alpha, beta) = await server.SeedProjectsAsync();
        var leased = await server.CreateTaskAsync(alpha.ProjectId, "Alpha card");
        var waiting = await server.CreateTaskAsync(alpha.ProjectId, "Second alpha card", state: "1-backlog");
        var foreign = await server.CreateTaskAsync(beta.ProjectId, "Beta card");
        await server.SetPolicyAsync("host-a", alpha.ProjectId);
        var coder = await server.CreateRunnerAsync("coder", RunnerCredentialRoles.Coding);
        using var runner = server.Client(coder.Credential);
        await RegisterAsync(runner, Coding("coder", "host-a"));
        var claim = await ClaimAsync(runner, "coder");
        Assert.Equal(leased.TaskId, claim.Task!.TaskId);

        var own = await runner.GetFromJsonAsync<TaskDto>(CardPath(leased));
        Assert.Equal("Alpha card", own!.Title);
        foreach (var path in new[]
                 {
                     CardPath(waiting), CardPath(foreign), CardPath(foreign) + "/attempts", CardPath(foreign) + "/history",
                     $"/api/v1/runner/tasks/{foreign.TaskKey}/attachments/crop.png",
                 })
            await AssertRefusedAsync(await runner.GetAsync(path), "runner-card-not-leased");
        foreach (var path in new[]
                 {
                     $"/api/v1/projects/{alpha.ProjectId}/tasks", "/api/v1/workspaces", "/api/v1/runners",
                     $"/api/v1/projects/{alpha.ProjectId}/board", "/api/v1/studio/auth/users",
                 })
            await AssertRefusedAsync(await runner.GetAsync(path), "runner-route-denied");
        await AssertRefusedAsync(await runner.GetAsync($"/api/v1/projects/{beta.ProjectId}/repository"), "runner-project-not-bound");
        var projects = await runner.GetFromJsonAsync<ProjectDto[]>("/api/v1/projects");
        Assert.Equal([alpha.ProjectId], projects!.Select(project => project.ProjectId));
    }

    [Fact]
    public async Task A_finished_card_shows_its_former_runner_only_its_lifecycle()
    {
        using var temp = new TempDirectory();
        await using var server = new BoundServer(temp.Path);
        var (alpha, _) = await server.SeedProjectsAsync();
        var task = await server.CreateTaskAsync(alpha.ProjectId, "Alpha card", body: "secret brief");
        await server.SetPolicyAsync("host-a", alpha.ProjectId);
        var coder = await server.CreateRunnerAsync("coder", RunnerCredentialRoles.Coding);
        using var runner = server.Client(coder.Credential);
        await RegisterAsync(runner, Coding("coder", "host-a"));
        var claim = await ClaimAsync(runner, "coder");
        var released = await runner.PostAsJsonAsync($"/api/v1/runs/{claim.Run!.RunId}/lease/release",
            new LeaseReleaseRequest("coder", "coder:1", claim.Lease!.LeaseId, claim.Lease.Fence, "failed"));
        released.EnsureSuccessStatusCode();

        var lifecycle = await runner.GetFromJsonAsync<TaskDto>(CardPath(task));

        Assert.Equal(task.TaskKey, lifecycle!.TaskKey);
        Assert.Equal(string.Empty, lifecycle.Title);
        Assert.Null(lifecycle.Body);
        await AssertRefusedAsync(await runner.GetAsync(CardPath(task) + "/history"), "runner-card-not-leased");
    }

    [Fact]
    public async Task Run_reads_and_reports_are_fenced_to_the_claimant()
    {
        using var temp = new TempDirectory();
        await using var server = new BoundServer(temp.Path);
        var (alpha, _) = await server.SeedProjectsAsync();
        await server.CreateTaskAsync(alpha.ProjectId, "Alpha card");
        await server.SetPolicyAsync("host-a", alpha.ProjectId);
        await server.SetPolicyAsync("host-b", alpha.ProjectId);
        var coder = await server.CreateRunnerAsync("coder", RunnerCredentialRoles.Coding);
        var other = await server.CreateRunnerAsync("other", RunnerCredentialRoles.Coding);
        using var claimant = server.Client(coder.Credential);
        using var intruder = server.Client(other.Credential);
        await RegisterAsync(claimant, Coding("coder", "host-a"));
        await RegisterAsync(intruder, Coding("other", "host-b"));
        var claim = await ClaimAsync(claimant, "coder");
        var run = $"/api/v1/runs/{claim.Run!.RunId}";

        await AssertRefusedAsync(await intruder.GetAsync(run + "/events"), "runner-not-claimant");
        await AssertRefusedAsync(await intruder.GetAsync(run + "/artifacts"), "runner-not-claimant");
        await AssertRefusedAsync(await intruder.PostAsJsonAsync(run + "/lease/renew",
            new LeaseRenewRequest("coder", "coder:1", claim.Lease!.LeaseId, claim.Lease.Fence)), "runner-not-claimant");
        await AssertRefusedAsync(await intruder.PostAsJsonAsync(run + "/events",
            new { runnerId = "coder", leaseId = claim.Lease.LeaseId, fence = claim.Lease.Fence }), "runner-not-claimant");
        await AssertRefusedAsync(await intruder.PostAsJsonAsync(run + "/completion",
            new { runnerId = "coder", leaseId = claim.Lease.LeaseId, fence = claim.Lease.Fence }), "runner-not-claimant");
        Assert.Equal(HttpStatusCode.OK, (await claimant.GetAsync(run + "/events")).StatusCode);
    }

    [Fact]
    public async Task A_review_claim_stays_inside_the_bound_projects_and_reports_stay_with_the_claimant()
    {
        using var temp = new TempDirectory();
        await using var server = new BoundServer(temp.Path);
        var (alpha, beta) = await server.SeedProjectsAsync();
        await server.SetPolicyAsync("coding-host", alpha.ProjectId, beta.ProjectId);
        var alphaSubject = await server.SeedReviewSubjectAsync(alpha.ProjectId, "Alpha review");
        var betaSubject = await server.SeedReviewSubjectAsync(beta.ProjectId, "Beta review");
        await server.SetPolicyAsync("review-host-b", beta.ProjectId);
        await server.SetPolicyAsync("review-host-a", alpha.ProjectId);
        var betaReviewer = await server.EnrolRunnerAsync("reviewer-b", RunnerCredentialRoles.Review);
        var alphaReviewer = await server.EnrolRunnerAsync("reviewer-a", RunnerCredentialRoles.Review);
        using var betaClient = server.Client(betaReviewer.Credential);
        using var alphaClient = server.Client(alphaReviewer.Credential);
        await RegisterAsync(betaClient, Reviewer("reviewer-b", "review-host-b"));
        await RegisterAsync(alphaClient, Reviewer("reviewer-a", "review-host-a"));

        var betaClaim = await ReviewClaimAsync(betaClient, "reviewer-b");
        var nothingElse = await ReviewClaimAsync(betaClient, "reviewer-b");
        var alphaClaim = await ReviewClaimAsync(alphaClient, "reviewer-a");

        Assert.Equal(betaSubject.SubjectId, betaClaim.Subject!.SubjectId);
        Assert.Equal("empty", nothingElse.Status);
        Assert.Equal(alphaSubject.SubjectId, alphaClaim.Subject!.SubjectId);
        var attempt = $"/api/v1/reviews/attempts/{alphaClaim.Attempt!.AttemptId}";
        var lease = alphaClaim.Lease!;
        await AssertRefusedAsync(await betaClient.GetAsync(attempt), "runner-not-claimant");
        await AssertRefusedAsync(await betaClient.GetAsync($"/api/v1/reviews/subjects/{alphaSubject.SubjectId}"), "runner-not-claimant");
        await AssertRefusedAsync(await betaClient.PostAsJsonAsync(attempt + "/lease/renew",
            new ReviewLeaseRenewRequest(lease.ExecutorId, lease.InstanceId, lease.LeaseId, lease.Fence, "renew-1")), "runner-not-claimant");
        await AssertRefusedAsync(await betaClient.PostAsJsonAsync(attempt + "/report",
            new { executorId = lease.ExecutorId, instanceId = lease.InstanceId, leaseId = lease.LeaseId, fence = lease.Fence }),
            "runner-not-claimant");
        await AssertRefusedAsync(await betaClient.PostAsJsonAsync(attempt + "/cleanup",
            new { executorId = lease.ExecutorId, instanceId = lease.InstanceId, leaseId = lease.LeaseId, fence = lease.Fence }),
            "runner-not-claimant");
        Assert.Equal(HttpStatusCode.OK, (await alphaClient.GetAsync(attempt)).StatusCode);
        // The reviewer reads the coding run it reviews, and no other.
        Assert.Equal(HttpStatusCode.OK,
            (await alphaClient.GetAsync($"/api/v1/runs/{alphaSubject.SourceRunId}/artifacts")).StatusCode);
        await AssertRefusedAsync(await alphaClient.GetAsync($"/api/v1/runs/{betaSubject.SourceRunId}/artifacts"),
            "runner-not-claimant");
    }

    [Fact]
    public async Task A_credential_bound_to_one_role_cannot_claim_the_other_role()
    {
        using var temp = new TempDirectory();
        await using var server = new BoundServer(temp.Path);
        var (alpha, _) = await server.SeedProjectsAsync();
        await server.SetPolicyAsync("host-a", alpha.ProjectId);
        var coder = await server.EnrolRunnerAsync("coder", RunnerCredentialRoles.Coding);
        var reviewer = await server.EnrolRunnerAsync("reviewer", RunnerCredentialRoles.Review);
        using var coding = server.Client(coder.Credential);
        using var review = server.Client(reviewer.Credential);
        await RegisterAsync(coding, Coding("coder", "host-a"));
        await RegisterAsync(review, Reviewer("reviewer", "host-a"));

        await AssertRefusedAsync(await coding.PostAsJsonAsync("/api/v1/runners/coder/review-claims",
            new ReviewClaimRequest("coder", "coder:1")), "runner-role-mismatch");
        await AssertRefusedAsync(await review.PostAsJsonAsync("/api/v1/runners/reviewer/claims",
            new ClaimRequest("reviewer", "reviewer:1")), "runner-role-mismatch");
    }

    [Fact]
    public async Task Repository_tokens_cover_only_the_repositories_of_the_bound_projects()
    {
        using var temp = new TempDirectory();
        var github = new FakeGitHub();
        await using var server = new BoundServer(temp.Path, github);
        var workspace = await server.Store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Workspace"), "test", default);
        var alpha = await server.RegisterRepositoryProjectAsync(workspace.WorkspaceId, "prj_alpha", "Alpha", "ALP", "https://github.com/owner/alpha");
        await server.RegisterRepositoryProjectAsync(workspace.WorkspaceId, "prj_beta", "Beta", "BET", "https://github.com/owner/beta");
        await server.SetPolicyAsync("host-a", alpha);
        var coder = await server.EnrolRunnerAsync("coder", RunnerCredentialRoles.Coding);
        var unbound = await server.EnrolRunnerAsync("idle", RunnerCredentialRoles.Coding);
        using var runner = server.Client(coder.Credential);
        using var idle = server.Client(unbound.Credential);
        await RegisterAsync(runner, Coding("coder", "host-a"));
        await RegisterAsync(idle, Coding("idle", "host-idle"));

        var issued = await runner.PostAsJsonAsync("/api/v1/runners/coder/repository-credentials",
            new RepositoryCredentialsRequest(RepositoryAccess.Write));
        var none = await idle.PostAsJsonAsync("/api/v1/runners/idle/repository-credentials",
            new RepositoryCredentialsRequest(RepositoryAccess.Read));

        issued.EnsureSuccessStatusCode();
        var credentials = (await issued.Content.ReadFromJsonAsync<RepositoryCredentialsResponse>())!.Credentials;
        Assert.Equal(["https://github.com/owner/alpha.git"], credentials.Select(item => item.RepositoryUrl));
        Assert.Equal(["alpha"], github.TokenRepositories);
        Assert.Equal(HttpStatusCode.NoContent, none.StatusCode);
    }

    private static string CardPath(TaskDto task) => $"/api/v1/projects/{task.ProjectId}/tasks/{task.TaskKey}";

    private static RegisterRunnerRequest Coding(string id, string host)
        => new(id, host, id + ":1", "1.0.0", TaskServerProtocol.Current, [ReviewCapabilities.CodingExecutor]);

    private static RegisterRunnerRequest Reviewer(string id, string host)
        => new(id, host, id + ":1", "1.0.0", TaskServerProtocol.Current,
        [
            ReviewCapabilities.ReviewExecutor,
            ReviewCapabilities.GitMaterialization,
            ReviewCapabilities.SemanticReview,
            ReviewCapabilities.VisionReview,
            ReviewCapabilities.BaselineComparison,
            ReviewCapabilities.DependencyPreparation,
            ReviewCapabilities.LibraryStepV1,
            CapabilityProtocol.CliExecution("codex"),
            CapabilityProtocol.ProviderAuthentication("codex"),
        ]);

    private static async Task RegisterAsync(HttpClient client, RegisterRunnerRequest request)
    {
        var response = await client.PutAsJsonAsync($"/api/v1/runners/{request.Name}", request);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<ClaimResponse> ClaimAsync(HttpClient client, string runnerId)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/runners/{runnerId}/claims",
            new ClaimRequest(runnerId, runnerId + ":1"));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ClaimResponse>())!;
    }

    private static async Task<ReviewClaimResponse> ReviewClaimAsync(HttpClient client, string runnerId)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/runners/{runnerId}/review-claims",
            new ReviewClaimRequest(runnerId, runnerId + ":1"));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ReviewClaimResponse>())!;
    }

    private static async Task AssertRefusedAsync(HttpResponseMessage response, string code)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Forbidden,
            $"{response.RequestMessage!.Method} {response.RequestMessage.RequestUri!.PathAndQuery} answered {(int)response.StatusCode}: {body}");
        Assert.Equal(code, JsonNode.Parse(body)!["code"]!.GetValue<string>());
    }

    /// <summary>An authenticated Task Server whose secrets are generated per test.</summary>
    private sealed class BoundServer : IAsyncDisposable
    {
        private readonly Factory _factory;
        private readonly HttpClient _manager;

        public BoundServer(string dataDirectory, FakeGitHub? github = null)
        {
            StudioToken = "studio-" + RandomNumberGenerator.GetHexString(48).ToLowerInvariant();
            _factory = new Factory(dataDirectory, StudioToken, "engine-" + RandomNumberGenerator.GetHexString(48).ToLowerInvariant(),
                Clock, github);
            _manager = Client(StudioToken);
            Store = _factory.Services.GetRequiredService<TaskServerStore>();
        }

        public ManualTimeProvider Clock { get; } = new(Start);
        public string StudioToken { get; }
        public TaskServerStore Store { get; }

        public HttpClient Client(string? credential)
        {
            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Add(TaskServerProtocol.HeaderName, TaskServerProtocol.Current.ToString());
            if (credential is not null)
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            return client;
        }

        public async Task<IssuedPrincipalCredential> CreateRunnerAsync(string runnerId, string? role)
        {
            if (role is not null) return await EnrolRunnerAsync(runnerId, role);
            var response = await _manager.PostAsJsonAsync("/api/v1/management/principals",
                new CreatePrincipalRequest("runner:" + runnerId, TaskServerPrincipalKinds.Runner, null, runnerId));
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<IssuedPrincipalCredential>())!;
        }

        public async Task<IssuedPrincipalCredential> EnrolRunnerAsync(string runnerId, string role)
        {
            // The Owner's enrolment route needs a Studio session; the code it returns is the same.
            var enrolment = await Store.CreateEnrolmentAsync(
                new CreateEnrolmentRequest("runner:" + runnerId, TaskServerPrincipalKinds.Runner, runnerId,
                    RepositoryWrite: role == RunnerCredentialRoles.Coding, Role: role), "owner", default);
            using var anonymous = Client(null);
            var exchanged = await anonymous.PostAsJsonAsync("/api/v1/enrolments/exchange",
                new ExchangeEnrolmentRequest(enrolment.EnrolmentCode, enrolment.InstallationId));
            exchanged.EnsureSuccessStatusCode();
            return (await exchanged.Content.ReadFromJsonAsync<ExchangedEnrolment>())!.Issued;
        }

        public async Task<(ProjectDto Alpha, ProjectDto Beta)> SeedProjectsAsync()
        {
            var workspace = await Store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Workspace"), "test", default);
            var alpha = await Store.CreateProjectAsync(new CreateProjectRequest(workspace.WorkspaceId, "Alpha", "ALP"), "test", default);
            var beta = await Store.CreateProjectAsync(new CreateProjectRequest(workspace.WorkspaceId, "Beta", "BET"), "test", default);
            return (alpha, beta);
        }

        public async Task<string> RegisterRepositoryProjectAsync(
            string workspaceId, string projectId, string name, string prefix, string repositoryUrl)
            => (await Store.RegisterProjectRepositoryAsync(new RegisterProjectRepositoryRequest(
                projectId, workspaceId, name, prefix, repositoryUrl, "main"), "test", default)).Registration.ProjectId;

        public async Task<TaskDto> CreateTaskAsync(string projectId, string title, string state = "2-ready", string body = "Do the work")
        {
            var task = await Store.CreateTaskAsync(projectId, new CreateTaskRequest(title, body, state), "test", default);
            Clock.Advance(TimeSpan.FromSeconds(1));
            return task;
        }

        public async Task SetPolicyAsync(string hostId, params string[] projectIds)
        {
            var current = await Store.GetHostProjectPolicyAsync(hostId, default);
            await Store.UpdateHostProjectPolicyAsync(hostId,
                new UpdateHostProjectPolicyRequest(false, projectIds, current?.Version ?? 0), "owner", default);
        }

        /// <summary>A finished coding run of a new card in <paramref name="projectId"/>, queued for review.</summary>
        public async Task<ReviewSubjectDto> SeedReviewSubjectAsync(string projectId, string title)
        {
            var task = await CreateTaskAsync(projectId, title);
            var codingId = "coding-" + task.TaskId;
            var instance = codingId + ":1";
            await Store.RegisterRunnerAsync(codingId, Coding(codingId, "coding-host"), "test", default);
            var coding = await Store.ClaimAsync(new ClaimRequest(codingId, instance), codingId, default);
            Assert.Equal(task.TaskId, coding.Task!.TaskId);
            var resultRef = FencedGitRefs.ImmutableResult(coding.Run!.RunId, coding.Lease!.Fence, ResultSha);
            var envelope = new ImmutableResultEnvelope(RepositoryId, coding.Run.RunId, new string('1', 40), ResultSha,
                resultRef, null, new string('2', 64), RepositoryUrl: "https://example.invalid/product.git");
            var digest = ResultEnvelopeDigest.Compute(envelope);
            await Store.AcknowledgeResultHandoffAsync(coding.Run.RunId,
                new ResultHandoffRequest(codingId, instance, coding.Lease.LeaseId, coding.Lease.Fence, 1,
                    $"handoff:{coding.Run.RunId}:{digest}", digest, envelope), codingId, default);
            await Store.CompleteRunAsync(coding.Run.RunId,
                new CompleteRunRequest(codingId, instance, coding.Lease.LeaseId, coding.Lease.Fence, "success", "done",
                    digest, $"completion:{coding.Run.RunId}:{digest}", 2), codingId, default);
            string[] aspects = ["completion", "build-tests", "requirements", "code-quality", "documentation", "evidence", "artifacts", "visual"];
            var plan = new ReviewPlanDto(
                aspects.Select(aspect => new ReviewCommandDto($"step-{aspect}", aspect, "review-tool", [aspect])).ToArray(),
                aspects, RequiresVisualReview: true, RequireDifferentHostFailureDomain: false);
            var subject = await Store.CreateReviewSubjectAsync(new CreateReviewSubjectRequest(
                task.TaskId, coding.Run.RunId, RepositoryId, "https://example.invalid/product.git", ResultSha,
                resultRef, null, null, "coding-host", "policy-v1", plan, $"subject-{task.TaskId}"), "orchestrator", default);
            Clock.Advance(TimeSpan.FromSeconds(1));
            return subject;
        }

        public async ValueTask DisposeAsync()
        {
            _manager.Dispose();
            await _factory.DisposeAsync();
        }
    }

    private sealed class Factory(
        string dataDirectory, string studioToken, string engineToken, TimeProvider clock, FakeGitHub? github)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TaskServer:DataDirectory"] = dataDirectory,
                    ["TaskServer:ListenUrl"] = string.Empty,
                    ["TaskServer:RetentionSchedulerEnabled"] = "false",
                    ["AUTH"] = "bearer",
                    ["STUDIO_AUTH_TOKEN"] = studioToken,
                    ["ENGINE_AUTH_TOKEN"] = engineToken,
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(clock);
                if (github is null) return;
                services.RemoveAll<IGitHubAppVault>();
                services.AddSingleton<IGitHubAppVault>(github.Vault);
                services.RemoveAll<GitHubAppBroker>();
                services.AddSingleton(provider => new GitHubAppBroker(
                    new GitHubAppClient(new HttpClient(github), TimeProvider.System),
                    github.Vault,
                    provider.GetRequiredService<IRegisteredRepositoryCatalog>(),
                    TimeProvider.System));
            });
        }
    }

    /// <summary>Just enough of GitHub to mint per-repository installation tokens.</summary>
    private sealed class FakeGitHub : HttpMessageHandler
    {
        private const long InstallationId = 11;

        public FakeGitHub()
        {
            using var key = RSA.Create(2048);
            Vault = new MemoryVault(
                new GitHubAppConnection(7001, "agent-studio-test", "Agent Studio test", "client", "owner",
                    "https://github.com/apps/agent-studio-test", DateTime.UnixEpoch, "owner", ["contents"],
                    [new GitHubAppInstallation(InstallationId, "owner", "User", DateTime.UnixEpoch, "owner")]),
                new GitHubAppSecrets(key.ExportRSAPrivateKeyPem(), "secret-" + RandomNumberGenerator.GetHexString(32)));
        }

        public MemoryVault Vault { get; }
        public List<string> TokenRepositories { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path.EndsWith("/installation", StringComparison.Ordinal))
                return Json(new { id = InstallationId });
            if (request.Method == HttpMethod.Post && path == $"/app/installations/{InstallationId}/access_tokens")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var name = body.RootElement.GetProperty("repositories")[0].GetString()!;
                lock (TokenRepositories) TokenRepositories.Add(name);
                return Json(new
                {
                    token = "fake-" + RandomNumberGenerator.GetHexString(32).ToLowerInvariant(),
                    expires_at = DateTime.UtcNow.AddHours(1),
                    permissions = body.RootElement.GetProperty("permissions"),
                    repositories = new[] { new { name } },
                });
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(object value)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
            };
    }

    private sealed class MemoryVault(GitHubAppConnection connection, GitHubAppSecrets secrets) : IGitHubAppVault
    {
        public Task<GitHubAppConnection?> LoadConnectionAsync(CancellationToken ct) => Task.FromResult<GitHubAppConnection?>(connection);
        public Task SaveConnectionAsync(GitHubAppConnection value, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteConnectionAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<GitHubAppSecrets?> ReadSecretsAsync(CancellationToken ct) => Task.FromResult<GitHubAppSecrets?>(secrets);
        public Task WriteSecretsAsync(GitHubAppSecrets value, string actor, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteSecretsAsync(string actor, CancellationToken ct) => Task.CompletedTask;
    }
}
