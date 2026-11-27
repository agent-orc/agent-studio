using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace AgentStudio.Tests;

public sealed class TaskResultEndpointTests : IDisposable
{
    private readonly TaskArtifactFixture _files = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 27, 0, 0, 0, TimeSpan.Zero));
    public void Dispose() => _files.Dispose();

    [Theory]
    [InlineData("results", "leaf")]
    [InlineData("results", "ancestor")]
    [InlineData("results", "root")]
    [InlineData("screenshot", "leaf")]
    [InlineData("screenshot", "ancestor")]
    [InlineData("screenshot", "root")]
    [InlineData("attachments", "leaf")]
    [InlineData("attachments", "root")]
    public async Task LinkedArtifact_ReturnsNotFound(string route, string kind)
    {
        if (route == "attachments" && kind == "leaf")
        {
            var outside = Path.Combine(_files.Root, "outside.png");
            File.WriteAllText(outside, "harmless outside fixture");
            Directory.CreateDirectory(Path.Combine(_files.Job, "attachments"));
            File.CreateSymbolicLink(Path.Combine(_files.Job, "attachments", "proof.png"), outside);
        }
        else _files.Link(route == "screenshot" ? "results" : route, kind);
        using var factory = Factory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(Url(route));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("results")]
    [InlineData("screenshot")]
    [InlineData("attachments")]
    public async Task RegularArtifact_StreamsFixtureBytes(string route)
    {
        var relative = route == "attachments" ? "attachments/proof.png" : "results/nested/proof.png";
        var path = _files.Write(relative);
        using var factory = Factory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(Url(route));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(File.ReadAllBytes(path), await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("results-private")]
    [InlineData("RESULTS-private")]
    public async Task AbsoluteSiblingScreenshot_ReturnsNotFound(string sibling)
    {
        var path = _files.Write(sibling + "/secret.png");
        using var factory = Factory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/api/tasks/{TaskArtifactFixture.Id}/screenshot?path={Uri.EscapeDataString(path)}&watchPath={Uri.EscapeDataString(_files.Watch)}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task NestedReportLinks_KeepHtmlSandboxDispositionAndImageRangeServing()
    {
        var report = _files.Write("results/reports/report.html");
        File.WriteAllText(report, "<a href='proof.png'>Evidence</a>");
        var image = _files.Write("results/reports/proof.png");
        using var factory = Factory();
        using var client = factory.CreateClient();
        var prefix = $"/api/tasks/{TaskArtifactFixture.Id}/results/reports/";
        var query = "?watchPath=" + Uri.EscapeDataString(_files.Watch);
        using var request = new HttpRequestMessage(HttpMethod.Get, prefix + "report.html" + query);
        request.Headers.Add("Sec-Fetch-Dest", "iframe");
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("inline", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Contains("sandbox", Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal(File.ReadAllText(report), await response.Content.ReadAsStringAsync());
        using var range = new HttpRequestMessage(HttpMethod.Get, prefix + "proof.png" + query);
        range.Headers.Range = new RangeHeaderValue(0, 7);
        using var imageResponse = await client.SendAsync(range);
        Assert.Equal(HttpStatusCode.PartialContent, imageResponse.StatusCode);
        Assert.Equal(File.ReadAllBytes(image)[..8], await imageResponse.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("results")]
    [InlineData("screenshot")]
    [InlineData("attachments")]
    public async Task LinkedTaskFolder_ReturnsNotFound(string route)
    {
        _files.Write("results/nested/proof.png");
        _files.Write("attachments/proof.png");
        using var factory = Factory();
        // Warm the scanner before replacing the authoritative folder with a link.
        var scanner = factory.Services.GetRequiredService<TaskScannerService>();
        Assert.NotNull(scanner.FindJob(TaskArtifactFixture.Id, _files.Watch));
        var moved = Path.Combine(_files.Root, "outside-task");
        Directory.Move(_files.Job, moved);
        Directory.CreateSymbolicLink(_files.Job, moved);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(Url(route));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task NetworkedArtifactReads_PreserveHumanOperatorAndRevocationAdmission()
    {
        _files.Write("results/nested/proof.png");
        _files.Write("attachments/proof.png");
        _files.Write("results/run.log");
        var outside = Path.Combine(_files.Root, "outside.log");
        File.WriteAllText(outside, "harmless outside fixture");
        File.CreateSymbolicLink(Path.Combine(_files.Job, "results", "report.png"), outside);
        using var factory = Factory(networked: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://studio.test"), HandleCookies = false });
        var store = factory.Services.GetRequiredService<AccessSecurityStore>();
        var password = "fixture password " + Guid.NewGuid().ToString("N");
        var owner = store.Bootstrap(new BootstrapRequest("fixture.owner", password, null));
        var principal = store.AuthenticateSession(owner.SessionToken)!;
        var secret = store.BeginTotpEnrollment(principal);
        store.ConfirmTotpEnrollment(principal, SecondFactorCodes.CurrentTotp(secret,
            factory.Services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime));
        var invite = store.InviteUser(new InviteUserRequest("fixture.reader", "Reader", StudioRoles.Contributor, ["test"]));
        var reader = store.AcceptInvitation(new AcceptInvitationRequest(invite.Token, password));
        var outsiderInvite = store.InviteUser(new InviteUserRequest("fixture.outsider", "Outsider", StudioRoles.Operator, ["elsewhere"]));
        var outsider = store.AcceptInvitation(new AcceptInvitationRequest(outsiderInvite.Token, password));
        var agent = store.CreateOperatorAgent(new CreateOperatorAgentRequest("fixture operator", null,
            [new IamGrantRequest(IamRoles.Developer, IamScope.ForProject("test"))]), owner.User.Id);
        var token = store.StartOperatorSession(agent.Id, new StartOperatorSessionRequest(null, null), owner.User.Id).Secret;
        Assert.NotNull(store.AuthenticateOperatorSession(token, recordUse: false));

        foreach (var route in new[] { "results", "screenshot", "attachments" })
        {
            await Expect(Url(route), null, false, HttpStatusCode.Unauthorized);
            await Expect(Url(route), outsider.SessionToken, false, HttpStatusCode.Forbidden);
            await Expect(Url(route), owner.SessionToken, false, HttpStatusCode.OK);
            // Attachments retain their existing runner-credential gate for service identities.
            await Expect(Url(route), token, true,
                route == "attachments" ? HttpStatusCode.Unauthorized : HttpStatusCode.OK);
        }
        await Expect(Url("results"), reader.SessionToken, false, HttpStatusCode.OK);
        var prefix = $"/api/tasks/{TaskArtifactFixture.Id}/results/";
        var query = "?watchPath=" + Uri.EscapeDataString(_files.Watch);
        await Expect(prefix + "run.log" + query, reader.SessionToken, false, HttpStatusCode.Forbidden);
        await Expect(prefix + "report.png" + query, reader.SessionToken, false, HttpStatusCode.NotFound);

        store.EndOperatorAccess(null, OperatorKillActor.Owner(owner.User.Id));
        foreach (var route in new[] { "results", "screenshot", "attachments" })
        {
            await Expect(Url(route), token, true, HttpStatusCode.Unauthorized);
            await Expect(Url(route), owner.SessionToken, false, HttpStatusCode.OK);
        }

        async Task Expect(string url, string? credential, bool bearer, HttpStatusCode status)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (bearer) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            else if (credential is not null) request.Headers.Add("Cookie", $"{AccessSecurityStore.SessionCookieName}={credential}");
            using var response = await client.SendAsync(request);
            Assert.True(response.StatusCode == status,
                $"GET {url}, bearer={bearer}: expected {status}, got {response.StatusCode}.");
        }
    }

    [Theory]
    [InlineData("/api/tasks/{jobId}/results/{**path}")]
    [InlineData("/api/tasks/{jobId}/attachments/{fileName}")]
    [InlineData("/api/tasks/{jobId}/screenshot")]
    public async Task ArtifactReadProtocols_KeepDeclaredProjectReadAndObserveClassification(string pattern)
    {
        foreach (var method in new[] { "GET", "HEAD", "OPTIONS" })
        {
            Assert.Equal(IamPermissions.ProjectRead, RoutePermissionCatalog.Resolve(method, pattern)?.Permission);
            var context = new DefaultHttpContext();
            context.Request.Method = method;
            var operation = await OperatorRequestClassifier.ClassifyAsync(context, true, null, (_, _) => null, _ => null);
            Assert.Equal(OperatorOperationIds.Observe, operation?.Id);
            Assert.Equal(OperatorTier.T0, operation?.Tier);
        }
    }

    private string Url(string route) => $"/api/tasks/{TaskArtifactFixture.Id}/" + (route switch
    {
        "screenshot" => "screenshot?path=nested%2Fproof.png&",
        "attachments" => "attachments/proof.png?",
        _ => "results/nested/proof.png?",
    }) + "watchPath=" + Uri.EscapeDataString(_files.Watch);

    private WebApplicationFactory<Program> Factory(bool networked = false) => new TestHostFactory().WithWebHostBuilder(builder =>
    {
        builder.UseEnvironment("Test");
        builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(_clock));
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _files.Workspace,
            ["Security:Profile"] = networked ? "networked" : "local",
            ["AllowedHosts"] = "studio.test;localhost",
            ["Secrets:KeyFile"] = Path.Combine(_files.Root, "fixture.kek"),
            ["Secrets:KeyEnvironmentVariable"] = "AGT4108_UNSET_" + Guid.NewGuid().ToString("N"),
            ["WatchPaths:0:Name"] = "test",
            ["WatchPaths:0:Path"] = _files.Watch,
            ["WatchPaths:0:RootPath"] = _files.Watch,
            ["WatchPaths:0:RepositoryPath"] = _files.Watch,
        }));
    });
}
