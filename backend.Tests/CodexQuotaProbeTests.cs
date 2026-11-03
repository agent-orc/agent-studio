
using Xunit;

namespace AgentStudio.Tests;

public class CodexQuotaProbeTests
{
    private static string ReadFixture(string name)
        => File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "quota",
            "codex",
            name));

    [Fact]
    public void ParseStatusWindows_V0_144_1Fixture_KeepsLegacySingleLineLayout()
    {
        var snapshot = ReadFixture("codex-status-v0.144.1.txt");

        var windows = CodexQuotaProbe.ParseStatusWindows(snapshot);

        Assert.Equal("Plus", CodexQuotaProbe.ParsePlan(snapshot));
        Assert.Collection(
            windows,
            w => { Assert.Equal("5-hour", w.Label); Assert.Equal(3, w.UsedPct); Assert.Equal("20:09", w.ResetLabel); },
            w => { Assert.Equal("Weekly", w.Label); Assert.Equal(14, w.UsedPct); Assert.Equal("23:43 on 11 Jun", w.ResetLabel); },
            w => { Assert.Equal("Spark 5-hour", w.Label); Assert.Equal(0, w.UsedPct); Assert.Equal("21:25", w.ResetLabel); },
            w => { Assert.Equal("Spark Weekly", w.Label); Assert.Equal(0, w.UsedPct); Assert.Equal("16:25 on 14 Jun", w.ResetLabel); });
    }

    [Fact]
    public void SharedParser_UsesTheObservationDateAndLocalZoneForLegacyResets()
    {
        // clock-independent: all reset comparisons use the fixed observation below.
        const string status = "Account: example (Plus)\n" +
            "5h limit: [░░] 97% left (resets 02:33)\n" +
            "Weekly limit: [██] 86% left (resets 21:33 on 3 May)";
        var observed = new DateTime(2026, 5, 2, 20, 0, 0, DateTimeKind.Utc);

        var windows = AgentStudio.CliHosting.SubscriptionQuotaParser.ParseCodex(status, observed);

        Assert.Collection(windows,
            fiveHour =>
            {
                Assert.Equal(3, fiveHour.UsedPct);
                Assert.Equal("02:33", fiveHour.ResetLabel);
                Assert.Equal(NextLocalOccurrence(observed, 2, 33), fiveHour.ResetAt);
            },
            weekly =>
            {
                Assert.Equal(14, weekly.UsedPct);
                Assert.Equal("21:33 on 3 May", weekly.ResetLabel);
                Assert.Equal(TimeZoneInfo.ConvertTimeToUtc(
                    new DateTime(2026, 5, 3, 21, 33, 0), TimeZoneInfo.Local), weekly.ResetAt);
            });
    }

    private static DateTime NextLocalOccurrence(DateTime observed, int hour, int minute)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(observed, TimeZoneInfo.Local);
        var next = new DateTime(local.Year, local.Month, local.Day, hour, minute, 0);
        if (next <= local) next = next.AddDays(1);
        return TimeZoneInfo.ConvertTimeToUtc(next, TimeZoneInfo.Local);
    }

    [Fact]
    public void ParseStatusWindows_V0_149_0Fixture_ReadsSplitResetRowsWithoutInventingFiveHour()
    {
        var snapshot = ReadFixture("codex-status-v0.149.0.txt");

        var windows = CodexQuotaProbe.ParseStatusWindows(snapshot);

        Assert.Equal("Pro", CodexQuotaProbe.ParsePlan(snapshot));
        Assert.DoesNotContain(windows, window => window.Label == "5-hour");
        Assert.Collection(
            windows,
            w => { Assert.Equal("Weekly", w.Label); Assert.Equal(61, w.UsedPct); Assert.Equal("17:12 on 1 Sep", w.ResetLabel); },
            w => { Assert.Equal("Spark 5-hour", w.Label); Assert.Equal(0, w.UsedPct); Assert.Equal("09:56", w.ResetLabel); },
            w => { Assert.Equal("Spark Weekly", w.Label); Assert.Equal(0, w.UsedPct); Assert.Equal("04:56 on 3 Sep", w.ResetLabel); });
    }

    [Fact]
    public void ParseSnapshot_V0_151_0HookReview_ReturnsAttributableErrorWithoutWindows()
    {
        var snapshot = CodexQuotaProbe.ParseSnapshot(ReadFixture("codex-hooks-v0.151.0.txt"));

        Assert.Equal(CodexQuotaProbe.HookReviewBlockedError, snapshot.Error);
        Assert.Empty(snapshot.Windows);
        Assert.Null(snapshot.Plan);
    }

    [Fact]
    public void ParseSnapshot_V0_153_4Fixture_ReadsWeeklyAndSparkWithoutInventingFiveHour()
    {
        var snapshot = CodexQuotaProbe.ParseSnapshot(ReadFixture("codex-status-v0.153.4.txt"));

        Assert.Equal("Pro", snapshot.Plan);
        Assert.Null(snapshot.Error);
        Assert.DoesNotContain(snapshot.Windows, window => window.Label == "5-hour");
        Assert.Collection(
            snapshot.Windows,
            w => { Assert.Equal("Weekly", w.Label); Assert.Equal(38, w.UsedPct); },
            w => { Assert.Equal("Spark 5-hour", w.Label); Assert.Equal(0, w.UsedPct); },
            w => { Assert.Equal("Spark Weekly", w.Label); Assert.Equal(0, w.UsedPct); });
    }

    [Fact]
    public void BuildProbeSteps_HookReviewDismissal_IsGuardedAndOnlySendsEscape()
    {
        var steps = CodexQuotaProbe.BuildProbeSteps();

        var hookReview = Assert.Single(steps, step => step.Name == "dismiss-hook-review");
        Assert.Equal(3000, hookReview.WaitTimeoutMs);
        Assert.True(hookReview.SendKeysOnlyIfMatched);
        Assert.Equal("<Esc>", hookReview.SendKeys);
        Assert.Equal(800, hookReview.SettleIdleMs);
        Assert.Equal(4000, hookReview.SettleTimeoutMs);
        Assert.Matches(hookReview.WaitForPattern!, "HOOKS NEED REVIEW");
        Assert.Matches(hookReview.WaitForPattern!, "PRESS T TO TRUST ALL");
        Assert.True(
            steps.Select(step => step.Name).ToList().IndexOf("dismiss-hook-review")
            < steps.Select(step => step.Name).ToList().IndexOf("await-welcome"));
        Assert.DoesNotContain(steps, step => string.Equals(step.SendKeys, "t", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ParseStatusWindows_ReadsStandardAndSparkLimitBlocks()
    {
        const string snapshot =
            "Account:        robertmischke@gmail.com (Plus)\n" +
            "5h limit:       [###################.] 97% left (resets 20:09)\n" +
            "Weekly limit:   [#################...] 86% left (resets 23:43 on 11 Jun)\n" +
            "GPT-5.3-Codex-Spark limit:\n" +
            "5h limit:       [####################] 100% left (resets 21:25)\n" +
            "Weekly limit:   [####################] 100% left (resets 16:25 on 14 Jun)\n";

        var windows = CodexQuotaProbe.ParseStatusWindows(snapshot);

        Assert.Equal(4, windows.Count);

        Assert.Equal("5-hour", windows[0].Label);
        Assert.Equal(3, windows[0].UsedPct);
        Assert.Equal("20:09", windows[0].ResetLabel);

        Assert.Equal("Weekly", windows[1].Label);
        Assert.Equal(14, windows[1].UsedPct);
        Assert.Equal("23:43 on 11 Jun", windows[1].ResetLabel);

        Assert.Equal("Spark 5-hour", windows[2].Label);
        Assert.Equal(0, windows[2].UsedPct);
        Assert.Equal("21:25", windows[2].ResetLabel);

        Assert.Equal("Spark Weekly", windows[3].Label);
        Assert.Equal(0, windows[3].UsedPct);
        Assert.Equal("16:25 on 14 Jun", windows[3].ResetLabel);
    }

    [Fact]
    public void ParseStatusWindows_StillReadsStandardLimitsWithoutSparkBlock()
    {
        const string snapshot =
            "Account:        robertmischke@gmail.com (Plus)\n" +
            "5h limit:       [############........] 60% left (resets 20:09)\n" +
            "Weekly limit:   [###############.....] 75% left (resets 23:43 on 11 Jun)\n";

        var windows = CodexQuotaProbe.ParseStatusWindows(snapshot);

        Assert.Equal(2, windows.Count);
        Assert.Equal("5-hour", windows[0].Label);
        Assert.Equal(40, windows[0].UsedPct);
        Assert.Equal("Weekly", windows[1].Label);
        Assert.Equal(25, windows[1].UsedPct);
    }

    // AGT-2064: the Spark sub-block header must be recognised regardless of the
    // Spark model version. The old regex pinned it to "GPT-5.3-Codex-Spark";
    // once the CLI advertised "GPT-5.6-Codex-Spark" the split collapsed. This
    // proves the standard/spark split still holds after a model bump.
    [Fact]
    public void ParseStatusWindows_BumpedSparkModel_StillSplitsStandardFromSpark()
    {
        const string snapshot =
            "Account:        robertmischke@gmail.com (Plus)\n" +
            "5h limit:       [############........] 60% left (resets 20:09)\n" +
            "Weekly limit:   [###############.....] 75% left (resets 23:43 on 11 Jun)\n" +
            "GPT-5.6-Codex-Spark limit:\n" +
            "5h limit:       [####################] 100% left (resets 21:25)\n" +
            "Weekly limit:   [####################] 100% left (resets 16:25 on 14 Jun)\n";

        var windows = CodexQuotaProbe.ParseStatusWindows(snapshot);

        Assert.Equal(4, windows.Count);
        Assert.Equal("5-hour", windows[0].Label);
        Assert.Equal(40, windows[0].UsedPct);
        Assert.Equal("Weekly", windows[1].Label);
        Assert.Equal(25, windows[1].UsedPct);
        Assert.Equal("Spark 5-hour", windows[2].Label);
        Assert.Equal(0, windows[2].UsedPct);
        Assert.Equal("Spark Weekly", windows[3].Label);
        Assert.Equal(0, windows[3].UsedPct);
    }

    // AGT-2064 root cause, reproduced. Near a reset the standard 5h/Weekly lines
    // can be momentarily absent while the near-empty Spark block still renders,
    // under a bumped ("GPT-5.6") Spark header. With the old version-pinned
    // header regex the standard-window parser latched onto the Spark line's high
    // "% left" and reported an exhausted account as MAIN "5-hour: 4% |
    // Weekly: 1%" - the exact false snapshot the operator acted on. The fix must
    // NEVER attribute Spark values to the main windows.
    [Fact]
    public void ParseStatusWindows_SparkOnlyWithBumpedHeader_DoesNotMasqueradeAsMainWindows()
    {
        const string snapshot =
            "Account:        robertmischke@gmail.com (Plus)\n" +
            "GPT-5.6-Codex-Spark limit:\n" +
            "5h limit:       [###################.] 96% left (resets 21:25)\n" +
            "Weekly limit:   [####################] 99% left (resets 16:25 on 14 Jun)\n";

        var windows = CodexQuotaProbe.ParseStatusWindows(snapshot);

        // The dangerous mislabel: the near-empty Spark values MUST NOT appear as
        // the main 5-hour / Weekly windows the admission gate reads.
        Assert.DoesNotContain(windows, w => w.Label == "5-hour");
        Assert.DoesNotContain(windows, w => w.Label == "Weekly");

        // They are correctly attributed to the Spark sub-windows instead.
        Assert.Contains(windows, w => w.Label == "Spark 5-hour" && w.UsedPct == 4);
        Assert.Contains(windows, w => w.Label == "Spark Weekly" && w.UsedPct == 1);
    }
}
