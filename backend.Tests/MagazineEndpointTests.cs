using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Magazine;
using AgentStudio.Registry;
using AgentStudio.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// MAG-1 (AGT-3714): the magazine API on a networked host. Upsert by slug
/// creates once and then appends revisions under If-Match, the history keeps
/// every revision, scopes follow project and workspace visibility, only a
/// credential holding magazine.write publishes, and oversized bodies are
/// refused by name. Every fixture is generated; no live data.
/// </summary>
public sealed class MagazineEndpointTests : IClassFixture<MagazineEndpointTests.Host>
{
    private readonly Host _host;

    public MagazineEndpointTests(Host host) => _host = host;

    [Fact]
    public async Task Upsert_by_slug_creates_once_then_updates_and_the_history_lists_every_revision()
    {
        var scope = _host.AlphaScope;
        const string slug = "decision-notes";
        var path = $"/api/magazine/{scope}/{slug}";

        var created = await _host.Send("PUT", path, Article("Decision notes", "29 cards wait for you.", "## First\n"), _host.OwnerSession);
        Assert.Equal(HttpStatusCode.Created, created.Status);
        Assert.Equal("\"1\"", created.Etag);
        using (var json = JsonDocument.Parse(created.Body))
        {
            Assert.Equal(1, json.RootElement.GetProperty("revision").GetInt32());
            Assert.Equal(scope, json.RootElement.GetProperty("scope").GetString());
            Assert.Equal("markdown", json.RootElement.GetProperty("bodyFormat").GetString());
            Assert.StartsWith("human:", json.RootElement.GetProperty("author").GetProperty("principal").GetString(), StringComparison.Ordinal);
            Assert.Equal("Editor", json.RootElement.GetProperty("author").GetProperty("persona").GetString());
        }

        var missingIfMatch = await _host.Send("PUT", path, Article("Decision notes", "Again.", "## Again\n"), _host.OwnerSession);
        Assert.Equal((HttpStatusCode)428, missingIfMatch.Status);
        Assert.Equal("if-match-required", Error(missingIfMatch));

        var updated = await _host.Send("PUT", path, Article("Decision notes", "28 cards wait for you.", "## Second\n"),
            _host.OwnerSession, ifMatch: "\"1\"");
        Assert.Equal(HttpStatusCode.OK, updated.Status);
        Assert.Equal("\"2\"", updated.Etag);

        var stale = await _host.Send("PUT", path, Article("Decision notes", "Stale.", "## Stale\n"), _host.OwnerSession, ifMatch: "\"1\"");
        Assert.Equal(HttpStatusCode.Conflict, stale.Status);
        Assert.Equal("revision-conflict", Error(stale));
        using (var json = JsonDocument.Parse(stale.Body))
            Assert.Equal(2, json.RootElement.GetProperty("currentRevision").GetInt32());

        var current = await _host.Send("GET", path, null, _host.OwnerSession);
        Assert.Equal(HttpStatusCode.OK, current.Status);
        using (var json = JsonDocument.Parse(current.Body))
        {
            Assert.Equal(2, json.RootElement.GetProperty("revision").GetInt32());
            Assert.Equal("## Second\n", json.RootElement.GetProperty("body").GetString());
        }
        var first = await _host.Send("GET", path + "?revision=1", null, _host.OwnerSession);
        using (var json = JsonDocument.Parse(first.Body))
        {
            Assert.Equal(1, json.RootElement.GetProperty("revision").GetInt32());
            Assert.Equal(2, json.RootElement.GetProperty("currentRevision").GetInt32());
            Assert.Equal("## First\n", json.RootElement.GetProperty("body").GetString());
        }
        Assert.Equal("magazine-revision-not-found", Error(await _host.Send("GET", path + "?revision=3", null, _host.OwnerSession)));

        var history = await _host.Send("GET", path + "/history", null, _host.OwnerSession);
        Assert.Equal(HttpStatusCode.OK, history.Status);
        using (var json = JsonDocument.Parse(history.Body))
        {
            Assert.Equal([2, 1], json.RootElement.GetProperty("revisions").EnumerateArray()
                .Select(entry => entry.GetProperty("revision").GetInt32()));
            Assert.Equal(["28 cards wait for you.", "29 cards wait for you."], json.RootElement.GetProperty("revisions").EnumerateArray()
                .Select(entry => entry.GetProperty("summary").GetString()));
        }

        // The store keeps the articles outside every workspace commit.
        var articleFolder = Path.Combine(_host.Workspace, MagazineStorePathPolicy.DirectoryName, "project-" + _host.AlphaId, slug);
        Assert.True(File.Exists(Path.Combine(articleFolder, MagazineStore.HeadFileName)));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(articleFolder, MagazineStore.RevisionFolderName), "*.json").Length);

        var audit = _host.Services.GetRequiredService<ControlPlaneAuditLog>()
            .ReadNewest(entry => entry.Operation == "magazine.article.upsert" && entry.Target == $"{scope}/{slug}@2", 5);
        Assert.Equal("updated", Assert.Single(audit).Outcome);
    }

    [Fact]
    public async Task The_list_has_no_bodies_and_puts_pinned_articles_first_and_archived_ones_away()
    {
        var scope = "workspace:" + _host.AlphaWorkspaceId;
        foreach (var slug in new[] { "older", "pinned", "newer", "archived" })
        {
            Assert.Equal(HttpStatusCode.Created,
                (await _host.Send("PUT", $"/api/magazine/{scope}/{slug}", Article(slug, "One paragraph.", "Body of " + slug), _host.OwnerSession)).Status);
            _host.Clock.Advance(TimeSpan.FromMinutes(1));
        }
        Assert.Equal(HttpStatusCode.OK, (await _host.Send("POST", $"/api/magazine/{scope}/pinned/pin", new { pinned = true }, _host.OwnerSession)).Status);
        var archived = await _host.Send("POST", $"/api/magazine/{scope}/archived/archive", null, _host.OwnerSession);
        Assert.Equal(HttpStatusCode.OK, archived.Status);

        var list = await _host.Send("GET", "/api/magazine?scope=" + scope, null, _host.OwnerSession);
        Assert.Equal(HttpStatusCode.OK, list.Status);
        using (var json = JsonDocument.Parse(list.Body))
        {
            var articles = json.RootElement.GetProperty("articles").EnumerateArray().ToList();
            Assert.Equal(["pinned", "newer", "older"], articles.Select(article => article.GetProperty("slug").GetString()));
            Assert.All(articles, article => Assert.False(article.TryGetProperty("body", out _)));
            Assert.Equal(1, articles[0].GetProperty("revision").GetInt32());
        }
        var withArchived = await _host.Send("GET", $"/api/magazine?scope={scope}&includeArchived=true", null, _host.OwnerSession);
        Assert.Contains("\"archived\"", withArchived.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_oversized_body_is_refused_by_name_and_nothing_is_stored()
    {
        var path = $"/api/magazine/{_host.AlphaScope}/oversized";
        var overLimit = await _host.Send("PUT", path,
            Article("Big", "Too big.", new string('x', MagazineLimits.MaxBodyBytes + 1)), _host.OwnerSession);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, overLimit.Status);
        Assert.Equal("magazine-body-too-large", Error(overLimit));

        var farOverLimit = await _host.Send("PUT", path,
            Article("Big", "Too big.", new string('x', 3 * 1024 * 1024)), _host.OwnerSession);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, farOverLimit.Status);
        Assert.Equal("magazine-body-too-large", Error(farOverLimit));

        Assert.Equal(HttpStatusCode.NotFound, (await _host.Send("GET", path, null, _host.OwnerSession)).Status);
        var atLimit = await _host.Send("PUT", path, Article("Big", "Just fits.", new string('x', MagazineLimits.MaxBodyBytes)), _host.OwnerSession);
        Assert.Equal(HttpStatusCode.Created, atLimit.Status);
    }

    [Fact]
    public async Task A_member_reads_only_the_magazines_of_what_they_can_see_and_cannot_publish()
    {
        var owner = await _host.Send("PUT", $"/api/magazine/{_host.BetaScope}/beta-digest", Article("Beta", "Beta only.", "Secret plans."), _host.OwnerSession);
        Assert.Equal(HttpStatusCode.Created, owner.Status);
        Assert.Equal(HttpStatusCode.Created,
            (await _host.Send("PUT", $"/api/magazine/{_host.AlphaScope}/alpha-digest", Article("Alpha", "Alpha.", "Alpha body."), _host.OwnerSession)).Status);

        var member = _host.AlphaViewerSession;
        Assert.Equal(HttpStatusCode.OK, (await _host.Send("GET", $"/api/magazine/{_host.AlphaScope}/alpha-digest", null, member)).Status);
        Assert.Equal(HttpStatusCode.OK, (await _host.Send("GET", "/api/magazine?scope=" + _host.AlphaScope, null, member)).Status);
        Assert.Equal(HttpStatusCode.OK, (await _host.Send("GET", "/api/magazine?scope=workspace:" + _host.AlphaWorkspaceId, null, member)).Status);

        foreach (var path in new[]
                 {
                     $"/api/magazine/{_host.BetaScope}/beta-digest",
                     $"/api/magazine/{_host.BetaScope}/beta-digest/history",
                     "/api/magazine?scope=" + _host.BetaScope,
                     "/api/magazine?scope=workspace:" + _host.BetaWorkspaceId,
                     "/api/magazine?scope=installation",
                 })
        {
            var refused = await _host.Send("GET", path, null, member);
            Assert.True(refused.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound, $"{path}: {refused.Status} {refused.Body}");
            Assert.DoesNotContain("Secret plans", refused.Body, StringComparison.Ordinal);
        }

        var write = await _host.Send("PUT", $"/api/magazine/{_host.AlphaScope}/member-note", Article("Mine", "Mine.", "Mine."), member);
        Assert.Equal(HttpStatusCode.Forbidden, write.Status);

        // The Product role reads its project's magazine through its route allowlist and cannot publish.
        var product = _host.AlphaProductSession;
        Assert.Equal(HttpStatusCode.OK, (await _host.Send("GET", $"/api/magazine/{_host.AlphaScope}/alpha-digest", null, product)).Status);
        Assert.Equal(HttpStatusCode.OK, (await _host.Send("GET", "/api/magazine?scope=" + _host.AlphaScope, null, product)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _host.Send("GET", "/api/magazine?scope=" + _host.BetaScope, null, product)).Status);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await _host.Send("PUT", $"/api/magazine/{_host.AlphaScope}/product-note", Article("Mine", "Mine.", "Mine."), product)).Status);
    }

    [Fact]
    public async Task An_operator_token_publishes_and_a_token_without_magazine_write_cannot()
    {
        var operatorToken = await _host.Send("PUT", "/api/magazine/installation/orchestrator-notes",
            Article("Orchestrator notes", "29 cards wait for you.", "## Waiting\n"), bearer: _host.OperatorToken);
        Assert.Equal(HttpStatusCode.Created, operatorToken.Status);
        using (var json = JsonDocument.Parse(operatorToken.Body))
            Assert.StartsWith("service-account:", json.RootElement.GetProperty("author").GetProperty("principal").GetString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await _host.Send("PUT", "/api/magazine/installation/orchestrator-notes",
            Article("Orchestrator notes", "28 cards wait for you.", "## Waiting\n"), bearer: _host.OperatorToken, ifMatch: "\"1\"")).Status);
        Assert.Equal(HttpStatusCode.Created, (await _host.Send("PUT", $"/api/magazine/{_host.BetaScope}/token-digest",
            Article("Token digest", "From the token.", "Body."), bearer: _host.OperatorToken)).Status);

        var viewer = await _host.Send("PUT", $"/api/magazine/{_host.AlphaScope}/viewer-note",
            Article("Viewer", "Viewer.", "Viewer."), bearer: _host.ViewerToken);
        Assert.Equal(HttpStatusCode.Forbidden, viewer.Status);
        Assert.Equal(HttpStatusCode.OK, (await _host.Send("GET", "/api/magazine/installation/orchestrator-notes", null, bearer: _host.ViewerToken)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _host.Send("POST", "/api/magazine/installation/orchestrator-notes/pin", null, bearer: _host.ViewerToken)).Status);

        // A project-scoped operator token publishes only in its project.
        Assert.Equal(HttpStatusCode.Created, (await _host.Send("PUT", $"/api/magazine/{_host.AlphaScope}/scoped-digest",
            Article("Scoped", "Scoped.", "Body."), bearer: _host.AlphaOperatorToken)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _host.Send("PUT", $"/api/magazine/{_host.BetaScope}/scoped-digest",
            Article("Scoped", "Scoped.", "Body."), bearer: _host.AlphaOperatorToken)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _host.Send("PUT", "/api/magazine/installation/scoped-digest",
            Article("Scoped", "Scoped.", "Body."), bearer: _host.AlphaOperatorToken)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _host.Send("GET", "/api/magazine/installation/scoped-digest", null, _host.OwnerSession)).Status);

        var audit = _host.Services.GetRequiredService<ControlPlaneAuditLog>()
            .ReadNewest(entry => entry.Operation == "magazine.article.upsert" && entry.Target == "installation/orchestrator-notes@1", 5);
        Assert.StartsWith("service-account:", Assert.Single(audit).Actor, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_scopes_slugs_and_articles_are_refused_by_name()
    {
        Assert.Equal("magazine-scope-invalid", Error(await _host.Send("GET", "/api/magazine?scope=team:x", null, _host.OwnerSession)));
        Assert.Equal("magazine-scope-not-found", Error(await _host.Send("GET", "/api/magazine?scope=workspace:ws-missing", null, _host.OwnerSession)));
        Assert.Equal("magazine-slug-invalid", Error(await _host.Send("PUT", $"/api/magazine/{_host.AlphaScope}/Not_A_Slug",
            Article("T", "S.", "B"), _host.OwnerSession)));
        Assert.Equal("magazine-kind-invalid", Error(await _host.Send("PUT", $"/api/magazine/{_host.AlphaScope}/bad-kind",
            new { title = "T", summary = "S.", body = "B", kind = "essay" }, _host.OwnerSession)));
        Assert.Equal("magazine-summary-paragraphs", Error(await _host.Send("PUT", $"/api/magazine/{_host.AlphaScope}/long-summary",
            Article("T", "One.\n\nTwo.", "B"), _host.OwnerSession)));
        Assert.Equal("if-match-invalid", Error(await _host.Send("PUT", $"/api/magazine/{_host.AlphaScope}/bad-etag",
            Article("T", "S.", "B"), _host.OwnerSession, ifMatch: "\"latest\"")));
    }

    private static object Article(string title, string summary, string body) => new
    {
        title, summary, body, kind = "decision-brief", persona = "Editor",
        sources = new[] { new { kind = "card", @ref = "AGT-3714" }, new { kind = "batch", @ref = "batch-17" } },
    };

    private static string? Error(Reply reply)
    {
        using var json = JsonDocument.Parse(reply.Body);
        return json.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null;
    }

    public sealed record Session(string Token, string Csrf);
    public sealed record Reply(HttpStatusCode Status, string Body, string? Etag);

    /// <summary>
    /// One networked host for the class: two workspaces with one generated
    /// project each, the Owner, a Viewer member of the alpha project, and
    /// API tokens for the operator and viewer roles.
    /// </summary>
    public sealed class Host : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "magazine-endpoints-" + Guid.NewGuid().ToString("N"));
        private readonly WebApplicationFactory<Program> _factory;
        private readonly HttpClient _client;

        public Host()
        {
            Directory.CreateDirectory(Workspace);
            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TaskRepository"] = Workspace,
                    ["Security:Profile"] = SecurityProfiles.Networked,
                    ["Security:BootstrapCode"] = "magazine-setup-code",
                    ["AllowedHosts"] = "studio.test",
                    ["Secrets:KeyFile"] = Path.Combine(_root, "keys", "secrets.kek"),
                    ["Secrets:KeyEnvironmentVariable"] = "AGENT_STUDIO_TEST_UNSET_KEK_" + Guid.NewGuid().ToString("N"),
                }));
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton<TimeProvider>(Clock);
                });
            });
            _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://studio.test"), HandleCookies = false,
            });

            var workspaces = Services.GetRequiredService<WorkspaceRegistry>();
            var projects = Services.GetRequiredService<ProjectRegistry>();
            workspaces.EnsureLoaded();
            var alphaWorkspace = workspaces.EnsureDefaultWorkspace(Clock);
            var betaWorkspace = workspaces.Create("Beta workspace", clock: Clock);
            AlphaWorkspaceId = alphaWorkspace.Id;
            BetaWorkspaceId = betaWorkspace.Id;
            AlphaId = projects.EnsureProjectForStorage(Path.Combine(Workspace, "projects", "alpha"), "Alpha", alphaWorkspace.Id, Clock).Id;
            BetaId = projects.EnsureProjectForStorage(Path.Combine(Workspace, "projects", "beta"), "Beta", betaWorkspace.Id, Clock).Id;

            var store = Services.GetRequiredService<AccessSecurityStore>();
            var owner = store.Bootstrap(new BootstrapRequest("magazine.owner", "correct horse battery staple!", null));
            OwnerSession = new Session(owner.SessionToken, owner.CsrfToken);
            AlphaViewerSession = SignIn(store, "alpha.viewer", StudioRoles.Viewer);
            AlphaProductSession = SignIn(store, "alpha.product", StudioRoles.Contributor);
            var expires = Clock.GetUtcNow().UtcDateTime.AddDays(30);
            OperatorToken = store.CreateApiToken(new CreateApiTokenRequest("orchestrator", StudioRoles.Operator, ["*"], expires), owner.User.Id).Secret;
            AlphaOperatorToken = store.CreateApiToken(new CreateApiTokenRequest("alpha operator", StudioRoles.Operator, [AlphaId], expires), owner.User.Id).Secret;
            ViewerToken = store.CreateApiToken(new CreateApiTokenRequest("reader", StudioRoles.Viewer, ["*"], expires), owner.User.Id).Secret;
        }

        public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero));
        public string Workspace => Path.Combine(_root, "workspace");
        public IServiceProvider Services => _factory.Services;
        public string AlphaWorkspaceId { get; }
        public string BetaWorkspaceId { get; }
        public string AlphaId { get; }
        public string BetaId { get; }
        public string AlphaScope => "project:" + AlphaId;
        public string BetaScope => "project:" + BetaId;
        public Session OwnerSession { get; }
        public Session AlphaViewerSession { get; }
        public Session AlphaProductSession { get; }
        public string OperatorToken { get; }
        public string AlphaOperatorToken { get; }
        public string ViewerToken { get; }

        private Session SignIn(AccessSecurityStore store, string username, string role)
        {
            var member = store.CreateUser(new CreateUserRequest(username, username, role, [AlphaId], null));
            var login = store.Login(username, member.TemporaryPassword, username + "|test");
            store.ChangePassword(store.AuthenticateSession(login.SessionToken)!,
                new ChangePasswordRequest(member.TemporaryPassword, "another long passphrase!"));
            return new Session(login.SessionToken, login.CsrfToken);
        }

        public async Task<Reply> Send(string method, string path, object? body, Session? session = null, string? bearer = null,
            string? ifMatch = null)
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
            if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
            using var response = await _client.SendAsync(request);
            return new Reply(response.StatusCode, await response.Content.ReadAsStringAsync(), response.Headers.ETag?.Tag);
        }

        public void Dispose()
        {
            _client.Dispose();
            _factory.Dispose();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }
}
