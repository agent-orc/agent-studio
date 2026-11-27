using AgentStudio.OrchestrationDecisions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class MachineBlockerPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(true, true, false, MachineBlockerAction.Requeue)]
    [InlineData(true, false, false, MachineBlockerAction.Wait)]
    [InlineData(true, false, true, MachineBlockerAction.RepairAndQuarantine)]
    [InlineData(false, false, false, MachineBlockerAction.RepairAndQuarantine)]
    public void Integration_uses_platform_facts(bool exists, bool integrated, bool failed, MachineBlockerAction expected)
        => Assert.Equal(expected, IntegrationBlockerPolicy.Resolve(new(
            exists, true, true, integrated, failed, false, Now, Now.AddMinutes(30))).Action);

    [Fact]
    public void Integration_wait_expires_without_an_owner_question()
        => Assert.Equal(MachineBlockerAction.RepairAndQuarantine,
            IntegrationBlockerPolicy.Resolve(new(true, true, true, false, false, false, Now, Now)).Action);

    [Fact]
    public void Shared_recovery_budget_ends_repeated_integrated_blockers_in_repair()
        => Assert.Equal(MachineBlockerAction.RepairAndQuarantine,
            IntegrationBlockerPolicy.Resolve(new(true, true, true, true, false, false, Now, Now.AddMinutes(30), true)).Action);

    [Fact]
    public void Owner_hold_wins_even_when_dependency_is_integrated()
        => Assert.Equal(MachineBlockerAction.HumanChoice,
            IntegrationBlockerPolicy.Resolve(new(true, true, true, true, false, true, Now, Now)).Action);

    [Theory]
    [InlineData(true, false, MachineBlockerAction.Wait)]
    [InlineData(true, true, MachineBlockerAction.RepairAndQuarantine)]
    [InlineData(false, true, MachineBlockerAction.RecordUnavailable)]
    public void Absent_hardware_has_bounded_disposition(bool mandatory, bool expired, MachineBlockerAction expected)
        => Assert.Equal(expected, IntegrationBlockerPolicy.Verification(new(
            true, mandatory, false, false, Now, expired ? Now : Now.AddMinutes(15))).Action);

    [Fact]
    public void Alternate_capable_host_keeps_exact_result_verification_claimable()
        => Assert.Equal(MachineBlockerAction.PlaceVerification,
            IntegrationBlockerPolicy.Verification(new(true, true, true, false, Now, Now)).Action);

    [Fact]
    public void Unreconstructed_finding_cannot_waive_hardware_acceptance_evidence()
        => Assert.Equal(MachineBlockerAction.RepairAndQuarantine,
            IntegrationBlockerPolicy.Verification(new(false, false, false, false, Now, Now)).Action);

    [Theory]
    [InlineData("Agent emitted [[TASK_BLOCKED]]: implementation of card AGT-4011 is not integrated")]
    [InlineData("agt-4011-implementation-not-integrated")]
    public void Both_terminal_signals_resolve_integration_facts(string fixture)
        => Assert.Equal("AGT-4011", IntegrationBlockerPolicy.Dependency("needs-human-input", fixture));

    [Fact]
    public void Product_question_is_not_reclassified_by_an_embedded_dependency_reference()
        => Assert.Null(IntegrationBlockerPolicy.Dependency("needs-human-input",
            "Should we change scope while implementation of card AGT-4011 is not integrated?"));
}
