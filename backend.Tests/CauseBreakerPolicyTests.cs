using System.Text.Json;
using AgentStudio.Runner;
using AgentStudio.Shared;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

public sealed class CauseBreakerPolicyTests
{
    private static readonly DateTime Now = new(2026, 9, 9, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Attempt_and_distinct_card_thresholds_open_the_same_fleet_breaker()
    {
        var threshold = CauseBreakerPolicy.Clamp(3, 2, 24);
        Assert.Equal(CauseBreakerAction.Retry, Decide(["AGT-1", "AGT-1"], threshold).Action);
        Assert.Equal(CauseBreakerAction.Open, Decide(["AGT-1", "AGT-1", "AGT-1"], threshold).Action);
        var acrossCards = Decide(["AGT-1", "AGT-2"], threshold);
        Assert.Equal(CauseBreakerAction.Open, acrossCards.Action);
        Assert.Equal(2, acrossCards.Cards);
        Assert.Equal(CauseBreakerAction.Wait,
            CauseBreakerPolicy.Decide(true, [new("AGT-3", Now)], threshold, Now).Action);
    }

    [Fact]
    public void Window_excludes_old_attempts_and_thresholds_are_clamped()
    {
        var threshold = CauseBreakerPolicy.Clamp(0, 1000, 0);
        Assert.Equal(1, threshold.Attempts);
        Assert.Equal(CauseBreakerPolicy.MaxThreshold, threshold.Cards);
        Assert.Equal(TimeSpan.FromHours(1), threshold.Window);
        Assert.Equal(TimeSpan.FromHours(CauseBreakerPolicy.MaxWindowHours),
            CauseBreakerPolicy.Clamp(3, 2, int.MaxValue).Window);
        Assert.Equal(CauseBreakerAction.Retry, CauseBreakerPolicy.Decide(false,
            [new("AGT-1", Now.AddDays(-2)), new("AGT-2", Now)],
            CauseBreakerPolicy.Clamp(3, 2, 24), Now).Action);
        Assert.Equal(3, new ProjectSettings().CauseBreakerAttemptThreshold);
        Assert.Equal(2, new ProjectSettings().CauseBreakerCardThreshold);
    }

    [Theory]
    [InlineData("6-completed", false, CauseBreakerCloseReason.CauseIntegrated)]
    [InlineData("7-archived", false, CauseBreakerCloseReason.None)]
    [InlineData("5-human-review", true, CauseBreakerCloseReason.ProbeGreen)]
    [InlineData(null, false, CauseBreakerCloseReason.None)]
    public void Close_requires_integrated_cause_or_green_probe(
        string? state, bool green, CauseBreakerCloseReason expected)
        => Assert.Equal(expected, CauseBreakerPolicy.Close(state, green));

    [Fact]
    public void Fingerprint_ignores_card_identity_but_preserves_failure_and_toolchain()
    {
        var first = CauseFingerprintPolicy.Compute("ReviewInfra", "PreparationFailed",
            "CAC-18: npm ci failed in /tmp/job-a with exit 127", 127, "tool:npm");
        var second = CauseFingerprintPolicy.Compute("ReviewInfra", "PreparationFailed",
            "CAC-99: npm ci failed in /tmp/job-b with exit 127", 127, "tool:npm");
        Assert.Equal(first.NormalizedText, second.NormalizedText);
        Assert.Equal(first.Value, second.Value);
        Assert.NotEqual(first.Value, CauseFingerprintPolicy.Compute("ReviewInfra", "PreparationFailed",
            "CAC-99: npm ci failed in /tmp/job-b with exit 127", 127, "tool:pnpm").Value);
        Assert.NotEqual(first.Value, CauseFingerprintPolicy.Compute("ReviewInfra", "ToolUnavailable",
            "CAC-99: npm ci failed in /tmp/job-b with exit 127", 127, "tool:npm").Value);
    }

    [Fact]
    public void Agent_model_and_executable_are_part_of_toolchain_context()
    {
        var withdrawn = new ReviewCommandDto("review-1", "requirements", "codex", [],
            ExecutionKind: ReviewCommandKinds.AgentAspect, CliType: "codex", Model: "withdrawn-model");
        var replacement = withdrawn with { Model = "replacement-model" };
        Assert.Equal("agent:codex:withdrawn-model", CauseFingerprintPolicy.Toolchain(withdrawn, null));
        Assert.NotEqual(CauseFingerprintPolicy.Toolchain(withdrawn, null),
            CauseFingerprintPolicy.Toolchain(replacement, null));
        Assert.Equal("tool:npm", CauseFingerprintPolicy.Toolchain(null, "/usr/bin/npm"));
        Assert.Equal("tool:pnpm", CauseFingerprintPolicy.Toolchain(null, "C:\\tools\\pnpm.cmd"));
    }

    [Fact]
    public void Preparation_step_identity_is_not_erased_as_a_card_key()
    {
        var first = CauseFingerprintPolicy.Compute("ReviewInfra", "PreparationFailed",
            "Dependency preparation 'prepare-2' failed: npm ci exit=127", 127, "tool:npm");
        var second = CauseFingerprintPolicy.Compute("ReviewInfra", "PreparationFailed",
            "Dependency preparation 'prepare-3' failed: npm ci exit=127", 127, "tool:npm");
        Assert.NotEqual(first.Value, second.Value);
    }

    // Replay retained Attempt Authority observations, including their real
    // attempt IDs, timestamps, terminal reasons and toolchain routes.
    [Theory]
    [InlineData("withdrawn-review-model", 59, 12)]
    [InlineData("repeated-preparation-failure", 411, 411)]
    public void E1_windows_stop_after_three_attempts_and_open_one_cause_card(
        string windowName, int historicalAttempts, int extractedAttempts)
    {
        using var replay = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "cause-breaker", "e1-attempts.json")));
        var window = replay.RootElement.GetProperty("windows").EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == windowName);
        Assert.Equal(historicalAttempts, window.GetProperty("historicalAttemptCount").GetInt32());
        var observations = window.GetProperty("observations").EnumerateArray().ToArray();
        Assert.Equal(extractedAttempts, observations.Length);
        if (windowName == "repeated-preparation-failure")
        {
            Assert.Equal(408, observations.Count(item => item.GetProperty("failureClassification").GetString() == "PreparationFailed"));
            Assert.Equal(2, observations.Count(item => item.GetProperty("outcome").GetString() == "pass"));
            Assert.Single(observations, item => item.GetProperty("outcome").GetString() == "superseded");
        }
        var seen = new List<CauseBreakerCount>();
        var runningAttempts = 0;
        var causeCards = 0;
        var open = false;
        string? firstFingerprint = null;
        foreach (var item in observations)
        {
            if (open) break;
            if (item.GetProperty("outcome").GetString() != "infrastructureFailure") continue;
            var observed = CauseFingerprintPolicy.Compute("ReviewInfra",
                item.GetProperty("failureClassification").GetString(),
                item.GetProperty("terminalReason").GetString(),
                item.GetProperty("exitCode").GetInt32(),
                item.GetProperty("toolchain").GetString());
            firstFingerprint ??= observed.Value;
            Assert.Equal(firstFingerprint, observed.Value);
            var at = DateTime.Parse(item.GetProperty("createdAt").GetString()!, null,
                System.Globalization.DateTimeStyles.AdjustToUniversal);
            runningAttempts++;
            seen.Add(new CauseBreakerCount(item.GetProperty("taskKey").GetString()!, at));
            var decision = CauseBreakerPolicy.Decide(open, seen,
                CauseBreakerPolicy.Clamp(3, 2, 24), at);
            if (decision.Action != CauseBreakerAction.Open) continue;
            open = true;
            causeCards++;
        }
        Assert.Equal(3, runningAttempts);
        Assert.Equal(1, causeCards);
        Assert.True(open);
    }

    private static CauseBreakerDecision Decide(string[] keys, CauseBreakerThresholds thresholds)
        => CauseBreakerPolicy.Decide(false, keys.Select(key => new CauseBreakerCount(key, Now)).ToArray(),
            thresholds, Now);
}
