using AgentStudio.Cli;
using AgentStudio.Shared;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Operator;

public sealed record CapacityPressureSnapshot(DateTime At, string HostInventory,
    IReadOnlyList<CapacityHostPressure> Hosts, IReadOnlyList<CapacityQuotaPressure> Quota,
    IReadOnlyList<CapacityReadyWork> ReadyWork, IReadOnlyList<CapacityCodingAdmission> CodingAdmissions);

public sealed record CapacityReadyWork(string Class, string Source, int? Count, DateTime? OldestAt,
    double? OldestAgeSeconds);

public sealed record CapacityDiskPressure(string Purpose, long FreeBytes, long TotalBytes, double FreePercent);
public sealed record CapacityClaimHold(string Code, string Reason, DateTime ObservedAt, string Source);

public sealed record CapacityHostPressure(string HostId, string ExecutorId, string Role, string Source,
    DateTime? HeartbeatAt, DateTime? ObservedAt, string TelemetryState, DateTime? SlotsObservedAt,
    int? EffectiveSlots, int? OccupiedSlots, int? Cores, double? Load1, double? LoadPerCore,
    double? CpuPercent, long? MemoryUsedBytes, long? MemoryTotalBytes, long? SwapUsedBytes,
    long? SwapTotalBytes, long? SwapInBytesPerSecond, long? SwapOutBytesPerSecond, double? IoWaitPercent,
    string DiskLevel, string? DiskReason, IReadOnlyList<CapacityDiskPressure>? Disk,
    ClaimLoadGateDto? ClaimLoadGate, CapacityClaimHold? ClaimHold, IReadOnlyList<CapacityReadyWork> ReadyWork);

public sealed record CapacityAdmissionReceipt(string Id, DateTimeOffset At, string Outcome, string Rule, string? Reason);
public sealed record CapacityAcceptedFallback(string ReceiptId, DateTimeOffset At, string? Provider,
    string? Model, string? ThinkingLevel);

public sealed record CapacityQuotaPressure(string Provider, string? SubscriptionKey, string? Window,
    string State, DateTime? ObservedAt, DateTime? ProbeFailedAt, string? Source, double? UsedPercent,
    double? Used, double? Limit, string? Unit, DateTime? ResetAt, string? ResetLabel, string? ResetSource,
    int? ReservePoints, int? CapPercent, int? RegularWorkCapPercent, QuotaPace? Pace, double? PacingTargetPercent,
    double? ObservedRatePercentPerHour, DateTime? RateFrom, DateTime? PaceReleaseAt,
    CapacityAdmissionReceipt? LastAdmission, CapacityAcceptedFallback? AcceptedFallback);

public sealed record CapacityCodingAdmission(string Project, string CardKey, string Class, bool HardPinned,
    string? Provider, string Outcome, string? Reason, DateTime? RetryAt, SelectionRoute? AcceptedFallback);

internal static class OperatorPressureProjection
{
    internal static CapacityReadyWork Queue(string workClass, string source, IEnumerable<DateTime>? timestamps, DateTime now)
    {
        var times = timestamps?.ToArray();
        return Queue(workClass, source, times?.Length,
            times is { Length: > 0 } && times.All(at => at != default && at <= now) ? times.Min() : null, now);
    }

    internal static CapacityReadyWork Queue(string workClass, string source, int? count, DateTime? oldest, DateTime now)
        => new(workClass, source, count, oldest,
            oldest is { } at && at != default && at <= now ? (now - at).TotalSeconds : null);

    internal static CapacityHostPressure Host(string hostId, string executorId, string role, string source,
        DateTime? heartbeat, HostTelemetrySnapshotDto? reading, DateTime? slotsAt, int? effectiveSlots,
        int? occupiedSlots, IReadOnlyList<CapacityReadyWork> ready, DateTime now,
        Func<IReadOnlyList<HostFilesystemUsageDto>, HostDiskPressureDecision> diskPressure,
        CapacityClaimHold? claimHold = null)
    {
        var observed = reading?.ObservedAt is { } at && at != default ? at : (DateTime?)null;
        var fresh = observed is { } timestamp && OperatorHostDiskFactSource.IsFresh(timestamp, now);
        var slotsFresh = slotsAt is { } slotTimestamp && OperatorHostDiskFactSource.IsFresh(slotTimestamp, now);
        var sample = fresh ? reading : null;
        var cores = sample?.CpuCores is > 0 ? sample.CpuCores : (int?)null;
        var load = Number(sample?.Load1);
        var filesystems = sample?.Filesystems;
        if ((filesystems is null or { Count: 0 }) && sample is { DiskFreeBytes: { } free, DiskTotalBytes: { } total })
            filesystems = [new("runner work", "", free, total)];
        var disks = filesystems?.Where(fs => fs.TotalBytes > 0 && fs.FreeBytes >= 0 && fs.FreeBytes <= fs.TotalBytes).ToArray();
        var disk = disks is { Length: > 0 } ? diskPressure(disks) : null;
        var diskLevel = disk is null || disk.Level == HostDiskPressureLevel.Healthy && disks!.Length != filesystems!.Count
            ? "unknown" : disk.Level.ToString().ToLowerInvariant();
        var loadGate = sample?.ClaimLoadGate;
        if (claimHold is { Code: "host-disk-pressure" } recorded && string.IsNullOrWhiteSpace(recorded.Reason))
            claimHold = recorded with { Reason = disk?.Level == HostDiskPressureLevel.Critical ? disk.Reason : "Disk pressure held claims." };
        claimHold ??= disk?.Level == HostDiskPressureLevel.Critical && observed is { } diskAt
            ? new("host-disk-pressure", disk.Reason, diskAt, "disk-admission")
            : loadGate is { State: "closed" } && observed is { } loadAt
                ? new("host-load-gate", "Host load gate closed.", loadAt, "runner-heartbeat") : null;
        return new(hostId, executorId, role, source, heartbeat, observed,
            fresh ? "fresh" : observed is null ? "unreported" : "stale", slotsAt,
            slotsFresh ? Nonnegative(effectiveSlots) : null, slotsFresh ? Nonnegative(occupiedSlots) : null,
            cores, load, load / cores, Number(sample?.CpuPercent), Nonnegative(sample?.MemoryUsedBytes),
            Nonnegative(sample?.MemoryTotalBytes), Nonnegative(sample?.SwapUsedBytes), Nonnegative(sample?.SwapTotalBytes),
            Nonnegative(sample?.SwapInBytesPerSecond), Nonnegative(sample?.SwapOutBytesPerSecond), Number(sample?.IoWaitPercent),
            diskLevel, diskLevel == "unknown" ? null : disk?.Reason,
            disks is { Length: > 0 } ? disks.Select(fs => new CapacityDiskPressure(fs.Purpose,
                fs.FreeBytes, fs.TotalBytes, 100d * fs.FreeBytes / fs.TotalBytes)).ToArray() : null,
            loadGate, claimHold, ready);
    }

    private static T? Nonnegative<T>(T? value) where T : struct, System.Numerics.INumber<T>
        => value is { } number && number >= T.Zero ? number : null;

    private static double? Number(double? value)
        => value is >= 0 and < double.PositiveInfinity ? value : null;

    internal static CapacityQuotaPressure[] Quota(QuotaReport report, CliQuotaCapsService caps,
        ModelSelectionPolicy policy, Func<string, DateTime, IReadOnlyList<QuotaHistoryPoint>> history,
        IReadOnlyList<ModelSelectionEvent> admissions, IReadOnlyList<ModelSelectionEvent> active, DateTime now)
    {
        var reserve = Math.Clamp(policy.ShortStepReservePoints, 0, 50);
        var snapshots = report.Snapshots.OrderByDescending(snapshot => snapshot.ProbeFailedAt ?? snapshot.FetchedAt)
            .DistinctBy(snapshot => (snapshot.CliType, snapshot.SubscriptionKey)).ToArray();
        var rateHistory = new Dictionary<string, IReadOnlyList<QuotaHistoryPoint>>(StringComparer.OrdinalIgnoreCase);
        return snapshots.SelectMany(snapshot => snapshot.Windows.Cast<QuotaWindow?>().DefaultIfEmpty()
            .Select(window =>
            {
                var state = SnapshotState(snapshot, report, now);
                var observed = snapshot.FetchedAt != default && snapshot.Windows.Count > 0 ? Utc(snapshot.FetchedAt) : (DateTime?)null;
                var failed = snapshot.ProbeFailedAt is { } failure ? Utc(failure) : (DateTime?)null;
                if (window is null)
                    return new CapacityQuotaPressure(snapshot.CliType, snapshot.SubscriptionKey, null, state,
                        observed, failed, snapshot.Source, null, null, null, null, null, null, null,
                        null, null, null, null, null, null, null, null, null, null);

                var windowCurrent = state == "fresh" && !(window.ResetAt <= now);
                var usage = windowCurrent ? Number(window.UsedPct) : null;
                var cap = caps.GetCap(snapshot.CliType, window.Label);
                var effectiveCap = SelectionQuotaMath.EffectiveCap(cap, reserve);
                var pace = policy.PaceFor(snapshot.CliType, window.Label);
                var reset = windowCurrent && string.IsNullOrWhiteSpace(window.ProjectionSuspiciousReason)
                    && window.ResetAt is { } reportedReset ? Utc(reportedReset) : (DateTime?)null;
                var projection = reset is not null && usage is not null
                    ? SelectionQuotaMath.Project(window, now, effectiveCap, pace) : null;
                QuotaForecast? rate = null;
                if (usage is not null && observed is { } timestamp)
                {
                    if (!rateHistory.TryGetValue(snapshot.CliType, out var historyPoints))
                        rateHistory[snapshot.CliType] = historyPoints = history(snapshot.CliType, now - QuotaForecastPolicy.RateLookback
                            - TimeSpan.FromSeconds(report.TtlSeconds));
                    var readings = historyPoints.Where(point => point.SubscriptionKey == snapshot.SubscriptionKey
                        && Same(point.Window, window.Label) && point.At <= timestamp && point.ResetAt == reset
                        && Number(point.UsedPct) is not null).ToArray();
                    if (readings.Any(point => point.At == timestamp && point.UsedPct == usage))
                        rate = QuotaForecastPolicy.Forecast(readings, now);
                }

                bool Matches(ModelSelectionEvent entry) => entry.At.UtcDateTime <= now && entry.Quota is { } quota
                    && Same(quota.Provider ?? entry.From?.CliType, snapshot.CliType) && Same(quota.Window, window.Label)
                    && (quota.SubscriptionKey is not null ? quota.SubscriptionKey == snapshot.SubscriptionKey
                        : snapshot.SubscriptionKey is null && snapshots.All(other => !Same(other.CliType, snapshot.CliType)
                            || other.SubscriptionKey is null));
                var last = admissions.FirstOrDefault(entry => Matches(entry) && AdmissionOutcome(entry) is not null);
                var fallback = active.FirstOrDefault(entry => entry.Kind == "switched" && entry.To is not null && Matches(entry));
                return new CapacityQuotaPressure(snapshot.CliType, snapshot.SubscriptionKey, window.Label,
                    state == "fresh" && !windowCurrent ? "expired-window" : state, observed, failed, snapshot.Source,
                    usage, windowCurrent ? Number(window.Used) : null, windowCurrent ? Number(window.Limit) : null,
                    window.Unit, reset, window.ResetLabel, reset is null ? null : snapshot.Source,
                    reserve, cap, effectiveCap, pace, pace.Mode == "unpaced" ? effectiveCap : projection?.AllowedPct,
                    rate?.RatePctPerHour, rate?.RateFrom, projection?.FreeAt > now ? projection.FreeAt : null,
                    last is null ? null : new(last.Id, last.At, AdmissionOutcome(last)!, last.Rule, last.Reason),
                    fallback is null ? null : new(fallback.Id, fallback.At, fallback.To!.CliType,
                        fallback.To.Model, fallback.To.ThinkingLevel));
            })).ToArray();
    }

    private static string SnapshotState(QuotaSnapshot snapshot, QuotaReport report, DateTime now)
    {
        if (snapshot.Error is not null || snapshot.ProbeFailedAt is not null) return "failed";
        if (snapshot.Suspicious) return "unconfirmed";
        if (snapshot.FetchedAt == default || snapshot.Windows.Count == 0) return "unreported";
        return snapshot.FetchedAt > now || now - snapshot.FetchedAt > TimeSpan.FromSeconds(report.TtlSeconds) ? "stale" : "fresh";
    }

    private static string? AdmissionOutcome(ModelSelectionEvent entry) => entry.AdmissionOutcome ?? entry.Kind switch
    {
        "selected" => "selected",
        "returned" => "primary",
        "waiting" => "deferred",
        "switched" => "fallback",
        "refused" => "refused",
        _ => null,
    };

    internal static bool Same(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Local
        ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
