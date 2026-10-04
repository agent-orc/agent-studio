using System.Text.Json;

namespace AgentStudio.Pipeline;

/// <summary>What the router did with one failed merge gate.</summary>
/// <param name="ParkReason">Formatted <c>[category] sentence</c> when <see cref="Parks"/>; otherwise null.</param>
public sealed record GateFailureRouting(
    GateFailureTriage Triage,
    GateFailureRoute Route,
    string? ParkReason,
    string? CauseKey = null)
{
    /// <summary>The card must be parked with <see cref="ParkReason"/>; otherwise an automatic route owns it.</summary>
    public bool Parks => Route.Parks;
}

/// <summary>Durable record of the last routing, so one gate log causes its side effects once.</summary>
public sealed record GateFailureTriageReceipt
{
    public const string FileName = "gate-triage.json";

    public string LogName { get; init; } = string.Empty;
    public string? DeliverySha { get; init; }
    public GateFailureTriage? Triage { get; init; }
    public GateFailureRoute? Route { get; init; }
    public string? ParkReason { get; init; }
    public string? CauseKey { get; init; }
    public DateTime RoutedAt { get; init; }
}

/// <summary>
/// Classifies a failed merge gate and carries out its route (AGT-3009). Called
/// by <see cref="DeliveryChainReconciler"/> at the one point where a red gate
/// used to park the card as an unexplained <c>operator-decision</c>.
/// <para>
/// Classification and routing are the pure
/// <see cref="GateFailureTriagePolicy"/> and <see cref="GateFailureRoutingPolicy"/>.
/// This class owns the reads (gate log, ladder receipts, fleet counter) and the
/// writes: it re-types the durable merge step to the class it decided, starts
/// the one fix round, or opens and joins the cause card through
/// <see cref="FailureInterventionService"/>. It never runs or weakens the gate.
/// </para>
/// </summary>
public sealed class GateFailureRouter
{
    public const string FixRoundSource = "gate-failure-fix-round";

    private static readonly string[] GateLogPrefixes =
    [
        IntegrationGateJournal.PreDevelopBuildGateStep,
        "pre-main-test-gate",
    ];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly TaskIntegrationStatusService _integrationStatus;
    private readonly PipelineExecutionLog _pipelineLog;
    private readonly ProjectSettingsService _settings;
    private readonly TimelineLog _timeline;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GateFailureRouter> _logger;
    private readonly IGateFailureFingerprintCounter? _counter;
    private readonly FailureInterventionService? _interventions;
    private readonly TaskRunnerService? _continuations;
    private readonly TimeProvider _time;

    public GateFailureRouter(
        TaskIntegrationStatusService integrationStatus,
        PipelineExecutionLog pipelineLog,
        ProjectSettingsService settings,
        TimelineLog timeline,
        IConfiguration configuration,
        ILogger<GateFailureRouter> logger,
        IGateFailureFingerprintCounter? counter = null,
        FailureInterventionService? interventions = null,
        TaskRunnerService? continuations = null,
        TimeProvider? time = null)
    {
        _integrationStatus = integrationStatus;
        _pipelineLog = pipelineLog;
        _settings = settings;
        _timeline = timeline;
        _configuration = configuration;
        _logger = logger;
        _counter = counter;
        _interventions = interventions;
        _continuations = continuations;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The integration failures this router owns: every red or interrupted merge gate.</summary>
    public static bool Handles(TaskIntegrationStatus? status)
        => status?.Failure?.Code is AcceptedIntegrationFailureCodes.BuildGateFailed
            or AcceptedIntegrationFailureCodes.GateEnvironmentFailure
            or AcceptedIntegrationFailureCodes.GateInterrupted;

    public async Task<GateFailureRouting> RouteAsync(
        TaskInfo card, TaskIntegrationStatus status, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(status);

        var deliverySha = DeliverySha(card);
        var newest = status.Failure?.Code == AcceptedIntegrationFailureCodes.GateInterrupted
            ? null : NewestGateLog(card.FolderPath);
        var logName = LogName(status, newest);
        var receipt = ReadReceipt(card.FolderPath);
        var settled = receipt?.Triage is not null && receipt.Route is not null
            && string.Equals(receipt.LogName, logName, StringComparison.Ordinal)
            && string.Equals(receipt.DeliverySha, deliverySha, StringComparison.OrdinalIgnoreCase);
        // The receipt already holds the verdict for this exact log, so a
        // reconciler tick does not re-read a 100 KB log every 30 seconds.
        var triage = settled ? receipt!.Triage! : Classify(status, newest);
        // A side-effecting route already ran for this exact log; repeat its answer.
        // An environment replay is re-decided every time, because the ladder may
        // have spent its last rung since.
        if (settled && receipt!.Route!.Action != GateFailureRouteAction.ReplayGate)
            return new GateFailureRouting(receipt.Triage!, receipt.Route, receipt.ParkReason, receipt.CauseKey);

        var cardKey = card.Key ?? card.Id;
        IReadOnlyList<string>? cards = null;
        if (triage.FailingItems.Count > 0 && _counter is not null && !settled)
        {
            cards = await _counter.RecordAndReadCardsAsync(
                triage.Fingerprint, cardKey, $"gate-triage:{cardKey}:{logName}:{deliverySha ?? "none"}", ct)
                .ConfigureAwait(false);
        }
        var otherCards = cards?.Where(key => !string.Equals(key, cardKey, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        var facts = new GateFailureRoutingFacts(
            ReplayBudgetLeft(card, deliverySha),
            otherCards?.Length,
            FixRoundsSpent(card, deliverySha),
            _settings.Get(card.ProjectName).AutomaticFailureContinuationsEnabled);
        var route = GateFailureRoutingPolicy.Decide(triage, facts);
        RetypeMergeStep(card, triage);

        string? causeKey = null;
        switch (route.Action)
        {
            case GateFailureRouteAction.FixRound:
                if (!await StartFixRoundAsync(card, status, triage, deliverySha, ct).ConfigureAwait(false))
                    route = new GateFailureRoute(GateFailureRouteAction.ParkExhausted,
                        GateFailureParkCategories.Product, "fix-round-could-not-start");
                break;
            case GateFailureRouteAction.AttachToCause:
                causeKey = await AttachToCauseAsync(card, triage, otherCards ?? [], logName, ct)
                    .ConfigureAwait(false);
                break;
        }

        var parkReason = route.Parks ? GateFailureRoutingPolicy.ParkReason(triage, route, causeKey) : null;
        if (!settled || receipt!.Route!.Action != route.Action)
        {
            WriteReceipt(card.FolderPath, new GateFailureTriageReceipt
            {
                LogName = logName,
                DeliverySha = deliverySha,
                Triage = triage,
                Route = route,
                ParkReason = parkReason,
                CauseKey = causeKey,
                RoutedAt = _time.GetUtcNow().UtcDateTime,
            });
            _logger.LogInformation(
                "gate-failure-routed project={Project} job={JobId} class={Class} kind={Kind} fingerprint={Fingerprint} action={Action} reason={Reason} otherCards={OtherCards} cause={Cause}",
                card.ProjectName, card.Id, triage.Class, triage.Kind ?? "none", triage.Fingerprint,
                route.Action, route.Reason, otherCards?.Length.ToString() ?? "unavailable", causeKey ?? "none");
            if (triage.Kind == GateEnvironmentKinds.RunBudget)
                _logger.LogWarning(
                    "gate-run-budget-finding project={Project} job={JobId} log={Log}: the merge gate exceeded its gate-run budget; review the budget for this suite",
                    card.ProjectName, card.Id, logName);
        }
        return new GateFailureRouting(triage, route, parkReason, causeKey);
    }

    private static string LogName(TaskIntegrationStatus status, string? newest)
        => status.Failure?.Code == AcceptedIntegrationFailureCodes.GateInterrupted
            ? AcceptedIntegrationFailureCodes.GateInterrupted
            : newest is null ? "none" : Path.GetFileName(newest);

    /// <summary>
    /// The newest merge-gate evidence log, classified. An interrupted gate wrote
    /// no verdict, so its class comes from the typed failure code instead.
    /// </summary>
    internal static GateFailureTriage Classify(TaskIntegrationStatus status, string? newest)
    {
        if (status.Failure?.Code == AcceptedIntegrationFailureCodes.GateInterrupted)
            return GateFailureTriagePolicy.Interrupted();
        if (newest is null) return GateFailureTriagePolicy.NoLog();
        try { return GateFailureTriagePolicy.Classify(File.ReadAllText(newest)); }
        catch (IOException) { return GateFailureTriagePolicy.NoLog(); }
    }

    internal static string? NewestGateLog(string folder)
    {
        var dir = Path.Combine(folder, "post-steps");
        if (!Directory.Exists(dir)) return null;
        return GateLogPrefixes
            .SelectMany(prefix => Directory.EnumerateFiles(dir, prefix + "-*.log"))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ThenByDescending(path => path, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private bool ReplayBudgetLeft(TaskInfo card, string? deliverySha)
    {
        var options = GateEnvironmentRetryOptions.FromConfiguration(_configuration);
        // The ladder sweep skips projects without automatic continuations, so a
        // replay promised there would never run.
        if (!options.Enabled || deliverySha is null
            || !_settings.Get(card.ProjectName).AutomaticFailureContinuationsEnabled)
            return false;
        var ledger = GateEnvironmentRetryReceipts.Read(_timeline, card.FolderPath, deliverySha);
        return !ledger.Parked && ledger.AttemptsSpent < options.MaxAttempts;
    }

    private int FixRoundsSpent(TaskInfo card, string? deliverySha)
        => deliverySha is null ? 0 : _timeline.ReadAll(card.FolderPath).Count(entry =>
            entry.Kind == TimelineEventKinds.IntegrationRecoveryQueued
            && entry.Details?.GetValueOrDefault("source") == FixRoundSource
            && string.Equals(entry.Details.GetValueOrDefault("deliverySha"), deliverySha,
                StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Makes the durable failure code agree with the class: an environment class
    /// becomes <c>gate-environment-failure</c>, which the bounded replay ladder
    /// owns, and any other class becomes <c>build-gate-failed</c>, which the
    /// ladder leaves alone. The raw reason stays, prefixed by the triage line.
    /// </summary>
    private void RetypeMergeStep(TaskInfo card, GateFailureTriage triage)
    {
        var step = _integrationStatus.ReadLatestMergeStep(card);
        if (step is null || step.Status != PipelineStepStatus.Failed) return;
        if (step.FailureCode == AcceptedIntegrationFailureCodes.GateInterrupted) return;
        var (code, verdict) = triage.IsEnvironment
            ? (AcceptedIntegrationFailureCodes.GateEnvironmentFailure, "gate-environment-failure")
            : (AcceptedIntegrationFailureCodes.BuildGateFailed, "gate-failed");
        // A new gate run records a new merge step, so a step that already
        // carries a triage line was typed for exactly this log.
        var reason = step.Reason ?? string.Empty;
        if (reason.StartsWith("[gate-triage ", StringComparison.Ordinal)) return;
        _pipelineLog.RecordStep(card.FolderPath, step with
        {
            FailureCode = code,
            Verdict = verdict,
            Reason = $"[gate-triage {triage.Class}] {triage.ReasonLine} {reason}",
        });
    }

    private async Task<bool> StartFixRoundAsync(
        TaskInfo card, TaskIntegrationStatus status, GateFailureTriage triage, string? deliverySha, CancellationToken ct)
    {
        if (_continuations is null || deliverySha is null) return false;
        var step = _integrationStatus.ReadLatestMergeStep(card);
        var subject = ReviewSubjectStore.Read(card.FolderPath);
        var items = string.Join("; ", triage.FailingItems);
        var prompt = GateFailureFixRoundPrompt.Build(
            card.Key ?? card.Id,
            subject?.ResultRef,
            deliverySha,
            status.IntegrationBranch ?? "develop",
            step?.StepId ?? IntegrationGateJournal.PreDevelopBuildGateStep,
            triage,
            step?.EvidenceRef);
        try
        {
            await _continuations.ContinueJobAsync(
                card.Id, prompt, card.WatchPath, mode: ContinueModes.Extend, ct: ct,
                reason: "Merge gate failed on the delivery's own items.", triggeredBy: FixRoundSource)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "gate-failure fix round could not start project={Project} job={JobId}",
                card.ProjectName, card.Id);
            return false;
        }
        _timeline.Append(card.FolderPath,
            TimelineEventKinds.IntegrationRecoveryQueued,
            TimelineActors.System,
            $"Automatically queued one fix round for the red merge gate: {triage.ReasonLine}",
            payloadRef: step?.EvidenceRef ?? "pipeline-execution.json",
            details: new Dictionary<string, string>
            {
                ["automatic"] = "true",
                ["source"] = FixRoundSource,
                ["deliverySha"] = deliverySha,
                ["gateClass"] = triage.Class,
                ["fingerprint"] = triage.Fingerprint,
                ["failingItems"] = items,
            });
        return true;
    }

    private async Task<string?> AttachToCauseAsync(
        TaskInfo card, GateFailureTriage triage, IReadOnlyList<string> otherCards, string logName, CancellationToken ct)
    {
        if (_interventions is null) return null;
        // One line, identical on every card with the same items: it is both the
        // intervention ledger's dedupe signature and the evidence in its prompt.
        var fingerprintLine = $"{FailureInterventionPolicy.GateFingerprintLinePrefix}{triage.Fingerprint} " +
                              $"class={triage.Class} items={string.Join(", ", triage.FailingItems)}";
        try
        {
            var raised = await _interventions.RaiseAsync(card, new FailureCommandEvidence(
                FailureInterventionPolicy.GateSharedCauseCode,
                triage.Class,
                StdoutTail: fingerprintLine,
                StderrTail: triage.ReasonLine + (otherCards.Count == 0
                    ? string.Empty
                    : " Also failing: " + string.Join(", ", otherCards) + "."),
                StepId: PipelineCatalogue.MergeIntoDevelopStepId,
                EvidencePointers: ["post-steps/" + logName, GateFailureTriageReceipt.FileName],
                OccurredAt: _time.GetUtcNow().UtcDateTime), ct).ConfigureAwait(false);
            return raised.Intervention.FollowUpKey;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "gate-failure cause card could not be raised project={Project} job={JobId}",
                card.ProjectName, card.Id);
            return null;
        }
    }

    private static string? DeliverySha(TaskInfo card)
    {
        var subject = ReviewSubjectStore.Read(card.FolderPath);
        return ReviewSubjectStore.IsValidResultSha(subject?.ResultSha) ? subject!.ResultSha : null;
    }

    internal static GateFailureTriageReceipt? ReadReceipt(string folder)
    {
        var path = Path.Combine(folder, "post-steps", GateFailureTriageReceipt.FileName);
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<GateFailureTriageReceipt>(File.ReadAllText(path), Json)
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            SilentCatch.Note(ex, "GateFailureRouter: an unreadable receipt is routed again");
            return null;
        }
    }

    private static void WriteReceipt(string folder, GateFailureTriageReceipt receipt)
    {
        var dir = Path.Combine(folder, "post-steps");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, GateFailureTriageReceipt.FileName);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, JsonSerializer.Serialize(receipt, Json));
        File.Move(temporary, path, overwrite: true);
    }
}

/// <summary>The fix-round prompt: the shared continuation contract plus the exact failing items.</summary>
public static class GateFailureFixRoundPrompt
{
    public static string Build(
        string taskKey,
        string? deliveryRef,
        string deliverySha,
        string integrationBranch,
        string stage,
        GateFailureTriage triage,
        string? evidenceRef)
        => IntegrationContinuationPrompt.Build(
            taskKey,
            deliveryRef,
            deliverySha,
            integrationBranch,
            stage,
            triage.ReasonLine.TrimEnd('.'),
            evidence: "failing items: " + string.Join("; ", triage.FailingItems),
            evidenceRef: evidenceRef);
}
