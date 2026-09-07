using AgentStudio.Cli;
using AgentStudio.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The algorithmic pre-launch admission check (AGT-2055). Mirrors the task's
/// acceptance scenarios: quota-full -> switch to fallback + event; both empty ->
/// quiet wait with a reason; reset -> normal start on primary; plus the
/// projection cases (umschichten / drosseln before the wall).
/// </summary>
public sealed class QuotaAdmissionPlannerTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "atp-admission-" + Guid.NewGuid().ToString("N"));
    private readonly IConfiguration _config;
    private readonly CliQuotaCapsService _caps;
    private readonly Dictionary<string, QuotaSnapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase);

    public QuotaAdmissionPlannerTests()
    {
        Directory.CreateDirectory(_root);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _root })
            .Build();
        _caps = new CliQuotaCapsService(NullLogger<CliQuotaCapsService>.Instance, _config);
    }

    // ── acceptance scenario 1: quota full -> fallback pick + documented switch ──
    [Fact]
    public void QuotaFull_SwitchesToFallback_WithDocumentedReason()
    {
        var fallback = Routing(new CliModelRouteProfile
        {
            CliType = "claude", PrimaryModel = "claude-opus",
            FallbackCliType = "codex", FallbackModel = "gpt-5.3-codex",
        });
        Snapshot("claude", ("Weekly", 100, Now.AddDays(3)));
        Snapshot("codex", ("Weekly", 10, Now.AddDays(3)));

        var plan = Plan("claude", fallback, occupiedSlots: 0);

        Assert.Equal(QuotaAdmissionOutcome.LaunchFallback, plan.Outcome);
        Assert.True(plan.IsFallback);
        Assert.Equal("codex", plan.CliType);
        Assert.Equal("gpt-5.3-codex", plan.Model);
        Assert.Contains("switched pre-launch", plan.Reason);
        Assert.Contains("Weekly", plan.Reason);
    }

    [Fact]
    public void NearbyResetWait_PrecedesUsableFallback()
    {
        var fallback = Routing(new CliModelRouteProfile
        {
            CliType = "claude", PrimaryModel = "claude-opus",
            FallbackCliType = "codex", FallbackModel = "gpt-5.3-codex",
        });
        Snapshot("claude", ("5-hour", 100, Now.AddMinutes(12)));
        Snapshot("codex", ("5-hour", 10, Now.AddHours(3)));

        var plan = Plan(
            "claude", fallback, occupiedSlots: 1,
            new ResolvedCliQuotaWaitPolicy(true, 30, "global", null, null, true, 30));

        Assert.Equal(QuotaAdmissionOutcome.Wait, plan.Outcome);
        Assert.True(plan.NearbyResetWait);
        Assert.Equal(Now.AddMinutes(12), plan.NextResetAt);
        Assert.Contains("12 min remaining", plan.Reason);
        Assert.DoesNotContain("switched", plan.Reason);
    }

    [Fact]
    public void NearbyReset_ExpensiveCardSwitchesInsteadOfWaiting()
    {
        var fallback = Routing(new CliModelRouteProfile
        {
            CliType = "codex",
            PrimaryModel = ModelIds.Gpt56Sol,
        });
        Snapshot("codex", ("Weekly", 98, Now.AddMinutes(12)));
        Snapshot("claude", ("Weekly", 34, Now.AddHours(4)));

        var plan = QuotaAdmissionPlanner.Plan(
            "codex",
            ModelIds.Gpt56Sol,
            "high",
            fallback,
            _caps,
            c => c != null && _snapshots.TryGetValue(c, out var snapshot) ? snapshot : null,
            Now,
            occupiedSlots: 1,
            new ResolvedCliQuotaWaitPolicy(true, 30, "global", null, null, true, 30),
            QuotaExpectedCostClass.Expensive,
            "remote-coding-claim");

        Assert.Equal(QuotaAdmissionOutcome.LaunchFallback, plan.Outcome);
        Assert.Equal(CliTypes.Claude, plan.CliType);
        Assert.Equal(ModelIds.ClaudeOpus5, plan.Model);
        Assert.Equal("high", plan.ThinkingLevel);
        Assert.Contains("cost expensive", plan.Reason);
        Assert.Contains("headroom", plan.Reason);
    }

    [Fact]
    public void LowFallbackHeadroom_AdmitsCheapCallButReservesCapacityFromExpensiveCard()
    {
        var fallback = Routing(new CliModelRouteProfile
        {
            CliType = "codex",
            PrimaryModel = ModelIds.Gpt56Sol,
        });
        Snapshot("codex", ("Weekly", 98, Now.AddHours(1)));
        Snapshot("claude", ("Weekly", 92, Now.AddHours(1)));

        QuotaAdmissionPlan Decide(QuotaExpectedCostClass cost) => QuotaAdmissionPlanner.Plan(
            "codex",
            ModelIds.Gpt56Sol,
            "medium",
            fallback,
            _caps,
            c => c != null && _snapshots.TryGetValue(c, out var snapshot) ? snapshot : null,
            Now,
            occupiedSlots: 0,
            waitPolicy: null,
            cost,
            "cost-aware-test");

        var cheap = Decide(QuotaExpectedCostClass.Cheap);
        var expensive = Decide(QuotaExpectedCostClass.Expensive);

        Assert.Equal(QuotaAdmissionOutcome.LaunchFallback, cheap.Outcome);
        Assert.Equal(QuotaAdmissionOutcome.Wait, expensive.Outcome);
        Assert.Contains("reserve required for expensive", expensive.Reason);
        Assert.Equal(QuotaExpectedCostClass.Expensive, expensive.ExpectedCost);
    }

    [Fact]
    public void DistantReset_UsesFallbackBeforeThrottle()
    {
        var fallback = Routing(new CliModelRouteProfile
        {
            CliType = "claude", PrimaryModel = "claude-opus",
            FallbackCliType = "codex", FallbackModel = "gpt-5.3-codex",
        });
        Snapshot("claude", ("Weekly", 100, Now.AddMinutes(31)));
        Snapshot("codex", ("Weekly", 10, Now.AddHours(3)));

        var plan = Plan(
            "claude", fallback, occupiedSlots: 1,
            new ResolvedCliQuotaWaitPolicy(true, 30, "project", true, 30, false, 30));

        Assert.Equal(QuotaAdmissionOutcome.LaunchFallback, plan.Outcome);
        Assert.False(plan.NearbyResetWait);
        Assert.Equal("codex", plan.CliType);
    }

    // ── acceptance scenario 2: both exhausted -> quiet wait + reason + reset ──
    [Fact]
    public void BothExhausted_Waits_WithReasonAndNextReset()
    {
        var fallback = Routing(new CliModelRouteProfile
        {
            CliType = "claude", FallbackCliType = "codex", FallbackModel = "gpt-5.3-codex",
        });
        Snapshot("claude", ("Weekly", 100, Now.AddHours(5)));
        Snapshot("codex", ("Weekly", 100, Now.AddHours(4)));

        var plan = Plan("claude", fallback, occupiedSlots: 0);

        Assert.Equal(QuotaAdmissionOutcome.Wait, plan.Outcome);
        Assert.False(plan.ShouldLaunch);
        Assert.StartsWith("waiting: all quotas exhausted", plan.Reason);
        Assert.Contains("next reset", plan.Reason);
        Assert.NotNull(plan.NextResetAt);
    }

    // ── acceptance scenario 3: after reset -> normal start on primary ──
    [Fact]
    public void AfterReset_LaunchesPrimary()
    {
        var fallback = Routing(new CliModelRouteProfile
        {
            CliType = "claude", PrimaryModel = "claude-opus",
            FallbackCliType = "codex", FallbackModel = "gpt-5.3-codex",
        });
        Snapshot("claude", ("Weekly", 12, Now.AddDays(3)));
        Snapshot("codex", ("Weekly", 10, Now.AddDays(3)));

        var plan = Plan("claude", fallback, occupiedSlots: 0);

        Assert.Equal(QuotaAdmissionOutcome.LaunchPrimary, plan.Outcome);
        Assert.False(plan.IsFallback);
        Assert.Equal("claude", plan.CliType);
        Assert.Equal("claude-opus", plan.Model);
    }

    // ── req 6: projected breach + usable fallback -> pre-emptive switch ──
    [Fact]
    public void ProjectedBreach_WithFallback_SwitchesBeforeTheWall()
    {
        var fallback = Routing(new CliModelRouteProfile
        {
            CliType = "claude", PrimaryModel = "claude-opus",
            FallbackCliType = "codex", FallbackModel = "gpt-5.3-codex",
        });
        // 60% used at the halfway point of a 5-hour window -> projects to 120%,
        // not yet over the 95% cap.
        Snapshot("claude", ("5-hour", 60, Now.AddHours(2.5)));
        Snapshot("codex", ("5-hour", 10, Now.AddHours(2.5)));

        var plan = Plan("claude", fallback, occupiedSlots: 0);

        Assert.Equal(QuotaAdmissionOutcome.LaunchFallback, plan.Outcome);
        Assert.True(plan.IsFallback);
        Assert.Equal("codex", plan.CliType);
        Assert.Contains("projected", plan.Reason);
        Assert.NotNull(plan.Projection);
        Assert.True(plan.Projection!.BreachesBeforeReset);
    }

    [Fact]
    public void AgT2107_SuspiciousFiveXProjection_DoesNotSwitchToFallback()
    {
        var fallback = Routing(new CliModelRouteProfile
        {
            CliType = "codex", PrimaryModel = "gpt-5.3-codex",
            FallbackCliType = "claude", FallbackModel = "claude-opus",
        });
        Snapshot("codex", ("5-hour", 19, Now.AddHours(4)));
        Snapshot("claude", ("5-hour", 10, Now.AddHours(2.5)));

        var plan = Plan("codex", fallback, occupiedSlots: 0);

        Assert.Equal(QuotaAdmissionOutcome.LaunchPrimary, plan.Outcome);
        Assert.False(plan.IsFallback);
        Assert.Null(plan.Projection);
        Assert.NotNull(plan.ProjectionWarning);
        Assert.Contains("projection ignored", plan.Reason);
        Assert.Contains("elapsed fraction", QuotaAdmissionPlanner.DescribeLoadNumbers(plan));
    }

    // ── req 6: projected breach, no fallback, a slot already busy -> throttle ──
    [Fact]
    public void ProjectedBreach_NoFallback_SlotBusy_Throttles()
    {
        var fallback = Routing(new CliModelRouteProfile { CliType = "claude", PrimaryModel = "claude-opus", FallbackDisabled = true });
        Snapshot("claude", ("5-hour", 60, Now.AddHours(2.5)));

        var plan = Plan("claude", fallback, occupiedSlots: 1);

        Assert.Equal(QuotaAdmissionOutcome.Throttle, plan.Outcome);
        Assert.StartsWith("throttling", plan.Reason);
    }

    // ── never throttle to zero: the first/only run always proceeds ──
    [Fact]
    public void ProjectedBreach_NoFallback_NoSlotBusy_LaunchesPrimaryFlagged()
    {
        var fallback = Routing(new CliModelRouteProfile { CliType = "claude", PrimaryModel = "claude-opus", FallbackDisabled = true });
        Snapshot("claude", ("5-hour", 60, Now.AddHours(2.5)));

        var plan = Plan("claude", fallback, occupiedSlots: 0);

        Assert.Equal(QuotaAdmissionOutcome.LaunchPrimary, plan.Outcome);
        Assert.False(plan.IsFallback);
        Assert.Contains("projection-flagged", plan.Reason);
    }

    // ── no routing service configured: still a correct primary/wait decision ──
    [Fact]
    public void NoRoutingService_ExhaustedPrimary_Waits()
    {
        Snapshot("claude", ("Weekly", 100, Now.AddDays(2)));
        var plan = Plan("claude", fallback: null, occupiedSlots: 0);
        Assert.Equal(QuotaAdmissionOutcome.Wait, plan.Outcome);
    }

    [Fact]
    public void NoRoutingService_HealthyPrimary_Launches()
    {
        Snapshot("claude", ("Weekly", 10, Now.AddDays(2)));
        var plan = Plan("claude", fallback: null, occupiedSlots: 0);
        Assert.Equal(QuotaAdmissionOutcome.LaunchPrimary, plan.Outcome);
    }

    // ── no cached snapshot at all -> never stall the queue, launch primary ──
    [Fact]
    public void NoSnapshot_LaunchesPrimary()
    {
        var fallback = Routing(new CliModelRouteProfile { CliType = "claude", PrimaryModel = "claude-opus", FallbackDisabled = true });
        var plan = Plan("claude", fallback, occupiedSlots: 0);
        Assert.Equal(QuotaAdmissionOutcome.LaunchPrimary, plan.Outcome);
    }

    // ── req 7: the load-distribution feed line carries the numbers ──
    [Fact]
    public void DescribeLoadNumbers_CarriesBurnRateBudgetAndTime()
    {
        var fallback = Routing(new CliModelRouteProfile { CliType = "claude", PrimaryModel = "claude-opus", FallbackDisabled = true });
        // 60% at the halfway point of a 5-hour window -> projects to 120%.
        Snapshot("claude", ("5-hour", 60, Now.AddHours(2.5)));
        var plan = Plan("claude", fallback, occupiedSlots: 1);   // throttle: projected, no fallback, slot busy
        Assert.Equal(QuotaAdmissionOutcome.Throttle, plan.Outcome);
        Assert.NotNull(plan.Projection);

        var text = QuotaAdmissionPlanner.DescribeLoadNumbers(plan);

        Assert.Contains("burn", text);
        Assert.Contains("%/h", text);          // burn rate
        Assert.Contains("budget left", text);  // remaining budget
        Assert.Contains("to reset", text);     // remaining time
        Assert.Contains("projected", text);
    }

    // ── the formatter degrades cleanly when there is no projectable window ──
    [Fact]
    public void DescribeLoadNumbers_NoProjection_FallsBackToOutcomeAndReset()
    {
        var plan = new QuotaAdmissionPlan(
            QuotaAdmissionOutcome.Wait, "claude", Model: null, ThinkingLevel: null,
            IsFallback: false, Reason: "waiting: all quotas exhausted",
            NextResetAt: Now.AddHours(4), Projection: null);

        var text = QuotaAdmissionPlanner.DescribeLoadNumbers(plan);

        Assert.Contains("Wait", text);
        Assert.Contains("next reset", text);
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private QuotaAdmissionPlan Plan(
        string cli,
        CliQuotaFallbackService? fallback,
        int occupiedSlots,
        ResolvedCliQuotaWaitPolicy? waitPolicy = null) =>
        QuotaAdmissionPlanner.Plan(
            cli, requestedModel: null, requestedThinking: null,
            fallback, _caps,
            c => c != null && _snapshots.TryGetValue(c, out var s) ? s : null,
            Now, occupiedSlots, waitPolicy);

    private CliQuotaFallbackService Routing(CliModelRouteProfile profile)
    {
        var svc = new CliQuotaFallbackService(_config, NullLogger<CliQuotaFallbackService>.Instance);
        svc.Set(profile);
        return svc;
    }

    private void Snapshot(string cli, params (string Label, double UsedPct, DateTime ResetAt)[] windows)
    {
        var snap = new QuotaSnapshot { CliType = cli };
        foreach (var w in windows)
            snap.Windows.Add(new QuotaWindow { Label = w.Label, UsedPct = w.UsedPct, ResetAt = w.ResetAt });
        _snapshots[cli] = snap;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }
}
