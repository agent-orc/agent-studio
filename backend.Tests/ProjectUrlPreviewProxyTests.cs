using AgentStudio.Registry;
using AgentStudio.Security;
using AgentStudio.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ProjectUrlPreviewProxyTests
{
    private static readonly ProjectRecord Project = new()
    {
        Id = "PROJ-001",
        Urls = [new ProjectUrlRecord { Id = "site", Url = "http://127.0.0.1:4200/app" }],
    };

    [Fact]
    public void Only_registered_project_urls_can_be_targets()
    {
        Assert.Equal("http://127.0.0.1:4200/app/assets/main.js?v=1",
            ProjectUrlPreviewProxy.Target(Project, "site", "assets/main.js", "?v=1")?.ToString());
        Assert.Null(ProjectUrlPreviewProxy.Target(Project, "missing", "", null));
        Assert.Null(ProjectUrlPreviewProxy.Target(null, "site", "", null));
        Assert.Null(ProjectUrlPreviewProxy.Target(Project, "site", "../admin", null));
        Assert.Null(ProjectUrlPreviewProxy.Target(Project, "site", "http:evil", null));
    }

    [Fact]
    public void Html_assets_and_base_stay_on_the_registered_proxy_route()
    {
        const string prefix = "/api/projects/PROJ-001/urls/site/preview/";
        var html = ProjectUrlPreviewProxy.RewriteHtml(
            "<html><head><base href=\"/\"></head><body><script src=\"/main.js\"></script><a href=\"//cdn.example/app\">CDN</a></body></html>",
            prefix);
        Assert.Contains($"<base href=\"{prefix}\">", html);
        Assert.Contains($"src=\"{prefix}main.js\"", html);
        Assert.Contains("href=\"//cdn.example/app\"", html);
        var script = ProjectUrlPreviewProxy.RewriteModuleImports(
            "import '/@vite/client'; export { app } from \"/src/app.js\"; import('/src/lazy.js'); fetch('/api/tasks');",
            prefix);
        Assert.Contains($"import '{prefix}@vite/client'", script);
        Assert.Contains($"from \"{prefix}src/app.js\"", script);
        Assert.Contains($"import('{prefix}src/lazy.js')", script);
        Assert.Contains("fetch('/api/tasks')", script);
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("image/svg+xml")]
    public async Task Preview_response_is_sandboxed_for_direct_navigation(string contentType)
    {
        var root = Path.Combine(Path.GetTempPath(), "studio-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = root,
            }).Build();
            var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
            var clock = new FakeTimeProvider();
            var access = new ProjectUrlPreviewAccess(clock);
            var project = registry.EnsureProjectForStorage(root, "Preview", "ws-1", clock);
            var registered = registry.AddUrl(project.Id, "Site", "http://127.0.0.1:4200/app").Urls.Single();
            var context = new DefaultHttpContext();
            context.Response.Body = new MemoryStream();
            var result = await ProjectUrlPreviewProxy.HandleAsync(project.Id, registered.Id, "", context,
                registry, new StubHttpClientFactory(contentType), access);
            await result.ExecuteAsync(context);

            Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
            Assert.Equal("sandbox allow-scripts; form-action 'none'; frame-ancestors 'self'",
                context.Response.Headers.ContentSecurityPolicy.ToString());
            Assert.DoesNotContain("allow-same-origin", context.Response.Headers.ContentSecurityPolicy.ToString());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Module_and_dynamic_import_assets_use_a_scoped_opaque_origin_capability()
    {
        var root = Path.Combine(Path.GetTempPath(), "studio-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var clock = new FakeTimeProvider();
            var access = new ProjectUrlPreviewAccess(clock);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = root,
            }).Build();
            var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
            var project = registry.EnsureProjectForStorage(root, "Preview", "ws-1", clock);
            var registered = registry.AddUrl(project.Id, "Site", "http://127.0.0.1:4200/app").Urls.Single();
            var upstream = new FixtureHttpClientFactory();
            var document = await Send("", null);
            Assert.Contains("sandbox allow-scripts", document.Context.Response.Headers.ContentSecurityPolicy.ToString());
            Assert.False(document.Context.Response.Headers.ContainsKey("Access-Control-Allow-Origin"));
            Assert.Equal("no-referrer", document.Context.Response.Headers["Referrer-Policy"].ToString());
            var prefix = $"/api/projects/{project.Id}/urls/{registered.Id}/preview/_asset/";
            var token = document.Body.Split(prefix)[1].Split('/')[0];
            Assert.Contains($"src=\"{prefix}{token}/main.js\"", document.Body);

            var module = await Send($"_asset/{token}/main.js", "null");
            Assert.Contains("import('./chunk.js')", module.Body);
            Assert.Contains($"from '{prefix}{token}/dependency.js'", module.Body);
            Assert.Equal("null", module.Context.Response.Headers.AccessControlAllowOrigin.ToString());
            Assert.Equal("Origin", module.Context.Response.Headers.Vary.ToString());
            Assert.False(module.Context.Response.Headers.ContainsKey("Access-Control-Allow-Credentials"));
            var chunk = await Send($"_asset/{token}/chunk.js", "null");
            Assert.Contains("window.previewLoaded = true", chunk.Body);
            Assert.Equal("null", chunk.Context.Response.Headers.AccessControlAllowOrigin.ToString());
            var dependency = await Send($"_asset/{token}/dependency.js", "null");
            Assert.Contains("export const value", dependency.Body);
            Assert.Equal(["/app/", "/app/main.js", "/app/chunk.js", "/app/dependency.js"], upstream.Paths);

            Assert.False(access.Allows(token, project.Id, "another-url"));
            clock.Advance(TimeSpan.FromMinutes(31));
            Assert.False(access.Allows(token, project.Id, registered.Id));

            async Task<(DefaultHttpContext Context, string Body)> Send(string path, string? origin)
            {
                var context = new DefaultHttpContext();
                context.Response.Body = new MemoryStream();
                if (origin is not null) context.Request.Headers.Origin = origin;
                var result = await ProjectUrlPreviewProxy.HandleAsync(project.Id, registered.Id, path,
                    context, registry, upstream, access);
                await result.ExecuteAsync(context);
                context.Response.Body.Position = 0;
                return (context, await new StreamReader(context.Response.Body).ReadToEndAsync());
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Opaque_preview_capability_never_authorizes_a_studio_api()
    {
        var root = Path.Combine(Path.GetTempPath(), "studio-preview-security-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var clock = new FakeTimeProvider();
            var access = new ProjectUrlPreviewAccess(clock);
            var token = access.Issue("PROJ-001", "site");
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:Profile"] = "networked",
                ["TaskRepository"] = root,
            }).Build();
            var store = new AccessSecurityStore(config, NullLogger<AccessSecurityStore>.Instance, clock);
            var session = store.Bootstrap(new BootstrapRequest("preview.owner", "correct horse battery staple!", null)).SessionToken;
            var forwarded = false;
            var middleware = new AccessSecurityMiddleware(_ =>
            {
                forwarded = true;
                return Task.CompletedTask;
            }, config, store, previewAccess: access);

            var asset = Request($"/api/projects/PROJ-001/urls/site/preview/_asset/{token}/main.js");
            await middleware.InvokeAsync(asset);
            Assert.True(forwarded);
            forwarded = false;

            foreach (var method in new[] { "POST", "HEAD", "OPTIONS" })
            {
                var nonGet = Request($"/api/projects/PROJ-001/urls/site/preview/_asset/{token}/main.js");
                nonGet.Request.Method = method;
                await middleware.InvokeAsync(nonGet);
                Assert.False(forwarded);
                Assert.True(nonGet.Response.StatusCode >= 400);
            }
            var cleartext = Request($"/api/projects/PROJ-001/urls/site/preview/_asset/{token}/main.js");
            cleartext.Request.Scheme = "http";
            await middleware.InvokeAsync(cleartext);
            Assert.False(forwarded);
            Assert.Equal(StatusCodes.Status426UpgradeRequired, cleartext.Response.StatusCode);

            var api = Request("/api/auth/session");
            api.Request.Headers.Cookie = $"{AccessSecurityStore.SessionCookieName}={session}";
            await middleware.InvokeAsync(api);
            Assert.False(forwarded);
            Assert.Equal(StatusCodes.Status403Forbidden, api.Response.StatusCode);
            Assert.False(api.Response.Headers.ContainsKey("Access-Control-Allow-Origin"));

            var otherProject = Request($"/api/projects/PROJ-002/urls/site/preview/_asset/{token}/main.js");
            await middleware.InvokeAsync(otherProject);
            Assert.False(forwarded);
            Assert.Equal(StatusCodes.Status403Forbidden, otherProject.Response.StatusCode);

            var foreignOrigin = Request($"/api/projects/PROJ-001/urls/site/preview/_asset/{token}/main.js");
            foreignOrigin.Request.Headers.Origin = "https://unrelated.example";
            await middleware.InvokeAsync(foreignOrigin);
            Assert.False(forwarded);
            Assert.Equal(StatusCodes.Status401Unauthorized, foreignOrigin.Response.StatusCode);
        }
        finally { Directory.Delete(root, true); }

        static DefaultHttpContext Request(string path)
        {
            var context = new DefaultHttpContext();
            context.Request.Scheme = "https";
            context.Request.Method = HttpMethods.Get;
            context.Request.Path = path;
            context.Request.Headers.Origin = "null";
            context.Response.Body = new MemoryStream();
            return context;
        }
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("[::1]")]
    [InlineData("[::ffff:127.0.0.1]")]
    [InlineData("0.0.0.0")]
    [InlineData("10.0.0.5")]
    [InlineData("172.16.0.5")]
    [InlineData("192.168.1.5")]
    [InlineData("169.254.169.254")]
    [InlineData("100.100.100.200")]
    [InlineData("168.63.129.16")]
    [InlineData("[fd00:ec2::254]")]
    [InlineData("[fe80::1]")]
    [InlineData("[fc00::1]")]
    public async Task Unapproved_internal_preview_is_refused_before_connecting(string host)
    {
        var connections = new List<IPEndPoint>();
        var connector = new ProjectUrlDestinationConnector((_, _) => throw new InvalidOperationException("Literal resolved"),
            (endpoint, _) => { connections.Add(endpoint); return Task.FromResult<Stream>(new MemoryStream()); });
        using var fixture = new PreviewFixture($"http://{host}:4200/app");
        using var client = new HttpClient(connector.CreateHandler(_ => null));
        var (context, result) = await fixture.Send(new SingleClientFactory(client));

        Assert.Equal(StatusCodes.Status502BadGateway, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Equal(0, context.Response.Body.Length);
        Assert.Empty(connections);
        var (assetContext, assetResult) = await fixture.Send(new SingleClientFactory(client), asset: true);
        Assert.Equal(StatusCodes.Status502BadGateway, Assert.IsAssignableFrom<IStatusCodeHttpResult>(assetResult).StatusCode);
        Assert.Equal(0, assetContext.Response.Body.Length);
        Assert.Empty(connections);
    }

    [Fact]
    public async Task Rebinding_and_failed_public_endpoints_never_connect_to_an_unchecked_fallback()
    {
        var publicIp = IPAddress.Parse("93.184.216.34");
        var loopback = IPAddress.Loopback;
        var answers = new Queue<IPAddress[]>([[loopback, publicIp], [loopback], [publicIp, loopback]]);
        var connections = new List<IPEndPoint>();
        var fail = false;
        var connector = new ProjectUrlDestinationConnector((_, _) => Task.FromResult(answers.Dequeue()), (endpoint, _) =>
        {
            connections.Add(endpoint);
            if (fail) throw new SocketException((int)SocketError.ConnectionRefused);
            return Task.FromResult<Stream>(new MemoryStream());
        });
        var target = new Uri("https://changing.example/app");
        using var first = await connector.ConnectAsync(target, null, CancellationToken.None);
        await Assert.ThrowsAsync<HttpRequestException>(() => connector.ConnectAsync(target, null, CancellationToken.None));
        fail = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => connector.ConnectAsync(target, null, CancellationToken.None));
        Assert.Equal([publicIp, publicIp], connections.Select(endpoint => endpoint.Address));
        Assert.All(connections, endpoint => Assert.Equal(443, endpoint.Port));
        Assert.Empty(answers);
    }

    [Fact]
    public async Task Local_permission_requires_the_matching_active_process_origin_and_port()
    {
        var url = new ProjectUrlRecord
        {
            Id = "site", Url = "http://localhost:4200/app",
            StartRule = new() { Command = "fixture-server", Port = 4200, HealthUrl = "http://localhost:4200/health" },
        };
        var project = Project with { Urls = [url] };
        var process = OwnedProcess(project, url);
        var target = new Uri(url.Url);
        var permission = ProjectUrlDestinationConnector.LocalPermission(project, url, process, target);
        Assert.NotNull(permission);
        Assert.True(permission.Allows(target, IPAddress.Loopback));
        Assert.True(permission.Allows(target, IPAddress.Parse("::ffff:127.0.0.1")));
        Assert.False(permission.Allows(target, IPAddress.Parse("169.254.169.254")));
        Assert.False(permission.Allows(target, IPAddress.Parse("10.0.0.5")));
        Assert.Null(ProjectUrlDestinationConnector.LocalPermission(project, url, null, target));
        Assert.Null(ProjectUrlDestinationConnector.LocalPermission(project, url, process with { State = "stopped" }, target));
        Assert.Null(ProjectUrlDestinationConnector.LocalPermission(project, url, process with { ProjectId = "other" }, target));
        Assert.Null(ProjectUrlDestinationConnector.LocalPermission(project, url, process with { UrlId = "other" }, target));
        Assert.Null(ProjectUrlDestinationConnector.LocalPermission(project, url, process with { Command = "other" }, target));
        Assert.Null(ProjectUrlDestinationConnector.LocalPermission(project, url with { StartRule = null }, process, target));
        Assert.Null(ProjectUrlDestinationConnector.LocalPermission(project, url, process, new Uri("http://localhost:4201/app")));
        Assert.Null(ProjectUrlDestinationConnector.LocalPermission(project, url, process, new Uri("https://localhost:4200/app")));
        Assert.NotNull(ProjectUrlDestinationConnector.LocalPermission(project, url, process, new Uri(url.StartRule.HealthUrl)));

        // Even an approved localhost name cannot rebind to a private network.
        var connector = new ProjectUrlDestinationConnector((_, _) => Task.FromResult(new[] { IPAddress.Parse("10.0.0.5") }),
            (_, _) => throw new InvalidOperationException("Blocked endpoint connected"));
        await Assert.ThrowsAsync<HttpRequestException>(() => connector.ConnectAsync(target, permission, CancellationToken.None));
    }

    [Fact]
    public void Local_permission_never_borrows_another_projects_process_for_missing_or_partial_owners()
    {
        var url = new ProjectUrlRecord
        {
            Id = "site", Url = "http://localhost:4200/app",
            StartRule = new() { Command = "fixture-server", Port = 4200 },
        };
        var other = Project with { Id = "PROJ-002", Urls = [url] };
        var otherProcess = OwnedProcess(other, url);
        Assert.NotNull(ProjectUrlDestinationConnector.LocalPermission(other, url, otherProcess, new Uri(url.Url)));
        foreach (var owner in new (string ProjectId, string UrlId)?[]
                 { null, (null!, url.Id), (other.Id, null!), ("", url.Id), (other.Id, " ") })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url.Url);
            if (owner is { } tag) request.Options.Set(ProjectUrlDestinationConnector.ProjectUrlOption, tag);
            var permission = ProjectUrlDestinationConnector.ResolvePermission(request,
                _ => throw new InvalidOperationException("An incomplete owner must not search projects."),
                (_, _) => throw new InvalidOperationException("An incomplete owner must not search processes."));
            Assert.Null(permission);
        }
    }

    [Fact]
    public void Local_permission_resolves_only_the_tagged_project_and_url_and_rechecks_its_process()
    {
        var url = new ProjectUrlRecord
        {
            Id = "site", Url = "http://localhost:4200/app",
            StartRule = new() { Command = "fixture-server", Port = 4200 },
        };
        var project = Project with { Urls = [url] };
        var other = project with { Id = "PROJ-002" };
        var process = OwnedProcess(other, url);
        using var request = new HttpRequestMessage(HttpMethod.Get, url.Url);
        request.Options.Set(ProjectUrlDestinationConnector.ProjectUrlOption, (project.Id, url.Id));
        Assert.Null(Resolve());
        request.Options.Set(ProjectUrlDestinationConnector.ProjectUrlOption, (other.Id, "missing"));
        Assert.Null(Resolve());
        request.Options.Set(ProjectUrlDestinationConnector.ProjectUrlOption, ("PROJ-999", url.Id));
        Assert.Null(Resolve());
        request.Options.Set(ProjectUrlDestinationConnector.ProjectUrlOption, (other.Id, url.Id));
        Assert.Equal(other.Id, Resolve()?.ProjectId);
        Assert.Equal(url.Id, Resolve()?.UrlId);
        process = process with { State = "stopped" };
        Assert.Null(Resolve());

        ProjectUrlLocalPreviewPermission? Resolve() => ProjectUrlDestinationConnector.ResolvePermission(request,
            id => new[] { project, other }.SingleOrDefault(row => row.Id == id),
            (projectId, urlId) => process.ProjectId == projectId && process.UrlId == urlId ? process : null);
    }

    [Fact]
    public async Task Readiness_requests_carry_the_project_url_owner()
    {
        using var handler = new OwnerCheckingHandler(Project.Id, "site");
        using var client = new HttpClient(handler);
        var readiness = new ProjectUrlReadinessService(new SingleClientFactory(client),
            NullLogger<ProjectUrlReadinessService>.Instance);
        var result = await readiness.ProbeAsync(Project, Project.Urls.Single(), null, CancellationToken.None);
        Assert.Equal("healthy", result.Kind);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task Diagnostic_requests_carry_the_project_url_owner()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var url = Project.Urls.Single() with { Url = $"http://127.0.0.1:{port}/app" };
        using var handler = new OwnerCheckingHandler(Project.Id, url.Id);
        using var client = new HttpClient(handler);
        using var processes = new ProjectUrlProcessService(NullLogger<ProjectUrlProcessService>.Instance,
            new SingleClientFactory(client));
        var result = await processes.ProbeAsync(Project, url, timeout.Token);
        Assert.Equal(ProjectUrlDiagnosisClasses.Running, result.Classification);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task Approved_loopback_preview_works_with_real_socket_and_forwards_only_accept()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serve = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = socket.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var headers = new List<string>();
            while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 } line) headers.Add(line);
            var body = "<html><head></head><body>disposable-preview</body></html>";
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"), timeout.Token);
            return headers;
        }, timeout.Token);
        using var fixture = new PreviewFixture($"http://127.0.0.1:{port}/app");
        var url = fixture.Url with { StartRule = new() { Command = "fixture-server", Port = port } };
        var permission = ProjectUrlDestinationConnector.LocalPermission(fixture.Project, url,
            OwnedProcess(fixture.Project, url), new Uri(url.Url));
        Assert.NotNull(permission);
        using var client = new HttpClient(new ProjectUrlDestinationConnector().CreateHandler(_ => permission));
        var (context, _) = await fixture.Send(new SingleClientFactory(client), timeout.Token);
        var headers = await serve;
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Contains(headers, header => header == "Accept: text/html");
        Assert.DoesNotContain(headers, header => header.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase)
            || header.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase)
            || header.StartsWith("X-Fixture-Secret:", StringComparison.OrdinalIgnoreCase));
        context.Response.Body.Position = 0;
        Assert.Contains("disposable-preview", await new StreamReader(context.Response.Body).ReadToEndAsync(timeout.Token));
        Assert.Contains("sandbox allow-scripts", context.Response.Headers.ContentSecurityPolicy.ToString());
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(303)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task Off_origin_redirects_are_refused_and_same_origin_redirects_reenter_the_proxy(int code)
    {
        using var fixture = new PreviewFixture("https://preview.example/app");
        using var handler = new RedirectHandler((HttpStatusCode)code);
        using var client = new HttpClient(handler);
        handler.Location = new Uri("http://169.254.169.254/latest/meta-data");
        var (_, refused) = await fixture.Send(new SingleClientFactory(client));
        Assert.Equal(502, Assert.IsAssignableFrom<IStatusCodeHttpResult>(refused).StatusCode);
        handler.Location = new Uri("https://preview.example/app/next");
        var (_, allowed) = await fixture.Send(new SingleClientFactory(client));
        Assert.StartsWith($"/api/projects/{fixture.Project.Id}/urls/{fixture.Url.Id}/preview/next",
            Assert.IsAssignableFrom<Microsoft.AspNetCore.Http.HttpResults.RedirectHttpResult>(allowed).Url);
        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public void Both_named_clients_use_the_shared_transport_without_proxy_redirects_or_permission_reuse()
    {
        using var fixture = new PreviewFixture("http://127.0.0.1:4200/app");
        using var factory = new TestHostFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["TaskRepository"] = fixture.Root }));
            builder.ConfigureTestServices(TestHostFactory.RemoveBackgroundHostedServices);
        });
        var handlers = factory.Services.GetRequiredService<IHttpMessageHandlerFactory>();
        foreach (var name in new[] { "project-url-preview", "project-url-readiness" })
        {
            HttpMessageHandler handler = handlers.CreateHandler(name);
            while (handler is DelegatingHandler delegating) handler = delegating.InnerHandler!;
            var sockets = Assert.IsType<SocketsHttpHandler>(handler);
            Assert.NotNull(sockets.ConnectCallback);
            Assert.False(sockets.AllowAutoRedirect);
            Assert.False(sockets.UseProxy);
            Assert.False(sockets.UseCookies);
            Assert.Equal(TimeSpan.Zero, sockets.PooledConnectionLifetime);
        }
    }

    [Fact]
    public async Task Preview_and_readiness_reads_remain_project_read_and_operator_observe_while_url_writes_are_t2()
    {
        foreach (var route in new[] { "/api/projects/{}/urls/{}/preview/{}", "/api/projects/{}/urls/{}/readiness" })
        {
            Assert.Equal(IamPermissions.ProjectRead, RoutePermissionCatalog.Resolve("GET", route)?.Permission);
            var operation = await OperatorRequestClassifier.ClassifyAsync(new DefaultHttpContext(), safeMethod: true,
                evidence: null, (_, _) => null, _ => null);
            Assert.Equal(OperatorOperationIds.Observe, operation?.Id);
            Assert.Equal(OperatorTier.T0, operation?.Tier);
        }
        var write = OperatorOperationCatalog.ResolveWrite("PUT", "/api/projects/{}/urls/{}");
        Assert.NotNull(write);
        var exposure = OperatorOperationCatalog.Refine(write, new OperatorRequestFacts());
        Assert.Equal(OperatorOperationIds.ExposureChange, exposure?.Id);
        Assert.Equal(OperatorTier.T2, exposure?.Tier);
    }

    private static ProjectUrlProcessSnapshot OwnedProcess(ProjectRecord project, ProjectUrlRecord url)
        => new(project.Id, url.Id, url.StartRule!.Command, Path.GetTempPath(), "running", 1,
            DateTimeOffset.UnixEpoch, null, null, []);

    private sealed class PreviewFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "preview-destination-" + Guid.NewGuid().ToString("N"));
        internal ProjectRegistry Registry { get; }
        internal ProjectRecord Project { get; }
        internal ProjectUrlRecord Url { get; }
        private readonly ProjectUrlPreviewAccess _access = new(new FakeTimeProvider());

        internal PreviewFixture(string target)
        {
            Directory.CreateDirectory(Root);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["TaskRepository"] = Root }).Build();
            Registry = new ProjectRegistry(configuration, NullLogger<ProjectRegistry>.Instance);
            Project = Registry.EnsureProjectForStorage(Root, "Preview", "ws-1", new FakeTimeProvider());
            Project = Registry.AddUrl(Project.Id, "Site", target);
            Url = Project.Urls.Single();
        }

        internal async Task<(DefaultHttpContext Context, IResult Result)> Send(IHttpClientFactory clients,
            CancellationToken cancellationToken = default, bool asset = false)
        {
            var context = new DefaultHttpContext();
            context.RequestAborted = cancellationToken;
            context.Response.Body = new MemoryStream();
            context.Request.Headers.Accept = "text/html";
            context.Request.Headers.Authorization = "Bearer " + Guid.NewGuid().ToString("N");
            context.Request.Headers.Cookie = "fixture=" + Guid.NewGuid().ToString("N");
            context.Request.Headers["X-Fixture-Secret"] = Guid.NewGuid().ToString("N");
            var result = await ProjectUrlPreviewProxy.HandleAsync(Project.Id, Url.Id,
                asset ? $"_asset/{_access.Issue(Project.Id, Url.Id)}/main.js" : "", context, Registry, clients, _access);
            return (context, result);
        }

        public void Dispose() => Directory.Delete(Root, true);
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class OwnerCheckingHandler(string projectId, string urlId) : HttpMessageHandler
    {
        internal int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Assert.True(request.Options.TryGetValue(ProjectUrlDestinationConnector.ProjectUrlOption, out var owner));
            Assert.Equal((projectId, urlId), owner);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<main>ready</main>", Encoding.UTF8, "text/html"),
            });
        }
    }

    private sealed class RedirectHandler(HttpStatusCode code) : HttpMessageHandler
    {
        internal Uri Location { get; set; } = new("https://other.example");
        internal int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var response = new HttpResponseMessage(code);
            response.Headers.Location = Location;
            return Task.FromResult(response);
        }
    }

    private sealed class StubHttpClientFactory(string contentType) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler(contentType), disposeHandler: true);
    }

    private sealed class StubHandler(string contentType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html><head></head><body>preview</body></html>"),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            return Task.FromResult(response);
        }
    }

    private sealed class FixtureHttpClientFactory : IHttpClientFactory
    {
        public List<string> Paths { get; } = [];

        public HttpClient CreateClient(string name) => new(new FixtureHandler(Paths), disposeHandler: true);
    }

    private sealed class FixtureHandler(List<string> paths) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.Null(request.Headers.Authorization);
            paths.Add(path);
            var (body, type) = path switch
            {
                "/app/" => ("<html><head></head><body><script type=\"module\" src=\"/main.js\"></script></body></html>", "text/html"),
                "/app/main.js" => ("import { value } from '/dependency.js'; import('./chunk.js');", "text/javascript"),
                "/app/chunk.js" => ("window.previewLoaded = true;", "text/javascript"),
                "/app/dependency.js" => ("export const value = 7;", "text/javascript"),
                _ => throw new InvalidOperationException($"Unexpected preview fixture path: {path}"),
            };
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue(type);
            return Task.FromResult(response);
        }
    }
}
