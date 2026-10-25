using System.Net;
using System.Net.Http.Json;
using AgentStudio.Projects;
using AgentStudio.Registry;
using AgentStudio.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ProjectExecutionSecurityEndpointTests : IDisposable
{
    private const string ProjectName = "security-placement";
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "studio-placement-security-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Networked_project_admin_cannot_disable_remote_execution_but_owner_can()
    {
        var watchPath = Path.Combine(_root, "projects", ProjectName);
        Directory.CreateDirectory(watchPath);
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseEnvironment("Test");
            host.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["TaskRepository"] = _root,
                    ["Security:Profile"] = "networked",
                    ["AllowedHosts"] = "studio.test",
                    ["WatchPaths:0:Name"] = ProjectName,
                    ["WatchPaths:0:Path"] = watchPath,
                    ["WatchPaths:0:RootPath"] = watchPath,
                }));
        });
        var store = factory.Services.GetRequiredService<AccessSecurityStore>();
        var owner = store.Bootstrap(new BootstrapRequest("first.owner", "correct horse battery staple!", null));
        var registry = factory.Services.GetRequiredService<ProjectRegistry>();
        var project = registry.EnsureProjectForStorage(watchPath, ProjectName, "default");
        var invitation = store.InviteUser(new InviteUserRequest("project.admin", "Project Admin",
            StudioRoles.Operator, [project.Id]));
        var admin = store.AcceptInvitation(new AcceptInvitationRequest(
            invitation.Token, "new project admin password!"));
        var settings = factory.Services.GetRequiredService<ProjectSettingsService>();
        settings.SetExecutionSettings(ProjectName, "manual", "runner-a", true);
        using var http = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://studio.test"), HandleCookies = false
        });

        async Task<(HttpStatusCode Status, string Body)> DisableAsync(string session, string csrf)
        {
            using var request = new HttpRequestMessage(HttpMethod.Put,
                $"/api/projects/{ProjectName}/execution-runner")
            {
                Content = JsonContent.Create(new { executionLocation = "local", remoteExecutionEnabled = false })
            };
            request.Headers.Add("Cookie", $"{AccessSecurityStore.SessionCookieName}={session}");
            request.Headers.Add("X-CSRF-Token", csrf);
            request.Headers.Add("Sec-Fetch-Site", "same-origin");
            request.Headers.Add("Origin", "https://studio.test");
            using var response = await http.SendAsync(request);
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        var refused = await DisableAsync(admin.SessionToken, admin.CsrfToken);
        Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
        Assert.Contains("owner-required", refused.Body);
        Assert.Equal("runner-a", ProjectExecutionPolicy.ResolveExecutionLocation(settings.Get(ProjectName)));
        Assert.Equal(HttpStatusCode.OK, (await DisableAsync(owner.SessionToken, owner.CsrfToken)).Status);
        Assert.Equal("local", ProjectExecutionPolicy.ResolveExecutionLocation(settings.Get(ProjectName)));

        async Task<(HttpStatusCode Status, string Body)> UpdateRegistryAsync(
            string session, string csrf, object body)
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/projects/{project.Id}")
            {
                Content = JsonContent.Create(body)
            };
            request.Headers.Add("Cookie", $"{AccessSecurityStore.SessionCookieName}={session}");
            request.Headers.Add("X-CSRF-Token", csrf);
            request.Headers.Add("Sec-Fetch-Site", "same-origin");
            request.Headers.Add("Origin", "https://studio.test");
            using var response = await http.SendAsync(request);
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        foreach (var body in new object[]
                 {
                     new { clearExecutionRunner = true },
                     new { executionRunner = "local" }
                 })
        {
            settings.SetExecutionSettings(ProjectName, "manual", "runner-a", true);
            var rejected = await UpdateRegistryAsync(admin.SessionToken, admin.CsrfToken, body);
            Assert.Equal(HttpStatusCode.Forbidden, rejected.Status);
            Assert.Contains("owner-required", rejected.Body);
            Assert.Equal("runner-a", ProjectExecutionPolicy.ResolveExecutionLocation(settings.Get(ProjectName)));
            var accepted = await UpdateRegistryAsync(owner.SessionToken, owner.CsrfToken, body);
            Assert.Equal(HttpStatusCode.OK, accepted.Status);
            Assert.Equal("local", ProjectExecutionPolicy.ResolveExecutionLocation(settings.Get(ProjectName)));
        }

        var workspaceId = factory.Services.GetRequiredService<WorkspaceRegistry>().List().First().Id;
        async Task<HttpStatusCode> CreateAsync(string session, string csrf, string name,
            string? executionRunner = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/projects")
            {
                Content = JsonContent.Create(new
                {
                    displayName = name,
                    workspaceId,
                    executionRunner,
                })
            };
            request.Headers.Add("Cookie", $"{AccessSecurityStore.SessionCookieName}={session}");
            request.Headers.Add("X-CSRF-Token", csrf);
            request.Headers.Add("Sec-Fetch-Site", "same-origin");
            request.Headers.Add("Origin", "https://studio.test");
            using var response = await http.SendAsync(request);
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.Forbidden, await CreateAsync(admin.SessionToken,
            admin.CsrfToken, "Explicit Local", "local"));
        Assert.Equal(HttpStatusCode.Created, await CreateAsync(owner.SessionToken,
            owner.CsrfToken, "Paused Project"));
        Assert.Equal(PickupModes.Paused, ProjectExecutionPolicy.ResolvePickupMode(
            settings.Get("Paused Project")));
        Assert.Equal(ExecutionLocations.Local, ProjectExecutionPolicy.ResolveExecutionLocation(
            settings.Get("Paused Project")));

        using var probe = new HttpRequestMessage(HttpMethod.Get, "/api/cli/_probe/claude?cmd=hello");
        probe.Headers.Add("Cookie", $"{AccessSecurityStore.SessionCookieName}={owner.SessionToken}");
        using var probeResponse = await http.SendAsync(probe);
        Assert.Equal(HttpStatusCode.NotFound, probeResponse.StatusCode);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
