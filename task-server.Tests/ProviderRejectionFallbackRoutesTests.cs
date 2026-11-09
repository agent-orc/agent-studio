using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace TaskServer.Tests;

public sealed class ProviderRejectionFallbackRoutesTests
{
    [Fact]
    public void Capacity_tries_sibling_before_equivalent_and_skips_duplicate_policy_route()
    {
        var sibling = new ProviderModelFallback("gpt-6-sol", "gpt-5.6-sol", "sibling", "codex", "medium");
        var equivalent = new ProviderModelFallback("gpt-6-sol", "claude-sonnet-5", "equivalent", "claude", "high");
        var routes = ProviderRejectionFallbackRoutes.Candidates("codex",
            false, sibling, equivalent, equivalent with { Reason = "policy" });

        Assert.Equal(["gpt-5.6-sol", "claude-sonnet-5"], routes.Select(route => route.To));
    }

    [Fact]
    public void Unauthorized_provider_excludes_its_sibling_on_the_affected_host()
    {
        var routes = ProviderRejectionFallbackRoutes.Candidates("codex",
            true,
            new("gpt-6-sol", "gpt-5.6-sol", "sibling", "codex", "medium"),
            new("gpt-6-sol", "claude-sonnet-5", "equivalent", "claude", "high"), null);

        Assert.Single(routes);
        Assert.Equal("claude", routes[0].CliType);
    }
}
