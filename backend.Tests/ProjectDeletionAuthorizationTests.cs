using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Configuration;
using AgentStudio.Registry;
using AgentStudio.Runner;
using AgentStudio.Security;
using AgentStudio.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ProjectDeletionAuthorizationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "project-delete-auth-" + Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _http;
    private readonly ProjectRegistry _projects;
    private readonly AccessSecurityStore _security;
    private readonly string _ownerSession;
    private readonly string _ownerCsrf;
    private readonly string _ownerId;
    private readonly string _storage;
    private readonly string _prompt;
    private readonly string _localSettings;
    private readonly ProjectRecord _project;

    public ProjectDeletionAuthorizationTests()
    {
        _storage = Path.Combine(_root, "projects", "deletion", "tasks");
        var job = Path.Combine(_storage, "2-ready", "delete-proof");
        Directory.CreateDirectory(job);
        _prompt = Path.Combine(job, "prompt.md");
        File.WriteAllText(_prompt, "Keep this task until deletion is authorized and confirmed.");
        File.WriteAllText(Path.Combine(job, "task.json"), """{"id":"delete-proof","key":"DEL-1","title":"Delete proof","state":"2-ready"}""");
        _localSettings = Path.Combine(_root, "appsettings.Local.json");
        File.WriteAllText(_localSettings, JsonSerializer.Serialize(new
        {
            WatchPaths = new[] { new { Name = "Deletion", Path = _storage } }
        }));
        _factory = new TestHostFactory().WithWebHostBuilder(host =>
        {
            host.UseEnvironment("Test");
            host.ConfigureAppConfiguration((_, config) => config
                .AddJsonFile(_localSettings, optional: false, reloadOnChange: false)
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TaskRepository"] = _root,
                    ["Security:Profile"] = SecurityProfiles.Networked,
                    ["AllowedHosts"] = "studio.test",
                    ["Logging:BackendFile:LogDirectory"] = Path.Combine(_root, "logs"),
                }));
            host.ConfigureTestServices(services =>
            {
                TestHostFactory.RemoveBackgroundHostedServices(services);
                services.AddSingleton(provider => new WorkspaceManagementService(
                    provider.GetRequiredService<IConfiguration>(), new DeletionEnvironment(_root),
                    provider.GetRequiredService<TaskScannerService>(),
                    provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<WorkspaceManagementService>>()));
            });
        });
        _projects = _factory.Services.GetRequiredService<ProjectRegistry>();
        _project = _projects.EnsureProjectForStorage(_storage, "Deletion", "default");
        _security = _factory.Services.GetRequiredService<AccessSecurityStore>();
        var owner = _security.Bootstrap(new BootstrapRequest("deletion.owner", "correct horse battery staple!", null));
        (_ownerSession, _ownerCsrf, _ownerId) = (owner.SessionToken, owner.CsrfToken, owner.User.Id);
        _http = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://studio.test"), HandleCookies = false
        });
    }

    [Fact]
    public async Task Project_admin_settings_authority_cannot_delete_populated_project()
    {
        var invitation = _security.InviteUser(new InviteUserRequest("deletion.admin", "Project admin",
            StudioRoles.Operator, [_project.Id]));
        var admin = _security.AcceptInvitation(new AcceptInvitationRequest(invitation.Token, "project admin password phrase!"));
        using var settingsUpdate = await Send(HttpMethod.Put, new { color = "#123456" }, admin.SessionToken, admin.CsrfToken);
        Assert.Equal(HttpStatusCode.OK, settingsUpdate.StatusCode);
        var before = Snapshot();
        using var refused = await Send(HttpMethod.Delete, Confirmation(), admin.SessionToken, admin.CsrfToken);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        AssertIntact(before);

        var endpoints = _factory.Services.GetServices<EndpointDataSource>().SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().Where(endpoint => RoutePermissionCatalog.Normalize(endpoint.RoutePattern) == "/api/projects/{}");
        var deletion = Assert.Single(endpoints.Where(endpoint => RoutePermissionCatalog.Methods(endpoint).Contains("DELETE")));
        var settings = Assert.Single(endpoints.Where(endpoint => RoutePermissionCatalog.Methods(endpoint).Contains("PUT")));
        Assert.Equal(IamPermissions.WorkspaceAdminister, RoutePermissionCatalog.Resolve(deletion, "DELETE")?.Permission);
        Assert.Equal(IamPermissions.ProjectSettingsWrite, RoutePermissionCatalog.Resolve(settings, "PUT")?.Permission);
    }

    [Theory]
    [InlineData(null, "retire project")]
    [InlineData("PROJ-999999", "retire project")]
    [InlineData("", "retire project")]
    [InlineData("correct", null)]
    [InlineData("correct", "  ")]
    public async Task Owner_missing_or_wrong_confirmation_or_reason_preserves_storage(string? confirmation, string? reason)
    {
        var before = Snapshot();
        using var response = await Send(HttpMethod.Delete, new
        {
            confirmation = confirmation == "correct" ? _project.Id : confirmation,
            reason
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertIntact(before);
    }

    [Fact]
    public async Task Owner_without_body_preserves_storage()
    {
        var before = Snapshot();
        using var response = await Send(HttpMethod.Delete, null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertIntact(before);
    }

    [Theory]
    [InlineData("3-progress")]
    [InlineData("4-auto-review")]
    public async Task Undrained_work_blocks_deletion_without_a_live_lease(string state)
    {
        var taskFile = Path.Combine(Path.GetDirectoryName(_prompt)!, "task.json");
        File.WriteAllText(taskFile, JsonSerializer.Serialize(new
        {
            id = "divergent-id-must-stay-unchanged", key = "DEL-1", title = "Delete proof", state
        }));
        var before = Snapshot();
        using var response = await Send(HttpMethod.Delete, Confirmation());
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AssertIntact(before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Active_run_blocks_deletion_for_canonical_and_legacy_task_keys(bool compositeKey)
    {
        var taskKey = compositeKey ? TaskIdentity.CreateKey(_storage, "delete-proof") : "DEL-1";
        var lease = _factory.Services.GetRequiredService<RunLeaseService>().TryAcquire(
            new RunLeaseAcquireRequest(taskKey, "delete-runner", "Delete runner", "host", 1, "test"));
        Assert.True(lease.Granted);
        var before = Snapshot();
        using var response = await Send(HttpMethod.Delete, Confirmation());
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("project-delete-active-work", await response.Content.ReadAsStringAsync());
        AssertIntact(before);
        Assert.Equal("Held", _factory.Services.GetRequiredService<RunLeaseService>().Peek(taskKey).Outcome);
    }

    [Fact]
    public async Task Confirmed_owner_deletion_removes_only_target_and_audits_actor_and_reason()
    {
        var other = Path.Combine(_root, "projects", "other");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "keep.txt"), "keep");
        var sibling = _projects.EnsureProjectForStorage(other, "Other", "default");
        using var response = await Send(HttpMethod.Delete, Confirmation());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(_projects.FindById(_project.Id));
        Assert.False(Directory.Exists(_storage));
        Assert.NotNull(_projects.FindById(sibling.Id));
        Assert.True(File.Exists(Path.Combine(other, "keep.txt")));
        Assert.Empty(JsonDocument.Parse(File.ReadAllText(_localSettings)).RootElement.GetProperty("WatchPaths").EnumerateArray());
        var entries = File.ReadLines(_factory.Services.GetRequiredService<ControlPlaneAuditLog>().FilePath)
            .Select(ControlPlaneAuditLog.Parse).Where(entry => entry?.Operation == "project.delete").ToList();
        Assert.Contains(entries, entry => entry!.Actor == _ownerId && entry.Target == _project.Id
            && entry.Outcome == "requested" && entry.Details?.GetProperty("reason").GetString() == "retire test project");
        Assert.Contains(entries, entry => entry!.Actor == _ownerId && entry.Outcome == "deleted");
    }

    private object Confirmation() => new { confirmation = _project.Id, reason = "retire test project" };

    private async Task<HttpResponseMessage> Send(HttpMethod method, object? body, string? session = null, string? csrf = null)
    {
        using var request = new HttpRequestMessage(method, $"/api/projects/{_project.Id}");
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Add("Cookie", $"{AccessSecurityStore.SessionCookieName}={session ?? _ownerSession}");
        request.Headers.Add("X-CSRF-Token", csrf ?? _ownerCsrf);
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
        request.Headers.Add("Origin", "https://studio.test");
        return await _http.SendAsync(request);
    }

    private Dictionary<string, string> Snapshot() => Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
        .Where(path => path.StartsWith(_storage, StringComparison.Ordinal) || path == RegistryPaths.ProjectsFilePath(_root) || path == _localSettings)
        .ToDictionary(path => path, File.ReadAllText);

    private void AssertIntact(Dictionary<string, string> before)
    {
        Assert.NotNull(_projects.FindById(_project.Id));
        Assert.True(File.Exists(_prompt));
        Assert.Equal(before.OrderBy(pair => pair.Key), Snapshot().OrderBy(pair => pair.Key));
    }

    public void Dispose()
    {
        _http.Dispose();
        _factory.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class DeletionEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "ProjectDeletionAuthorizationTests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
