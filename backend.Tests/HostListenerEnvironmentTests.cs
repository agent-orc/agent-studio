using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Direct matrix for the boundary that keeps a gate child from inheriting the
/// listener configuration of the Studio backend that started it (AGT-2840).
/// </summary>
public sealed class HostListenerEnvironmentTests
{
    [Theory]
    [InlineData("ASPNETCORE_URLS", true)]
    [InlineData("aspnetcore_urls", true)]
    [InlineData("URLS", true)]
    [InlineData("urls", true)]
    [InlineData("DOTNET_URLS", true)]
    [InlineData("ASPNETCORE_HTTP_PORTS", true)]
    [InlineData("ASPNETCORE_HTTPS_PORTS", true)]
    [InlineData("ASPNETCORE_HTTPS_PORT", true)]
    [InlineData("Kestrel__Endpoints__Http__Url", true)]
    [InlineData("KESTREL__ENDPOINTS__HTTPS__URL", true)]
    [InlineData("Kestrel__Certificates__Default__Path", true)]
    [InlineData("ASPNETCORE_ENVIRONMENT", false)]
    [InlineData("PATH", false)]
    [InlineData("NPM_CONFIG_CACHE", false)]
    [InlineData("TaskRepository", false)]
    [InlineData("URLSHORTENER", false)]
    [InlineData("", false)]
    public void Carries_names_only_the_variables_that_configure_a_listener(string name, bool carries)
        => Assert.Equal(carries, HostListenerEnvironment.Carries(name));

    [Fact]
    public void Carries_rejects_a_missing_name() => Assert.False(HostListenerEnvironment.Carries(null));

    [Fact]
    public void RemoveFrom_drops_every_listener_variable_and_keeps_the_rest()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ASPNETCORE_URLS"] = "http://127.0.0.1:5031",
            ["Kestrel__Endpoints__Http__Url"] = "http://127.0.0.1:5031",
            ["PATH"] = "/usr/bin",
            ["NPM_CONFIG_CACHE"] = "/cache/npm",
        };

        var removed = HostListenerEnvironment.RemoveFrom(environment);

        Assert.Equal(
            ["ASPNETCORE_URLS", "Kestrel__Endpoints__Http__Url"],
            removed.Order(StringComparer.Ordinal));
        Assert.Equal(["NPM_CONFIG_CACHE", "PATH"], environment.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void RemoveFrom_leaves_an_environment_without_a_listener_untouched()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal) { ["PATH"] = "/usr/bin" };

        Assert.Empty(HostListenerEnvironment.RemoveFrom(environment));
        Assert.Equal("/usr/bin", environment["PATH"]);
    }
}
