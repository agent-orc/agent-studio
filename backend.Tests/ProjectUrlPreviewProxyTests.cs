using AgentStudio.Registry;
using AgentStudio.Security;
using AgentStudio.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using System.Net;
using System.Net.Http.Headers;
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
