using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace TaskServer.Tests;

public sealed class ProviderProbeComparisonPolicyTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Only_a_fresh_other_host_with_a_distinct_registered_credential_can_corroborate()
    {
        var target = Host("runner-a", "host-a", null);
        var other = Host("runner-b", "host-b", Failure());
        var targetCredential = Credential("host-a", "credential-a");
        var otherCredential = Credential("host-b", "credential-b");
        ProviderProbeComparisonResponseDto Find(
            RunnerCapabilitySnapshotDto candidate, CredentialRegistryRecordDto credential)
            => ProviderProbeComparisonPolicy.Find("runner-a", "codex", "g1", "native-cli-store", "unauthorized",
                [target, candidate], [targetCredential, credential], At);

        var match = Find(other, otherCredential);
        Assert.Equal("credential-a", match.CredentialIdentity);
        Assert.Equal("host-b", match.Comparison?.HostId);
        Assert.Equal("credential-b", match.Comparison?.CredentialIdentity);
        Assert.Null(Find(other, otherCredential with { CredentialId = "credential-a" }).Comparison);
        Assert.Null(Find(other with { HostId = "host-a" }, otherCredential).Comparison);
        Assert.Null(Find(other with { Capabilities = [Failure() with {
            LastRealSuccessAt = At.AddMinutes(-11).UtcDateTime }] }, otherCredential).Comparison);
        Assert.Null(Find(other with { Capabilities = [Failure() with {
            EvidenceExcerpt = "unauthorized:service-account-shaped" }] }, otherCredential).Comparison);
        Assert.Null(Find(other with { Capabilities = [Failure() with { IsFresh = false }] }, otherCredential).Comparison);
        Assert.Null(Find(other with { Capabilities = [Failure() with {
            EffectiveSource = "absent" }] }, otherCredential).Comparison);
        Assert.Null(ProviderProbeComparisonPolicy.Find("runner-a", "codex", "g1",
            "environment-file", "unauthorized", [target, other],
            [targetCredential, otherCredential], At).Comparison);
    }

    private static RunnerCapabilitySnapshotDto Host(
        string runner, string host, CapabilityHealthDto? capability) => new(
        runner, runner, host, "instance", "1", 1, "active", At.UtcDateTime,
        At.UtcDateTime, new(host, "active", null, null, null, null),
        capability is null ? [] : [capability], null);

    private static CapabilityHealthDto Failure() => new(
        CapabilityProtocol.ProviderAuthentication("codex"), "provider-auth", "ready",
        "healthy", null, At.UtcDateTime, At.AddMinutes(5).UtcDateTime, true,
        null, null, null, null, 0, null, null, null, [], [],
        Signal: "indeterminate", EvidenceExcerpt: "unauthorized",
        CredentialGeneration: "g1", LastRealSuccessAt: At.AddMinutes(-3).UtcDateTime,
        EffectiveSource: "native-cli-store");

    private static CredentialRegistryRecordDto Credential(string host, string id) => new(
        "installation", host, id, "codex", "owner", "recovery-owner", "g1", "R3", 1,
        "codex_chatgpt_login", null, [], [],
        new("native-cli-store", "host-local-reference", "active"), "unknown",
        new Dictionary<string, string>(), null, null, null, null, null, "indeterminate", [],
        null, null, null, null, null, null, null, null, null);
}
