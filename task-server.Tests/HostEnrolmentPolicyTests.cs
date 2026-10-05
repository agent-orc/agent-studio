using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace TaskServer.Tests;

public sealed class HostEnrolmentPolicyTests
{
    private static readonly DateTime At = new(2026, 9, 30, 9, 0, 0, DateTimeKind.Utc);

    private static HostEnrolmentDto Host(string status = HostEnrolmentStatuses.Enrolled, int total = 3, int coding = 2, int review = 2)
        => new("host-a", "linux", status, 1,
            [new HostRolePrincipalDto(HostRoles.Coding, "c"), new HostRolePrincipalDto(HostRoles.Review, "r")],
            new HostEnvelopeDto(total, coding, review), At, At, null);

    [Theory]
    [InlineData(HostRoles.Coding, "c", 0, 0, null)]
    [InlineData(HostRoles.Coding, "r", 0, 0, HostAdmissionReasons.PrincipalNotEnrolled)]
    [InlineData(HostRoles.Coding, "c", 2, 0, HostAdmissionReasons.RoleSlotBudgetFull)]
    [InlineData(HostRoles.Review, "r", 2, 0, null)]
    [InlineData(HostRoles.Review, "r", 2, 1, HostAdmissionReasons.SlotBudgetFull)]
    [InlineData(HostRoles.Review, "r", 1, 2, HostAdmissionReasons.SlotBudgetFull)]
    [InlineData(HostRoles.Review, "r", 0, 2, HostAdmissionReasons.RoleSlotBudgetFull)]
    public void Decide_conserves_the_shared_envelope_and_role_caps(
        string role, string principal, int coding, int review, string? expected)
    {
        var decision = HostEnrolmentPolicy.Decide(Host(), role, principal, coding, review);
        Assert.Equal(expected is null, decision.Admitted);
        Assert.Equal(expected, decision.Reason);
    }

    [Fact]
    public void Unenrolled_hosts_keep_legacy_admission_and_removed_hosts_refuse()
    {
        Assert.True(HostEnrolmentPolicy.Decide(null, HostRoles.Coding, "x", 99, 99).Admitted);
        Assert.Equal(HostAdmissionReasons.HostRemoved,
            HostEnrolmentPolicy.Decide(Host(HostEnrolmentStatuses.Removed), HostRoles.Coding, "c", 0, 0).Reason);
        var codingOnly = Host() with { Roles = [new HostRolePrincipalDto(HostRoles.Coding, "c")] };
        Assert.Equal(HostAdmissionReasons.RoleNotEnrolled,
            HostEnrolmentPolicy.Decide(codingOnly, HostRoles.Review, "r", 0, 0).Reason);
    }

    [Fact]
    public void Validate_reports_an_empty_role_entry_instead_of_throwing()
    {
        var request = new EnrolHostRequest(
            "linux", [new HostRolePrincipalDto(HostRoles.Coding, "c"), null!], new HostEnvelopeDto(1, 1, 0), 0);
        Assert.Equal("A role entry is empty.", HostEnrolmentPolicy.Validate("host-a", request));
    }

    [Theory]
    [InlineData(2, 2, 1, "c", "r", null)]
    [InlineData(2, 2, 1, "same", "same", "distinct principals")]
    [InlineData(0, 0, 0, "c", "r", "totalSlots")]
    [InlineData(2, 3, 1, "c", "r", "cannot exceed")]
    [InlineData(2, 2, 0, "c", "r", "needs at least one slot")]
    public void Validate_requires_distinct_role_principals_and_a_bounded_envelope(
        int total, int coding, int review, string codingPrincipal, string reviewPrincipal, string? error)
    {
        var request = new EnrolHostRequest(
            "linux",
            [new HostRolePrincipalDto(HostRoles.Coding, codingPrincipal), new HostRolePrincipalDto(HostRoles.Review, reviewPrincipal)],
            new HostEnvelopeDto(total, coding, review),
            0);
        var result = HostEnrolmentPolicy.Validate("host-a", request);
        if (error is null) Assert.Null(result);
        else Assert.Contains(error, result);
    }

    [Theory]
    [InlineData("provider-auth:codex", HostAdmissionReasons.CapabilityMissing, HostAdmissionReasons.ProviderLoginMissing)]
    [InlineData("repository:access", HostAdmissionReasons.CapabilityUnavailable, HostAdmissionReasons.RepositoryProofFailed)]
    [InlineData("git:push", HostAdmissionReasons.CapabilityMissing, HostAdmissionReasons.RepositoryProofFailed)]
    [InlineData("provider-auth:codex", HostAdmissionReasons.CapabilityStale, HostAdmissionReasons.CapabilityStale)]
    [InlineData("toolchain:node", HostAdmissionReasons.CapabilityMissing, HostAdmissionReasons.CapabilityMissing)]
    public void Capability_refusals_are_classified(string key, string failure, string expected)
        => Assert.Equal(expected, HostAdmissionReasons.ForCapability(key, failure));
}
