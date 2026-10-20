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
    public void Every_first_run_route_declares_the_owner_requirement()
    {
        using var factory = BuildFactory(SecurityProfiles.Networked);
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
    [InlineData(StudioRoles.Owner, StudioRoles.Viewer, false)]
    [InlineData(StudioRoles.Operator, StudioRoles.Owner, true)]
    [InlineData(StudioRoles.Operator, StudioRoles.Operator, true)]
    [InlineData(StudioRoles.Operator, StudioRoles.Viewer, false)]
    [InlineData(StudioRoles.Owner, "unknown", false)]
    public void Role_requirement_admits_the_minimum_role_and_above(string minimum, string role, bool admitted)
        => Assert.Equal(admitted, new StudioRoleRequirement(minimum).IsSatisfiedBy(role));

    [Fact]
    public async Task Networked_first_run_routes_admit_only_the_owner()
    {
        using var factory = BuildFactory(SecurityProfiles.Networked);
        var store = factory.Services.GetRequiredService<AccessSecurityStore>();
        var owner = store.Bootstrap(new BootstrapRequest("first.owner", Password, "Owner"));
        var operatorSession = Invite(store, "site.operator", StudioRoles.Operator);
        var viewerSession = Invite(store, "site.viewer", StudioRoles.Viewer);
        var expires = _clock.GetUtcNow().UtcDateTime.AddDays(1);
        var operatorToken = store.CreateApiToken(new CreateApiTokenRequest("automation", StudioRoles.Operator,
            [ProjectAccessAuthorization.AllProjectsGrant], expires), owner.User.Id);
        var viewerToken = store.CreateApiToken(new CreateApiTokenRequest("reader", StudioRoles.Viewer,
            [ProjectAccessAuthorization.AllProjectsGrant], expires), owner.User.Id);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://studio.test"), HandleCookies = false });

        var callers = new (string Name, Action<HttpRequestMessage> Authenticate, HttpStatusCode? Status, string? Error)[]
        {
            ("owner", request => Session(request, owner.SessionToken, owner.CsrfToken), null, null),
            ("operator", request => Session(request, operatorSession.SessionToken, operatorSession.CsrfToken),
                HttpStatusCode.Forbidden, "owner-required"),
            ("viewer", request => Session(request, viewerSession.SessionToken, viewerSession.CsrfToken),
                HttpStatusCode.Forbidden, "owner-required"),
            ("operator api token", request => Bearer(request, operatorToken.Secret), HttpStatusCode.Forbidden, "owner-required"),
            ("viewer api token", request => Bearer(request, viewerToken.Secret), HttpStatusCode.Forbidden, null),
            ("anonymous", _ => { }, HttpStatusCode.Unauthorized, "authentication-required"),
        };

        var failures = new List<string>();
        foreach (var (method, _, path, ownerStatus) in Routes)
        {
            foreach (var (name, authenticate, status, error) in callers)
            {
                using var request = NewRequest(method, path);
                authenticate(request);
                using var response = await client.SendAsync(request);
                var expected = status ?? ownerStatus;
                var actualError = response.Content.Headers.ContentType?.MediaType == "application/json"
                                  && response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()
                    : null;
                if (response.StatusCode != expected || error is not null && actualError != error)
                    failures.Add($"{name} {method} {path}: expected {(int)expected} {error}, got {(int)response.StatusCode} {actualError}");
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
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
