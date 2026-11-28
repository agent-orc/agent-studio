using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentStudio.Shared;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TaskServer.Contracts.GitHubApp;
using AgentStudio.TestSupport;
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
[Trait("ProcessReference", "AgentRunner")]
public sealed class RunnerCredentialBindingTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
    private const string ResultSha = "589c462f589c462f589c462f589c462f589c462f";
    private const string RepositoryId = "repo_sm9binding0001";

    [Fact]
    public async Task Only_the_owner_can_issue_a_gate_token_and_the_code_redeems_once_for_the_gate_role()
    {
        using var temp = new TempDirectory();
        await using var server = new BoundServer(temp.Path);
        const string path = "/api/v1/management/host-join-tokens";
        var request = new CreateHostJoinTokenRequest("https://tasks.example.test", "host-a", "gate");
        using var anonymous = server.Client(null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(path, request)).StatusCode);
        using var studio = server.Client(server.StudioToken);
        await AssertRefusedAsync(await studio.PostAsJsonAsync(path, request), "owner-role-required");
        studio.DefaultRequestHeaders.Add(CredentialViewProtocol.ActingRoleHeader, StudioUserRoles.Operator);
        await AssertRefusedAsync(await studio.PostAsJsonAsync(path, request), "owner-role-required");

        var (issued, exchange) = await server.IssueGateJoinTokenAsync("host-a");
        Assert.Equal("host-a-gate", issued.RunnerId);
        Assert.Equal(Start.UtcDateTime.AddMinutes(15), issued.ExpiresAt);
        var credential = await server.ExchangeJoinTokenAsync(exchange);
        Assert.Equal(issued.RunnerId, credential.Principal.RunnerId);
        Assert.Equal(RunnerCredentialRoles.Gate, credential.Principal.Role);
        Assert.DoesNotContain(TaskServerScopes.RepositoriesWrite, credential.Principal.Scopes);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/v1/enrolments/exchange", exchange)).StatusCode);

        using var gate = server.Client(credential.Credential);
        await RegisterGateAsync(server, gate, issued.RunnerId, "host-a");
        await AssertRefusedAsync(await gate.PostAsJsonAsync($"/api/v1/runners/{issued.RunnerId}/review-claims",
            new ReviewClaimRequest(issued.RunnerId, issued.RunnerId + ":1")), "runner-role-mismatch");
        await AssertRefusedAsync(await gate.PostAsJsonAsync($"/api/v1/runners/{issued.RunnerId}/claims",
            new ClaimRequest(issued.RunnerId, issued.RunnerId + ":1")), "runner-role-mismatch");

        var review = await server.EnrolRunnerAsync("review", RunnerCredentialRoles.Review);
        using var reviewer = server.Client(review.Credential);
        await RegisterGateAsync(server, reviewer, "review", "host-a");
        await AssertRefusedAsync(await reviewer.PostAsJsonAsync("/api/v1/gates/claims",
            new GateClaimRequest("review", "review:1")), "runner-role-mismatch");
    }

    [Fact]
    public async Task Supervised_gate_advertises_claims_and_drains_a_merge_verification()
    {
        if (!OperatingSystem.IsLinux()) return;

        using var temp = new TempDirectory();
        using var hostFiles = new TempDirectory();
        await using var server = new BoundServer(temp.Path, useKestrel: true);
        var (issued, exchange) = await server.IssueGateJoinTokenAsync("gate-host");
        var credential = await server.ExchangeJoinTokenAsync(exchange);
        using var gate = server.Client(credential.Credential);
        var (alpha, _) = await server.SeedProjectsAsync();
        await server.RegisterRepositoryProjectAsync(alpha.WorkspaceId, alpha.ProjectId, alpha.Name,
            "ALP", "https://example.invalid/alpha.git");
        const string hostname = "gate-host";
        await server.SetPolicyAsync(hostname, alpha.ProjectId);

        var root = Directory.CreateDirectory(hostFiles.Path).FullName;
        var tokenPath = Path.Combine(root, "gate.token");
        await using (var tokenFile = new FileStream(tokenPath, new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        }))
        await using (var writer = new StreamWriter(tokenFile))
            await writer.WriteLineAsync(credential.Credential);
        object Role(string role, string runnerId)
        {
            var environmentFile = Path.Combine(root, role + ".env");
            var homeDirectory = Directory.CreateDirectory(Path.Combine(root, role + "-home")).FullName;
            File.WriteAllLines(environmentFile,
            [
                $"RUNNER_SERVER_URL={gate.BaseAddress}",
                $"RUNNER_ID={runnerId}",
                $"RUNNER_NAME={runnerId}",
                $"RUNNER_HOSTNAME={hostname}",
                $"RUNNER_WORKDIR={Path.Combine(root, role + "-work")}",
                $"RUNNER_REVIEW_WORKDIR={Path.Combine(root, role + "-review-work")}",
                $"RUNNER_STATE_DIR={Path.Combine(root, role + "-state")}",
                "RUNNER_POLL_SECONDS=1",
                "RUNNER_DRAIN_TIMEOUT_SECONDS=30",
                .. role == "gate" ? new[] { $"RUNNER_AUTH_TOKEN_FILE={tokenPath}" } : [],
            ]);
            return new { environmentFile, homeDirectory };
        }
        var stateDirectory = Path.Combine(root, "supervisor");
        var config = Path.Combine(root, "host-agent.json");
        await File.WriteAllTextAsync(config, JsonSerializer.Serialize(new
        {
            stateDirectory, coding = Role("coding", "gate-host"), review = Role("review", "gate-host-review"),
            gate = Role("gate", issued.RunnerId),
        }));

        var projectDirectory = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
        await BuiltProcessLauncher.RunAsync("git", ["init", "--quiet", "--initial-branch=develop"], projectDirectory);
        await BuiltProcessLauncher.RunAsync("git",
            ["-c", "user.name=Fixture", "-c", "user.email=fixture@example.test", "commit", "--quiet", "--allow-empty", "-m", "Fixture"],
            projectDirectory);
        var shaFile = Path.Combine(root, "sha");
        await BuiltProcessLauncher.RunAsync("sh", ["-c", "git rev-parse HEAD > \"$1\"", "sh", shaFile], projectDirectory);
        var sha = (await File.ReadAllTextAsync(shaFile)).Trim();
        var bundlePath = Path.Combine(root, "source.bundle");
        await BuiltProcessLauncher.RunAsync("git", ["bundle", "create", bundlePath, "refs/heads/develop"], projectDirectory);
        await using var bundleFile = File.OpenRead(bundlePath);
        var digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(bundleFile));
        bundleFile.Position = 0;
        var bundle = await server.Store.PutGateBundleAsync(digest, bundleFile.Length, alpha.ProjectId, bundleFile, "test", default);

        var release = Path.Combine(root, "finish-gate");
        var plan = new GatePlan("post-build-test-gate", 1,
            [new("verify", "sh", ["-c", "while [ ! -f \"$1\" ]; do sleep 0.1; done", "sh", release], 180, "")],
            "", 180, [], 1024, "always");
        var subject = await server.Store.CreateGateSubjectAsync(new CreateGateSubjectRequest(
            "merge-card", "candidate-1", alpha.ProjectId, null, sha, null, null, null,
            GateRules.PlanHash(plan), "policy", "1", "selection", plan, server.UtcNow.AddMinutes(10),
            Kind: GateSubjectKinds.Merge, Bundle: bundle), "test", default);
        var repoRoot = ProtocolTests.RepositoryRoot();
        var childEnvironment = new Dictionary<string, string?>
        {
            ["XDG_CACHE_HOME"] = Path.Combine(root, "cache"),
            ["AGENT_STUDIO_LOCAL_STATE_ROOT"] = Path.Combine(root, "local-state"),
        };
        ManagedProcess Start(string operation, params string[] role)
            => BuiltProcessLauncher.StartBuiltIn(repoRoot, root, "runner", "agent-host.dll", childEnvironment,
                ["host-agent", operation, config, .. role]);
        var timeout = ProcessWaiters.DefaultReadinessTimeout;
        using var supervisor = Start("run");
        await ProcessWaiters.WaitForFileAsync(Path.Combine(stateDirectory, "attempt-inventory.json"), supervisor, timeout);
        Assert.Equal(GateStates.Queued, (await server.Store.GetGateStatusAsync(subject.Subject.SubjectId, default))!.Phase);
        Assert.False(supervisor.Contains("adapter started"));
        Assert.False(File.Exists(Path.Combine(stateDirectory, "roles.json")));

        using var enable = Start("enable-role", "gate");
        await enable.WaitForExitAsync(timeout);
        Assert.True(enable.Process.ExitCode == 0, enable.ToString());
        await ProcessWaiters.WaitForConditionAsync(async () =>
            (await server.Store.GetGateStatusAsync(subject.Subject.SubjectId, default))!.Phase == GateStates.Running,
            supervisor, timeout, "supervised gate executing the merge");
        Assert.True(supervisor.Contains("role=gate adapter started"));
        var running = (await server.Store.GetGateStatusAsync(subject.Subject.SubjectId, default))!;
        Assert.Equal(issued.RunnerId, Assert.Single(running.Attempts).ExecutorId);
        Assert.Equal(hostname, running.Attempts[0].HostId);
        var readiness = Assert.Single(await server.Store.ListRunnerCapabilitySnapshotsAsync(default),
            runner => runner.RunnerId == issued.RunnerId);
        foreach (var key in new[] { GateCapabilities.Executor, GateCapabilities.PostMerge,
                     GateCapabilities.GitMaterialization, GateCapabilities.BundleMaterialization })
            Assert.Contains(readiness.Capabilities, item => item.Key == key && item.IsFresh
                && item.AdvertisedStatus == "ready");
        Assert.True((await server.Store.PostMergeHostsAsync(default)).Gate);
        await ProcessWaiters.WaitForConditionAsync(async () =>
            (await File.ReadAllTextAsync(Path.Combine(stateDirectory, "attempt-inventory.json")))
                .Contains(running.Attempts[0].AttemptId, StringComparison.Ordinal),
            supervisor, timeout, "gate attempt in host inventory");

        using var drain = Start("drain-role", "gate");
        var gateState = Path.Combine(root, "gate-state");
        await ProcessWaiters.WaitForFileAsync(Path.Combine(gateState, "host-gate-drain-acknowledged.json"), drain, timeout);
        using (var acknowledgement = JsonDocument.Parse(await File.ReadAllTextAsync(
                   Path.Combine(gateState, "host-gate-drain-acknowledged.json"))))
            Assert.Equal(1, acknowledgement.RootElement.GetProperty("activeSlots").GetInt32());
        Assert.False(drain.Process.HasExited);

        await File.WriteAllTextAsync(release, "");
        await drain.WaitForExitAsync(timeout);
        Assert.True(drain.Process.ExitCode == 0, drain.ToString());
        var passed = (await server.Store.GetGateStatusAsync(subject.Subject.SubjectId, default))!;
        Assert.Equal(GateStates.Passed, passed.Phase);
        Assert.Equal(sha, passed.Report!.TestedSha);
        Assert.False(File.Exists(Path.Combine(gateState, "gate-claim.json")));
        using var state = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(stateDirectory, "roles.json")));
        Assert.False(state.RootElement.GetProperty("roles").GetProperty("gate").GetProperty("enabled").GetBoolean());
    }

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
    [Trait("Category", "Guard")]
    public async Task Clean_context_lookup_projects_only_terminal_hashes_from_the_callers_runs()
    {
        using var temp = new TempDirectory();
        await using var server = new BoundServer(temp.Path);
        var (alpha, _) = await server.SeedProjectsAsync();
        var task = await server.CreateTaskAsync(alpha.ProjectId, "Alpha card");
        await server.SetPolicyAsync("host-a", alpha.ProjectId);
        var coder = await server.CreateRunnerAsync("coder", RunnerCredentialRoles.Coding);
        var other = await server.CreateRunnerAsync("other", RunnerCredentialRoles.Coding);
        using var claimant = server.Client(coder.Credential);
        using var unrelated = server.Client(other.Credential);
        await RegisterAsync(claimant, Coding("coder", "host-a"));
        var claim = await ClaimAsync(claimant, "coder");
        Assert.Equal(task.TaskId, claim.Task!.TaskId);
        const string path = "/api/v1/runners/coder/clean-context/terminal";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(task.TaskKey))).ToLowerInvariant();
        var hashes = new[] { hash, new string('f', 64) };
        async Task<string[]> Lookup(HttpClient client, string route)
        {
            using var response = await client.PostAsJsonAsync(route, hashes);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<string[]>())!;
        }

        Assert.Empty(await Lookup(claimant, path));
        using var released = await claimant.PostAsJsonAsync($"/api/v1/runs/{claim.Run!.RunId}/lease/release",
            new LeaseReleaseRequest("coder", "coder:1", claim.Lease!.LeaseId, claim.Lease.Fence, "failed"));
        released.EnsureSuccessStatusCode();
        var current = await server.Store.GetTaskAsync(alpha.ProjectId, task.TaskKey, default);
        await server.Store.UpdateTaskAsync(alpha.ProjectId, task.TaskKey,
            new UpdateTaskRequest(null, null, TaskStates.Completed, current!.Version), "test", default);
        Assert.Equal(new[] { hash }, await Lookup(claimant, path));
        Assert.Empty(await Lookup(unrelated, "/api/v1/runners/other/clean-context/terminal"));
        await AssertRefusedAsync(await unrelated.PostAsJsonAsync(path, hashes), "runner-identity-mismatch");
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
    public async Task Package_review_claims_and_reports_obey_the_bound_project_and_role()
    {
        using var temp = new TempDirectory();
        await using var server = new BoundServer(temp.Path);
        var (alpha, beta) = await server.SeedProjectsAsync();
        var alphaPackage = await server.Store.CreateReviewPackageSubjectAsync(
            PackageRequest(alpha.ProjectId, "alpha"), "orchestrator", default);
        var betaPackage = await server.Store.CreateReviewPackageSubjectAsync(
            PackageRequest(beta.ProjectId, "beta"), "orchestrator", default);
        await server.SetPolicyAsync("alpha-host", alpha.ProjectId);
        await server.SetPolicyAsync("beta-host", beta.ProjectId);
        var alphaCredential = await server.EnrolRunnerAsync("alpha-reviewer", RunnerCredentialRoles.Review);
        var betaCredential = await server.EnrolRunnerAsync("beta-reviewer", RunnerCredentialRoles.Review);
        var codingCredential = await server.EnrolRunnerAsync("coder", RunnerCredentialRoles.Coding);
        using var alphaClient = server.Client(alphaCredential.Credential);
        using var betaClient = server.Client(betaCredential.Credential);
        using var codingClient = server.Client(codingCredential.Credential);
        await RegisterAsync(alphaClient, Reviewer("alpha-reviewer", "alpha-host"));
        await RegisterAsync(betaClient, Reviewer("beta-reviewer", "beta-host"));
        await RegisterAsync(codingClient, Coding("coder", "alpha-host"));

        await AssertRefusedAsync(await codingClient.PostAsJsonAsync(
            "/api/v1/runners/coder/review-package-claims",
            new ReviewPackageClaimRequest("coder", "coder:1")), "runner-role-mismatch");
        var betaClaim = await betaClient.PostAsJsonAsync("/api/v1/runners/beta-reviewer/review-package-claims",
            new ReviewPackageClaimRequest("beta-reviewer", "beta-reviewer:1"));
        betaClaim.EnsureSuccessStatusCode();
        var betaResult = (await betaClaim.Content.ReadFromJsonAsync<ReviewPackageClaimResponse>())!;
        Assert.Equal(betaPackage.PackageId, betaResult.Subject!.PackageId);
        var betaEmpty = await betaClient.PostAsJsonAsync("/api/v1/runners/beta-reviewer/review-package-claims",
            new ReviewPackageClaimRequest("beta-reviewer", "beta-reviewer:1"));
        Assert.Equal("empty", (await betaEmpty.Content.ReadFromJsonAsync<ReviewPackageClaimResponse>())!.Status);

        var alphaClaim = await alphaClient.PostAsJsonAsync("/api/v1/runners/alpha-reviewer/review-package-claims",
            new ReviewPackageClaimRequest("alpha-reviewer", "alpha-reviewer:1"));
        alphaClaim.EnsureSuccessStatusCode();
        var alphaResult = (await alphaClaim.Content.ReadFromJsonAsync<ReviewPackageClaimResponse>())!;
        Assert.Equal(alphaPackage.PackageId, alphaResult.Subject!.PackageId);
        var attempt = $"/api/v1/review-packages/attempts/{alphaResult.Lease!.AttemptId}";
        await AssertRefusedAsync(await betaClient.PostAsJsonAsync(attempt + "/lease/renew",
            new ReviewPackageRenewRequest("beta-reviewer", "beta-reviewer:1",
                alphaResult.Lease.LeaseId, alphaResult.Lease.Fence)), "runner-project-not-bound");
        await AssertRefusedAsync(await betaClient.PostAsJsonAsync(attempt + "/report",
            new ReviewPackageReportRequest("beta-reviewer", "beta-reviewer:1",
                alphaResult.Lease.LeaseId, alphaResult.Lease.Fence, "report:foreign",
                ReviewPackageOutcomes.Completed, "done", null, null, [], [], [], [], 0, 0)),
            "runner-project-not-bound");
    }

    private static CreateReviewPackageSubjectRequest PackageRequest(string projectId, string suffix)
    {
        var commands = ReviewPackageAspects.Agent.Select(aspect => new ReviewCommandDto(
            "package-" + aspect, aspect, "codex", [], ExecutionKind: ReviewCommandKinds.AgentAspect,
            Prompt: "Review " + aspect, CliType: "codex", Model: "gpt-6-sol")).ToArray();
        return new CreateReviewPackageSubjectRequest(projectId, "docs/review/index.html", ReviewPackageModes.Shadow,
            [new ReviewPackageMemberDto("AGT-" + suffix + "-1", "run-" + suffix + "-1", new string('a', 40), 0),
             new ReviewPackageMemberDto("AGT-" + suffix + "-2", "run-" + suffix + "-2", new string('b', 40), 1)],
            new ReviewPackagePlanDto("repo", "https://example.invalid/review.git", "main", commands),
            "package:" + suffix);
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

    [Theory]
    [InlineData(GateSubjectKinds.Run, false)]
    [InlineData(GateSubjectKinds.Batch, false)]
    [InlineData(GateSubjectKinds.Merge, false)]
    [InlineData(GateSubjectKinds.Promotion, false)]
    [InlineData(GateSubjectKinds.PostMerge, false)]
    [InlineData(GateSubjectKinds.Run, true)]
    [InlineData(GateSubjectKinds.Batch, true)]
    [InlineData(GateSubjectKinds.Merge, true)]
    [InlineData(GateSubjectKinds.Promotion, true)]
    [InlineData(GateSubjectKinds.PostMerge, true)]
    public async Task A_gate_claim_discloses_only_subjects_in_the_host_project_scope(string kind, bool missingPolicy)
    {
        using var temp = new TempDirectory();
        await using var server = new BoundServer(temp.Path);
        var (alpha, beta) = await server.SeedProjectsAsync();
        await server.SetPolicyAsync("coding-host", alpha.ProjectId, beta.ProjectId);
        var betaSubject = await server.SeedGateSubjectAsync(beta, kind, "beta");
        var alphaSubject = await server.SeedGateSubjectAsync(alpha, kind, "alpha");
        if (!missingPolicy) await server.SetPolicyAsync("gate-host", alpha.ProjectId);
        var credential = await server.CreateRunnerAsync("gate", role: null);
        using var runner = server.Client(credential.Credential);
        await RegisterGateAsync(server, runner, "gate", "gate-host");

        var claim = await GateClaimAsync(runner, "gate");
        if (missingPolicy)
        {
            Assert.Equal("empty", claim.Status);
            Assert.Null(claim.Subject);
            Assert.Null(claim.Attempt);
            Assert.Null(claim.Lease);
            await server.SetPolicyAsync("gate-host", alpha.ProjectId);
            claim = await GateClaimAsync(runner, "gate");
        }
        Assert.Equal("claimed", claim.Status);
        Assert.Equal(alphaSubject.Subject.SubjectId, claim.Subject!.SubjectId);
        Assert.Equal(alpha.ProjectId, claim.Subject.ProjectId);
        Assert.Equal(GateStates.Queued,
            (await server.Store.GetGateStatusAsync(betaSubject.Subject.SubjectId, default))!.Phase);
        await AssertRefusedAsync(await runner.GetAsync($"/api/v1/gates/subjects/{betaSubject.Subject.SubjectId}"),
            "runner-not-claimant");

        var lease = claim.Lease!;
        var authority = new GateAuthority("gate", "gate:1", lease.LeaseId, lease.Fence, lease.AuthorityEpoch);
        var path = $"/api/v1/gates/attempts/{claim.Attempt!.AttemptId}";
        foreach (var phase in new[] { GateStates.Materializing, GateStates.Running, GateStates.Reporting, GateStates.Cleaning })
            (await runner.PostAsJsonAsync(path + "/phase", new GatePhaseRequest(authority, phase))).EnsureSuccessStatusCode();
        var report = new GateReport(GateStates.Passed, null, ResultSha, new string('b', 40), false, false,
            [new GateCommandEvidence("verify", 0, false, new string('c', 64), "ok", Start.UtcDateTime, Start.UtcDateTime)],
            "host", "toolchain", [], [], "clean",
            GateSubjectKinds.IsVerification(kind) ? new(claim.Subject.PlanHash, claim.Subject.PolicyHash, ResultSha) : null);
        var request = new SubmitGateReportRequest(authority, report);
        var intruderCredential = await server.CreateRunnerAsync("intruder", role: null);
        using var intruder = server.Client(intruderCredential.Credential);
        await AssertRefusedAsync(await intruder.PostAsJsonAsync(path + "/report", request), "runner-identity-mismatch");
        Assert.Equal(HttpStatusCode.Conflict, (await runner.PostAsJsonAsync(path + "/report",
            request with { Authority = authority with { Fence = authority.Fence + 1 } })).StatusCode);
        var otherPath = $"/api/v1/gates/attempts/{betaSubject.Attempts.Single().AttemptId}/report";
        Assert.Equal(HttpStatusCode.Conflict, (await runner.PostAsJsonAsync(otherPath, request)).StatusCode);
        var accepted = await runner.PostAsJsonAsync(path + "/report", request);
        accepted.EnsureSuccessStatusCode();
        Assert.Equal(GateStates.Passed, (await accepted.Content.ReadFromJsonAsync<GateStatus>())!.Phase);
        (await runner.PostAsJsonAsync(path + "/report", request)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await runner.PostAsJsonAsync(path + "/report",
            request with { Report = report with { EnvironmentIdentity = "changed" } })).StatusCode);
        Assert.Equal("empty", (await GateClaimAsync(runner, "gate")).Status);
        // Trusted orchestration retains its direct global queue API.
        var trusted = await server.Store.ClaimGateAsync(new("gate", "gate:1"), "orchestrator", default);
        Assert.Equal(betaSubject.Subject.SubjectId, trusted.Subject!.SubjectId);
    }

    [Fact]
    public async Task A_gate_claim_requires_the_runner_credential_to_name_its_executor()
    {
        using var temp = new TempDirectory();
        await using var server = new BoundServer(temp.Path);
        var credential = "unbound-" + RandomNumberGenerator.GetHexString(48).ToLowerInvariant();
        await server.Store.EnsureBootstrapPrincipalAsync("runner:unbound", TaskServerPrincipalKinds.Runner,
            credential, null, default);
        using var runner = server.Client(credential);
        await AssertRefusedAsync(await runner.PostAsJsonAsync("/api/v1/gates/claims",
            new GateClaimRequest("another-executor", "instance")), "runner-identity-mismatch");
    }

    private static async Task RegisterGateAsync(BoundServer server, HttpClient client, string id, string host)
    {
        string[] keys = [GateCapabilities.Executor, GateCapabilities.PostMerge, GateCapabilities.GitMaterialization,
            CapabilityProtocol.GitFetch, CapabilityProtocol.RepositoryAccess,
            CapabilityProtocol.TaskServerConnectivity, CapabilityProtocol.Disk];
        await RegisterAsync(client, new(id, host, id + ":1", "1", TaskServerProtocol.Current, keys));
        var advertisement = await client.PutAsJsonAsync($"/api/v1/runners/{id}/capabilities",
            new CapabilityAdvertisementRequest(id, id + ":1", CapabilityProtocol.CurrentSchemaVersion,
                server.Clock.GetUtcNow().UtcDateTime, 300, 1,
                keys.Select(key => new AdvertisedCapabilityDto(key, key.Split(':')[0])).ToArray()));
        advertisement.EnsureSuccessStatusCode();
    }

    private static async Task<GateClaimResponse> GateClaimAsync(HttpClient client, string id)
    {
        var response = await client.PostAsJsonAsync("/api/v1/gates/claims", new GateClaimRequest(id, id + ":1"));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<GateClaimResponse>())!;
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

        public BoundServer(string dataDirectory, FakeGitHub? github = null, bool useKestrel = false)
        {
            StudioToken = "studio-" + RandomNumberGenerator.GetHexString(48).ToLowerInvariant();
            _factory = new Factory(dataDirectory, StudioToken, "engine-" + RandomNumberGenerator.GetHexString(48).ToLowerInvariant(),
                useKestrel ? null : Clock, github);
            if (useKestrel) _factory.UseKestrel(0);
            _manager = Client(StudioToken);
            Store = _factory.Services.GetRequiredService<TaskServerStore>();
        }

        public ManualTimeProvider Clock { get; } = new(Start);
        public DateTime UtcNow => _factory.Services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;
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

        public async Task<(IssuedHostJoinToken Issued, ExchangeEnrolmentRequest Exchange)> IssueGateJoinTokenAsync(string host)
        {
            using var owner = Client(StudioToken);
            owner.DefaultRequestHeaders.Add(CredentialViewProtocol.ActingRoleHeader, StudioUserRoles.Owner);
            using var response = await owner.PostAsJsonAsync("/api/v1/management/host-join-tokens",
                new CreateHostJoinTokenRequest("https://tasks.example.test", host, "gate"));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.True(response.Headers.CacheControl!.NoStore);
            var issued = (await response.Content.ReadFromJsonAsync<IssuedHostJoinToken>())!;
            var body = issued.JoinToken.Split('.')[1].Replace('-', '+').Replace('_', '/');
            using var payload = JsonDocument.Parse(Convert.FromBase64String(
                body.PadRight(body.Length + (4 - body.Length % 4) % 4, '=')));
            return (issued, new ExchangeEnrolmentRequest(payload.RootElement.GetProperty("enrolmentCode").GetString()!,
                payload.RootElement.GetProperty("installationId").GetString()!));
        }

        public async Task<IssuedPrincipalCredential> ExchangeJoinTokenAsync(ExchangeEnrolmentRequest exchange)
        {
            using var anonymous = Client(null);
            using var response = await anonymous.PostAsJsonAsync("/api/v1/enrolments/exchange", exchange);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<ExchangedEnrolment>())!.Issued;
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

        public async Task<GateStatus> SeedGateSubjectAsync(ProjectDto project, string kind, string name)
        {
            var plan = new GatePlan("post-build-test-gate", 1,
                [new GateCommand("verify", "bash", ["-lc", "true"], 30)], "", 300, [], 4096, "always",
                SubjectBindingDigest: kind == GateSubjectKinds.Batch ? new string('d', 64) : null,
                PostMerge: kind == GateSubjectKinds.PostMerge ? new(project.ProjectId, "develop", "window") : null);
            CreateGateSubjectRequest request;
            if (kind == GateSubjectKinds.Run)
            {
                var source = await SeedReviewSubjectAsync(project.ProjectId, name);
                request = new(source.TaskId, source.SourceRunId, source.RepositoryId, source.RepositoryUrl,
                    ResultSha, source.ResultRef, null, null, GateRules.PlanHash(plan), "policy", "1", "audit", plan,
                    Clock.GetUtcNow().UtcDateTime.AddMinutes(15));
            }
            else
            {
                await RegisterRepositoryProjectAsync(project.WorkspaceId, project.ProjectId, project.Name,
                    name == "alpha" ? "ALP" : "BET", $"https://example.invalid/{name}.git");
                var repository = await Store.GetProjectRepositoryAsync(project.ProjectId, default);
                // This identity is owned by the monolith and is deliberately not a Task Server card row.
                request = new(project.ProjectId, kind + ":" + name, repository!.RepositoryId, repository.RepositoryUrl,
                    ResultSha, ResultSha, null, null, GateRules.PlanHash(plan), "policy", "1", "audit", plan,
                    Clock.GetUtcNow().UtcDateTime.AddMinutes(15), Kind: kind);
            }
            if (kind == GateSubjectKinds.Batch)
            {
                var batch = new string(name == "alpha" ? 'a' : 'b', 32);
                request = request with
                {
                    BatchId = batch, MembershipDigest = plan.SubjectBindingDigest, BaseSha = new string('1', 40),
                    MemberRunIds = [request.SourceRunId],
                    ResultRef = $"refs/agent-studio/batch-candidates/{batch}/{plan.SubjectBindingDigest}/1",
                };
            }
            var status = await Store.CreateGateSubjectAsync(request, "orchestrator", default);
            Clock.Advance(TimeSpan.FromSeconds(1));
            return status;
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
        string dataDirectory, string studioToken, string engineToken, TimeProvider? clock, FakeGitHub? github)
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
                if (clock is not null)
                {
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton(clock);
                }
                if (github is null) return;
                services.RemoveAll<IGitHubAppVault>();
                services.AddSingleton<IGitHubAppVault>(github.Vault);
                services.RemoveAll<GitHubAppBroker>();
                services.AddSingleton(provider => new GitHubAppBroker(
                    new GitHubAppClient(new HttpClient(github), provider.GetRequiredService<TimeProvider>()),
                    github.Vault,
                    provider.GetRequiredService<IRegisteredRepositoryCatalog>(),
                    provider.GetRequiredService<TimeProvider>()));
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
                    expires_at = Start.AddHours(1),
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
