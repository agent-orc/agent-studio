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
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
