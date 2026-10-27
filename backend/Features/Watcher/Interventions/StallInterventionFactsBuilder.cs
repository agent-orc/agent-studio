using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentStudio.Watcher;

/// <summary>
/// AGT-3207: freezes the typed facts of one stall case or one follow-up check
/// from the Watcher case, the intervention history and the live ports. It
/// reads; it never decides or mutates.
/// </summary>
public sealed partial class StallInterventionFactsBuilder(
    IHostAdmissionControl admission,
    IServiceRestartControl restarts,
    IServiceWorkload workloads,
    IProviderRouteControl routes,
    IBatchGateControl gate,
    IContinuationControl continuations,
    IConfiguration configuration)
{
    public static readonly TimeSpan TelemetryFreshness = TimeSpan.FromMinutes(10);

    public static bool IsCurrentPressure(string kind, HostAdmissionReading? reading,
        DateTime nowUtc, OperationalStallThresholds thresholds)
    {
        if (reading?.SampleUtc is not { } sampled || sampled > nowUtc
            || nowUtc - sampled > TelemetryFreshness) return false;
        return kind switch
        {
            StallKinds.HostOverload => reading.CpuPercent > reading.TargetPercent
                || reading.LoadPerCore * 100 > reading.TargetPercent,
            StallKinds.MemoryPressure => reading.MemoryUsedPercent > thresholds.MemoryUsedPercent
                || reading.SwapBytesPerSecond > thresholds.SwapBytesPerSecond,
            _ => false,
        };
    }
    public static string? KindOf(WatcherCase item) => item.LastDetails.GetValueOrDefault("stallKind");

    /// <summary>The stall is still there, whether or not a person was asked to decide it.</summary>
    public static bool IsOpen(WatcherCase item)
        => item.State is WatcherCaseStates.Open or WatcherCaseStates.DecisionRequired;

    /// <summary>The AI Operator acts only on an open case; a case in decision-required belongs to a person.</summary>
    public static bool IsActionable(WatcherCase item) => item.State == WatcherCaseStates.Open;

    /// <summary>
    /// The history the guards need: the subject's and host's records and the
    /// records that touched one of the affected cards, of the loop window, of
    /// the current stall episode, and every pending one. "Would do" receipts of
    /// observe mode changed nothing and are left out.
    /// </summary>
    public static IReadOnlyList<StallInterventionHistoryEntry> History(IEnumerable<StallInterventionRecord> records,
        string subject, DateTime? sinceUtc, StallInterventionOptions options, DateTime nowUtc,
        IReadOnlyCollection<string>? cards = null)
    {
        var host = StallSubjects.HostOf(subject);
        var windowStart = nowUtc.AddHours(-Math.Max(1, options.Limits.LoopWindowHours));
        return records.Where(item => item.Status != StallInterventionStatuses.Observed)
            .Where(item => item.Subject == subject || item.Host == host
                || cards is { Count: > 0 } && item.Members.Any(cards.Contains))
            .Where(item => StallInterventionStatuses.Pending.Contains(item.Status) || item.DecidedAtUtc >= windowStart
                || sinceUtc is { } since && item.DecidedAtUtc >= since)
            .TakeLast(100)
            .Select(item => new StallInterventionHistoryEntry(item.Id, item.Subject, item.Host,
                item.Action, item.DecidedAtUtc, item.Status, item.Members.Count == 0 ? null : item.Members.ToArray())).ToArray();
    }

    public StallInterventionFacts Stall(WatcherCase item, IReadOnlyList<WatcherCase> openCases,
        IReadOnlyList<StallInterventionRecord> records, StallInterventionOptions options, DateTime nowUtc)
    {
        var kind = KindOf(item) ?? throw new ArgumentException("The case is not an operational stall.");
        var subject = item.Subject!;
        var parts = subject.Split(':');
        var facts = new StallInterventionFacts(StallInterventionTriggers.Stall, kind, subject, nowUtc,
            item.SinceUtc, item.AffectedCards.ToArray(), options.Limits,
            History(records, subject, item.SinceUtc, options, nowUtc, item.AffectedCards), CaseId: item.Id);
        var provider = parts.Length > 2 ? parts[2] : "unknown";
        return kind switch
        {
            StallKinds.HostOverload or StallKinds.MemoryPressure => facts with { Host = HostFacts(item, parts[1], records, options, nowUtc) },
            StallKinds.HostOffline or StallKinds.IdleCapacity => facts with
            {
                Service = ServiceFacts(item, parts[1], parts.Length > 2 ? parts[2] : "coding", openCases, options, nowUtc),
            },
            StallKinds.ProviderAuthInvalid => facts with
            {
                Provider = new StallProviderFacts(parts[1], provider, "auth-invalid",
                    routes.HealthyHosts(provider, parts[1]),
                    Math.Max(item.BlockedCount, item.AffectedCards.Count),
                    routes.IsRerouted(parts[1], provider), routes.IsReviewHost(parts[1])),
            },
            StallKinds.ProviderRateLimited => facts with { Requeue = continuations.RateLimitedRuns(item.AffectedCards) },
            StallKinds.ClaimRefusal => facts with { ClaimReason = subject.StartsWith("queue:", StringComparison.Ordinal)
                ? subject["queue:".Length..] : subject },
            StallKinds.BatchGateStuck => facts with { Gate = gate.Read(parts.Length > 1 ? parts[1] : item.Project, nowUtc) },
            StallKinds.DeployDrift => facts with { Deploy = DeployFacts(item, parts[1], parts.Length > 2 ? parts[2] : "unknown", options) },
            _ => facts,
        };
    }

    public StallInterventionFacts FollowUp(StallInterventionRecord record, WatcherCase? item,
        IReadOnlyList<StallInterventionRecord> records, StallInterventionOptions options, DateTime nowUtc)
    {
        var conditionActive = item is not null && IsOpen(item);
        var effect = Effect(record, options, nowUtc, out var evidence, out var reading);
        if (record.Action == StallInterventionActions.RestoreAdmission) conditionActive = false;
        var check = new StallFollowUpFacts(record.Id, record.Action, record.DecidedAtUtc,
            record.FollowUpDueUtc ?? record.DecidedAtUtc, conditionActive, effect, evidence,
            record.Chain.Count + 1, record.PreviousAdmission, reading?.Admission, reading?.LoadPerCore,
            reading?.TargetPercent, reading?.CalmSinceUtc, reading?.ActiveSlots);
        return new StallInterventionFacts(StallInterventionTriggers.FollowUp, record.Kind, record.Subject, nowUtc,
            item?.SinceUtc, record.AffectedCards.ToArray(), options.Limits,
            History(records, record.Subject, item?.SinceUtc, options, nowUtc), FollowUp: check, CaseId: record.CaseId);
    }

    /// <summary>
    /// The restore check of a lowered admission that is still in force after
    /// its own follow-up settled. <paramref name="reading"/> is the admission
    /// read now; the condition is active while an overload case of the host is open.
    /// </summary>
    public StallInterventionFacts Restore(StallInterventionRecord record, HostAdmissionReading reading, bool overloadOpen,
        IReadOnlyList<StallInterventionRecord> records, StallInterventionOptions options, DateTime nowUtc)
    {
        var check = new StallFollowUpFacts(record.Id, record.Action, record.DecidedAtUtc,
            record.FollowUpDueUtc ?? record.DecidedAtUtc, overloadOpen, record.EffectObserved == true, record.EffectEvidence,
            record.Chain.Count + 1, record.PreviousAdmission, reading.Admission, reading.LoadPerCore,
            reading.TargetPercent, reading.CalmSinceUtc, reading.ActiveSlots);
        return new StallInterventionFacts(StallInterventionTriggers.Restore, record.Kind, record.Subject, nowUtc,
            null, record.AffectedCards.ToArray(), options.Limits,
            History(records, record.Subject, null, options, nowUtc), FollowUp: check, CaseId: record.CaseId);
    }

    /// <summary>The admission of the host a reduction was set on, read by its client identity id.</summary>
    public HostAdmissionReading? AdmissionOf(StallInterventionRecord record, StallInterventionOptions options, DateTime nowUtc)
        => record.HostClientId is { } clientId
            ? admission.Read(clientId, options.Limits.RestoreBelowTargetFraction, nowUtc) : null;

    /// <summary>Measures the effect of an applied action now, not the decision that ordered it.</summary>
    public bool Effect(StallInterventionRecord record, StallInterventionOptions options, DateTime nowUtc,
        out string evidence, out HostAdmissionReading? reading)
    {
        reading = null;
        switch (record.Action)
        {
            case StallInterventionActions.LowerAdmission or StallInterventionActions.RestoreAdmission:
                reading = record.HostClientId is { } clientId
                    ? admission.Read(clientId, options.Limits.RestoreBelowTargetFraction, nowUtc) : null;
                evidence = $"admission reads {reading?.Admission?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}, expected {record.TargetAdmission}";
                return reading?.Admission == record.TargetAdmission;
            case StallInterventionActions.RerouteProvider:
                var rerouted = routes.IsRerouted(record.Host, record.Provider ?? "unknown");
                evidence = rerouted ? $"{record.Provider} is not routed to {record.Host}" : $"{record.Provider} is routed to {record.Host} again";
                return rerouted;
            case StallInterventionActions.ReconcileBatchGate:
                var still = record.AttemptIds.Where(attempt => gate.IsPending(record.Project, attempt)).ToArray();
                evidence = still.Length == 0 ? "no dropped member is pending" : $"still pending: {string.Join(", ", still)}";
                return still.Length == 0;
            case StallInterventionActions.RequeueFromDelivery:
                var missing = record.Members.Where(card => continuations.Read(card) is null && !continuations.LeftReady(card)).ToArray();
                evidence = missing.Length == 0 ? "every requeued card holds its delivery base or already started from it"
                    : $"no base on {string.Join(", ", missing)}";
                return missing.Length == 0;
            default:
                evidence = record.EffectEvidence ?? "the action reported its own outcome";
                return record.EffectObserved ?? false;
        }
    }

    /// <summary>
    /// The host's admission by its client identity id (the case's <c>clientId</c>
    /// detail); without one the host has no admission control. A reduction of
    /// the AI Operator that is still in force names the person's baseline.
    /// </summary>
    private StallHostFacts? HostFacts(WatcherCase item, string host, IReadOnlyList<StallInterventionRecord> records,
        StallInterventionOptions options, DateTime nowUtc)
    {
        var clientId = item.LastDetails.GetValueOrDefault("clientId");
        var reading = string.IsNullOrWhiteSpace(clientId) ? null
            : admission.Read(clientId, options.Limits.RestoreBelowTargetFraction, nowUtc);
        if (!IsCurrentPressure(KindOf(item) ?? string.Empty, reading, nowUtc,
            WatcherOptions.FromConfiguration(configuration).OperationalThresholds)) return null;
        double? Detail(string key) => item.LastDetails.TryGetValue(key, out var value)
            && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;
        var target = reading?.TargetPercent ?? Detail("targetPercent") ?? 85;
        var lowering = reading is null ? null : records.LastOrDefault(record => record.AdmissionInForce
            && record.HostClientId == reading.ClientId && record.TargetAdmission == reading.Admission);
        return new StallHostFacts(host, reading?.Cores ?? 0, reading?.Load1, reading?.CpuPercent ?? Detail("cpuPercent"),
            reading?.MemoryUsedPercent ?? Detail("memoryUsedPercent"), target, reading?.Admission, reading?.ActiveSlots ?? 0,
            reading?.ClientId, lowering?.PreviousAdmission, lowering?.Id);
    }

    private StallServiceFacts ServiceFacts(WatcherCase item, string host, string role,
        IReadOnlyList<WatcherCase> openCases, StallInterventionOptions options, DateTime nowUtc)
    {
        var service = options.Services.GetValueOrDefault(role) ?? role;
        var providerStalled = openCases.Any(other => KindOf(other) == StallKinds.ProviderAuthInvalid
            && other.Subject?.StartsWith($"host:{host}:", StringComparison.Ordinal) == true);
        var freeSlots = int.TryParse(item.LastDetails.GetValueOrDefault("freeSlots"), out var free) ? free : 0;
        DateTime? lastSeen = item.LastDetails.GetValueOrDefault("logExcerpt") is { } excerpt
            && excerpt.StartsWith("lastSeenUtc=", StringComparison.Ordinal)
            && DateTime.TryParse(excerpt["lastSeenUtc=".Length..], CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var seen) ? seen : item.SinceUtc;
        var workload = role == "coding" ? null : workloads.Read(host, role, nowUtc);
        return new StallServiceFacts(host, role, service,
            role != "coding" && restarts.CanRestart(host, service),
            item.LastDetails.GetValueOrDefault("stopped") == "true", lastSeen,
            Math.Max(item.BlockedCount, item.AffectedCards.Count), freeSlots, providerStalled,
            workload?.BusySlots, workload?.RunningWork ?? false, workload?.ClaimableWaiting ?? 0, workload?.GateSlots);
    }

    private StallDeployFacts DeployFacts(WatcherCase item, string host, string role, StallInterventionOptions options)
    {
        var service = options.Services.GetValueOrDefault(role) ?? role;
        var error = item.LastDetails.GetValueOrDefault("logExcerpt");
        var missing = error is null ? [] : MissingVariable().Matches(error)
            .Select(match => match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value)
            .Distinct(StringComparer.Ordinal).ToArray();
        return new StallDeployFacts(host, service, item.LastDetails.GetValueOrDefault("expectedVersion"),
            item.LastDetails.GetValueOrDefault("runningVersion"), error, missing,
            error is not null && restarts.CanRestart(host, service));
    }

    /// <summary>Compose interpolation errors: <c>${NAME:?...}</c>, "NAME missing", "set NAME".</summary>
    [GeneratedRegex(@"\$\{([A-Z][A-Z0-9_]{2,})|\b([A-Z][A-Z0-9_]{2,})\b(?=\s+(?:missing|is not set|variable is not set))")]
    private static partial Regex MissingVariable();
}
