using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The Access administration routes on the real networked host: the
/// overview, groups, bindings with their wire names, and a service account
/// whose own token reaches a route through its binding.
/// </summary>
public sealed class IamEndpointTests : IDisposable
{
    private const string Password = "correct horse battery staple!";
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "iam-endpoints-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Owner_manages_groups_bindings_and_service_accounts_over_http()
    {
        using var factory = BuildFactory();
        var store = factory.Services.GetRequiredService<AccessSecurityStore>();
        store.Bootstrap(new BootstrapRequest("first.owner", Password, null));
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://studio.test"), HandleCookies = true });
        var csrf = await LoginAsync(owner, "first.owner", Password);

        var overview = await owner.GetFromJsonAsync<JsonElement>("/api/auth/iam");
        Assert.Equal(AccessSecurityStore.CurrentIamVersion, overview.GetProperty("iamVersion").GetInt32());
        Assert.Contains(overview.GetProperty("roles").EnumerateArray(), role => role.GetProperty("id").GetString() == IamRoles.Approver);
        var ownerBinding = Assert.Single(overview.GetProperty("bindings").EnumerateArray());
        Assert.Equal("user", ownerBinding.GetProperty("principalKind").GetString());
        Assert.Equal("workspace", ownerBinding.GetProperty("scope").GetProperty("kind").GetString());

        var group = await SendAsync<JsonElement>(owner, HttpMethod.Post, "/api/auth/iam/groups", csrf,
            new { name = "Team Website", description = "Website crew", members = Array.Empty<string>() });
        var binding = await SendAsync<JsonElement>(owner, HttpMethod.Post, "/api/auth/iam/bindings", csrf, new
        {
            principalKind = "group", principalId = group.GetProperty("id").GetString(), role = "developer",
            scope = new { kind = "project", project = "alpha" },
        });
        Assert.Equal("developer", binding.GetProperty("role").GetString());
        Assert.Equal("alpha", binding.GetProperty("scope").GetProperty("project").GetString());

        var invalid = await SendRawAsync(owner, HttpMethod.Post, "/api/auth/iam/bindings", csrf, new
        {
            principalKind = "group", principalId = group.GetProperty("id").GetString(), role = "owner",
            scope = new { kind = "project", project = "alpha" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var account = await SendAsync<JsonElement>(owner, HttpMethod.Post, "/api/auth/iam/service-accounts", csrf, new
        {
            name = "tag-reader", bindings = new[] { new { role = "viewer", scope = new { kind = "workspace" } } },
        });
        var accountId = account.GetProperty("id").GetString()!;
        var token = await SendAsync<JsonElement>(owner, HttpMethod.Post, $"/api/auth/iam/service-accounts/{accountId}/tokens", csrf,
            new { name = "ci" });
        var secret = token.GetProperty("token").GetString()!;
        Assert.StartsWith("sat.", secret);

        using var bot = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://studio.test"), HandleCookies = false });
        bot.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        Assert.Equal(HttpStatusCode.OK, (await bot.GetAsync("/api/tags")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bot.PostAsJsonAsync("/api/tags", new { name = "nope" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bot.GetAsync("/api/auth/iam")).StatusCode);

        var revoke = await SendRawAsync(owner, HttpMethod.Delete,
            $"/api/auth/iam/service-accounts/{accountId}/tokens/{token.GetProperty("tokenId").GetString()}", csrf, null);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await bot.GetAsync("/api/tags")).StatusCode);

        var check = await owner.GetFromJsonAsync<JsonElement>(
            $"/api/auth/iam/check?principalKind=service-account&principalId={accountId}&permission=project.read&project=alpha");
        Assert.True(check.GetProperty("allowed").GetBoolean());
    }

    [Fact]
    public async Task Access_administration_needs_the_access_permissions()
    {
        using var factory = BuildFactory();
        var store = factory.Services.GetRequiredService<AccessSecurityStore>();
        store.Bootstrap(new BootstrapRequest("first.owner", Password, null));
        var admin = store.InviteUser(new InviteUserRequest("workspace.admin", "Admin", StudioRoles.Operator, ["*"]));
        store.AcceptInvitation(new AcceptInvitationRequest(admin.Token, "admin pass phrase!!"));
        var viewer = store.InviteUser(new InviteUserRequest("project.viewer", "Viewer", StudioRoles.Viewer, ["alpha"]));
        store.AcceptInvitation(new AcceptInvitationRequest(viewer.Token, "viewer pass phrase!!"));
        using var adminClient = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://studio.test"), HandleCookies = true });
        using var viewerClient = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://studio.test"), HandleCookies = true });
        var adminCsrf = await LoginAsync(adminClient, "workspace.admin", "admin pass phrase!!");
        await LoginAsync(viewerClient, "project.viewer", "viewer pass phrase!!");

        // Access stays Owner-only: a migrated all-projects operator (Admin on
        // the workspace) neither reads nor changes it.
        Assert.Equal(HttpStatusCode.Forbidden, (await adminClient.GetAsync("/api/auth/iam")).StatusCode);
        var adminWrite = await SendRawAsync(adminClient, HttpMethod.Post, "/api/auth/iam/groups", adminCsrf, new { name = "Ops" });
        Assert.Equal(HttpStatusCode.Forbidden, adminWrite.StatusCode);
        var viewerRead = await viewerClient.GetAsync("/api/auth/iam");
        Assert.Equal(HttpStatusCode.Forbidden, viewerRead.StatusCode);
        Assert.Equal("permission-denied",
            (await viewerRead.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task In_the_local_profile_access_administration_still_needs_a_session()
    {
        using var factory = BuildFactory(SecurityProfiles.Local);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");

        var overview = await client.GetAsync("/api/auth/iam");
        Assert.Equal(HttpStatusCode.Unauthorized, overview.StatusCode);
        Assert.Equal("authentication-required",
            (await overview.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/auth/iam/groups", new { name = "Local" })).StatusCode);
    }

    [Fact]
    public async Task A_legacy_user_put_never_rewrites_binding_managed_access()
    {
        using var factory = BuildFactory();
        var store = factory.Services.GetRequiredService<AccessSecurityStore>();
        var ownerId = store.Bootstrap(new BootstrapRequest("first.owner", Password, null)).User.Id;
        var developer = store.InviteUser(new InviteUserRequest("dev.user", "Dev", "", null,
            [new IamGrantRequest(IamRoles.Developer, IamScope.ForProject("alpha"))]), ownerId).User;
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://studio.test"), HandleCookies = true });
        var csrf = await LoginAsync(owner, "first.owner", Password);
        var listed = (await owner.GetFromJsonAsync<JsonElement>("/api/auth/users")).EnumerateArray()
            .Single(user => user.GetProperty("id").GetString() == developer.Id);

        var rename = await SendRawAsync(owner, HttpMethod.Put, $"/api/auth/users/{developer.Id}", csrf, new
        {
            displayName = "Renamed Dev", role = listed.GetProperty("role").GetString(),
            projects = listed.GetProperty("projects"), disabled = false,
        });
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
        var change = await SendRawAsync(owner, HttpMethod.Put, $"/api/auth/users/{developer.Id}", csrf, new
        {
            role = StudioRoles.Operator, projects = new[] { "alpha" },
        });
        Assert.Equal(HttpStatusCode.Conflict, change.StatusCode);
        Assert.Equal("bindings-managed", (await change.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        var binding = Assert.Single(store.ReadIamSnapshot().Bindings, candidate => candidate.PrincipalId == developer.Id);
        Assert.Equal((IamRoles.Developer, "alpha"), (binding.Role, binding.Scope.Project));
    }

    [Fact]
    public async Task Api_tokens_are_issued_as_service_accounts_and_listed_in_both_views()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero));
        using var factory = BuildFactory(clock: clock);
        var store = factory.Services.GetRequiredService<AccessSecurityStore>();
        store.Bootstrap(new BootstrapRequest("first.owner", Password, null));
        using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://studio.test"), HandleCookies = true });
        var csrf = await LoginAsync(owner, "first.owner", Password);

        var created = await SendAsync<JsonElement>(owner, HttpMethod.Post, "/api/auth/api-tokens", csrf, new
        {
            name = "ci reader", role = StudioRoles.Viewer, projects = new[] { "alpha" },
            expiresAt = clock.GetUtcNow().UtcDateTime.AddDays(7),
        });
        var tokenId = created.GetProperty("token").GetProperty("id").GetString();
        Assert.StartsWith("api.", created.GetProperty("secret").GetString(), StringComparison.Ordinal);

        var tokens = await owner.GetFromJsonAsync<JsonElement>("/api/auth/api-tokens");
        Assert.Equal(tokenId, Assert.Single(tokens.EnumerateArray()).GetProperty("id").GetString());
        var overview = await owner.GetFromJsonAsync<JsonElement>("/api/auth/iam");
        var account = Assert.Single(overview.GetProperty("serviceAccounts").EnumerateArray());
        Assert.Equal(ServiceAccountOrigins.ApiToken, account.GetProperty("origin").GetString());
        Assert.Equal(tokenId, Assert.Single(account.GetProperty("tokens").EnumerateArray()).GetProperty("id").GetString());

        Assert.Equal(HttpStatusCode.NoContent,
            (await SendRawAsync(owner, HttpMethod.Delete, $"/api/auth/api-tokens/{tokenId}", csrf, null)).StatusCode);
        Assert.NotNull((await owner.GetFromJsonAsync<JsonElement>("/api/auth/api-tokens")).EnumerateArray().Single()
            .GetProperty("revokedAt").GetString());
    }

    private static async Task<string> LoginAsync(HttpClient client, string username, string password)
    {
        client.DefaultRequestHeaders.Add("Origin", "https://studio.test");
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthStatusResponse>())!.CsrfToken!;
    }

    private static async Task<T> SendAsync<T>(HttpClient client, HttpMethod method, string path, string csrf, object? body)
    {
        var response = await SendRawAsync(client, method, path, csrf, body);
        Assert.True(response.IsSuccessStatusCode, $"{method} {path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<HttpResponseMessage> SendRawAsync(HttpClient client, HttpMethod method, string path, string csrf, object? body)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Add("X-CSRF-Token", csrf);
        return await client.SendAsync(request);
    }

    private WebApplicationFactory<Program> BuildFactory(string profile = SecurityProfiles.Networked, TimeProvider? clock = null)
        => new TestHostFactory().WithWebHostBuilder(builder =>
    {
        builder.UseEnvironment("Test");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _workspace,
            ["Security:Profile"] = profile,
            ["AllowedHosts"] = profile == SecurityProfiles.Networked ? "studio.test" : "*",
        }));
        if (clock is not null)
            builder.ConfigureServices(services => services.AddSingleton(provider => new AccessSecurityStore(
                provider.GetRequiredService<IConfiguration>(), NullLogger<AccessSecurityStore>.Instance, clock)));
    });

    public void Dispose()
    {
        if (Directory.Exists(_workspace)) Directory.Delete(_workspace, true);
    }
}
