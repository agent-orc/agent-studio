using System.Net;
using System.Text.RegularExpressions;
using AgentStudio.Security;
using AgentStudio.TestSupport;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace AgentStudio.Tests;

public sealed class StudioResponseHeaderPolicyTests
{
    private static readonly string Stack = Path.Combine(
        RepositoryRoot.Find(), "deploy", "compose", "monolith-networked");

    [Fact]
    public void Response_without_own_policy_gets_the_full_baseline()
    {
        IHeaderDictionary headers = new HeaderDictionary();

        StudioResponseHeaderPolicy.Apply(headers);

        Assert.Equal(
            "default-src 'none'; frame-ancestors 'self'; base-uri 'none'; form-action 'none'",
            headers.ContentSecurityPolicy.ToString());
        Assert.Equal("SAMEORIGIN", headers.XFrameOptions.ToString());
        Assert.Equal("same-origin", headers["Cross-Origin-Opener-Policy"].ToString());
        Assert.Equal(StudioResponseHeaderPolicy.PermissionsPolicy, headers["Permissions-Policy"].ToString());
        Assert.Equal("same-origin", headers["Referrer-Policy"].ToString());
        Assert.Equal("nosniff", headers.XContentTypeOptions.ToString());
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("text/html; charset=utf-8")]
    [InlineData("application/xhtml+xml")]
    [InlineData("IMAGE/SVG+XML")]
    [InlineData("text/xml; charset=utf-8")]
    [InlineData("application/xml")]
    public void Renderable_documents_without_own_policy_are_sandboxed_without_script(string contentType)
    {
        IHeaderDictionary headers = new HeaderDictionary { ["Content-Type"] = contentType };

        StudioResponseHeaderPolicy.Apply(headers);

        var policy = headers.ContentSecurityPolicy.ToString();
        Assert.Equal(
            "sandbox; default-src 'none'; style-src 'unsafe-inline'; img-src data:; frame-ancestors 'self'",
            policy);
        Assert.DoesNotContain("script-src", policy);
        Assert.Equal("SAMEORIGIN", headers.XFrameOptions.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("application/json; charset=utf-8")]
    [InlineData("text/plain")]
    [InlineData("application/octet-stream")]
    [InlineData("text/htmlx")]
    [InlineData("image/png")]
    public void Other_api_responses_get_the_no_content_policy(string? contentType)
    {
        Assert.Equal(StudioResponseHeaderPolicy.ApiContentSecurityPolicy,
            StudioResponseHeaderPolicy.DefaultContentSecurityPolicy(contentType));
        Assert.DoesNotContain("unsafe-inline", StudioResponseHeaderPolicy.ApiContentSecurityPolicy);
    }

    [Theory]
    [InlineData("sandbox allow-scripts; form-action 'none'", false)]
    [InlineData("sandbox; default-src 'none'", false)]
    [InlineData("default-src 'none'; frame-ancestors 'none'", true)]
    [InlineData("default-src 'self'; sandboxed-name 'x'", true)]
    public void Endpoint_policy_is_kept_and_sandboxed_documents_get_no_frame_options(string ownPolicy, bool frameOptions)
    {
        IHeaderDictionary headers = new HeaderDictionary { ["Content-Security-Policy"] = ownPolicy };

        StudioResponseHeaderPolicy.Apply(headers);

        Assert.Equal(ownPolicy, headers.ContentSecurityPolicy.ToString());
        Assert.Equal(frameOptions, headers.ContainsKey("X-Frame-Options"));
        Assert.Equal("nosniff", headers.XContentTypeOptions.ToString());
        Assert.Equal(StudioResponseHeaderPolicy.PermissionsPolicy, headers["Permissions-Policy"].ToString());
    }

    [Fact]
    public void Endpoint_frame_and_referrer_choices_are_not_weakened()
    {
        IHeaderDictionary headers = new HeaderDictionary
        {
            ["X-Frame-Options"] = "DENY",
            ["Referrer-Policy"] = "no-referrer",
        };

        StudioResponseHeaderPolicy.Apply(headers);

        Assert.Equal("DENY", headers.XFrameOptions.ToString());
        Assert.Equal("no-referrer", headers["Referrer-Policy"].ToString());
    }

    [Fact]
    public void Frontend_shell_policy_matches_the_named_shell_constant_and_stays_out_of_the_api()
    {
        var frontend = File.ReadAllText(Path.Combine(Stack, "Caddyfile.frontend"));

        Assert.Contains($"Content-Security-Policy \"{StudioResponseHeaderPolicy.FrontendShellContentSecurityPolicy}\"", frontend);
        Assert.Single(Regex.Matches(frontend, "Content-Security-Policy"));
        Assert.Contains($"Permissions-Policy \"{StudioResponseHeaderPolicy.PermissionsPolicy}\"", frontend);
        Assert.Contains("X-Frame-Options SAMEORIGIN", frontend);
        Assert.Contains("Cross-Origin-Opener-Policy same-origin", frontend);
    }

    [Theory]
    [InlineData("Caddyfile.public")]
    [InlineData("Caddyfile.private")]
    [InlineData("Caddyfile.cloudflare")]
    public void Every_edge_mode_forces_hsts_and_forwards_only_its_resolved_client(string caddyfile)
    {
        var edge = File.ReadAllText(Path.Combine(Stack, caddyfile));

        Assert.Contains($">Strict-Transport-Security \"{StudioResponseHeaderPolicy.StrictTransportSecurity}\"", edge);
        Assert.Contains("header_up X-Forwarded-For {client_ip}", edge);
    }

    [Fact]
    public void Proxy_trust_chain_matches_the_compose_addresses()
    {
        var compose = File.ReadAllText(Path.Combine(Stack, "compose.yaml"));
        var frontend = File.ReadAllText(Path.Combine(Stack, "Caddyfile.frontend"));
        var edgeAddress = ServiceAddress(compose, "edge");
        var frontendAddress = ServiceAddress(compose, "frontend");

        Assert.Contains($"trusted_proxies static {edgeAddress}/32", frontend);
        Assert.Contains($"Security__TrustedProxyAddresses: {frontendAddress}", compose);
        Assert.Contains("./cloudflare-ips.caddy:/etc/caddy/cloudflare-ips.caddy:ro", compose);
        Assert.Contains("STUDIO_CLOUDFLARE_ALLOW_DIRECT: ${STUDIO_CLOUDFLARE_ALLOW_DIRECT:-false}", compose);
    }

    [Fact]
    public void Cloudflare_mode_trusts_only_the_versioned_ranges_and_refuses_other_peers()
    {
        var cloudflare = File.ReadAllText(Path.Combine(Stack, "Caddyfile.cloudflare"));
        var ranges = File.ReadAllLines(Path.Combine(Stack, "cloudflare-ips.caddy"))
            .Select(line => Regex.Match(line, @"^\s*([0-9A-Fa-f.:]+/\d+)"))
            .Where(match => match.Success)
            .Select(match => IPNetwork.Parse(match.Groups[1].Value))
            .ToList();

        Assert.Contains("import /etc/caddy/cloudflare-ips.caddy", cloudflare);
        Assert.Contains("client_ip_headers CF-Connecting-IP", cloudflare);
        Assert.Contains("vars {http.vars.trusted_proxy} false", cloudflare);
        Assert.Contains("not vars {env.STUDIO_CLOUDFLARE_ALLOW_DIRECT} true", cloudflare);
        Assert.Contains(ranges, range => range.BaseAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
        Assert.Contains(ranges, range => range.BaseAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6);
        Assert.DoesNotContain(ranges, range => range.Contains(IPAddress.Parse("172.30.83.2")));
        Assert.DoesNotContain(ranges, range => range.Contains(IPAddress.Loopback));
    }

    private static string ServiceAddress(string compose, string service)
    {
        var match = Regex.Match(
            compose,
            $@"^  {service}:\r?\n(?:(?!^  \S).*\r?\n)*?\s+ipv4_address: (?<address>[0-9.]+)",
            RegexOptions.Multiline);
        Assert.True(match.Success, $"compose.yaml names no address for {service}");
        return match.Groups["address"].Value;
    }
}
