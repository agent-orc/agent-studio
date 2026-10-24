using AgentStudio.Security;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentStudio.Tests;

public sealed class NetworkedCutoverContractTests
{
    [Fact]
    public void Public_edge_requires_an_account_specific_client_certificate()
    {
        var root = FindRepositoryRoot();
        var stack = Path.Combine(root, "deploy", "compose", "monolith-networked");
        var publicCaddy = File.ReadAllText(Path.Combine(stack, "Caddyfile.public"));
        var cloudflareCaddy = File.ReadAllText(Path.Combine(stack, "Caddyfile.cloudflare"));
        var frontendCaddy = File.ReadAllText(Path.Combine(stack, "Caddyfile.frontend"));
        var compose = File.ReadAllText(Path.Combine(stack, "compose.yaml"));

        Assert.Contains("client_auth {", publicCaddy);
        Assert.Contains("mode require_and_verify", publicCaddy);
        Assert.Contains("trust_pool file /etc/caddy/origin-pull/ca.pem", publicCaddy);
        Assert.Contains("client_auth {", cloudflareCaddy);
        Assert.Contains("mode require_and_verify", cloudflareCaddy);
        Assert.Contains("trust_pool file /etc/caddy/origin-pull/ca.pem", cloudflareCaddy);
        Assert.Contains("header_up X-Forwarded-For {client_ip}", publicCaddy);
        Assert.Contains("trusted_proxies static 172.30.83.2", frontendCaddy);
        Assert.Contains("handle @health {", frontendCaddy);
        Assert.DoesNotContain("respond @health 200", frontendCaddy);
        Assert.Contains("Security__TrustedProxyAddresses: 172.30.83.3,172.30.83.2", compose);
        Assert.Contains("0.0.0.0:443:443", compose);
        Assert.Contains("${STUDIO_DATA_DIR}/origin-pull:/etc/caddy/origin-pull:ro", compose);
    }

    [Theory]
    [InlineData("172.30.83.3", true)]
    [InlineData("192.168.1.2", true)]
    [InlineData("10.0.0.1", true)]
    [InlineData("fd00::1", true)]
    [InlineData("203.0.113.15", false)]
    [InlineData("2001:db8::1", false)]
    public void Login_address_diagnostic_rejects_internal_peers(string raw, bool expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Security:TrustedProxyAddresses"] = "172.30.83.3,172.30.83.2"
        }).Build();
        Assert.Equal(expected, NetworkedLoginAddressHealth.IsInvalid(System.Net.IPAddress.Parse(raw), configuration));
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, ".agent-studio", "project.yml")))
                return directory.FullName;
        throw new InvalidOperationException("Repository root not found.");
    }
}
