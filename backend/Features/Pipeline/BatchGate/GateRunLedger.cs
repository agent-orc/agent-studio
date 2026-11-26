using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentStudio.Pipeline;

/// <summary>
/// One durable, immutable fact for each invocation of a machine gate.
/// AGT-3858: <see cref="SameCodeRerunMs"/> splits the time spent gating code
/// that was already gated by purpose (<see cref="SameCodeRerunPurposes"/>);
/// rows written before AGT-3858 carry null.
/// </summary>
public sealed record GateRunLedgerRow(
    string RunId, string Type, string Project, IReadOnlyList<string> Cards,
    string? BaseSha, string? Profile, DateTimeOffset RequestedAtUtc,
    DateTimeOffset? SlotAcquiredAtUtc, DateTimeOffset? LeaseAcquiredAtUtc,
    DateTimeOffset? StartedAtUtc, DateTimeOffset FinishedAtUtc,
    string Outcome, long DurationMs, long QueueWaitMs,
    IReadOnlyList<GateRunSpan> Spans, string? RerunCause, long RerunDurationMs,
    double? HostLoadStart, double? HostLoadEnd, string? TriggerEvent,
    string? BatchId = null, IReadOnlyDictionary<string, long>? SameCodeRerunMs = null)
{
    public string? TestSelector { get; init; }
    public IReadOnlyList<string>? AttemptedSuites { get; init; }
    public IReadOnlyList<string>? OmittedSuites { get; init; }
}

/// <summary>AGT-3858: why a gate ran code that was already gated.</summary>
public static class SameCodeRerunPurposes
{
    /// <summary>The targeted rerun of failed tests inside one gate run.</summary>
    public const string FlakyTestRerun = "flaky-test-rerun";
    /// <summary>The same candidate SHA again after an infrastructure red.</summary>
    public const string InfrastructureRetry = "infrastructure-retry";
    /// <summary>A red cohort that no isolation child reproduced, gated once more.</summary>
    public const string FlakySuspectRegate = "flaky-suspect-regate";

    public static IReadOnlyDictionary<string, long>? Split(string? triggerEvent, long durationMs, long inGateRerunMs)
    {
        var split = new Dictionary<string, long>(StringComparer.Ordinal);
        if (inGateRerunMs > 0) split[FlakyTestRerun] = inGateRerunMs;
        var purpose = triggerEvent switch
        {
            "infrastructure-red" => InfrastructureRetry,
            { } value when value.StartsWith(FlakySuspectRegate + ":", StringComparison.Ordinal) => FlakySuspectRegate,
            _ => null,
        };
        // The in-gate rerun is part of the run's duration; count it once.
        if (purpose is not null) split[purpose] = Math.Max(0, durationMs - inGateRerunMs);
        return split.Count == 0 ? null : split;
    }
}

public sealed record GateRunSpan(string Name, string Kind, string Host,
    DateTimeOffset? StartedAtUtc, DateTimeOffset? FinishedAtUtc,
    long DurationMs, string Outcome, string? RerunCause = null, long RerunDurationMs = 0,
    IReadOnlyList<string>? Classes = null,
    IReadOnlyDictionary<string, double>? ClassDurationsMs = null);

public sealed record GateRunLedgerPage(IReadOnlyList<GateRunLedgerRow> Rows, int Total, int Page, int PageSize);
public sealed record GateRunDailySummary(DateOnly Day,
    IReadOnlyDictionary<string, double> GateMinutesByType, int CardsPublished,
    double CardsPublishedPerHour, double? MedianBatchGateMinutes,
    double? P90BatchGateMinutes, double? HostMinutes, double? ReconciliationPercent,
    IReadOnlyDictionary<string, BatchPhasePercentiles>? PhaseMinutes = null,
    double? DiagnosticRerunShare = null, BatchPreCheckYield? PreCheckYield = null,
    IReadOnlyDictionary<string, double>? SameCodeRerunMinutesByPurpose = null,
    PerCardSuiteFollowUp? PerCardSuites = null)
{
    public GateSelectionEfficiency? SelectionEfficiency { get; init; }
}

public sealed record GateSelectionCohort(int Runs, int Cards, double Minutes, double? MinutesPerCard);
public sealed record GateSelectionEfficiency(GateSelectionCohort Before, GateSelectionCohort After);

/// <summary>
/// AGT-3856: green per-task fallback suites requested on the day, how many of
/// their cards went through a batch gate afterwards (within
/// <see cref="GateRunLedger.FollowUpDays"/> days), and that share. On 3 and 4
/// November 2026 it was 36 of 40; the admission rule aims below 10%.
/// </summary>
public sealed record PerCardSuiteFollowUp(int GreenSuites, int FollowedByBatchGate, double? Share);

/// <summary>Instance-local records outside the workspace repository. Ninety days are retained.</summary>
public sealed class GateRunLedger(string root, TimeProvider? timeProvider = null,
    ILogger<GateRunLedger>? logger = null)
{
    public const int RetentionDays = 90;

    /// <summary>Days after a green per-card suite in which a batch gate of the same card counts as its follow-up.</summary>
    public const int FollowUpDays = 2;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object _writeLock = new();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly ILogger<GateRunLedger> _logger = logger ?? NullLogger<GateRunLedger>.Instance;

    public void Append(GateRunLedgerRow row)
    {
        if (string.IsNullOrWhiteSpace(row.RunId) || row.RunId.Length > 100
            || !row.RunId.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z'
                or >= '0' and <= '9' or '-' or '_')
            || row.FinishedAtUtc < row.RequestedAtUtc)
            throw new InvalidDataException("Invalid gate run ledger row.");
        var directory = Path.Combine(root, row.RequestedAtUtc.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        lock (_writeLock)
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, row.RunId + ".json");
            var temporary = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(file, row, Json);
                    file.Flush(true);
                }
                try { File.Move(temporary, path, false); }
                catch (IOException) when (File.Exists(path))
                {
                    if (!File.ReadAllBytes(temporary).AsSpan().SequenceEqual(File.ReadAllBytes(path)))
                        _logger.LogWarning("gate-run-ledger-conflict run_id={RunId} path={Path}",
                            row.RunId, path);
                    return;
                }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            Prune(_time.GetUtcNow().UtcDateTime.Date.AddDays(-RetentionDays));
        }
    }

    public GateRunLedgerPage Read(string project, DateOnly? day, string? type, int page = 1, int pageSize = 50)
    {
        pageSize = Math.Clamp(pageSize, 1, 200);
        page = Math.Clamp(page, 1, int.MaxValue / pageSize);
        var directories = day is { } date
            ? [Path.Combine(root, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))]
            : Directory.Exists(root) ? Directory.GetDirectories(root) : [];
        var rows = directories.Where(Directory.Exists)
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.json"))
            .Select(path => JsonSerializer.Deserialize<GateRunLedgerRow>(File.ReadAllText(path), Json))
            .OfType<GateRunLedgerRow>()
            .Where(row => string.Equals(row.Project, project, StringComparison.OrdinalIgnoreCase)
                && (type is null || string.Equals(row.Type, type, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(row => row.RequestedAtUtc).ThenBy(row => row.RunId).ToArray();
        return new GateRunLedgerPage(rows.Skip((page - 1) * pageSize).Take(pageSize).ToArray(),
            rows.Length, page, pageSize);
    }

    public IReadOnlyList<GateRunLedgerRow> ReadAll(string project, DateOnly? day = null)
    {
        var directories = day is { } date
            ? [Path.Combine(root, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))]
            : Directory.Exists(root) ? Directory.GetDirectories(root) : [];
        return directories.Where(Directory.Exists)
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.json"))
            .Select(path => JsonSerializer.Deserialize<GateRunLedgerRow>(File.ReadAllText(path), Json))
            .OfType<GateRunLedgerRow>()
            .Where(row => string.Equals(row.Project, project, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    public GateRunDailySummary Summary(BatchGateStore store, string project, DateOnly day,
        IReadOnlyList<GateRunLedgerRow>? allRows = null)
    {
        var checks = store.ListPreChecks(project);
        var todayManifests = store.ListManifests().Where(manifest =>
            string.Equals(manifest.Scope.Project, project, StringComparison.OrdinalIgnoreCase)
            && store.ReadPublication(manifest.BatchId) is { } publication
            && DateOnly.FromDateTime(publication.VerifiedAtUtc.UtcDateTime) == day).ToArray();
        allRows ??= BatchMemberTimelineProjection.Rows(this, project,
            new[] { day, day.AddDays(-1) }
                .Concat(todayManifests.SelectMany(manifest => BatchMemberTimelineProjection.Days(
                    manifest.Members.Min(member => member.ReviewCompletedAtUtc),
                    store.ReadPublication(manifest.BatchId)!.VerifiedAtUtc)))
                .Concat(checks.Where(check => todayManifests.Any(manifest => manifest.Members.Any(member =>
                    member.TaskKey == check.TaskKey && member.ResultSha == check.ResultSha)))
                    .SelectMany(check => new[] { DateOnly.FromDateTime(check.StartedAtUtc.UtcDateTime),
                        DateOnly.FromDateTime(check.StartedAtUtc.UtcDateTime).AddDays(-1) })));
        var rows = allRows.Where(row => DateOnly.FromDateTime(row.RequestedAtUtc.UtcDateTime) == day).ToArray();
        var minutes = rows.GroupBy(row => row.Type).ToDictionary(group => group.Key,
            group => group.Sum(row => row.DurationMs) / 60_000d);
        var durations = rows.Where(row => row.Type == "batch-gate")
            .Select(row => row.DurationMs / 60_000d).Order().ToArray();
        var median = durations.Length == 0 ? (double?)null
            : durations.Length % 2 == 1 ? durations[durations.Length / 2]
            : (durations[durations.Length / 2 - 1] + durations[durations.Length / 2]) / 2;
        var p90 = durations.Length == 0 ? (double?)null
            : durations[(int)Math.Ceiling(.9 * durations.Length) - 1];
        var batchPublished = store.ListManifests().Where(manifest =>
                string.Equals(manifest.Scope.Project, project, StringComparison.OrdinalIgnoreCase))
            .Select(manifest => (manifest, publication: store.ReadPublication(manifest.BatchId)))
            .Where(item => item.publication is not null
                && DateOnly.FromDateTime(item.publication.VerifiedAtUtc.UtcDateTime) == day)
            .Sum(item => item.manifest.Members.Count);
        var fallbackPublished = store.ListFallbackSuites(project).Count(fact =>
            fact.Outcome == "published"
            && DateOnly.FromDateTime(fact.RecordedAtUtc.UtcDateTime) == day);
        var published = batchPublished + fallbackPublished;
        var host = store.ListExecutions(project)
            .Where(fact => DateOnly.FromDateTime(fact.StartedAtUtc.UtcDateTime) == day)
            .Sum(fact => (fact.DurationMs ?? (long)(fact.CompletedAtUtc - fact.StartedAtUtc).TotalMilliseconds) / 60_000d);
        var sum = minutes.Values.Sum();
        var causes = store.ListFallbackCauses(project);
        var allPending = store.ListAllPending();
        var timelines = todayManifests
            .SelectMany(manifest => manifest.Members.Select(member =>
                BatchMemberTimelineProjection.Build(store, member, manifest, allRows, checks, causes, allPending)))
            .Where(item => item.Outcome == "published").ToArray();
        var todayPreChecks = rows.Where(row => row.Type == "batch-pre-check").ToArray();
        var todayChecks = checks.Where(check => todayPreChecks.Any(row =>
            row.TriggerEvent == "pending:" + check.ReviewAttemptId)).ToArray();
        var yield = new BatchPreCheckYield(todayPreChecks.Length,
            todayChecks.Count(check => check.Outcome == "red"),
            todayChecks.Count(check => check.Outcome == "red"
                && store.ReadPendingOutcome(check.ReviewAttemptId) == "pre-check-red"),
            todayPreChecks.Sum(row => row.DurationMs) / 60_000d);
        return new GateRunDailySummary(day, minutes, published, published / 24d,
            median, p90, host,
            rows.Length > 0 && host > 0 ? Math.Abs(sum - host) / host * 100 : null,
            BatchMemberTimelineProjection.Phases(timelines),
            timelines.Length == 0 ? null : (double)timelines.Count(item => item.DiagnosticRerun) / timelines.Length,
            yield,
            rows.SelectMany(row => row.SameCodeRerunMs ?? new Dictionary<string, long>())
                .GroupBy(item => item.Key, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Sum(item => item.Value) / 60_000d,
                    StringComparer.Ordinal),
            PerCardSuites(rows, Enumerable.Range(0, FollowUpDays + 1)
                .SelectMany(offset => ReadAll(project, day.AddDays(offset))).ToArray()))
        {
            SelectionEfficiency = SelectionEfficiency(rows),
        };
    }

    internal static GateSelectionEfficiency SelectionEfficiency(IReadOnlyList<GateRunLedgerRow> rows)
    {
        var batches = rows.Where(row => row.Type == "batch-gate"
            || row.Type == "clean-diagnostic-rerun" && row.BatchId is not null).ToArray();
        GateSelectionCohort Cohort(bool after)
        {
            var samples = batches.Where(row =>
                (row.TestSelector == AgentStudio.TaskServer.Contracts.ProjectTestSelectionPolicy.Selector) == after).ToArray();
            var cards = samples.SelectMany(row => row.Cards).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var minutes = samples.Sum(row => row.DurationMs) / 60_000d;
            return new(samples.Length, cards, minutes, cards == 0 ? null : minutes / cards);
        }
        return new(Cohort(false), Cohort(true));
    }

    /// <summary>
    /// AGT-3856: of the green per-task fallback suites in <paramref name="dayRows"/>,
    /// the ones whose card a later batch gate row in <paramref name="laterRows"/> tested again.
    /// </summary>
    internal static PerCardSuiteFollowUp PerCardSuites(IReadOnlyList<GateRunLedgerRow> dayRows,
        IReadOnlyList<GateRunLedgerRow> laterRows)
    {
        var green = dayRows.Where(row => row.Type == "per-task-fallback"
            && row.Outcome == nameof(BuildTestGateVerdict.Ok) && row.Cards.Count > 0).ToArray();
        var batches = laterRows.Where(row => row.Type == "batch-gate").ToArray();
        var followed = green.Count(row => batches.Any(batch => batch.RequestedAtUtc >= row.FinishedAtUtc
            && batch.Cards.Intersect(row.Cards, StringComparer.Ordinal).Any()));
        return new PerCardSuiteFollowUp(green.Length, followed,
            green.Length == 0 ? null : (double)followed / green.Length);
    }

    private void Prune(DateTime before)
    {
        foreach (var directory in Directory.Exists(root) ? Directory.GetDirectories(root) : [])
            if (DateTime.TryParseExact(Path.GetFileName(directory), "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) && day < before)
                Directory.Delete(directory, true);
    }

    public static double? HostLoad()
    {
        try
        {
            var first = File.ReadAllText("/proc/loadavg").Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            return double.Parse(first, CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException or IndexOutOfRangeException)
        { return null; }
    }

    public static GateRunLedgerRow FromResult(BuildTestGateRequest request, BuildTestGateResult result,
        DateTimeOffset requested, DateTimeOffset finished, DateTimeOffset? slot,
        double? loadStart, double? loadEnd, string? runId = null)
    {
        var spans = result.Processes.Select((process, index) => new GateRunSpan(
            $"process-{index + 1}", process.Phase, process.Host ?? Environment.MachineName,
            process.StartedAtUtc, process.CompletedAtUtc,
            Math.Max(0, (long)(process.CompletedAtUtc - process.StartedAtUtc).TotalMilliseconds),
            process.ExitCode == 0 ? "pass" : process.Cancelled ? "cancelled" : "fail")).ToList();
        foreach (var suite in result.TestShards)
            spans.AddRange(suite.Shards.Select(shard => new GateRunSpan(
                suite.SuiteId + ":" + shard.Index, "shard", shard.Host,
                shard.StartedAtUtc, shard.CompletedAtUtc, shard.DurationMs, shard.Outcome,
                shard.Rerun?.Reason, shard.Rerun?.DurationMs ?? 0,
                shard.ClassNames, shard.ClassDurationsMs)));
        var reruns = result.Processes.Where(process => process.Phase == BuildTestGateRunner.FlakyRerunPhase).ToArray();
        var rerunMs = reruns.Sum(process => Math.Max(0,
            (long)(process.CompletedAtUtc - process.StartedAtUtc).TotalMilliseconds))
            + result.TestShards.SelectMany(suite => suite.Shards).Sum(shard => shard.Rerun?.DurationMs ?? 0);
        var type = request.LedgerType ?? (request.GateId switch
        {
            "batch-gate" => "batch-gate",
            "batch-base" => "batch-base",
            "batch-pre-check" => "batch-pre-check",
            _ when request.Executor == "batch-fallback" => "per-task-fallback",
            _ => "per-task-gate",
        });
        var cleanRerun = type == "clean-diagnostic-rerun";
        var trigger = request.LedgerTriggerEvent ?? request.Executor + ":" + (request.JobId ?? request.ExpectedSha);
        return new GateRunLedgerRow(runId ?? request.LedgerRunId ?? result.GateRunId ?? Guid.NewGuid().ToString("N"),
            type, request.Project ?? "unknown", request.LedgerCards ?? (request.JobId is null ? [] : [request.JobId]),
            request.IntegrationRef, result.GateProfileDigest ?? request.LedgerProfile, requested, slot,
            request.LedgerLeaseAcquiredAtUtc, result.Processes.Count > 0
                ? result.Processes.Min(process => process.StartedAtUtc) : slot,
            finished, result.Verdict.ToString(),
            result.DurationMs, result.GateQueueWaitMs, spans,
            cleanRerun ? request.LedgerTriggerEvent : rerunMs > 0 ? "flaky-test" : null,
            cleanRerun ? result.DurationMs : rerunMs,
            result.GateHostLoadAtStart ?? loadStart, result.GateHostLoadAtEnd ?? loadEnd,
            trigger, request.LedgerBatchId,
            SameCodeRerunPurposes.Split(request.LedgerTriggerEvent, result.DurationMs, rerunMs))
        {
            TestSelector = result.TestSelection?.Selector,
            AttemptedSuites = result.TestSelection?.AttemptedSuites,
            OmittedSuites = result.TestSelection?.OmittedSuites,
        };
    }
}

/// <summary>Captures a run once at the caller boundary, including remote failures.</summary>
public sealed class LedgeredBuildTestGateRunner(IBuildTestGateRunner inner, GateRunLedger ledger,
    BatchGateStore? store = null, ILogger<LedgeredBuildTestGateRunner>? logger = null)
    : IBuildTestGateRunner
{
    public async Task<BuildTestGateResult> RunAsync(BuildTestGateRequest request,
        IReadOnlyList<string>? changedFiles, BuildProfile? profile, PostStepMode mode,
        TimeSpan timeout, CancellationToken ct)
    {
        if (mode == PostStepMode.Off)
            return await inner.RunAsync(request, changedFiles, profile, mode, timeout, ct).ConfigureAwait(false);
        var requested = request.LedgerRequestedAtUtc ?? DateTimeOffset.UtcNow;
        var load = GateRunLedger.HostLoad();
        DateTimeOffset? slot = null;
        var acquired = request.OnMachineGateAcquired;
        request = request with { OnMachineGateAcquired = () =>
        {
            slot = DateTimeOffset.UtcNow;
            load = GateRunLedger.HostLoad();
            acquired?.Invoke();
        } };
        var result = await inner.RunAsync(request, changedFiles, profile, mode, timeout, ct).ConfigureAwait(false);
        if (result.VerdictSource == GateVerdictSource.CacheHit
            || (result.Processes.Count == 0
                && result.Verdict is (BuildTestGateVerdict.Skipped or BuildTestGateVerdict.NotApplicable)))
            return result;
        var finished = DateTimeOffset.UtcNow;
        slot ??= result.GateStartedAtUtc is { } start && result.GateQueueWaitMs >= 0
            ? start.AddMilliseconds(result.GateQueueWaitMs) : null;
        try
        {
            var row = GateRunLedger.FromResult(request, result, requested, finished, slot,
                load, GateRunLedger.HostLoad());
            ledger.Append(row);
            if (store is not null && row.Type is ("batch-pre-check" or "per-task-gate"))
                store.RecordExecution(new BatchGateExecutionFact(row.Project, null, row.RunId,
                    result.TestedSha ?? request.ExpectedSha ?? "", row.Outcome, Environment.MachineName,
                    requested, finished, row.QueueWaitMs, 0, result.OriginEvidencePath ?? "",
                    row.DurationMs, row.Cards));
        }
        catch (Exception exception)
        {
            (logger ?? NullLogger<LedgeredBuildTestGateRunner>.Instance).LogError(exception,
                "gate-run-ledger-append-failed run_id={RunId}", request.LedgerRunId ?? result.GateRunId);
        }
        return result;
    }
}
