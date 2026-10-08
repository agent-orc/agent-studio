using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Xunit;
using System.Text.Json;

namespace TaskServer.Tests;

public sealed class CredentialReminderPolicyTests
{
    private static readonly DateTime Now = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(15, null)]
    [InlineData(14, 14)]
    [InlineData(8, 14)]
    [InlineData(7, 7)]
    [InlineData(2, 7)]
    [InlineData(1, 1)]
    [InlineData(-1, 1)]
    public void Thresholds_and_late_discovery_select_one_item(int days, int? threshold)
    {
        var item = CredentialReminderPolicy.Evaluate(Fixture() with { ExpiresAt = Now.AddDays(days) }, Now);
        Assert.Equal(threshold, item?.ThresholdDays);
    }

    [Fact]
    public void Duplicate_observations_share_identity_and_rotation_resets_it()
    {
        var old = Fixture() with { ExpiresAt = Now.AddDays(7) };
        var first = CredentialReminderPolicy.Evaluate(old, Now);
        Assert.Equal(first, CredentialReminderPolicy.Evaluate(old, Now));
        Assert.Equal(first!.Id, CredentialReminderPolicy.Evaluate(old, Now.AddDays(6))!.Id);
        var renewed = old with { Generation = "generation-b", ExpiresAt = Now.AddDays(90), LastOutcome = "healthy" };
        Assert.Null(CredentialReminderPolicy.Evaluate(renewed, Now));
        Assert.NotEqual(first.Id, CredentialReminderPolicy.Evaluate(renewed with { ExpiresAt = Now.AddDays(7) }, Now)!.Id);
    }

    [Fact]
    public void Unknown_and_refreshable_access_token_dates_do_not_warn()
    {
        Assert.Null(CredentialReminderPolicy.Evaluate(Fixture() with { AccessTokenExpiresAt = Now.AddHours(1) }, Now));
        Assert.Null(CredentialReminderPolicy.Evaluate(Fixture() with { ExpiresAt = Now.AddDays(1), ExpiryKnowledge = "unknown" }, Now));
    }

    [Fact]
    public void Future_verification_is_clock_skew_and_suppresses_warning()
    {
        Assert.Null(CredentialReminderPolicy.Evaluate(Fixture() with { ExpiresAt = Now.AddDays(1), LastVerifiedAt = Now.AddMinutes(3) }, Now));
    }

    [Fact]
    public void Incident_has_retry_without_action_and_invalid_login_has_host_renewal()
    {
        var incident = CredentialViewPolicy.Project(Fixture() with {
            LastOutcome = "provider_incident", NextProbeAt = Now.AddMinutes(2),
            ExpiresAt = Now.AddDays(1)
        }, Now);
        Assert.Null(incident.RenewalAction);
        Assert.Equal(Now.AddMinutes(2), incident.NextProbeAt);
        Assert.Null(incident.Reminder);
        var invalid = CredentialViewPolicy.Project(Fixture() with { LastOutcome = "credential_invalid" }, Now);
        Assert.Equal("host", invalid.HostId);
        Assert.Equal("claude-sign-in", invalid.RenewalAction);
        Assert.DoesNotContain("localRef", JsonSerializer.Serialize(invalid));
        Assert.DoesNotContain("publicFingerprint", JsonSerializer.Serialize(invalid));
    }

    private static CredentialRegistryRecordDto Fixture() => new(
        "installation", "host", "credential", "claude", "owner", "owner", "generation-a", "R2", 1,
        "claude_native_login", null, ["workspace"], [new("coding", "provider", "native")],
        new("native-cli-store", "native", "active"), "issuer", new Dictionary<string, string>(),
        null, null, null, null, null, "healthy", [], null, null, Now, null, null, null, null, Now, null);
}
