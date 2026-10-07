using System.Net;
using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class GitHubCredentialDiscoveryTests
{
    [Fact]
    public async Task Subtype_grants_purpose_and_unknown_expiry_are_independent()
    {
        using var discovery = new GitHubCredentialDiscovery(new Session(), new Handler());

        var result = await discovery.DiscoverAsync("fixture-id", "workspace",
            "https://github.com/example/private-workspace.git", true, CancellationToken.None);

        Assert.Equal("oauth-app", result.TokenSubtype);
        Assert.Equal("workspace", result.RepositoryPurpose);
        Assert.True(result.FetchGrant);
        Assert.False(result.PushGrant);
        Assert.Null(result.IssuerExpiresAt);
        Assert.Equal("unknown", result.ExpiryKnowledge);
        Assert.DoesNotContain("redacted-fixture-bearer",
            System.Text.Json.JsonSerializer.Serialize(result));
    }

    private sealed class Session : IGitHubCredentialInspectorSession
    {
        public Task<(string Bearer, string? IssuerSubtype, DateTimeOffset? IssuerExpiresAt)?>
            OpenAsync(string credentialId, CancellationToken ct)
            => Task.FromResult<(string, string?, DateTimeOffset?)?>(
                new("redacted-fixture-bearer", "oauth-app", null));
    }

    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https://api.github.com/repos/example/private-workspace",
                request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent("{\"permissions\":{\"pull\":true,\"push\":false}}")
            });
        }
    }
}
