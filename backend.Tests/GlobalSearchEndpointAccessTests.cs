using AgentStudio.Search;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class GlobalSearchEndpointAccessTests
{
    [Fact]
    public void HumanPrincipalFilter_AppliesProjectAccessToDossierAndWikiFrames()
    {
        var registry = new ProjectRegistry(
            new ConfigurationBuilder().Build(), NullLogger<ProjectRegistry>.Instance);
        var alpha = registry.EnsureProjectForStorage("/tmp/search-alpha", "Alpha", DefaultWorkspace.Id);
        registry.EnsureProjectForStorage("/tmp/search-beta", "Beta", DefaultWorkspace.Id);
        var user = new StudioUser
        {
            Id = "usr_scoped", Username = "scoped", DisplayName = "Scoped",
            Role = StudioRoles.Operator, PasswordHash = "unused", Projects = [alpha.Id],
            CreatedAt = DateTime.UtcNow, PasswordChangedAt = DateTime.UtcNow,
        };
        var session = new StudioSession
        {
            Id = "sess", UserId = user.Id, TokenHash = "h", CsrfHash = "h",
            CreatedAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1), AbsoluteExpiresAt = DateTime.UtcNow.AddHours(8),
        };
        var context = new DefaultHttpContext();
        context.Items[AccessSecurityMiddleware.HumanPrincipalItem] = new HumanPrincipal(user, session);
        var allowed = GlobalSearchEndpoints.AccessFilter(context, registry);
        GlobalSearchItem[] items =
        [
            new("dossiers", "Alpha", "#fff", "Allowed dossier", "AGT-W1"),
            new("dossiers", "Beta", "#fff", "Hidden dossier", "AGT-W2"),
        ];

        var dossier = Assert.IsType<GlobalSearchDossiersFrame>(GlobalSearchEndpoints.Authorize(
            new GlobalSearchStreamEvent("dossiers", new GlobalSearchDossiersFrame(items, 1, null)), allowed));
        var wiki = Assert.IsType<GlobalSearchWikiFrame>(GlobalSearchEndpoints.Authorize(
            new GlobalSearchStreamEvent("wiki", new GlobalSearchWikiFrame(
                items.Select(item => item with { Domain = "wiki" }).ToList(), 1, null)), allowed));

        Assert.Equal("Alpha", Assert.Single(dossier.Items).ProjectName);
        Assert.Equal("Alpha", Assert.Single(wiki.Items).ProjectName);
    }
}
