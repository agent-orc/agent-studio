using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AgentStudio.Bus;
using AgentStudio.Git;
using AgentStudio.Pipeline;
using AgentStudio.Publishing;
using AgentStudio.Quota;
using AgentStudio.Registry;
using AgentStudio.Review;
using AgentStudio.Secrets;
using AgentStudio.TaskServer.Contracts.GitHubApp;
using AgentStudio.Tokens;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

[Trait("Category", "MachineBound")]
public sealed class GitHubPublishCanaryTests : IDisposable
{
    private const string Project = "Canary";
    private const string Origin = "https://github.com/example/publish-canary.git";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "publish-canary-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 28, 10, 0, 0, TimeSpan.Zero));
    private readonly IConfiguration _config;
    private readonly GitHubDouble _github;
    private readonly PublishSecret _secret = new();
    private readonly ProjectRegistry _projects;
    private readonly WorkspaceRegistry _workspaces;
    private readonly TaskScannerService _scanner;
    private readonly GitService _git;
    private readonly PublishTargetService _targets;
    private readonly GitHubPublishClient _client;
    private readonly ProjectSettingsService _settings;
    private readonly WorkspaceQuotaService _quota;
    private readonly PublishActionService _actions;
    private readonly string _repo;

    public GitHubPublishCanaryTests()
    {
        _repo = Path.Combine(_root, "checkout");
        var tasks = Path.Combine(_root, "tasks");
        Directory.CreateDirectory(Path.Combine(_repo, ".github", "workflows"));
        Directory.CreateDirectory(Path.Combine(_repo, "website"));
        Directory.CreateDirectory(tasks);
        Git(_repo, "init", "-q", "-b", "main");
        Git(_repo, "config", "user.email", "test@example.com");
        Git(_repo, "config", "user.name", "Publish fixture");
        Git(_repo, "config", "commit.gpgsign", "false");
        Git(_repo, "remote", "add", "origin", Origin);
        File.WriteAllText(Path.Combine(_repo, ".github", "workflows", "pages.yml"), """
            on:
              workflow_dispatch:
            jobs:
              deploy:
                steps:
                  - uses: actions/upload-pages-artifact@v3
                    with:
                      path: website
                  - uses: actions/deploy-pages@v4
            """);
        File.WriteAllText(Path.Combine(_repo, "package.json"), """{"name":"canary","version":"1.0.0"}""");
        File.WriteAllText(Path.Combine(_repo, "website", "index.html"), "First build");
        Git(_repo, "add", "--all");
        Git(_repo, "commit", "-qm", "Initial build");
        Git(_repo, "tag", "v1.0.0");
        File.WriteAllText(Path.Combine(_repo, "website", "index.html"), "Canary build");
        Git(_repo, "commit", "-qam", "Build website canary");

        _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["WatchPaths:0:Name"] = Project,
            ["WatchPaths:0:Path"] = tasks,
            ["WatchPaths:0:RootPath"] = _repo,
            ["WatchPaths:0:RepositoryPath"] = _repo,
            ["Publishing:GitHubApp:AppId"] = "123",
        }).Build();
        _workspaces = new WorkspaceRegistry(_config, Log<WorkspaceRegistry>());
        var workspace = _workspaces.EnsureDefaultWorkspace(_clock);
        _projects = new ProjectRegistry(_config, Log<ProjectRegistry>());
        var project = _projects.EnsureProjectForStorage(tasks, Project, workspace.Id, _clock);
        _config[$"Publishing:GitHubApp:Projects:{project.Id}"] = Origin;
        var summary = new SummaryGenerationService(Log<SummaryGenerationService>(), _config);
        _scanner = new TaskScannerService(_config, Log<TaskScannerService>(), summary);
        _git = new GitService(Log<GitService>(), _scanner, _config);
        _settings = new ProjectSettingsService(Log<ProjectSettingsService>(), _config);
        _targets = new PublishTargetService(_git, _settings, Log<PublishTargetService>(), _clock);
        _github = new GitHubDouble(_clock);
        _client = new GitHubPublishClient(_config, _projects, _secret, _github, _clock);
        _quota = OpenQuota();
        _quota.SetLimits(workspace.Id, null, new QuotaLimits(null, null, 1));
        _actions = OpenActions(_quota);
    }

    [Fact]
    public void Website_workflow_keeps_its_run_id_through_retries_and_reconciles_the_deployment()
    {
        var dispatched = _actions.DeployWebsite(Project);
        Assert.Equal("queued", dispatched.Status);
        Assert.Equal(42, dispatched.RunId);
        Assert.Equal(42, Assert.Single(OpenQuota().PendingDispatches("website")).RunId);

        _github.FailStatus = true;
        using (var scope = GitReadRequestGuard.BeginRead("GET", "/api/projects/{project}/publish/{targetId}/run"))
        {
            var refused = _actions.RefreshRun(Project, "website")!;
            Assert.Equal(42, refused.RunId);
            Assert.Contains("503", refused.Error);
            Assert.True(_quota.HasProjectEnvironment(Project, "website"));
            _github.FailStatus = false;
            _github.Status = "in_progress";
            Assert.Equal("in_progress", _actions.RefreshRun(Project, "website")!.Status);

            _github.Status = "completed";
            _github.FailDeployment = true;
            var completed = _actions.RefreshRun(Project, "website")!;
            Assert.Equal("completed", completed.Status);
            Assert.Equal("success", completed.Conclusion);
            Assert.Equal(42, completed.RunId);
            Assert.Contains("503", completed.Error);
            Assert.True(_quota.HasProjectEnvironment(Project, "website"));
            Assert.Empty(_quota.PendingDispatches("website"));
            Assert.Equal(0, scope.Spawns);
        }

        _github.FailDeployment = false;
        _github.DeploymentState = "inactive";
        OpenActions(OpenQuota()).ReconcileRunningWebsites();
        Assert.False(_quota.HasProjectEnvironment(Project, "website"));
        Assert.Equal(0, _github.RunLists);
        Assert.Equal(1, _github.Dispatches);
        Assert.Equal("actions:write", _github.Permissions[0]);
        Assert.Contains("actions:read", _github.Permissions);
        Assert.Contains("deployments:read", _github.Permissions);
    }

    [Fact]
    public void A_legacy_dispatch_receipt_saves_the_discovered_id_before_a_status_failure()
    {
        _github.OmitDispatchReceipt = true;
        Assert.Null(_actions.DeployWebsite(Project).RunId);
        _github.FailStatus = true;
        var retried = _actions.RefreshRun(Project, "website")!;
        Assert.Equal(42, retried.RunId);
        Assert.NotNull(retried.Error);
        var quotaAfterRestart = OpenQuota();
        Assert.Equal(42, Assert.Single(quotaAfterRestart.PendingDispatches("website")).RunId);

        _github.FailStatus = false;
        _github.Status = "completed";
        _github.Conclusion = "failure";
        OpenActions(quotaAfterRestart).ReconcileRunningWebsites();
        Assert.Equal(1, _github.RunLists);
        Assert.False(_quota.HasProjectEnvironment(Project, "website"));
    }

    [Theory]
    [InlineData("refused-dispatch", "422", 1)]
    [InlineData("uninstalled", "does not cover", 0)]
    [InlineData("extra-permission", "token-scope-mismatch", 0)]
    [InlineData("unapproved-origin", "not authorized", 0)]
    public void Failed_dispatch_releases_its_reservation_and_unauthorized_repositories_never_dispatch(
        string failure, string message, int dispatches)
    {
        _github.RefuseDispatch = failure == "refused-dispatch";
        _github.Uninstalled = failure == "uninstalled";
        _github.Overgrant = failure == "extra-permission";
        if (failure == "unapproved-origin")
            Git(_repo, "remote", "set-url", "origin", "https://github.com/another/publish-canary.git");

        var error = Assert.Throws<InvalidOperationException>(() => _actions.DeployWebsite(Project));
        Assert.Contains(message, error.Message);
        Assert.Equal(dispatches, _github.Dispatches);
        if (failure == "unapproved-origin") Assert.Equal(0, _github.Requests);
        Assert.False(_quota.HasProjectEnvironment(Project, "website"));
        Assert.Empty(_quota.PendingDispatches("website"));
        Assert.Null(_actions.RefreshRun(Project, "website"));
    }

    [Fact]
    public async Task Atomic_git_tag_push_and_release_lookup_share_the_tag_and_commit()
    {
        var bare = Path.Combine(_root, "release.git");
        Git(_root, "init", "-q", "--bare", bare);
        Git(_repo, "remote", "set-url", "--push", "origin", bare);
        var head = PublishActionService.PublishPackageTagPath(_repo, PublishEcosystems.Npm, "1.1.0");
        Assert.Equal(head, Git(bare, "rev-parse", "refs/heads/main"));
        Assert.Equal(head, Git(bare, "rev-parse", "refs/tags/v1.1.0"));
        _github.TagHead = head;
        var repo = _client.RequireRepository(Project, Origin);
        var id = await _client.FindRunAsync(repo, new PublishWorkflowRun
        {
            Project = Project, TargetId = "package:npm", Version = "1.1.0", Workflow = "release.yml",
            TriggeredAt = _clock.GetUtcNow().UtcDateTime, HeadSha = head,
        });
        Assert.Equal(42, id);
        _github.Status = "completed";
        Assert.Equal("success", (await _client.GetRunAsync(repo, id!.Value)).Conclusion);
        Assert.All(_github.Permissions, permission => Assert.Equal("actions:read", permission));
        Assert.Equal(0, _github.Dispatches);
    }

    private WorkspaceQuotaService OpenQuota()
    {
        var bus = new AgentMessageBusStore();
        var ledger = new ProjectTokenLedger(bus, _config, new ProjectTokenReceiptReader());
        var pricing = new TokenPricingService(new TokenEconomyPriceProvider());
        var pipeline = new PipelineExecutionLog(pricing, Log<PipelineExecutionLog>());
        return new WorkspaceQuotaService(_config, _clock, _workspaces, _projects, ledger, pricing, _scanner, pipeline, bus);
    }

    private PublishActionService OpenActions(WorkspaceQuotaService quota) => new(_git, _targets,
        new TaskPublishableService(_targets, Log<TaskPublishableService>()), _settings, _scanner,
        Log<PublishActionService>(), _client, quota, _clock);

    private static NullLogger<T> Log<T>() => NullLogger<T>.Instance;

    private static string Git(string root, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        var result = GitProcessBoundary.Run(start);
        Assert.True(result.ExitCode == 0, result.StandardError);
        return result.StandardOutput.Trim();
    }

    public void Dispose()
    {
        _targets.Dispose();
        _github.Dispose();
        _secret.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private sealed class GitHubDouble(TimeProvider clock) : HttpMessageHandler, IHttpClientFactory
    {
        private readonly Dictionary<string, string> _tokens = [];
        public List<string> Permissions { get; } = [];
        public int Dispatches { get; private set; }
        public int Requests { get; private set; }
        public int RunLists { get; private set; }
        public bool RefuseDispatch { get; set; }
        public bool FailStatus { get; set; }
        public bool FailDeployment { get; set; }
        public bool OmitDispatchReceipt { get; set; }
        public bool Uninstalled { get; set; }
        public bool Overgrant { get; set; }
        public string Status { get; set; } = "queued";
        public string Conclusion { get; set; } = "success";
        public string DeploymentState { get; set; } = "success";
        public string? TagHead { get; set; }

        public HttpClient CreateClient(string name)
        {
            Assert.Equal("github-app", name);
            return new HttpClient(this, disposeHandler: false);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            var bearer = request.Headers.Authorization!.Parameter!;
            var path = request.RequestUri.AbsolutePath;
            if (path == "/repos/example/publish-canary/installation")
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal(3, bearer.Split('.').Length);
                return Uninstalled ? new(HttpStatusCode.NotFound) : Json(new { id = 10 });
            }
            if (path == "/app/installations/10/access_tokens")
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal(3, bearer.Split('.').Length);
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                Assert.Equal("publish-canary", Assert.Single(body.RootElement.GetProperty("repositories").EnumerateArray()).GetString());
                var permissions = body.RootElement.GetProperty("permissions").EnumerateObject()
                    .ToDictionary(item => item.Name, item => item.Value.GetString());
                Assert.Equal("read", permissions["metadata"]);
                var grant = Assert.Single(permissions, item => item.Key != "metadata");
                var scope = $"{grant.Key}:{grant.Value}";
                Permissions.Add(scope);
                var token = Guid.NewGuid().ToString("N");
                _tokens.Add(token, scope);
                if (Overgrant) permissions["contents"] = "write";
                return Json(new { token, expires_at = clock.GetUtcNow().AddHours(1), permissions,
                    repositories = new[] { new { name = "publish-canary" } } }, HttpStatusCode.Created);
            }

            var granted = _tokens[bearer];
            const string target = "/repos/example/publish-canary";
            switch (path)
            {
                case target + "/actions/workflows/pages.yml/dispatches":
                    Assert.Equal(HttpMethod.Post, request.Method);
                    Assert.Equal("actions:write", granted);
                    using (var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)))
                    {
                        Assert.Equal("main", body.RootElement.GetProperty("ref").GetString());
                        Assert.True(body.RootElement.GetProperty("return_run_details").GetBoolean());
                    }
                    Dispatches++;
                    if (RefuseDispatch) return new(HttpStatusCode.UnprocessableEntity);
                    if (OmitDispatchReceipt) return new(HttpStatusCode.NoContent);
                    return Json(new { workflow_run_id = 42, html_url = Origin + "/actions/runs/42" });
                case target + "/actions/workflows/pages.yml/runs":
                case target + "/actions/workflows/release.yml/runs":
                    Assert.Equal("actions:read", granted);
                    var refName = TagHead is null ? "main" : "v1.1.0";
                    Assert.Contains($"branch={refName}", request.RequestUri.Query);
                    Assert.Contains(TagHead is null ? "event=workflow_dispatch" : "event=push", request.RequestUri.Query);
                    if (TagHead is not null) Assert.Contains($"head_sha={TagHead}", request.RequestUri.Query);
                    RunLists++;
                    return Json(new { workflow_runs = new[]
                    {
                        new { id = 99, created_at = clock.GetUtcNow(), head_branch = "other", head_sha = TagHead },
                        new { id = 42, created_at = clock.GetUtcNow(), head_branch = refName, head_sha = TagHead },
                    } });
                case target + "/actions/runs/42":
                    Assert.Equal("actions:read", granted);
                    return FailStatus ? new(HttpStatusCode.ServiceUnavailable)
                        : Json(new { id = 42, status = Status, conclusion = Status == "completed" ? Conclusion : null });
                case target + "/deployments":
                    Assert.Equal("deployments:read", granted);
                    Assert.Contains("environment=github-pages", request.RequestUri.Query);
                    return FailDeployment ? new(HttpStatusCode.ServiceUnavailable)
                        : Json(new[] { new { id = 7, environment = "github-pages", created_at = clock.GetUtcNow().AddSeconds(1) } });
                case target + "/deployments/7/statuses":
                    Assert.Equal("deployments:read", granted);
                    return Json(new[] { new { state = DeploymentState } });
                default:
                    throw new InvalidOperationException($"Unscripted canary request {request.Method} {path}");
            }
        }

        private static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK)
            => new(status) { Content = JsonContent.Create(body) };
    }

    private sealed class PublishSecret : ISecretProvider, IDisposable
    {
        private readonly RSA _key = RSA.Create(2048);

        public Task<ResolvedSecret> GetForPlatformAsync(SecretReference reference, string purpose, CancellationToken ct = default)
        {
            Assert.Equal(SecretScope.Workspace, reference.Scope);
            Assert.Equal(SecretAccessPolicy.GitHubPublishAppPrivateKeyName, reference.Name);
            Assert.Equal("github-publish-token-issue", purpose);
            var workload = new SecretWorkload("runner", "canary", Project, null, [reference.Name]);
            Assert.Equal("platform-secret", SecretAccessPolicy.ForWorkload(reference, workload, "task:build").Code);
            return Task.FromResult(new ResolvedSecret(reference, reference, _key.ExportPkcs8PrivateKeyPem()));
        }

        public void Dispose() => _key.Dispose();
        public Task<SecretListing> ListAsync(SecretScope? scope = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SecretRecord> PutAsync(SecretScope scope, string name, string value, SecretActor actor, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SecretRecord> RotateAsync(SecretScope scope, string name, string value, SecretActor actor, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SecretRecord> RevokeAsync(SecretScope scope, string name, int? version, SecretActor actor, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(SecretScope scope, string name, SecretActor actor, CancellationToken ct = default) => throw new NotSupportedException();
        public Task EnsureWritableAsync(SecretScope scope, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SecretAuditEntry>> ReadAuditAsync(SecretScope? scope, int limit, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ResolvedSecret> GetForWorkloadAsync(SecretReference reference, SecretWorkload workload, string purpose, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
