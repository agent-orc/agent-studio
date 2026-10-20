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
    IProviderRouteControl routes,
    IBatchGateControl gate,
    IContinuationControl continuations)
{
    public static string? KindOf(WatcherCase item) => item.LastDetails.GetValueOrDefault("stallKind");

    public static bool IsOpen(WatcherCase item)
        => item.State is WatcherCaseStates.Open or WatcherCaseStates.DecisionRequired;

    /// <summary>
    /// The history the guards need: the subject's and host's records of the
    /// loop window, of the current stall episode, and every pending one.
    /// </summary>
    public static IReadOnlyList<StallInterventionHistoryEntry> History(IEnumerable<StallInterventionRecord> records,
        string subject, DateTime? sinceUtc, StallInterventionOptions options, DateTime nowUtc)
    {
        var host = StallSubjects.HostOf(subject);
        var windowStart = nowUtc.AddHours(-Math.Max(1, options.Limits.LoopWindowHours));
        return records.Where(item => item.Subject == subject || item.Host == host)
            .Where(item => item.Status == StallInterventionStatuses.Applied || item.DecidedAtUtc >= windowStart
                || sinceUtc is { } since && item.DecidedAtUtc >= since)
            .TakeLast(100)
            .Select(item => new StallInterventionHistoryEntry(item.Id, item.Subject, item.Host,
                item.Action, item.DecidedAtUtc, item.Status)).ToArray();
    }

    public StallInterventionFacts Stall(WatcherCase item, IReadOnlyList<WatcherCase> openCases,
        IReadOnlyList<StallInterventionRecord> records, StallInterventionOptions options, DateTime nowUtc)
    {
        var kind = KindOf(item) ?? throw new ArgumentException("The case is not an operational stall.");
        var subject = item.Subject!;
        var parts = subject.Split(':');
        var facts = new StallInterventionFacts(StallInterventionTriggers.Stall, kind, subject, nowUtc,
            item.SinceUtc, item.AffectedCards.ToArray(), options.Limits,
            History(records, subject, item.SinceUtc, options, nowUtc));
        return kind switch
        {
            StallKinds.HostOverload or StallKinds.MemoryPressure => facts with { Host = HostFacts(item, parts[1], options, nowUtc) },
            StallKinds.HostOffline or StallKinds.IdleCapacity => facts with
            {
                Service = ServiceFacts(item, parts[1], parts.Length > 2 ? parts[2] : "coding", openCases, options),
            },
            StallKinds.ProviderAuthInvalid => facts with
            {
                Provider = new StallProviderFacts(parts[1], parts.Length > 2 ? parts[2] : "unknown", "auth-invalid",
                    routes.HealthyHosts(parts.Length > 2 ? parts[2] : "unknown", parts[1]),
                    Math.Max(item.BlockedCount, item.AffectedCards.Count),
                    routes.IsRerouted(parts[1], parts.Length > 2 ? parts[2] : "unknown")),
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
            reading?.TargetPercent, reading?.CalmSinceUtc);
        return new StallInterventionFacts(StallInterventionTriggers.FollowUp, record.Kind, record.Subject, nowUtc,
            item?.SinceUtc, record.AffectedCards.ToArray(), options.Limits,
            History(records, record.Subject, item?.SinceUtc, options, nowUtc), FollowUp: check);
    }

    /// <summary>Measures the effect of an applied action now, not the decision that ordered it.</summary>
    public bool Effect(StallInterventionRecord record, StallInterventionOptions options, DateTime nowUtc,
        out string evidence, out HostAdmissionReading? reading)
    {
        reading = null;
        switch (record.Action)
        {
            case StallInterventionActions.LowerAdmission or StallInterventionActions.RestoreAdmission:
                reading = admission.Read(record.Host, options.Limits.RestoreBelowTargetFraction, nowUtc);
                evidence = $"admission reads {reading?.Admission?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}, expected {record.TargetAdmission}";
                return reading?.Admission == record.TargetAdmission;
            case StallInterventionActions.RerouteProvider:
                var rerouted = routes.IsRerouted(record.Host, record.Provider ?? "unknown");
                evidence = rerouted ? $"{record.Provider} is not routed to {record.Host}" : $"{record.Provider} is routed to {record.Host} again";
                return rerouted;
            case StallInterventionActions.ReconcileBatchGate:
                var still = record.Members.Where(card => gate.IsPending(record.Project, card)).ToArray();
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

    private StallHostFacts HostFacts(WatcherCase item, string host, StallInterventionOptions options, DateTime nowUtc)
    {
        var reading = admission.Read(host, options.Limits.RestoreBelowTargetFraction, nowUtc);
        double? Detail(string key) => item.LastDetails.TryGetValue(key, out var value)
            && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;
        var target = reading?.TargetPercent ?? Detail("targetPercent") ?? 85;
        return new StallHostFacts(host, reading?.Cores ?? 0, reading?.Load1, reading?.CpuPercent ?? Detail("cpuPercent"),
            reading?.MemoryUsedPercent ?? Detail("memoryUsedPercent"), target, reading?.Admission, reading?.ActiveSlots ?? 0);
    }

    private StallServiceFacts ServiceFacts(WatcherCase item, string host, string role,
        IReadOnlyList<WatcherCase> openCases, StallInterventionOptions options)
    {
        var service = options.Services.GetValueOrDefault(role) ?? role;
        var providerStalled = openCases.Any(other => KindOf(other) == StallKinds.ProviderAuthInvalid
            && other.Subject?.StartsWith($"host:{host}:", StringComparison.Ordinal) == true);
        var freeSlots = int.TryParse(item.LastDetails.GetValueOrDefault("freeSlots"), out var free) ? free : 0;
        DateTime? lastSeen = item.LastDetails.GetValueOrDefault("logExcerpt") is { } excerpt
            && excerpt.StartsWith("lastSeenUtc=", StringComparison.Ordinal)
            && DateTime.TryParse(excerpt["lastSeenUtc=".Length..], CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var seen) ? seen : item.SinceUtc;
        return new StallServiceFacts(host, role, service,
            role != "coding" && restarts.CanRestart(host, service),
            item.LastDetails.GetValueOrDefault("stopped") == "true", lastSeen,
            Math.Max(item.BlockedCount, item.AffectedCards.Count), freeSlots, providerStalled);
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
