using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentStudio.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ContributorAccessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "studio-product-role-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));

    public static TheoryData<string, string, string?, bool, bool> RouteMatrix => new()
    {
        { "GET", "/api/tasks", null, false, true },
        { "GET", "/api/projects/PROJ-1/chat/scroll", null, false, true },
        { "GET", "/api/projects/PROJ-1/visual-evidence", null, false, true },
        { "POST", "/api/tasks", null, false, true },
        { "PUT", "/api/tasks/card/title", TaskStates.Backlog, false, true },
        { "PUT", "/api/tasks/card/files/prompt.md", TaskStates.Ready, false, true },
        { "POST", "/api/tasks/card/preview-comments", TaskStates.HumanReview, false, true },
        { "POST", "/api/runner/PROJ-1/orchestrator-chat", null, false, true },
        { "PUT", "/api/tasks/card/release", TaskStates.HumanReview, true, true },
        { "PUT", "/api/tasks/card/release", TaskStates.HumanReview, false, false },
        { "PUT", "/api/tasks/card/title", TaskStates.Progress, false, false },
        { "POST", "/api/tasks/card/git/commit", TaskStates.Ready, false, false },
        { "GET", "/api/runner/PROJ-1/orchestrator-log", null, false, false },
        { "GET", "/api/tasks/card/logs/raw", null, false, false },
        { "GET", "/api/tasks/card/LOGS/raw", null, false, false },
        { "GET", "/api/tasks/card/Raw-Logs", null, false, false },
        { "HEAD", "/api/tasks/card/LoGs/raw", null, false, false },
        { "OPTIONS", "/api/tasks/card/RAW-LOGS", null, false, false },
        { "GET", "/api/projects/PROJ-1/SeCrEtS", null, false, false },
        { "GET", "/api/tasks/card/session-events", null, false, false },
        { "GET", "/api/tasks/card/Session-Events", null, false, false },
        { "GET", "/api/tasks/card/AGENT-WORK-DETAIL", null, false, false },
        { "GET", "/api/tasks/card/conversation", null, false, false },
        { "GET", "/api/tasks/card/Conversation", null, false, false },
        { "HEAD", "/api/tasks/card/CONVERSATION", null, false, false },
        { "GET", "/api/tasks/card/results/report.html", null, false, true },
        { "GET", "/api/tasks/card/results/screens/final.PNG", null, false, true },
        { "GET", "/api/tasks/card/results/notes.md", null, false, true },
        { "GET", "/api/tasks/card/results/run.log", null, false, false },
        { "GET", "/api/tasks/card/RESULTS/run.log", null, false, false },
        { "GET", "/API/TASKS/card/Results/run.log", null, false, false },
        { "GET", "/api/tasks/card/results/tests.trx", null, false, false },
        { "GET", "/api/tasks/card/results/events.jsonl", null, false, false },
        { "GET", "/api/tasks/card/results/report", null, false, false },
        { "GET", "/api/tasks/card/files/prompt.md", null, false, true },
        { "GET", "/api/tasks/card/files/status.md/history", null, false, true },
        { "GET", "/api/tasks/card/files/results/run.log", null, false, false },
        { "GET", "/api/tasks/card/files/results/report.html", null, false, true },
        { "GET", "/api/tasks/card/files/task.json", null, false, false },
        { "GET", "/api/tasks/card/checkout/credentials.json", null, false, false },
        { "GET", "/api/tasks/card/CHECKOUT/credentials.json", null, false, false },
        { "GET", "/api/projects/PROJ-1/CREDENTIALS", null, false, false },
        { "GET", "/api/projects/PROJ-1/settings", null, false, false },
        { "GET", "/api/projects/PROJ-1/autonomy", null, false, false },
        { "GET", "/api/v1/management/credential-view", null, false, false },
        { "POST", "/api/auth/invitations", null, false, false },
    };

    [Theory]
    [MemberData(nameof(RouteMatrix))]
    public void Contributor_route_matrix(string method, string path, string? state, bool productionApproval, bool allowed)
        => Assert.Equal(allowed, ContributorAccessPolicy.Allows(method, path, state, productionApproval));

    [Theory]
    [InlineData(TaskStates.Backlog, TaskStates.Ready, false, false, true)]
    [InlineData(TaskStates.HumanReview, TaskStates.Completed, false, false, true)]
    [InlineData(TaskStates.HumanReview, TaskStates.Completed, true, false, false)]
    [InlineData(TaskStates.Ready, TaskStates.Progress, false, false, false)]
    [InlineData(TaskStates.Progress, TaskStates.Completed, false, false, false)]
    public void Contributor_lane_matrix(string current, string target, bool operatorOverride, bool archiveOverride, bool allowed)
        => Assert.Equal(allowed, ContributorAccessPolicy.AllowsTransition(current, target, operatorOverride, archiveOverride));

    [Fact]
    public void Product_create_drops_run_controls_and_records_the_session_user()
    {
        var forged = new CreateTaskRequest
        {
            Title = "A product wish", Project = "PROJ-1", PromptMarkdown = "Make the header calmer.",
            TargetState = TaskStates.Ready, Model = "forged-model", CliType = CliTypes.Codex, ThinkingLevel = "max",
            ModelExplicit = true, ThinkingLevelExplicit = true, Mode = TaskModes.Research, AllowWebAccess = true,
            NoBranchExpected = true, RequiresIntegration = false, Fixture = true, CreatedBy = "forged-operator",
        };

        var product = ContributorAccessPolicy.ForProductCreate(forged, "usr_product");

        Assert.Equal(new CreateTaskRequest
        {
            Title = "A product wish", Project = "PROJ-1", PromptMarkdown = "Make the header calmer.",
            TargetState = TaskStates.Ready, CreatedBy = "usr_product",
        }, product);
    }

    [Fact]
    public async Task Product_session_over_http_is_denied_conversation_and_raw_results_and_creates_plain_wishes()
    {
        const string project = "product-project";
        const string taskKey = "AGT-PRODUCT-1";
        var projectPath = Path.Combine(_root, "projects", project);
        var taskPath = Path.Combine(projectPath, TaskStates.HumanReview, taskKey);
        foreach (var lane in new[] { TaskStates.Backlog, TaskStates.Ready, TaskStates.HumanReview })
            Directory.CreateDirectory(Path.Combine(projectPath, lane));
        Directory.CreateDirectory(Path.Combine(taskPath, "results", "screens"));
        Directory.CreateDirectory(Path.Combine(taskPath, "logs"));
        File.WriteAllText(Path.Combine(taskPath, "task.json"), JsonSerializer.Serialize(new
        {
            id = taskKey, title = "Delivered header", state = TaskStates.HumanReview, order = 1, agent = "codex", kind = "task",
        }));
        File.WriteAllText(Path.Combine(taskPath, "prompt.md"), "Calm the header.");
        File.WriteAllText(Path.Combine(taskPath, "logs", "cli-output.log"), "raw agent transcript");
        foreach (var name in new[] { "report.html", "notes.md", "screens/final.png", "run.log", "tests.trx", "events.jsonl" })
            File.WriteAllText(Path.Combine(taskPath, "results", name), "content of " + name);

        using var factory = new TestHostFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root,
                ["Security:Profile"] = "networked",
                ["Security:BootstrapCode"] = "product-setup-code",
                ["AllowedHosts"] = "studio.test",
                ["WatchPaths:0:Name"] = project,
                ["WatchPaths:0:Path"] = projectPath,
                ["WatchPaths:0:RootPath"] = projectPath,
                ["WatchPaths:0:RepositoryPath"] = projectPath,
            }));
            builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(_clock));
        });
        using var http = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://studio.test"), HandleCookies = false });
        var store = factory.Services.GetRequiredService<AccessSecurityStore>();
        var owner = store.Bootstrap(new BootstrapRequest("owner.account", "owner password phrase!", null));
        var invite = store.InviteUser(new InviteUserRequest("product.user", "Product User", StudioRoles.Contributor, [project]));
        var product = store.AcceptInvitation(new AcceptInvitationRequest(invite.Token, "new product password!"));

        foreach (var route in new[] { "conversation", "Conversation", "CONVERSATION" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(HttpMethod.Get, $"/api/tasks/{taskKey}/{route}", product.SessionToken));
        }
        Assert.NotEqual(HttpStatusCode.Forbidden, await StatusAsync(HttpMethod.Get, $"/api/tasks/{taskKey}/conversation", owner.SessionToken));

        foreach (var name in new[] { "report.html", "notes.md", "screens/final.png" })
        {
            using var response = await SendAsync(HttpMethod.Get, $"/api/tasks/{taskKey}/results/{name}", product.SessionToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("content of " + name, await response.Content.ReadAsStringAsync());
        }
        foreach (var route in new[] { "results/run.log", "results/RUN.LOG", "RESULTS/run.log", "results/tests.trx", "results/events.jsonl" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(HttpMethod.Get, $"/api/tasks/{taskKey}/{route}", product.SessionToken));
        }
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(HttpMethod.Get, $"/api/tasks/{taskKey}/results/run.log", owner.SessionToken));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(HttpMethod.Get, $"/api/tasks/{taskKey}/files/prompt.md", product.SessionToken));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(HttpMethod.Get, $"/api/tasks/{taskKey}/files/results/report.html", product.SessionToken));
        foreach (var route in new[]
        {
            "files/results/run.log", "FILES/results/run.log", "files/logs/cli-output.log",
            "files/task.json", "checkout/credentials.json", "CHECKOUT/credentials.json",
            "files/prompt.md?scope=code",
        })
            Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(HttpMethod.Get, $"/api/tasks/{taskKey}/{route}", product.SessionToken));

        using var decision = await SendAsync(HttpMethod.Post, "/api/tasks", product.SessionToken, product.CsrfToken, new
        {
            title = "Hidden preparation decision", watchPath = projectPath, kind = TaskKinds.Decision,
            promptMarkdown = "Choose a color.",
        });
        Assert.Equal(HttpStatusCode.Forbidden, decision.StatusCode);

        var projects = factory.Services.GetRequiredService<ProjectRegistry>();
        var destinationPath = Path.Combine(_root, "projects", "unassigned-project");
        Directory.CreateDirectory(destinationPath);
        var destination = projects.EnsureProjectForStorage(destinationPath, "Unassigned project", DefaultWorkspace.Id);
        projects.UpsertOwnershipMapping(destination.Id, new ComponentOwnershipMapping
        {
            Id = "unassigned-component", Component = "Unassigned component",
            ObservedSurfaces = ["Unassigned surface"], PrimaryProjectId = destination.Id,
            AllowedTicketPrefix = destination.ShortCode, Confidence = 1,
        });
        using var crossProject = await SendAsync(HttpMethod.Post, "/api/tasks", product.SessionToken, product.CsrfToken, new
        {
            title = "Cross-project wish", watchPath = projectPath,
            routing = new { observedSurface = "Unassigned surface", component = "Unassigned component" },
        });
        Assert.Equal(HttpStatusCode.Forbidden, crossProject.StatusCode);
        Assert.DoesNotContain(Directory.EnumerateFiles(destinationPath, "task.json", SearchOption.AllDirectories),
            file => File.ReadAllText(file).Contains("Cross-project wish", StringComparison.Ordinal));

        using var created = await SendAsync(HttpMethod.Post, "/api/tasks", product.SessionToken, product.CsrfToken, new
        {
            title = "A product wish", watchPath = projectPath, promptMarkdown = "Make the header calmer.", targetState = TaskStates.Backlog,
            model = "forged-model", cliType = CliTypes.Gemini, thinkingLevel = "max", modelExplicit = true,
            thinkingLevelExplicit = true, mode = TaskModes.Research, allowWebAccess = true, noBranchExpected = true,
            requiresIntegration = false, fixture = true, createdBy = "forged-operator",
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var jobId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var job = factory.Services.GetRequiredService<TaskScannerService>().FindJob(jobId);
        Assert.NotNull(job);
        var stored = JsonNode.Parse(File.ReadAllText(Path.Combine(job.FolderPath, "task.json")))!.AsObject();
        Assert.Equal(product.User.Id, (string?)stored["createdBy"]);
        Assert.Equal(TaskModes.Coding, (string?)stored["mode"]);
        Assert.False((bool?)stored["allowWebAccess"]);
        Assert.False((bool?)stored["modelExplicit"]);
        Assert.False((bool?)stored["thinkingLevelExplicit"]);
        Assert.NotEqual("forged-model", (string?)stored["model"]);
        Assert.NotEqual(CliTypes.Gemini, (string?)stored["cliType"]);
        Assert.NotEqual("max", (string?)stored["thinkingLevel"]);
        foreach (var field in new[] { "noBranchExpected", "requiresIntegration", "fixture" })
            Assert.False(stored.ContainsKey(field), field);

        async Task<HttpStatusCode> StatusAsync(HttpMethod method, string path, string session)
        {
            using var response = await SendAsync(method, path, session);
            return response.StatusCode;
        }

        Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string session, string? csrf = null, object? body = null)
        {
            var request = new HttpRequestMessage(method, path);
            request.Headers.Add("Cookie", $"{AccessSecurityStore.SessionCookieName}={session}");
            if (csrf is not null) request.Headers.Add("X-CSRF-Token", csrf);
            if (body is not null) request.Content = JsonContent.Create(body);
            return http.SendAsync(request);
        }
    }

    [Fact]
    public void Invitation_is_hashed_single_use_and_expires_with_injected_clock()
    {
        var store = NewStore();
        store.Bootstrap(new BootstrapRequest("owner.account", "owner password phrase!", null));
        var invite = store.InviteUser(new InviteUserRequest("product.user", "Product User", StudioRoles.Contributor, ["PROJ-1"]));
        var persisted = File.ReadAllText(Path.Combine(store.SecurityFolder, "access-state.json"));
        Assert.DoesNotContain(invite.Token, persisted);
        var accepted = store.AcceptInvitation(new AcceptInvitationRequest(invite.Token, "new product password!"));
        Assert.Equal(StudioRoles.Contributor, accepted.User.Role);
        Assert.NotNull(store.AuthenticateSession(accepted.SessionToken));
        Assert.Throws<SecurityOperationException>(() => store.AcceptInvitation(new AcceptInvitationRequest(invite.Token, "another password phrase!")));

        var expired = store.InviteUser(new InviteUserRequest("expired.user", "Expired", StudioRoles.Contributor, ["PROJ-1"]));
        _clock.Advance(TimeSpan.FromHours(25));
        Assert.Throws<SecurityOperationException>(() => store.AcceptInvitation(new AcceptInvitationRequest(expired.Token, "new expired password!")));
    }

    [Fact]
    public async Task Authenticated_contributor_identity_overrides_attribution_header()
    {
        var store = NewStore();
        store.Bootstrap(new BootstrapRequest("owner.account", "owner password phrase!", null));
        var invite = store.InviteUser(new InviteUserRequest("product.user", "Product User", StudioRoles.Contributor, ["PROJ-1"]));
        var accepted = store.AcceptInvitation(new AcceptInvitationRequest(invite.Token, "new product password!"));
        var called = false;
        var middleware = new AccessSecurityMiddleware(_ => { called = true; return Task.CompletedTask; },
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Security:Profile"] = "networked" }).Build(), store);
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Method = "POST";
        context.Request.Path = "/api/tasks";
        context.Request.Headers.Cookie = new StringValues($"{AccessSecurityStore.SessionCookieName}={accepted.SessionToken}");
        context.Request.Headers["X-CSRF-Token"] = accepted.CsrfToken;
        context.Request.Headers["X-Client-Id"] = "forged-operator";
        await middleware.InvokeAsync(context);
        Assert.True(called);
        Assert.Equal(accepted.User.Id, context.Items["ClientId"]);
    }

    private AccessSecurityStore NewStore() => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["Security:Profile"] = "networked"
        }).Build(), NullLogger<AccessSecurityStore>.Instance, _clock);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
