using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// AGT-2993: scenario and smoke residue filled agent-runner-01 to 95-96 % twice
/// on 2026-09-28. The review executor logs the free space before a compose
/// scenario step and refuses it below the floor. These matrices pin which
/// commands count as a compose scenario and where the floor sits.
/// </summary>
public sealed class ComposeScenarioDiskAdmissionTests
{
    public static TheoryData<string, string[], bool> Commands => new()
    {
        { "scripts/scenario.sh", ["--target", "compose", "--level", "full"], true },
        { "bash", ["scripts/scenario.sh", "--target", "compose", "--level", "smoke"], true },
        { "/repo/scripts/scenario.sh", ["--level", "full", "--target=compose"], true },
        { "/bin/sh", ["-c", "SCENARIO_PROVIDER_REVIEW=1 scripts/scenario.sh --target compose --level full"], true },
        { "bash", ["-c", "cd repo && ./scripts/compose-smoke-test.sh"], true },
        { "scripts/compose-smoke-test.sh", [], true },
        { "scripts\\scenario.sh", ["--target", "compose", "--level", "full"], true },
        { "scripts/scenario.sh", ["--target", "inproc", "--level", "full"], false },
        { "scripts/scenario.sh", ["--target", "remote", "--level", "smoke"], false },
        { "scripts/scenario-remote-smoke.sh", ["--target", "compose"], false },
        { "dotnet", ["test", "task-server.Tests", "--filter", "compose"], false },
        { "bash", ["scripts/compose-smoke-version.test.sh"], false },
        { "bash", ["-c", "echo --target compose"], false },
    };

    [Theory]
    [MemberData(nameof(Commands))]
    public void Recognizes_compose_scenario_commands(string fileName, string[] arguments, bool expected)
    {
        var command = new ReviewCommandDto("verify-1", "scenario", fileName, arguments);

        Assert.Equal(expected, ComposeScenarioDiskAdmission.IsComposeScenario(command));
    }

    [Fact]
    public void An_agent_aspect_that_mentions_the_scenario_is_not_a_compose_scenario()
    {
        var command = new ReviewCommandDto(
            "aspect-1",
            "correctness",
            "claude",
            ["scripts/scenario.sh", "--target", "compose"],
            ExecutionKind: ReviewCommandKinds.AgentAspect);

        Assert.False(ComposeScenarioDiskAdmission.IsComposeScenario(command));
    }

    public static TheoryData<long?, long?, int, bool, double?> Decisions => new()
    {
        // 2026-09-28 incident shape: 4 % free on a 480 GB disk.
        { 19_200_000_000, 480_000_000_000, 10, false, 4.0 },
        { 47_999_999_999, 480_000_000_000, 10, false, 10.0 },
        { 48_000_000_000, 480_000_000_000, 10, true, 10.0 },
        { 240_000_000_000, 480_000_000_000, 10, true, 50.0 },
        { 0, 480_000_000_000, 10, false, 0.0 },
        // 0 disables the refusal but still measures.
        { 0, 480_000_000_000, 0, true, 0.0 },
        { 96_000_000_000, 480_000_000_000, 25, false, 20.0 },
        // An unreadable disk is not evidence of a full one.
        { null, null, 10, true, null },
        { 10, 0, 10, true, null },
    };

    [Theory]
    [MemberData(nameof(Decisions))]
    public void Refuses_below_the_free_space_floor(
        long? free,
        long? total,
        int floor,
        bool admit,
        double? percent)
    {
        var decision = ComposeScenarioDiskAdmission.Decide("/var/lib/docker", free, total, floor);

        Assert.Equal(admit, decision.Admit);
        Assert.Equal(percent, decision.FreePercent);
    }

    [Fact]
    public void Log_line_and_refusal_name_the_measured_disk_and_the_floor()
    {
        var decision = ComposeScenarioDiskAdmission.Decide(
            "/var/lib/docker", 19_200_000_000, 480_000_000_000, 10);

        Assert.Equal(
            "review-compose-scenario-disk step=verify-4 path=/var/lib/docker freeBytes=19200000000 " +
            "totalBytes=480000000000 freePercent=4.0 minFreePercent=10 decision=refuse",
            decision.Describe("verify-4"));
        var summary = decision.RefusalSummary("verify-4", "scripts/scenario.sh --target compose --level full");
        Assert.Contains("4.0 % free", summary, StringComparison.Ordinal);
        Assert.Contains("below the 10 % floor", summary, StringComparison.Ordinal);
        Assert.Contains("scripts/docker-scenario-retention.sh", summary, StringComparison.Ordinal);
    }
}
