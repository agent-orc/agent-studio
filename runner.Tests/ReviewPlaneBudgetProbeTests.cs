using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class ReviewPlaneBudgetProbeTests
{
    private static readonly RunnerOptions Options = new()
    {
        ServerUrl = "http://127.0.0.1:5030",
        RunnerId = "review-budget-test",
        RunnerName = "review-budget-test",
        Hostname = "review-budget-test",
        BackendName = "test",
        WorkDir = "/tmp/review-budget-test",
        BaseBranch = "develop",
        ClaudeCliBin = "codex",
        HostCodingSlots = 2,
        HostReviewSlots = 2,
    };

    [Fact]
    public void Cpu_max_parses_unlimited_quota()
    {
        var parsed = ReviewPlaneBudgetProbe.ParseCpuMax("max 100000\n");

        Assert.True(parsed.IsUnlimited);
        Assert.Null(parsed.QuotaCores);
        Assert.Null(parsed.QuotaPercent);
        Assert.Equal("max 100000", parsed.Raw);
    }

    [Fact]
    public void Cpu_max_parses_numeric_quota()
    {
        var parsed = ReviewPlaneBudgetProbe.ParseCpuMax("400000 100000");

        Assert.False(parsed.IsUnlimited);
        Assert.Equal(4, parsed.QuotaCores);
        Assert.Equal(400, parsed.QuotaPercent);
    }

    [Theory]
    [InlineData("0-11", 12)]
    [InlineData("0-3,8-11", 8)]
    [InlineData("0,2,4", 3)]
    public void Online_cpu_topology_reports_host_cores_independently_of_role_quota(
        string onlineCpus,
        int expected)
    {
        Assert.Equal(expected, ReviewPlaneBudgetProbe.ParseOnlineCpuCount(onlineCpus));
    }

    [Fact]
    public void Admission_polls_do_not_reset_the_throttled_share_window()
    {
        var cpuMax = "400000 100000";
        var throttledUsec = 0L;
        var probe = Probe(
            () => cpuMax,
            () => $"usage_usec 0\nthrottled_usec {throttledUsec}\n");
        var started = new DateTime(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc);

        Assert.Null(probe.Sample(Options, currentCeiling: 2, started).ThrottledShare);
        throttledUsec = 2_000_000;

        for (var poll = 0; poll < 100; poll++)
        {
            var admission = ReviewSlotAdmissionPolicy.Decide(
                Sample(),
                activeSlots: 1,
                slotCeiling: 3,
                maxLoadPerCore: 1.5,
                WorkerResourceEnvelope.Compute(hostCores: 12, codingSlots: 2, reviewSlots: 2),
                probe.ReadRoleQuotaCores());

            Assert.True(admission.Admitted);
        }

        var advertised = probe.Sample(Options, currentCeiling: 2, started.AddSeconds(10));

        Assert.Equal(0.2, advertised.ThrottledShare);
        Assert.False(advertised.SustainedThrottling);
    }

    [Fact]
    public void Admission_reads_a_live_role_quota_before_the_next_sampler_tick()
    {
        var cpuMax = "400000 100000";
        var probe = Probe(() => cpuMax, () => "throttled_usec 0");
        var started = new DateTime(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc);
        var advertised = probe.Sample(Options, currentCeiling: 3, started);
        var beforeChange = ReviewSlotAdmissionPolicy.Decide(
            Sample(),
            activeSlots: 2,
            slotCeiling: 3,
            maxLoadPerCore: 1.5,
            WorkerResourceEnvelope.Compute(hostCores: 12, codingSlots: 2, reviewSlots: 2),
            probe.ReadRoleQuotaCores());

        cpuMax = "800000 100000";
        var afterChange = ReviewSlotAdmissionPolicy.Decide(
            Sample(),
            activeSlots: 2,
            slotCeiling: 3,
            maxLoadPerCore: 1.5,
            WorkerResourceEnvelope.Compute(hostCores: 12, codingSlots: 2, reviewSlots: 2),
            probe.ReadRoleQuotaCores());

        Assert.Equal(4, advertised.PlaneCpuCores);
        Assert.False(beforeChange.Admitted);
        Assert.True(afterChange.Admitted);
    }

    [Fact]
    public void Sustained_alarm_counts_sampler_ticks_only()
    {
        var throttledUsec = 0L;
        var probe = Probe(
            () => "400000 100000",
            () => $"throttled_usec {throttledUsec}");
        var started = new DateTime(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc);
        _ = probe.Sample(Options, currentCeiling: 2, started);

        throttledUsec = 2_000_000;
        for (var poll = 0; poll < 100; poll++)
            _ = probe.ReadRoleQuotaCores();
        var firstHighSample = probe.Sample(Options, currentCeiling: 2, started.AddSeconds(10));

        throttledUsec = 4_000_000;
        for (var poll = 0; poll < 100; poll++)
            _ = probe.ReadRoleQuotaCores();
        var secondHighSample = probe.Sample(Options, currentCeiling: 2, started.AddSeconds(20));

        Assert.Equal(0.2, firstHighSample.ThrottledShare);
        Assert.False(firstHighSample.SustainedThrottling);
        Assert.Equal(0.2, secondHighSample.ThrottledShare);
        Assert.True(secondHighSample.SustainedThrottling);
        Assert.Contains("Raise the review role quota", secondHighSample.AlarmSuggestion, StringComparison.Ordinal);
    }

    private static ReviewPlaneBudgetProbe Probe(
        Func<string> readCpuMax,
        Func<string> readCpuStat)
        => new(() => 12, readCpuMax, readCpuStat);

    private static HostTelemetrySample Sample()
        => new(
            DateTime.UtcNow,
            CpuPercent: 10,
            Load1: 1,
            Load5: 1,
            Load15: 1,
            MemoryUsedBytes: null,
            MemoryTotalBytes: null,
            SwapInBytesPerSecond: null,
            SwapOutBytesPerSecond: null,
            CpuStealPercent: 0,
            IoWaitPercent: 0,
            CpuCores: 12,
            ActiveSlots: 1);
}
