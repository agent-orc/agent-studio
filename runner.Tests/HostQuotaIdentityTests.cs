using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class HostQuotaIdentityTests
{
    [Fact]
    public void AccountIdentityIsStableOpaqueAndDistinct()
    {
        var first = HostQuotaIdentity.DigestAccount("codex", "Account-A");
        Assert.Equal(first, HostQuotaIdentity.DigestAccount("codex", " account-a "));
        Assert.NotEqual(first, HostQuotaIdentity.DigestAccount("codex", "Account-B"));
        Assert.NotEqual(first, HostQuotaIdentity.DigestAccount("claude", "Account-A"));
        Assert.Equal(64, first.Length);
        Assert.DoesNotContain("Account", first, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProviderFallbackIsStableWhenNoAccountIdentityIsAvailable()
    {
        Assert.Equal(HostQuotaIdentity.DigestAccount("claude", null),
            HostQuotaIdentity.DigestAccount("claude", " "));
        Assert.NotEqual(HostQuotaIdentity.DigestAccount("claude", null),
            HostQuotaIdentity.DigestAccount("codex", null));
    }
}
