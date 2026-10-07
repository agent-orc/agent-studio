using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

public sealed class RepositoryCredentialRegistryProjectionTests
{
    [Fact]
    public void Completed_proof_promotes_only_public_metadata_at_fake_clock_time()
    {
        var now = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
        var current = new CredentialRegistryRecordDto(
            "installation", "host", "workspace-key", "github", "owner", "recovery", "old",
            "R5", 1, "github_deploy_key", null, [], [],
            new("ssh-private-key", "host-key-ref", "active"), "none",
            new Dictionary<string, string>(), null, null, null, "RB-GITHUB-WORKSPACE",
            null, "not_verified", [], null, null, null, null, null, null, null, null, null);
        var request = new RepositoryRotationRequest("op", "host", "old", "example", "private-workspace",
            "workspace", "git@github.com:example/private-workspace.git", true, 11, "provisioning-record");
        var receipt = new RepositoryRotationReceipt("op", "old",
            new("new", "ssh-ed25519 PUBLIC", "SHA256:public", "host-key-ref"),
            12, "provisioning-record", true, true, true, true, "completed");

        var promoted = RepositoryCredentialRegistryProjection.CompletedDeployKey(
            current, request, receipt, new FixedClock(now));

        Assert.Equal("new", promoted.Generation);
        Assert.Equal(12, promoted.GitHubKeyId);
        Assert.Equal("SHA256:public", promoted.PublicFingerprint);
        Assert.Equal("provisioning-record", promoted.ProvisioningCredentialId);
        Assert.Equal(now.UtcDateTime, promoted.LastVerifiedAt);
        Assert.DoesNotContain("PRIVATE", System.Text.Json.JsonSerializer.Serialize(promoted));
    }

    private sealed class FixedClock(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
