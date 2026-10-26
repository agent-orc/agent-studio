using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Secrets;
using AgentStudio.Security;
using AgentStudio.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// HTTP contract of the secret routes: write-without-read on every route,
/// the role and project matrix for people, lease-bound resolution for
/// workloads, audit, and scrubbing of resolved values from task folders.
/// </summary>
public sealed class SecretEndpointTests : IDisposable
{
    private const string Project = "memowikis";
    private const string TaskKey = "AGT-SECRETS-1";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "secret-endpoints-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 19, 9, 0, 0, TimeSpan.Zero));
    private readonly FakeLeases _leases = new();
    private string Workspace => Path.Combine(_root, "workspace");
    private string ProjectPath => Path.Combine(Workspace, "projects", Project);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static string NewValue() => "value-" + Guid.NewGuid().ToString("N");

    /// <summary>Every secret route a person can call. A new route must be added here and exercised below.</summary>
    private static readonly string[] HumanRoutes =
    [
        "GET /api/secrets",
        "GET /api/secrets/audit",
        "PUT /api/secrets/backends",
        .. ScopeRoutes("/api/secrets/workspace"),
        .. ScopeRoutes("/api/projects/{project}/secrets"),
        .. ScopeRoutes("/api/projects/{project}/environments/{environment}/secrets"),
    ];

    private static IEnumerable<string> ScopeRoutes(string prefix) =>
    [
        $"GET {prefix}", $"POST {prefix}", $"GET {prefix}/{{name}}",
        $"POST {prefix}/{{name}}/rotate", $"POST {prefix}/{{name}}/revoke", $"DELETE {prefix}/{{name}}",
    ];

    [Fact]
    public async Task Route_inventory_is_exactly_the_tested_human_routes_plus_the_workload_resolve()
    {
        using var factory = Networked();
        using var client = Client(factory);
        await client.GetAsync("/healthz");

        var routes = factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.Contains("/secrets", StringComparison.Ordinal) == true)
            .SelectMany(endpoint => endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods
                .Select(method => $"{method} {endpoint.RoutePattern.RawText!.TrimEnd('/')}"))
            .Distinct().Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(HumanRoutes.Append("POST " + SecretEndpoints.ResolvePath).Order(StringComparer.Ordinal), routes);
    }

    [Fact]
    public async Task No_human_route_returns_a_value_and_an_owner_session_cannot_resolve()
    {
        using var factory = Networked();
        using var client = Client(factory);
        var owner = Owner(factory);
        var values = new List<string>();
        var exercised = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (prefix, pattern) in new[]
                 {
                     ("/api/secrets/workspace", "/api/secrets/workspace"),
                     ($"/api/projects/{Project}/secrets", "/api/projects/{project}/secrets"),
                     ($"/api/projects/{Project}/environments/stage/secrets", "/api/projects/{project}/environments/{environment}/secrets"),
                 })
        {
            var (first, second) = (NewValue(), NewValue());
            values.AddRange([first, second]);
            await Expect(HttpStatusCode.Created, "POST", prefix, pattern, new { name = "DATABASE_URL", value = first });
            await Expect(HttpStatusCode.OK, "GET", prefix, pattern);
            await Expect(HttpStatusCode.OK, "GET", prefix + "/DATABASE_URL", pattern + "/{name}");
            await Expect(HttpStatusCode.OK, "POST", prefix + "/DATABASE_URL/rotate", pattern + "/{name}/rotate", new { value = second });
            await Expect(HttpStatusCode.OK, "POST", prefix + "/DATABASE_URL/revoke", pattern + "/{name}/revoke", new { version = 1 });
            await Expect(HttpStatusCode.NoContent, "DELETE", prefix + "/DATABASE_URL", pattern + "/{name}");
            await Expect(HttpStatusCode.Created, "POST", prefix, null, new { name = "MAIL_KEY", value = second });
        }
        await Expect(HttpStatusCode.OK, "GET", "/api/secrets", "/api/secrets");
        await Expect(HttpStatusCode.OK, "GET", "/api/secrets/audit", "/api/secrets/audit");
        await Expect(HttpStatusCode.OK, "PUT", "/api/secrets/backends", "/api/secrets/backends",
            new { scope = "workspace", kind = "builtin", url = "", mount = (string?)null,
                vaultId = (string?)null, credentialReference = (string?)null,
                credentialEnvironmentVariable = (string?)null, ttlSeconds = 30 });

        var resolve = await Send(client, "POST", SecretEndpoints.ResolvePath, Resolve($"secret://{Project}/MAIL_KEY"), owner);
        Assert.Equal(HttpStatusCode.Unauthorized, resolve.Status);
        Assert.Equal(HumanRoutes.Order(StringComparer.Ordinal), exercised.Order(StringComparer.Ordinal));

        async Task Expect(HttpStatusCode status, string method, string path, string? pattern, object? body = null)
        {
            var response = await Send(client, method, path, body, owner);
            Assert.True(status == response.Status, $"{method} {path}: {response.Status} {response.Body}");
            Assert.All(values, value => Assert.DoesNotContain(value, response.Body, StringComparison.Ordinal));
            if (pattern is not null) exercised.Add($"{method} {pattern}");
        }
    }

    [Fact]
    public async Task Metadata_shows_versions_and_who_but_no_value()
    {
        using var factory = Networked();
        using var client = Client(factory);
        var owner = Owner(factory);
        var value = NewValue();
        await Send(client, "POST", $"/api/projects/{Project}/environments/prod/secrets", new { name = "DATABASE_URL", value }, owner);
        _clock.Advance(TimeSpan.FromHours(1));
        await Send(client, "POST", $"/api/projects/{Project}/environments/prod/secrets/DATABASE_URL/rotate", new { value = NewValue() }, owner);

        var overview = await Send(client, "GET", "/api/secrets", null, owner);
        using var json = JsonDocument.Parse(overview.Body);
        var secret = Assert.Single(json.RootElement.GetProperty("secrets").EnumerateArray());
        Assert.Equal("secret://memowikis/prod/DATABASE_URL", secret.GetProperty("reference").GetString());
        Assert.Equal(2, secret.GetProperty("currentVersion").GetInt32());
        Assert.Equal("human:first.owner", secret.GetProperty("createdBy").GetString());
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, secret.GetProperty("rotatedAt").GetDateTime());
        Assert.Equal([2, 1], secret.GetProperty("versions").EnumerateArray().Select(item => item.GetProperty("version").GetInt32()));
        Assert.Equal("builtin", json.RootElement.GetProperty("store").GetString());
        Assert.DoesNotContain(secret.EnumerateObject(), property => property.Name.Contains("value", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Viewer_reads_metadata_but_cannot_write_and_product_role_sees_nothing()
    {
        using var factory = Networked();
        using var client = Client(factory);
        var owner = Owner(factory);
        await Send(client, "POST", "/api/secrets/workspace", new { name = "MAIL_KEY", value = NewValue() }, owner);
        var viewer = SignIn(factory, "team.viewer", StudioRoles.Viewer, ["*"]);
        var product = SignIn(factory, "team.product", StudioRoles.Contributor, [Project]);

        Assert.Equal(HttpStatusCode.OK, (await Send(client, "GET", "/api/secrets", null, viewer)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Send(client, "GET", "/api/secrets/workspace/MAIL_KEY", null, viewer)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(client, "POST", "/api/secrets/workspace/MAIL_KEY/rotate", new { value = NewValue() }, viewer)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(client, "POST", "/api/secrets/workspace/MAIL_KEY/revoke", new { }, viewer)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(client, "GET", "/api/secrets", null, product)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(client, "GET", $"/api/projects/{Project}/secrets", null, product)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, "GET", "/api/secrets", null, null)).Status);

        using var audit = JsonDocument.Parse((await Send(client, "GET", "/api/secrets/audit", null, owner)).Body);
        var denied = audit.RootElement.GetProperty("entries").EnumerateArray()
            .Where(entry => entry.GetProperty("actor").GetString() == "human:team.viewer")
            .Select(entry => (entry.GetProperty("action").GetString(), entry.GetProperty("reference").GetString(),
                entry.GetProperty("outcome").GetString()!.StartsWith("denied:", StringComparison.Ordinal)))
            .ToList();
        Assert.Equal([("revoke", "secret://workspace/MAIL_KEY", true), ("rotate", "secret://workspace/MAIL_KEY", true)], denied);
    }

    [Fact]
    public async Task Repeated_identical_refused_writes_are_merged_per_actor_within_the_window()
    {
        using var factory = Networked();
        using var client = Client(factory);
        var owner = Owner(factory);
        await Send(client, "POST", "/api/secrets/workspace", new { name = "MAIL_KEY", value = NewValue() }, owner);
        var viewer = SignIn(factory, "team.viewer", StudioRoles.Viewer, ["*"]);

        for (var attempt = 0; attempt < 4; attempt++)
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await Send(client, "POST", "/api/secrets/workspace/MAIL_KEY/rotate", new { value = NewValue() }, viewer)).Status);
            _clock.Advance(TimeSpan.FromSeconds(10));
        }
        _clock.Advance(SecretAuditLog.RefusalMergeWindow);
        await Send(client, "POST", "/api/secrets/workspace", new { name = "OTHER_KEY", value = NewValue() }, owner);

        using var audit = JsonDocument.Parse((await Send(client, "GET", "/api/secrets/audit", null, owner)).Body);
        var refused = audit.RootElement.GetProperty("entries").EnumerateArray()
            .Where(entry => entry.GetProperty("actor").GetString() == "human:team.viewer")
            .Select(entry => (entry.GetProperty("action").GetString(),
                entry.TryGetProperty("repeated", out var repeated) && repeated.ValueKind == JsonValueKind.Number ? repeated.GetInt32() : (int?)null))
            .ToList();
        Assert.Equal([("rotate", 3), ("rotate", (int?)null)], refused);
    }

    [Fact]
    public async Task Overview_flags_an_item_that_fails_its_check_instead_of_failing_the_page()
    {
        using var factory = Networked();
        using var client = Client(factory);
        var owner = Owner(factory);
        await Send(client, "POST", "/api/secrets/workspace", new { name = "MAIL_KEY", value = NewValue() }, owner);
        await Send(client, "POST", "/api/secrets/workspace", new { name = "BROKEN", value = NewValue() }, owner);
        var metadata = Path.Combine(Workspace, ".security", "secrets", "items",
            BuiltInSecretStore.SecretId(SecretScope.Workspace, "BROKEN"), "metadata.json");
        File.WriteAllText(metadata, File.ReadAllText(metadata).Replace("\"mac\": \"", "\"mac\": \"AAAA", StringComparison.Ordinal));

        var overview = await Send(client, "GET", "/api/secrets", null, owner);

        Assert.Equal(HttpStatusCode.OK, overview.Status);
        using var json = JsonDocument.Parse(overview.Body);
        Assert.Equal("MAIL_KEY", Assert.Single(json.RootElement.GetProperty("secrets").EnumerateArray()).GetProperty("name").GetString());
        var flagged = Assert.Single(json.RootElement.GetProperty("unreadable").EnumerateArray());
        Assert.Equal("integrity-failed", flagged.GetProperty("problem").GetString());
        Assert.Equal("BROKEN", flagged.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Project_scoped_operator_writes_only_member_projects_and_sees_no_foreign_metadata()
    {
        using var factory = Networked();
        using var client = Client(factory);
        var owner = Owner(factory);
        await Send(client, "POST", "/api/projects/other/secrets", new { name = "FOREIGN", value = NewValue() }, owner);
        await Send(client, "POST", "/api/secrets/workspace", new { name = "SHARED", value = NewValue() }, owner);
        var operatorSession = SignIn(factory, "team.operator", StudioRoles.Operator, [Project]);

        Assert.Equal(HttpStatusCode.Created, (await Send(client, "POST", $"/api/projects/{Project}/environments/stage/secrets",
            new { name = "DATABASE_URL", value = NewValue() }, operatorSession)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(client, "POST", "/api/projects/other/secrets",
            new { name = "DATABASE_URL", value = NewValue() }, operatorSession)).Status);
        var workspaceWrite = await Send(client, "POST", "/api/secrets/workspace", new { name = "OWN", value = NewValue() }, operatorSession);
        Assert.Equal(HttpStatusCode.Forbidden, workspaceWrite.Status);
        var backendWrite = await Send(client, "PUT", "/api/secrets/backends", new
        {
            scope = Project, kind = "vault", url = "https://vault.example.test", mount = "secret", vaultId = (string?)null,
            credentialReference = "secret://workspace/VAULT_TOKEN", credentialEnvironmentVariable = (string?)null, ttlSeconds = 30
        }, operatorSession);
        Assert.Equal(HttpStatusCode.Forbidden, backendWrite.Status);
        Assert.Contains("project-scope-denied", workspaceWrite.Body, StringComparison.Ordinal);

        var overview = await Send(client, "GET", "/api/secrets", null, operatorSession);
        Assert.DoesNotContain("SHARED", overview.Body, StringComparison.Ordinal);
        Assert.Contains("DATABASE_URL", overview.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("FOREIGN", overview.Body, StringComparison.Ordinal);
        Assert.Contains("\"canWriteWorkspace\":false", overview.Body, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Send(client, "GET", "/api/secrets/workspace", null, operatorSession)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(client, "GET", "/api/secrets/audit?scope=other", null, operatorSession)).Status);
        var visibleAudit = (await Send(client, "GET", "/api/secrets/audit", null, operatorSession)).Body;
        Assert.DoesNotContain("secret://other/", visibleAudit, StringComparison.Ordinal);
        Assert.DoesNotContain("secret://workspace/SHARED", visibleAudit, StringComparison.Ordinal);

        // Both writes are refused by the access middleware, which audits them:
        // the workspace scope needs a workspace binding (AGT-3228), and the
        // middleware does not read the body, so a refused put names its scope only.
        using var audit = JsonDocument.Parse((await Send(client, "GET", "/api/secrets/audit", null, owner)).Body);
        var refused = audit.RootElement.GetProperty("entries").EnumerateArray()
            .Where(entry => entry.GetProperty("actor").GetString() == "human:team.operator"
                            && entry.GetProperty("outcome").GetString() != "ok")
            .Select(entry => (entry.GetProperty("action").GetString(), entry.GetProperty("reference").GetString(), entry.GetProperty("outcome").GetString()))
            .ToList();
        Assert.Equal(
        [
            ("put", "secret://workspace/", "denied:project-scope-denied"),
            ("put", "secret://other/", "denied:project-scope-denied"),
        ], refused);
    }

    [Fact]
    public async Task Deploy_host_resolves_only_through_its_claimed_job_and_every_attempt_is_audited()
    {
        using var factory = Networked();
        using var client = Client(factory);
        var owner = Owner(factory);
        var value = NewValue();
        await Send(client, "POST", $"/api/projects/{Project}/environments/stage/secrets", new { name = "DATABASE_URL", value }, owner);
        await Send(client, "POST", $"/api/projects/{Project}/environments/prod/secrets", new { name = "DATABASE_URL", value = NewValue() }, owner);
        var security = factory.Services.GetRequiredService<AccessSecurityStore>();
        var host = security.EnrollRunner(security.CreateEnrollment(new RunnerEnrollmentRequest("stage-host", [RunnerScopes.Deploy], null, null)).Code);
        var coder = security.EnrollRunner(security.CreateEnrollment(new RunnerEnrollmentRequest("coder", null, null, null)).Code);
        _leases.Jobs["job-1"] = new DeployJobLease("job-1", Project, "stage", host.Runner.Id, ["DATABASE_URL"]);
        var workload = new { kind = "deploy-job", project = Project, jobId = "job-1", leaseToken = "lease-token" };

        var resolved = await Send(client, "POST", SecretEndpoints.ResolvePath,
            new { reference = $"secret://{Project}/stage/DATABASE_URL", purpose = "deploy:stage", workload }, bearer: host.Secret);
        Assert.Equal(HttpStatusCode.OK, resolved.Status);
        Assert.Equal("no-store", resolved.CacheControl);
        using (var json = JsonDocument.Parse(resolved.Body))
        {
            Assert.Equal(value, json.RootElement.GetProperty("value").GetString());
            Assert.Equal($"secret://{Project}/stage/DATABASE_URL@1", json.RootElement.GetProperty("resolvedReference").GetString());
        }

        var foreignEnvironment = await Send(client, "POST", SecretEndpoints.ResolvePath,
            new { reference = $"secret://{Project}/prod/DATABASE_URL", purpose = "deploy:stage", workload }, bearer: host.Secret);
        Assert.Equal(HttpStatusCode.Forbidden, foreignEnvironment.Status);
        Assert.Contains("environment-scope-denied", foreignEnvironment.Body, StringComparison.Ordinal);
        var unknownJob = await Send(client, "POST", SecretEndpoints.ResolvePath,
            new { reference = $"secret://{Project}/stage/DATABASE_URL", purpose = "deploy:stage", workload = workload with { jobId = "job-2" } }, bearer: host.Secret);
        Assert.Contains("workload-lease-invalid", unknownJob.Body, StringComparison.Ordinal);
        var wrongScope = await Send(client, "POST", SecretEndpoints.ResolvePath,
            new { reference = $"secret://{Project}/stage/DATABASE_URL", purpose = "deploy:stage", workload }, bearer: coder.Secret);
        Assert.Contains("runner-scope-denied", wrongScope.Body, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(client, "POST", SecretEndpoints.ResolvePath,
            new { reference = $"secret://{Project}/stage/DATABASE_URL", purpose = "deploy:stage", workload })).Status);

        var audit = await Send(client, "GET", $"/api/secrets/audit?scope={Project}/stage", null, owner);
        Assert.DoesNotContain(value, audit.Body, StringComparison.Ordinal);
        using var entries = JsonDocument.Parse(audit.Body);
        var resolveEntry = entries.RootElement.GetProperty("entries").EnumerateArray()
            .Single(entry => entry.GetProperty("action").GetString() == "resolve" && entry.GetProperty("outcome").GetString() == "ok");
        Assert.Equal("workload:" + host.Runner.Id, resolveEntry.GetProperty("actor").GetString());
        Assert.Equal("deploy:stage", resolveEntry.GetProperty("purpose").GetString());
        using var all = JsonDocument.Parse((await Send(client, "GET", "/api/secrets/audit", null, owner)).Body);
        var outcomes = all.RootElement.GetProperty("entries").EnumerateArray()
            .Where(entry => entry.GetProperty("action").GetString() == "resolve")
            .Select(entry => (entry.GetProperty("outcome").GetString(), entry.GetProperty("reference").GetString())).ToList();
        Assert.Equal(
        [
            ("denied:runner-scope-denied", $"secret://{Project}/stage/DATABASE_URL"),
            ("denied:workload-lease-invalid", $"secret://{Project}/stage/DATABASE_URL"),
            ("denied:environment-scope-denied", $"secret://{Project}/prod/DATABASE_URL"),
            ("ok", $"secret://{Project}/stage/DATABASE_URL"),
        ], outcomes);
    }

    [Fact]
    public async Task Local_profile_scrubs_a_resolved_value_from_runner_logs_written_to_the_task_folder()
    {
        SeedTask();
        using var factory = Factory(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = Project,
            ["WatchPaths:0:Path"] = ProjectPath,
            ["WatchPaths:0:RootPath"] = ProjectPath,
            ["WatchPaths:0:RepositoryPath"] = ProjectPath,
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        // The local profile attributes every mutation to a registered client, as the browser does.
        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);
        var value = NewValue();
        var put = await client.PostAsJsonAsync($"/api/projects/{Project}/secrets", new { name = "API_TOKEN", value });
        Assert.Equal(HttpStatusCode.Created, put.StatusCode);
        Assert.DoesNotContain(value, await put.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains("human:local-operator", await put.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var security = factory.Services.GetRequiredService<AccessSecurityStore>();
        var runner = security.EnrollRunner(security.CreateEnrollment(new RunnerEnrollmentRequest("coder", null, null, null)).Code);
        _leases.Runs[TaskKey] = new TaskRunLease(TaskKey, Project, runner.Runner.Id, "lease-1");
        using var resolveRequest = new HttpRequestMessage(HttpMethod.Post, SecretEndpoints.ResolvePath)
        {
            Content = JsonContent.Create(new
            {
                reference = $"secret://{Project}/API_TOKEN", purpose = "task:build",
                workload = new { kind = "task-run", taskKey = TaskKey, leaseId = "lease-1" },
            }),
        };
        resolveRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", runner.Secret);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(resolveRequest)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(SecretEndpoints.ResolvePath,
            new { reference = $"secret://{Project}/API_TOKEN", purpose = "task:build", workload = new { kind = "task-run", taskKey = TaskKey, leaseId = "lease-1" } })).StatusCode);

        var logs = await client.PostAsJsonAsync("/api/runner/logs", new
        {
            taskKey = TaskKey,
            lines = new[] { new { timestamp = _clock.GetUtcNow().UtcDateTime, stream = "stdout", text = $"curl -H 'X-Token: {value}' https://api.example" } },
        });
        Assert.Equal(HttpStatusCode.OK, logs.StatusCode);

        var log = File.ReadAllText(Assert.Single(Directory.GetFiles(ProjectPath, "cli-output.log", SearchOption.AllDirectories)));
        Assert.DoesNotContain(value, log, StringComparison.Ordinal);
        Assert.Contains($"[secret:secret://{Project}/API_TOKEN]", log, StringComparison.Ordinal);
        Assert.DoesNotContain(Directory.EnumerateFiles(ProjectPath, "*", SearchOption.AllDirectories),
            file => File.ReadAllText(file).Contains(value, StringComparison.Ordinal));
    }

    private void SeedTask()
    {
        var taskPath = Path.Combine(ProjectPath, "3-progress", TaskKey);
        Directory.CreateDirectory(taskPath);
        Directory.CreateDirectory(Path.Combine(ProjectPath, "2-ready"));
        File.WriteAllText(Path.Combine(taskPath, "task.json"), JsonSerializer.Serialize(new
        {
            id = TaskKey, title = "Secret scrubbing fixture", state = "3-progress", order = 1, agent = "codex", kind = "task",
        }));
        File.WriteAllText(Path.Combine(taskPath, "prompt.md"), "Use the API token.");
        File.WriteAllText(Path.Combine(taskPath, "status.md"), "Result: pending.");
    }

    private WebApplicationFactory<Program> Networked() => Factory(new Dictionary<string, string?>
    {
        ["Security:Profile"] = "networked",
        ["Security:BootstrapCode"] = "website-setup-code",
        ["AllowedHosts"] = "studio.test",
    });

    private WebApplicationFactory<Program> Factory(Dictionary<string, string?> settings)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            settings["TaskRepository"] = Workspace;
            settings["Secrets:KeyFile"] = Path.Combine(_root, "keys", "secrets.kek");
            settings["Secrets:KeyEnvironmentVariable"] = "AGENT_STUDIO_TEST_UNSET_KEK_" + Guid.NewGuid().ToString("N");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(_clock);
                services.RemoveAll<ISecretWorkloadLeases>();
                services.AddSingleton<ISecretWorkloadLeases>(_leases);
            });
        });

    private static HttpClient Client(WebApplicationFactory<Program> factory)
        => factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://studio.test"), HandleCookies = false });

    private static Session Owner(WebApplicationFactory<Program> factory)
    {
        var created = factory.Services.GetRequiredService<AccessSecurityStore>()
            .Bootstrap(new BootstrapRequest("first.owner", "correct horse battery staple!", null));
        return new Session(created.SessionToken, created.CsrfToken);
    }

    private static Session SignIn(WebApplicationFactory<Program> factory, string username, string role, IReadOnlyList<string> projects)
    {
        var store = factory.Services.GetRequiredService<AccessSecurityStore>();
        var created = store.CreateUser(new CreateUserRequest(username, username, role, projects, null));
        var login = store.Login(username, created.TemporaryPassword, username + "|test");
        store.ChangePassword(store.AuthenticateSession(login.SessionToken)!, new ChangePasswordRequest(created.TemporaryPassword, "another long passphrase!"));
        return new Session(login.SessionToken, login.CsrfToken);
    }

    private static object Resolve(string reference) => new
    {
        reference, purpose = "deploy:stage", workload = new { kind = "deploy-job", project = Project, jobId = "job-1", leaseToken = "lease-token" },
    };

    private static async Task<Reply> Send(HttpClient client, string method, string path, object? body, Session? session = null, string? bearer = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (body is not null) request.Content = JsonContent.Create(body);
        if (session is not null)
        {
            request.Headers.Add("Cookie", $"{AccessSecurityStore.SessionCookieName}={session.Token}");
            request.Headers.Add("X-CSRF-Token", session.Csrf);
            request.Headers.Add("Sec-Fetch-Site", "same-origin");
        }
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using var response = await client.SendAsync(request);
        return new Reply(response.StatusCode, await response.Content.ReadAsStringAsync(), response.Headers.CacheControl?.ToString());
    }

    private sealed record Session(string Token, string Csrf);
    private sealed record Reply(HttpStatusCode Status, string Body, string? CacheControl);

    private sealed class FakeLeases : ISecretWorkloadLeases
    {
        public Dictionary<string, DeployJobLease> Jobs { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, TaskRunLease> Runs { get; } = new(StringComparer.Ordinal);

        public DeployJobLease? FindClaimedDeployJob(string project, string jobId, string leaseToken)
            => leaseToken == "lease-token" && Jobs.TryGetValue(jobId, out var job) && job.Project == project ? job : null;

        public TaskRunLease? FindLiveTaskRun(string taskKey) => Runs.GetValueOrDefault(taskKey);
    }
}
