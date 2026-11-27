extern alias TaskServerAssembly;

using System.Net;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using Upstream = TaskServerAssembly::AgentStudio.TaskServer;

namespace AgentStudio.Tests;

public sealed class TaskServerProxyCredentialBoundaryTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "proxy-boundary-" + Guid.NewGuid().ToString("N"));
    private readonly List<ObservedRequest> _observed = [];
    private readonly FakeTimeProvider _clock = new();
    private readonly string _proof = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
    private Upstream.TaskServerStore _upstream = null!;
    private string _edgeToken = null!;
    private string TokenPath => Path.Combine(_root, "edge-token");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _upstream = new Upstream.TaskServerStore(
            Options.Create(new Upstream.TaskServerOptions { DataDirectory = Path.Combine(_root, "upstream") }),
            _clock);
        await _upstream.InitializeAsync();
        _edgeToken = Assert.IsType<string>((await _upstream.CreatePrincipalAsync(
            new("studio-edge", TaskServerPrincipalKinds.Studio,
                [TaskServerScopes.TasksRead, TaskServerScopes.Management]), "setup", default)).Credential);
        File.WriteAllText(TokenPath, _edgeToken);
        WriteProof(_proof);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Arbitrary_bearer_reaches_upstream_unchanged_without_edge_proof_and_is_refused(bool fileBacked)
    {
        await using var app = CreateEdge(fileBacked);
        var authorization = "bEaReR " + Guid.NewGuid().ToString("N");

        var context = await Send(app, authorization: authorization);

        var observed = Assert.Single(_observed);
        Assert.Equal(authorization, observed.Authorization);
        Assert.Null(observed.ConsumerId);
        Assert.Null(observed.ConsumerProof);
        Assert.Null(observed.ActingRole);
        Assert.Null(context.Items[AccessSecurityMiddleware.RequestAccessItem]);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Narrow_runner_bearer_keeps_its_own_scope_even_with_a_browser_cookie(bool fileBacked, bool browserCookie)
    {
        var runner = await _upstream.CreatePrincipalAsync(
            new("narrow-runner", TaskServerPrincipalKinds.Runner, [TaskServerScopes.TasksRead], "runner-a"), "setup", default);
        await using var app = CreateEdge(fileBacked);
        var session = browserCookie ? SignIn(app) : null;

        var context = await Send(app, authorization: "Bearer " + runner.Credential, session: session);

        var observed = Assert.Single(_observed);
        Assert.Equal("Bearer " + runner.Credential, observed.Authorization);
        Assert.Null(observed.ConsumerId);
        Assert.Null(observed.ConsumerProof);
        Assert.Null(observed.ActingRole);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Null(context.Items[AccessSecurityMiddleware.RequestAccessItem]);
    }

    [Fact]
    public async Task Authorized_browser_uses_edge_credential_and_reloads_token_and_proof_on_next_request()
    {
        await using var app = CreateEdge();
        var session = SignIn(app);
        var first = await Send(app, session: session, callerProof: true);
        var rotated = (await _upstream.CreatePrincipalAsync(
            new("studio-edge-next", TaskServerPrincipalKinds.Studio,
                [TaskServerScopes.TasksRead, TaskServerScopes.Management]), "setup", default)).Credential;
        var nextProof = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        File.WriteAllText(TokenPath, rotated);
        WriteProof(nextProof);

        var second = await Send(app, session: session);

        Assert.Equal(StatusCodes.Status200OK, first.Response.StatusCode);
        Assert.Equal(StatusCodes.Status200OK, second.Response.StatusCode);
        Assert.IsType<RequestAccess>(first.Items[AccessSecurityMiddleware.RequestAccessItem]);
        Assert.IsType<RequestAccess>(second.Items[AccessSecurityMiddleware.RequestAccessItem]);
        Assert.Equal(new[] { "Bearer " + _edgeToken, "Bearer " + rotated }, _observed.Select(request => request.Authorization));
        Assert.Equal(new[] { _proof, nextProof }, _observed.Select(request => request.ConsumerProof));
        Assert.All(_observed, request =>
        {
            Assert.Equal("studio-edge", request.ConsumerId);
            Assert.Equal(StudioRoles.Owner, request.ActingRole);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Browser_without_session_or_valid_csrf_never_reaches_the_edge_client(bool invalidCsrf)
    {
        await using var app = CreateEdge();
        var session = invalidCsrf ? SignIn(app) : null;

        var context = await Send(app, session: session, method: "POST");

        Assert.Equal(invalidCsrf ? StatusCodes.Status403Forbidden : StatusCodes.Status401Unauthorized,
            context.Response.StatusCode);
        Assert.Empty(_observed);
    }

    [Fact]
    public async Task Bearer_passthrough_does_not_read_the_edge_credential_or_proof_files()
    {
        await using var app = CreateEdge();
        File.Delete(TokenPath);
        File.WriteAllText(TokenPath + ".consumer-proof", "invalid proof file");
        var authorization = "Bearer " + Guid.NewGuid().ToString("N");

        var context = await Send(app, authorization: authorization);

        Assert.Equal(authorization, Assert.Single(_observed).Authorization);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task Bearer_passthrough_keeps_the_callers_own_consumer_metadata()
    {
        await using var app = CreateEdge();
        var authorization = "Bearer " + Guid.NewGuid().ToString("N");

        await Send(app, authorization: authorization, callerProof: true);

        var observed = Assert.Single(_observed);
        Assert.Equal(authorization, observed.Authorization);
        Assert.Equal("caller-consumer", observed.ConsumerId);
        Assert.Equal("caller-proof", observed.ConsumerProof);
    }

    [Fact]
    public async Task Authorized_browser_can_use_a_directly_configured_edge_credential()
    {
        await using var app = CreateEdge(fileBacked: false);

        var context = await Send(app, session: SignIn(app), callerProof: true);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var observed = Assert.Single(_observed);
        Assert.Equal("Bearer " + _edgeToken, observed.Authorization);
        Assert.Equal("configured-edge-consumer", observed.ConsumerId);
        Assert.Null(observed.ConsumerProof);
        Assert.Equal(StudioRoles.Owner, observed.ActingRole);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Local_profile_requires_the_existing_registered_loopback_identity_for_edge_credentials(bool registered)
    {
        await using var app = CreateEdge(profile: "local");

        var context = await Send(app, localIdentity: registered);

        Assert.Equal(registered ? StatusCodes.Status200OK : StatusCodes.Status401Unauthorized,
            context.Response.StatusCode);
        var observed = Assert.Single(_observed);
        Assert.Equal(registered ? "Bearer " + _edgeToken : null, observed.Authorization);
        Assert.Equal(registered ? _proof : null, observed.ConsumerProof);
        Assert.Equal(registered ? StudioRoles.Operator : null, observed.ActingRole);
    }

    private WebApplication CreateEdge(bool fileBacked = true, string profile = "networked")
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Security:Profile"] = profile,
            ["TaskRepository"] = Path.Combine(_root, "studio"),
            ["TaskServer:BaseUrl"] = "https://upstream.test",
            ["TaskServer:AuthTokenFile"] = fileBacked ? TokenPath : null,
            ["TaskServer:AuthToken"] = fileBacked ? null : _edgeToken,
            ["TaskServer:ConsumerId"] = "configured-edge-consumer",
        });
        builder.Services.AddSingleton(provider => new AccessSecurityStore(
            builder.Configuration, NullLogger<AccessSecurityStore>.Instance, _clock));
        builder.Services.AddTaskServerPlaneProxy(builder.Configuration);
        // Exercise the production credential handler with an in-process upstream.
        builder.Services.ConfigureHttpClientDefaults(client =>
            client.ConfigurePrimaryHttpMessageHandler(() => new UpstreamHandler(this)));
        var app = builder.Build();
        app.MapTaskServerPlaneProxy();
        return app;
    }

    private static string SignIn(WebApplication app)
        => app.Services.GetRequiredService<AccessSecurityStore>()
            .Bootstrap(new BootstrapRequest("proxy.owner", "correct horse battery staple!", null)).SessionToken;

    private static async Task<DefaultHttpContext> Send(WebApplication app, string? authorization = null,
        string? session = null, string method = "GET", bool localIdentity = false, bool callerProof = false)
    {
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).Single();
        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.SetEndpoint(endpoint);
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("studio.test");
        context.Request.Path = "/api/v1/management/status";
        context.Request.Method = method;
        context.Request.Headers["Sec-Fetch-Site"] = "same-origin";
        context.Request.Headers.Origin = "https://studio.test";
        if (localIdentity)
        {
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            context.Request.Headers["X-Client-Id"] = DefaultClientIdentity.Id;
            context.Items["ClientId"] = DefaultClientIdentity.Id;
        }
        if (authorization is not null) context.Request.Headers.Authorization = authorization;
        if (session is not null) context.Request.Headers.Cookie = $"{AccessSecurityStore.SessionCookieName}={session}";
        // Caller role claims must never become trusted edge metadata.
        context.Request.Headers[CredentialViewProtocol.ActingRoleHeader] = StudioRoles.Owner;
        if (callerProof)
        {
            context.Request.Headers["X-Principal-Consumer-Id"] = "caller-consumer";
            context.Request.Headers["X-Principal-Consumer-Proof"] = "caller-proof";
        }
        context.Response.Body = new MemoryStream();
        await new AccessSecurityMiddleware(endpoint.RequestDelegate!, app.Configuration,
            app.Services.GetRequiredService<AccessSecurityStore>()).InvokeAsync(context);
        return context;
    }

    private void WriteProof(string proof)
    {
        File.WriteAllText(TokenPath + ".consumer-proof", "studio-edge\n" + proof + "\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(TokenPath + ".consumer-proof", UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public Task DisposeAsync()
    {
        Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    private sealed record ObservedRequest(string? Authorization, string? ConsumerId, string? ConsumerProof, string? ActingRole);

    private sealed class UpstreamHandler(TaskServerProxyCredentialBoundaryTests fixture) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? Header(string name) => request.Headers.TryGetValues(name, out var values) ? string.Join(", ", values) : null;
            fixture._observed.Add(new(Header("Authorization"), Header("X-Principal-Consumer-Id"),
                Header("X-Principal-Consumer-Proof"), Header(CredentialViewProtocol.ActingRoleHeader)));
            var context = new DefaultHttpContext();
            context.Request.Path = request.RequestUri!.AbsolutePath;
            context.Request.Method = request.Method.Method;
            foreach (var header in request.Headers) context.Request.Headers[header.Key] = header.Value.ToArray();
            context.Response.Body = new MemoryStream();
            context.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
                new EndpointMetadataCollection(new Upstream.TaskServerScopeMetadata(TaskServerScopes.Management)), "management"));
            var bootstrap = Upstream.TaskServerBootstrapOptions.Load(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["AUTH"] = "bearer" }).Build());
            await new Upstream.TaskServerAuthenticationMiddleware(_ => Task.CompletedTask, bootstrap, fixture._upstream,
                NullLogger<Upstream.TaskServerAuthenticationMiddleware>.Instance).InvokeAsync(context);
            return new HttpResponseMessage((HttpStatusCode)context.Response.StatusCode)
            {
                Content = new ByteArrayContent(((MemoryStream)context.Response.Body).ToArray()),
            };
        }
    }
}
