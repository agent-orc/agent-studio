using System.Globalization;

namespace AgentRunner;

/// <summary>
/// The CPU and task budget a single detached worker may consume on a shared
/// agent host.
///
/// <para>
/// AGT-2866: nothing used to bound one run's process tree. On 17.09.2026 three
/// Opus coding runs on <c>agent-runner-01</c> (12 cores, 2 coding + 2 review
/// slots) started 24, 49 and 104 CPU busy loops while reproducing a flaky test
/// "under contention" and drove the one-minute load average to 76, 35 and 41.
/// The runs themselves delivered; the collateral was a review suite that flaked
/// and a promotion gate that crawled. The role unit already carries an aggregate
/// <c>CPUQuota</c>/<c>CPUWeight</c> pair (see
/// docs/operations/haertung-verteilte-ausfuehrung/target-architecture/resource-governance.md),
/// but an aggregate cannot stop one slot from eating the whole role, and the
/// load-aware admission in <c>RUNNER_CLAIM_MAX_LOAD_PER_CORE</c> only delays the
/// <em>next</em> claim.
/// </para>
///
/// <para>
/// This record is the single derived budget. One number drives three consumers:
/// the per-worker cgroup written by <see cref="WorkerCgroup"/>, the claim
/// admission of the review daemon (<see cref="ReviewSlotAdmissionPolicy"/>), and
/// the usage line the runner reports at the end of a run. Pure on purpose, so
/// the matrix can be asserted directly.
/// </para>
/// </summary>
public sealed record WorkerResourceEnvelope(
    int HostCores,
    int TotalSlots,
    double CoresPerSlot,
    int CpuQuotaPercent,
    int CpuWeight,
    int TasksMax)
{
    /// <summary>cgroup v2 <c>cpu.max</c> accounting period, in microseconds (the kernel default).</summary>
    public const int CpuPeriodMicroseconds = 100_000;

    /// <summary>
    /// How far above its fair share one worker may burst while its siblings are
    /// idle. The constraint on AGT-2866 was that an otherwise idle host keeps its
    /// current behaviour, and a hard fair-share ceiling would visibly slow a
    /// normal build. Two fair shares covers every measured build, test and lint
    /// tree on this fleet while still being two orders of magnitude below the
    /// 24 to 104 busy loops the envelope exists to stop.
    /// </summary>
    public const double DefaultCpuBurst = 2.0;

    public const double MinimumCpuBurst = 1.0;
    public const double MaximumCpuBurst = 8.0;

    /// <summary>A worker always gets at least one whole core, however small the host is.</summary>
    public const int MinimumCpuQuotaPercent = 100;

    /// <summary>
    /// Every worker of a role carries the same weight, so no worker can starve a
    /// sibling under contention. Cross-role priority stays where it was decided:
    /// the role unit's own <c>CPUWeight</c>.
    /// </summary>
    public const int WorkerCpuWeight = 100;

    /// <summary>
    /// The daemon leaf outranks its own workers by an order of magnitude. Lease
    /// renewal, heartbeat and log shipping are cheap and must never lose the run
    /// they are keeping alive to the CPU the run is burning.
    /// </summary>
    public const int SupervisorCpuWeight = 1000;

    /// <summary>
    /// Tasks (processes plus threads) granted per core of fair share. A dotnet
    /// plus node plus shell build and test tree on this fleet peaks in the low
    /// hundreds; 128 per core puts a 12-core, 4-slot host at 384 per worker,
    /// which is a few hundred as the card asked and far below the hundreds of
    /// forked loops the incident produced.
    /// </summary>
    public const int TasksPerCore = 128;

    public const int MinimumTasksMax = 192;
    public const int MaximumTasksMax = 4096;

    /// <summary>
    /// Smallest fair share a slot may be admitted with. A host whose declared
    /// slot count leaves less than one core per slot is oversubscribed by
    /// configuration, and no cgroup can fix that.
    /// </summary>
    public const double MinimumCoresPerSlot = 1.0;

    /// <summary>cgroup v2 <c>cpu.max</c> value, for example <c>600000 100000</c> for 600%.</summary>
    public string CpuMax => string.Create(
        CultureInfo.InvariantCulture,
        $"{(long)CpuQuotaPercent * CpuPeriodMicroseconds / 100} {CpuPeriodMicroseconds}");

    /// <summary>systemd spelling of the same ceiling, for operator-facing output.</summary>
    public string CpuQuota => string.Create(CultureInfo.InvariantCulture, $"{CpuQuotaPercent}%");

    public string Describe() => string.Create(
        CultureInfo.InvariantCulture,
        $"cores={HostCores} slots={TotalSlots} coresPerSlot={CoresPerSlot:0.00} " +
        $"cpuQuota={CpuQuotaPercent}% cpuWeight={CpuWeight} tasksMax={TasksMax}");

    /// <summary>
    /// Derive one worker's envelope from the host's core count and the slot
    /// counts the two roles declare. Both roles read the same two slot numbers,
    /// so a coding worker and a review worker on the same host agree on what one
    /// slot is worth.
    /// </summary>
    public static WorkerResourceEnvelope Compute(
        int hostCores,
        int codingSlots,
        int reviewSlots,
        double cpuBurst = DefaultCpuBurst)
    {
        var cores = Math.Max(1, hostCores);
        var slots = Math.Max(1, Math.Max(0, codingSlots) + Math.Max(0, reviewSlots));
        var burst = Math.Clamp(cpuBurst, MinimumCpuBurst, MaximumCpuBurst);
        var coresPerSlot = cores / (double)slots;

        var quota = (int)Math.Round(coresPerSlot * burst * 100, MidpointRounding.AwayFromZero);
        quota = Math.Clamp(quota, MinimumCpuQuotaPercent, cores * 100);

        var tasks = (int)Math.Round(coresPerSlot * TasksPerCore, MidpointRounding.AwayFromZero);
        tasks = Math.Clamp(tasks, MinimumTasksMax, MaximumTasksMax);

        return new WorkerResourceEnvelope(
            cores,
            slots,
            coresPerSlot,
            quota,
            WorkerCpuWeight,
            tasks);
    }

    /// <summary>
    /// The envelope this host's configuration implies. <see cref="Environment.ProcessorCount"/>
    /// is the same core count the host telemetry sampler reports, so admission and
    /// enforcement never disagree about how big the machine is.
    /// </summary>
    public static WorkerResourceEnvelope FromOptions(RunnerOptions options)
        => Compute(
            Environment.ProcessorCount,
            options.HostCodingSlots,
            options.HostReviewSlots,
            options.WorkerCpuBurst);

    /// <summary>
    /// Highest slot ceiling this host can still hand a full envelope to. The
    /// review daemon's adaptive ceiling is clamped by this, so a centrally raised
    /// recommendation can never admit more slots than there are cores to give
    /// them (AGT-2866 deliverable 3).
    /// </summary>
    public static int SlotCeilingForCores(int hostCores)
        => Math.Max(1, (int)(Math.Max(1, hostCores) / MinimumCoresPerSlot));
}
