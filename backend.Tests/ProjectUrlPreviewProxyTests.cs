using AgentStudio.Registry;
using AgentStudio.Shared;
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
}
