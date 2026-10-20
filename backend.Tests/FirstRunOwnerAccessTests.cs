using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Diagnostics;
using AgentStudio.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3224: the local first-run routes change Git access of the installation.
/// In the networked profile they require the Owner role, matching the Task
/// Server runner-git-setups rules. The loopback profile is unchanged.
/// </summary>
public sealed class FirstRunOwnerAccessTests : IDisposable
{
    private const string Password = "correct horse battery staple!";
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "studio-first-run-owner-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));

    /// <summary>Every first-run route, with the status its handler gives an owner for a missing target.</summary>
    private static readonly (string Method, string Pattern, string Path, HttpStatusCode OwnerStatus)[] Routes =
    [
        ("POST", "/api/first-run/git-setups", "/api/first-run/git-setups", HttpStatusCode.BadRequest),
        ("GET", "/api/first-run/git-setups/{id}", "/api/first-run/git-setups/local-git-missing", HttpStatusCode.NotFound),
        ("POST", "/api/first-run/git-setups/{id}/verify", "/api/first-run/git-setups/local-git-missing/verify", HttpStatusCode.NotFound),
        ("GET", "/api/first-run/projects/{projectId}/repository", "/api/first-run/projects/missing-project/repository", HttpStatusCode.NotFound),
    ];

    [Fact]
    public void Local_first_run_routes_declare_the_owner_requirement()
    {
        using var factory = BuildFactory(SecurityProfiles.Local);
        var endpoints = factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints).ToArray();
        var firstRun = endpoints.OfType<RouteEndpoint>()
            .Where(route => AccessSecurityRouteInventory.RequiresOwner(route.RoutePattern.RawText ?? string.Empty))
            .ToArray();

        foreach (var (method, pattern, _, _) in Routes)
            Assert.Single(firstRun, route => route.RoutePattern.RawText?.TrimEnd('/') == pattern
                && route.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(method) == true);
        Assert.Equal(Routes.Length, firstRun.Length);
        Assert.Empty(AccessSecurityRouteInventory.MissingOwnerRequirement(endpoints));
    }

    [Fact]
    public void Route_inventory_flags_a_first_run_route_without_the_owner_requirement()
    {
        var app = WebApplication.CreateBuilder().Build();
        app.MapGet("/api/first-run/undeclared", () => Results.Ok());
        app.MapPost("/api/first-run/operator-step", () => Results.Ok()).RequireStudioRole(StudioRoles.Operator);
        app.MapPost("/api/first-run/owner-step", () => Results.Ok()).RequireStudioRole(StudioRoles.Owner);
        app.MapGet("/api/projects", () => Results.Ok());
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints);

        Assert.Equal(
            ["/api/first-run/undeclared", "/api/first-run/operator-step"],
            AccessSecurityRouteInventory.MissingOwnerRequirement(endpoints));
    }

    [Theory]
    [InlineData(StudioRoles.Owner, StudioRoles.Owner, true)]
    [InlineData(StudioRoles.Owner, StudioRoles.Operator, false)]
    [InlineData(StudioRoles.Owner, StudioRoles.Contributor, false)]
    [InlineData(StudioRoles.Owner, StudioRoles.ContributorDisplayName, false)]
    [InlineData(StudioRoles.Owner, StudioRoles.Viewer, false)]
    [InlineData(StudioRoles.Operator, StudioRoles.Owner, true)]
    [InlineData(StudioRoles.Operator, StudioRoles.Operator, true)]
    [InlineData(StudioRoles.Operator, StudioRoles.Viewer, false)]
    [InlineData(StudioRoles.Owner, "unknown", false)]
    public void Role_requirement_admits_the_minimum_role_and_above(string minimum, string role, bool admitted)
        => Assert.Equal(admitted, new StudioRoleRequirement(minimum).IsSatisfiedBy(role));

    [Fact]
    public async Task Networked_monolith_does_not_map_first_run_backend_routes()
    {
        using var factory = BuildFactory(SecurityProfiles.Networked);
        var owner = factory.Services.GetRequiredService<AccessSecurityStore>()
            .Bootstrap(new BootstrapRequest("first.owner", Password, "Owner"));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://studio.test"), HandleCookies = false });

        foreach (var (method, _, path, ownerStatus) in Routes)
        {
            using var request = NewRequest(method, path);
            Session(request, owner.SessionToken, owner.CsrfToken);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Fact]
    public async Task Networked_project_repository_url_changes_require_owner()
    {
        using var factory = BuildFactory(SecurityProfiles.Networked);
        var store = factory.Services.GetRequiredService<AccessSecurityStore>();
        var owner = store.Bootstrap(new BootstrapRequest("first.owner", Password, "Owner"));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://studio.test"), HandleCookies = false });

        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/projects")
        {
            Content = JsonContent.Create(new { displayName = "Repository Guard", workspaceId = "ws-default" }),
        };
        Session(create, owner.SessionToken, owner.CsrfToken);
        using var created = await client.SendAsync(create);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var project = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var previousRepositoryUrl = factory.Services.GetRequiredService<AgentStudio.Registry.ProjectRegistry>()
            .FindById(project)!.Urls.FirstOrDefault(url => url.Id == "repo")?.Url;
        var invitation = store.InviteUser(new InviteUserRequest("site.operator", "Operator",
            StudioRoles.Operator, [ProjectAccessAuthorization.AllProjectsGrant]));
        var contributorInvitation = store.InviteUser(new InviteUserRequest("product.contributor", "Product",
            StudioRoles.Contributor, [project]));
        var operatorSession = store.AcceptInvitation(new AcceptInvitationRequest(invitation.Token, Password));
        var contributor = store.AcceptInvitation(new AcceptInvitationRequest(contributorInvitation.Token, Password));

        using var rejected = new HttpRequestMessage(HttpMethod.Put, $"/api/projects/{project}")
        {
            Content = JsonContent.Create(new { repositoryUrl = "https://github.com/example/repo.git" }),
        };
        Session(rejected, operatorSession.SessionToken, operatorSession.CsrfToken);
        using var rejectedResponse = await client.SendAsync(rejected);
        Assert.Equal(HttpStatusCode.Forbidden, rejectedResponse.StatusCode);
        Assert.Equal("owner-required", (await rejectedResponse.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetString());
        Assert.Equal(previousRepositoryUrl, factory.Services.GetRequiredService<AgentStudio.Registry.ProjectRegistry>()
            .FindById(project)!.Urls.FirstOrDefault(url => url.Id == "repo")?.Url);

        using var accepted = new HttpRequestMessage(HttpMethod.Put, $"/api/projects/{project}")
        {
            Content = JsonContent.Create(new { repositoryUrl = "https://github.com/example/repo.git" }),
        };
        Session(accepted, owner.SessionToken, owner.CsrfToken);
        using var acceptedResponse = await client.SendAsync(accepted);
        Assert.Equal(HttpStatusCode.OK, acceptedResponse.StatusCode);
        Assert.Equal("https://github.com/example/repo.git", factory.Services
            .GetRequiredService<AgentStudio.Registry.ProjectRegistry>().FindById(project)!
            .Urls.First(url => url.Id == "repo").Url);
        var auditPath = Path.Combine(_workspace, ".security", "project-repository-audit.jsonl");
        var audit = JsonSerializer.Deserialize<AgentStudio.Security.ProjectRepositoryAuditEvent>(
            File.ReadAllText(auditPath).Trim())!;
        Assert.Equal(project, audit.ProjectId);
        Assert.Equal(owner.User.Id, audit.ActorId);
        Assert.Equal(owner.User.Username, audit.Actor);
        Assert.Equal(previousRepositoryUrl ?? string.Empty, audit.PreviousRepositoryUrl);
        Assert.Equal("https://github.com/example/repo.git", audit.RepositoryUrl);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, audit.Timestamp);
    }

    [Fact]
    public async Task Local_profile_first_run_routes_keep_the_loopback_behaviour()
    {
        using var factory = BuildFactory(SecurityProfiles.Local);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");

        foreach (var (method, _, path, ownerStatus) in Routes)
        {
            using var request = NewRequest(method, path);
            using var response = await client.SendAsync(request);
            Assert.True(ownerStatus == response.StatusCode, $"{method} {path}: got {(int)response.StatusCode}");
        }
    }

    private static (StudioUser User, string SessionToken, string CsrfToken) Invite(
        AccessSecurityStore store, string username, string role)
    {
        var invitation = store.InviteUser(new InviteUserRequest(username, username, role, [ProjectAccessAuthorization.AllProjectsGrant]));
        return store.AcceptInvitation(new AcceptInvitationRequest(invitation.Token, Password));
    }

    private static HttpRequestMessage NewRequest(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
            request.Content = JsonContent.Create(new { projectId = "missing-project", method = "deploy-key" });
        return request;
    }

    private static void Session(HttpRequestMessage request, string session, string csrf)
    {
        request.Headers.Add("Cookie", $"{AccessSecurityStore.SessionCookieName}={session}");
        request.Headers.Add("X-CSRF-Token", csrf);
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
    }

    private static void Bearer(HttpRequestMessage request, string secret)
        => request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);

    private WebApplicationFactory<Program> BuildFactory(string profile) => new TestHostFactory().WithWebHostBuilder(builder =>
    {
        builder.UseEnvironment("Test");
        builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(_clock));
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _workspace,
            ["Security:Profile"] = profile,
            ["Security:BootstrapCode"] = "website-setup-code",
            ["AllowedHosts"] = profile == SecurityProfiles.Networked ? "studio.test" : "*",
        }));
    });

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true);
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "FirstRunOwnerAccessTests: fixture cleanup is best-effort");
        }
    }
}
