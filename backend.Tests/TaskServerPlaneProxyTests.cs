using AgentStudio.Security;
using AgentStudio.Clients;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class TaskServerPlaneProxyTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("http://task-server.test", true)]
    [InlineData("https://task-server.test/control", true)]
    public void Only_an_explicit_absolute_HTTP_or_HTTPS_URL_selects_remote_mode(
        string? configured,
        bool expectedRemote)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskServer:BaseUrl"] = configured,
            })
            .Build();

        Assert.Equal(expectedRemote, TaskServerPlaneProxy.IsConfigured(configuration));
        Assert.Equal(!expectedRemote, EndpointMapping.MapsLocalV1(configuration));
    }

    [Fact]
    public void Standalone_base_url_disables_every_local_v1_owner()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskServer:BaseUrl"] = "http://127.0.0.1:5071",
            })
            .Build();

        Assert.False(EndpointMapping.MapsLocalV1(configuration));
        Assert.True(TaskServerPlaneProxy.IsConfigured(configuration));
    }

    [Theory]
    [InlineData("task-server")]
    [InlineData("ftp://task-server.test")]
    public void Invalid_standalone_base_url_cannot_fall_back_to_local_task_store(string configured)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskServer:BaseUrl"] = configured,
            })
            .Build();

        Assert.Throws<InvalidOperationException>(() => TaskServerPlaneProxy.IsConfigured(configuration));
        Assert.Throws<InvalidOperationException>(() => EndpointMapping.MapsLocalV1(configuration));
    }

    [Fact]
    public void Proxy_maps_one_catch_all_v1_surface()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["TaskServer:BaseUrl"] = "http://127.0.0.1:5071";
        builder.Services.AddTaskServerPlaneProxy(builder.Configuration);
        var app = builder.Build();

        Assert.True(app.MapTaskServerPlaneProxy());

        var routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .ToList();
        Assert.Equal(["/api/v1/{**path}"], routes);
    }

    [Fact]
    public async Task File_backed_proxy_uses_new_credential_on_next_request()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "generation-one");
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TaskServer:BaseUrl"] = "https://task-server.example",
                    ["TaskServer:AuthTokenFile"] = path,
                }).Build();
            var observed = new List<string?>();
            var observedProof = new List<string?>();
            var services = new ServiceCollection();
            services.AddTaskServerPlaneProxy(configuration);
            services.AddHttpClient(TaskServerPlaneProxy.ClientName)
                .ConfigurePrimaryHttpMessageHandler(() => new RecordingHandler(request =>
                {
                    observed.Add(request.Headers.Authorization?.Parameter);
                    observedProof.Add(request.Headers.TryGetValues("X-Principal-Consumer-Proof", out var values)
                        ? values.Single() : null);
                    return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
                }));
            using var provider = services.BuildServiceProvider();
            using var client = provider.GetRequiredService<IHttpClientFactory>()
                .CreateClient(TaskServerPlaneProxy.ClientName);
            await client.GetAsync("/api/v1/workspaces");
            File.WriteAllText(path, "generation-two");
            File.WriteAllText(path + ".consumer-proof", "studio-edge\n" + new string('c', 64) + "\n");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path + ".consumer-proof", UnixFileMode.UserRead | UnixFileMode.UserWrite);
            await client.GetAsync("/api/v1/workspaces");
            Assert.Equal(["generation-one", "generation-two"], observed);
            Assert.Equal([null, new string('c', 64)], observedProof);
        }
        finally
        {
            File.Delete(path + ".consumer-proof");
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(IamRoles.Viewer, "viewer")]
    [InlineData(IamRoles.Developer, "viewer")]
    [InlineData(IamRoles.Admin, "operator")]
    [InlineData(IamRoles.Owner, "owner")]
    [InlineData(null, null)]
    public void Proxy_acts_for_a_role_derived_from_the_bindings_of_the_authenticated_principal(
        string? workspaceRole, string? expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Security:Profile"] = "networked" })
            .Build();
        var context = new DefaultHttpContext();
        context.Request.Headers[CredentialViewProtocol.ActingRoleHeader] = StudioRoles.Owner;
        if (workspaceRole is not null)
        {
            var subject = new IamSubject(IamPrincipalKind.User, "usr", new HashSet<string>());
            RoleBinding[] bindings =
            [
                new()
                {
                    Id = "rb", PrincipalKind = IamPrincipalKind.User, PrincipalId = "usr", Role = workspaceRole,
                    Scope = IamScope.Workspace, Source = IamBindingSources.Manual,
                },
            ];
            context.Items[AccessSecurityMiddleware.RequestAccessItem] = new RequestAccess(
                subject, bindings, IamPermissions.WorkspaceRead, (bound, requested) => bound == requested);
        }
        // The legacy summary of the human principal no longer decides.
        var clock = new FakeTimeProvider();
        var now = clock.GetUtcNow().UtcDateTime;
        context.Items[AccessSecurityMiddleware.HumanPrincipalItem] = new HumanPrincipal(
            new StudioUser { Id = "usr", Username = "user", DisplayName = "User", Role = StudioRoles.Operator, PasswordHash = "redacted" },
            new StudioSession
            {
                Id = "ses", UserId = "usr", TokenHash = "redacted", CsrfHash = "redacted",
                CreatedAt = now, LastSeenAt = now,
                ExpiresAt = now.AddHours(1), AbsoluteExpiresAt = now.AddHours(8),
            });

        Assert.Equal(expected, TaskServerPlaneProxy.ActingRole(context, configuration));
        // A client-supplied claim is never forwarded verbatim.
        Assert.False(TaskServerPlaneProxy.ForwardsRequestHeader(CredentialViewProtocol.ActingRoleHeader));
        Assert.True(TaskServerPlaneProxy.ForwardsRequestHeader("Accept"));
    }

    [Fact]
    public void Local_profile_acts_as_operator_only_for_the_loopback_default_client()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Security:Profile"] = "local" })
            .Build();
        var local = new DefaultHttpContext();
        local.Request.Headers["X-Client-Id"] = "local-default";
        local.Items["ClientId"] = "local-default";
        local.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
        Assert.Equal(StudioRoles.Operator, TaskServerPlaneProxy.ActingRole(local, configuration));

        var remote = new DefaultHttpContext();
        remote.Request.Headers["X-Client-Id"] = "local-default";
        remote.Items["ClientId"] = "local-default";
        remote.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("192.0.2.10");
        Assert.Null(TaskServerPlaneProxy.ActingRole(remote, configuration));
    }

    [Fact]
    public async Task Proxied_local_credential_view_receives_the_registered_loopback_operator_role()
    {
        var repository = Path.Combine(Path.GetTempPath(), "credential-view-" + Guid.NewGuid().ToString("N"));
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Security:Profile"] = "local",
                    ["TaskServer:BaseUrl"] = "http://127.0.0.1:5071",
                    ["TaskRepository"] = repository,
                }).Build();
            var identities = new ClientIdentityStore(configuration, NullLogger<ClientIdentityStore>.Instance);
            var context = new DefaultHttpContext();
            context.Request.Path = "/api/v1/management/credential-view";
            context.Request.Headers["X-Client-Id"] = DefaultClientIdentity.Id;
            context.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
            string? role = null;
            var middleware = new ClientIdentityMiddleware(
                request => { role = TaskServerPlaneProxy.ActingRole(request, configuration); return Task.CompletedTask; },
                identities, configuration, NullLogger<ClientIdentityMiddleware>.Instance);

            await middleware.InvokeAsync(context);

            Assert.Equal(StudioRoles.Operator, role);
        }
        finally
        {
            if (Directory.Exists(repository)) Directory.Delete(repository, recursive: true);
        }
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
