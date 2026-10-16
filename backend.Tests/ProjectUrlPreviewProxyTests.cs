using AgentStudio.Registry;
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
            var project = registry.EnsureProjectForStorage(root, "Preview", "ws-1", new FakeTimeProvider());
            var registered = registry.AddUrl(project.Id, "Site", "http://127.0.0.1:4200/app").Urls.Single();
            var context = new DefaultHttpContext();
            context.Response.Body = new MemoryStream();
            var result = await ProjectUrlPreviewProxy.HandleAsync(project.Id, registered.Id, "", context,
                registry, new StubHttpClientFactory(contentType));
            await result.ExecuteAsync(context);

            Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
            Assert.Equal("sandbox allow-scripts; form-action 'none'",
                context.Response.Headers.ContentSecurityPolicy.ToString());
            Assert.DoesNotContain("allow-same-origin", context.Response.Headers.ContentSecurityPolicy.ToString());
        }
        finally { Directory.Delete(root, true); }
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
}
