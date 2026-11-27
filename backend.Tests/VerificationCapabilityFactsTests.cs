using AgentStudio.Operator;
using AgentStudio.OrchestrationDecisions;
using Contract = AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

public sealed class VerificationCapabilityFactsTests
{
    private static readonly DateTime Now = new(2026, 11, 27, 10, 0, 0, DateTimeKind.Utc);
    // Synthetic requirement. This is not a reconstruction of the suspected F07 incident.
    private static readonly Contract.ReviewPlanDto HardwarePlan = new(
        [new("device-test", "review-verification", "device-test", [], Required: true,
            LibraryStep: new("device-test", 1, "digest", new string('a', 40), ["hardware:test-device"], 60))],
        ["review-verification"], LibraryVersion: 1);

    [Fact]
    public void Absent_hardware_cannot_be_counted_as_verification()
        => Assert.Empty(EvidenceGate.CapableVerificationHosts(HardwarePlan, [Host("ordinary", [])], Now));

    [Fact]
    public void Alternate_capable_host_is_selected_without_changing_the_subject()
    {
        var before = HardwarePlan.Commands[0].LibraryStep;
        Assert.Equal(["hardware-host"], EvidenceGate.CapableVerificationHosts(HardwarePlan,
            [Host("ordinary", []), Host("hardware-host", ["hardware:test-device"])], Now));
        Assert.Same(before, HardwarePlan.Commands[0].LibraryStep);
        Assert.True(HardwarePlan.Commands[0].Required);
    }

    [Fact]
    public void Same_physical_host_cannot_satisfy_an_explicit_different_host_requirement()
    {
        var plan = HardwarePlan with { RequireDifferentHostFailureDomain = true };
        Assert.Empty(EvidenceGate.CapableVerificationHosts(plan,
            [Host("coding-host", ["hardware:test-device"])], Now, codingHost: "coding-host"));
        Assert.False(EvidenceGate.HasOwnerHeldVerificationHost(plan,
            [Host("coding-host", ["hardware:test-device"], "drained")], codingHost: "coding-host"));
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("drained")]
    [InlineData("unhealthy")]
    [InlineData("expired")]
    public void Unusable_hardware_advertisements_do_not_satisfy_acceptance(string state)
        => Assert.Empty(EvidenceGate.CapableVerificationHosts(HardwarePlan, [Host("hardware-host", ["hardware:test-device"], state)], Now));

    [Fact]
    public void Machine_quarantine_does_not_hide_a_later_real_owner_hold()
    {
        var machine = new TaskInfo
        {
            Id = "card", Key = "AGT-4098", State = TaskStates.Escalated,
            ParkedBlocker = new(IntegrationBlockerPolicy.QuarantineCategory, "manual", "quarantine", Now, 0,
                "Repair linked", "blocked", null, "mandatory evidence unsatisfied"),
        };
        Assert.Empty(OperatorDecisionFacts.FromTasks([machine], _ => null));
        var held = machine with { ParkedBlocker = machine.ParkedBlocker with { BlockerType = ParkedBlockerCatalog.OperatorDecision } };
        Assert.Single(OperatorDecisionFacts.FromTasks([held], _ => null));
    }

    [Fact]
    public void Optional_disposition_preserves_every_mandatory_command_preparation_and_noncommand_requirement()
    {
        var plan = HardwarePlan with
        {
            Commands = [.. HardwarePlan.Commands, new("optional-check", "optional-aspect", "optional", [], Required: false)],
            RequiredAspects = ["review-verification", "required-artifact", "optional-aspect"],
            Preparation = [new("prepare", "dotnet", ["restore"])],
        };
        var result = RemoteReviewPlanBuilder.WithoutUnavailableOptionalCommands(plan, "Optional capability unavailable.");
        Assert.Same(plan.Commands[0], Assert.Single(result.Commands));
        Assert.Same(plan.Preparation, result.Preparation);
        Assert.Equal(["review-verification", "required-artifact"], result.RequiredAspects);
        Assert.Equal("optional-aspect", Assert.Single(result.SkippedAspects!).Aspect);
        Assert.Same(plan.Commands[0].LibraryStep, result.Commands[0].LibraryStep);
    }

    [Fact]
    public void Provider_routing_readiness_does_not_become_missing_hardware()
    {
        var command = HardwarePlan.Commands[0];
        var plan = HardwarePlan with { Commands = [command with
        {
            LibraryStep = command.LibraryStep! with
            {
                RequiredCapabilities = ["hardware:test-device", "provider-auth:codex", "cli-execution:codex"],
            },
        }] };
        Assert.Equal(["hardware-host"], EvidenceGate.CapableVerificationHosts(plan,
            [Host("hardware-host", ["hardware:test-device"])], Now));
    }

    [Fact]
    public void Explicit_host_drain_is_a_human_hold_on_known_matching_verification_capacity()
    {
        var hosts = new[] { Host("hardware-host", ["hardware:test-device"], "drained") };
        Assert.Empty(EvidenceGate.CapableVerificationHosts(HardwarePlan, hosts, Now));
        Assert.True(EvidenceGate.HasOwnerHeldVerificationHost(HardwarePlan, hosts));
    }

    private static Contract.RunnerCapabilitySnapshotDto Host(string id, IReadOnlyList<string> capabilities, string state = "ready")
    {
        var caps = capabilities.Append(Contract.ReviewCapabilities.ReviewExecutor).Select(key => new Contract.CapabilityHealthDto(
            key, "verification", "ready", state == "unhealthy" ? "draining" : "healthy", null,
            Now, state == "expired" ? Now.AddSeconds(-1) : Now.AddMinutes(1), true,
            null, null, null, null, 0, null, null, null, [], [])).ToArray();
        return new(id, id, id, "instance-1", "1.0", 1, "active", Now, state == "stale" ? Now.AddMinutes(-3) : Now,
            new(id, state == "drained" ? "operator-drain" : "open", null, null, null, null), caps, null);
    }
}
