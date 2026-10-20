using AgentStudio.Security;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace AgentStudio.Tests;

public sealed class BrowserOriginPolicyTests
{
    [Theory]
    [InlineData("GET", true, "https://evil.example", "cross-site", null)]
    [InlineData("HEAD", true, null, null, null)]
    [InlineData("OPTIONS", true, "https://evil.example", null, null)]
    [InlineData("POST", false, null, null, null)]
    [InlineData("POST", false, "https://evil.example", "cross-site", null)]
    [InlineData("POST", true, "https://studio.test", null, null)]
    [InlineData("POST", true, "https://STUDIO.test:443", null, null)]
    [InlineData("POST", true, null, "same-origin", null)]
    [InlineData("PUT", true, "https://studio.test", "same-origin", null)]
    [InlineData("DELETE", true, "https://studio.test", null, null)]
    [InlineData("POST", true, "https://other.studio.test", null, "origin-mismatch")]
    [InlineData("POST", true, "http://studio.test", null, "origin-mismatch")]
    [InlineData("POST", true, "https://studio.test:8443", null, "origin-mismatch")]
    [InlineData("POST", true, "https://studio.test/path", null, "origin-mismatch")]
    [InlineData("POST", true, "null", null, "origin-mismatch")]
    [InlineData("PATCH", true, "https://other.studio.test", "same-origin", "origin-mismatch")]
    [InlineData("POST", true, null, "same-site", "cross-site-request")]
    [InlineData("POST", true, null, "cross-site", "cross-site-request")]
    [InlineData("POST", true, null, "none", "cross-site-request")]
    [InlineData("POST", true, "https://studio.test", "cross-site", "cross-site-request")]
    [InlineData("POST", true, null, null, "origin-required")]
    [InlineData("DELETE", true, "", "", "origin-required")]
    public void Unsafe_browser_requests_need_same_origin_proof(
        string method, bool browserCredential, string? origin, string? secFetchSite, string? rejection)
    {
        var decision = BrowserOriginPolicy.Decide(
            method, browserCredential, eventStream: false, origin, secFetchSite, "https", new HostString("studio.test"));

        Assert.Equal(rejection is null, decision.Allowed);
        Assert.Equal(rejection, decision.Code);
    }

    [Theory]
    [InlineData("GET", true, "https://other.studio.test", null, "origin-mismatch")]
    [InlineData("GET", true, "null", null, "origin-mismatch")]
    [InlineData("GET", true, null, "same-site", "cross-site-request")]
    [InlineData("GET", true, "https://studio.test", "cross-site", "cross-site-request")]
    [InlineData("POST", true, "https://other.studio.test", "same-origin", "origin-mismatch")]
    [InlineData("GET", true, "https://studio.test", "same-origin", null)]
    [InlineData("GET", true, "https://studio.test", null, null)]
    [InlineData("GET", true, null, "same-origin", null)]
    [InlineData("GET", true, null, null, null)]
    [InlineData("POST", true, null, null, null)]
    [InlineData("GET", false, "https://other.studio.test", "cross-site", null)]
    public void Event_streams_with_a_browser_credential_need_same_origin_proof_on_every_method(
        string method, bool browserCredential, string? origin, string? secFetchSite, string? rejection)
    {
        var decision = BrowserOriginPolicy.Decide(
            method, browserCredential, eventStream: true, origin, secFetchSite, "https", new HostString("studio.test"));

        Assert.Equal(rejection is null, decision.Allowed);
        Assert.Equal(rejection, decision.Code);
    }

    [Theory]
    [InlineData("/hubs/jobs", null, true)]
    [InlineData("/HUBS/workbench", null, true)]
    [InlineData("/hubs", null, true)]
    [InlineData("/api/tasks", "websocket", true)]
    [InlineData("/api/tasks", "WebSocket", true)]
    [InlineData("/api/tasks", "h2c", false)]
    [InlineData("/hubsx", null, false)]
    [InlineData("/api/tasks", null, false)]
    public void Hubs_and_websocket_upgrades_are_event_streams(string path, string? upgrade, bool expected)
    {
        IHeaderDictionary headers = new HeaderDictionary();
        if (upgrade is not null) headers.Upgrade = upgrade;

        Assert.Equal(expected, BrowserOriginPolicy.IsEventStream(path, headers));
    }

    [Theory]
    [InlineData("http://studio.test:5030", "http", "studio.test:5030", true)]
    [InlineData("http://studio.test", "http", "studio.test:80", true)]
    [InlineData("https://studio.test", "http", "studio.test", false)]
    [InlineData("https://user@studio.test", "https", "studio.test", false)]
    [InlineData("https://studio.test?x=1", "https", "studio.test", false)]
    [InlineData("not an origin", "https", "studio.test", false)]
    public void Origin_matches_scheme_host_and_effective_port(string origin, string scheme, string host, bool expected)
        => Assert.Equal(expected, BrowserOriginPolicy.OriginMatches(origin, scheme, new HostString(host)));

    [Theory]
    [InlineData("/api/auth/login", true)]
    [InlineData("/API/AUTH/BOOTSTRAP", true)]
    [InlineData("/api/auth/invite/accept", true)]
    [InlineData("/api/auth/runner-enroll", false)]
    [InlineData("/api/auth/status", false)]
    public void Only_session_issuing_endpoints_count_as_browser_sign_in(string path, bool expected)
        => Assert.Equal(expected, BrowserOriginPolicy.IsBrowserSignIn(path));
}
