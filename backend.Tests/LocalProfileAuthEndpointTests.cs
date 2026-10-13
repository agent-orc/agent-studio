using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The local security profile must report itself truthfully from every auth
/// endpoint. Login/bootstrap/session previously hardcoded "networked" in their
/// AuthStatus response, which mislabelled the shape the browser stores after a
/// local-profile sign-in. These tests pin the derived profile.
/// </summary>
[Collection(WebApplicationFactorySerialCollection.Name)]
public sealed class LocalProfileAuthEndpointTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "studio-local-auth-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Local_profile_status_bootstrap_and_login_report_the_local_profile()
    {
        using var factory = BuildFactory();
        using var browser = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        // The local profile keeps the client-identity boundary; the browser stamps
        // the seeded bootstrap identity on every /api write, so mirror that here.
        browser.DefaultRequestHeaders.Add("X-Client-Id", "local-default");

        const string password = "correct horse battery staple!";

        var status = await browser.GetFromJsonAsync<AuthStatusResponse>("/api/auth/status");
        Assert.Equal(SecurityProfiles.Local, status!.Profile);

        var bootstrap = await browser.PostAsJsonAsync("/api/auth/bootstrap", new
        {
            username = "local.owner",
            password,
            displayName = "Local Owner"
        });
        bootstrap.EnsureSuccessStatusCode();
        var bootstrapped = await bootstrap.Content.ReadFromJsonAsync<AuthStatusResponse>();
        Assert.Equal(SecurityProfiles.Local, bootstrapped!.Profile);
        Assert.True(bootstrapped.Authenticated);

        var login = await browser.PostAsJsonAsync("/api/auth/login", new { username = "local.owner", password });
        login.EnsureSuccessStatusCode();
        var loggedIn = await login.Content.ReadFromJsonAsync<AuthStatusResponse>();
        Assert.Equal(SecurityProfiles.Local, loggedIn!.Profile);
        Assert.True(loggedIn.Authenticated);
        Assert.Equal("local.owner", loggedIn.User!.Username);

        var refreshed = await browser.GetFromJsonAsync<AuthStatusResponse>("/api/auth/status");
        Assert.True(refreshed!.Authenticated);
        Assert.Equal("local.owner", refreshed.User!.Username);
    }

    [Fact]
    public async Task Administrative_auth_gets_reject_client_identity_without_a_human_session()
    {
        using var factory = BuildFactory();
        using var client = CreateClient(factory);

        foreach (var path in new[] { "/api/auth/users", "/api/auth/runners" })
        {
            var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("authentication-required", body.GetProperty("error").GetString());
        }
    }

    [Fact]
    public async Task Users_list_rejects_an_authenticated_non_owner()
    {
        using var factory = BuildFactory();
        using var owner = CreateClient(factory);
        await BootstrapOwner(owner);

        const string operatorPassword = "temporary operator password!";
        var create = await owner.PostAsJsonAsync("/api/auth/users", new
        {
            username = "local.operator",
            displayName = "Local Operator",
            role = StudioRoles.Operator,
            projects = Array.Empty<string>(),
            temporaryPassword = operatorPassword
        });
        create.EnsureSuccessStatusCode();

        using var operatorClient = CreateClient(factory);
        var login = await operatorClient.PostAsJsonAsync("/api/auth/login", new
        {
            username = "local.operator",
            password = operatorPassword
        });
        login.EnsureSuccessStatusCode();

        foreach (var path in new[] { "/api/auth/users", "/api/auth/runners" })
        {
            var response = await operatorClient.GetAsync(path);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("owner-required", body.GetProperty("error").GetString());
        }
    }

    [Fact]
    public async Task Users_list_allows_an_owner_session()
    {
        using var factory = BuildFactory();
        using var owner = CreateClient(factory);
        await BootstrapOwner(owner);

        var usersResponse = await owner.GetAsync("/api/auth/users");
        usersResponse.EnsureSuccessStatusCode();
        var users = await usersResponse.Content.ReadFromJsonAsync<AuthUserResponse[]>();
        var listedOwner = Assert.Single(users!);
        Assert.Equal("local.owner", listedOwner.Username);
        Assert.Equal(StudioRoles.Owner, listedOwner.Role);

        var runnersResponse = await owner.GetAsync("/api/auth/runners");
        runnersResponse.EnsureSuccessStatusCode();
        var runners = await runnersResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(runners.EnumerateArray());
    }

    [Fact]
    public async Task Bootstrap_returns_conflict_once_an_owner_exists()
    {
        using var factory = BuildFactory();
        using var client = CreateClient(factory);
        await BootstrapOwner(client);

        var response = await client.PostAsJsonAsync("/api/auth/bootstrap", new
        {
            username = "second.owner",
            password = "another correct horse battery staple!",
            displayName = "Second Owner"
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("bootstrap-complete", body.GetProperty("error").GetString());
    }

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        return client;
    }

    private static async Task BootstrapOwner(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/auth/bootstrap", new
        {
            username = "local.owner",
            password = "correct horse battery staple!",
            displayName = "Local Owner"
        });
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Local_profile_management_accepts_the_loopback_default_operator_without_a_human_session()
    {
        using var factory = BuildFactory();
        using var browser = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        browser.DefaultRequestHeaders.Add("X-Client-Id", "local-default");

        var response = await browser.GetAsync("/api/v1/management/status");

        response.EnsureSuccessStatusCode();
        var status = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("healthy", status.GetProperty("health").GetProperty("state").GetString());
    }

    [Fact]
    public async Task Session_without_a_session_returns_unauthorized_instead_of_an_unhandled_error()
    {
        using var factory = BuildFactory();
        using var browser = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        browser.DefaultRequestHeaders.Add("X-Client-Id", "local-default");

        var response = await browser.GetAsync("/api/auth/session");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("authentication-required", body.GetProperty("error").GetString());
    }

    private WebApplicationFactory<Program> BuildFactory() => new TestHostFactory().WithWebHostBuilder(builder =>
    {
        builder.UseEnvironment("Test");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _workspace,
            ["Security:Profile"] = "local"
        }));
    });

    public void Dispose()
    {
        try { Directory.Delete(_workspace, true); } catch { }
    }
}
