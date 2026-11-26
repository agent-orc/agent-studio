using AgentStudio.Pipeline;
using AgentStudio.Runner;
using AgentStudio.Shared;
using AgentStudio.Watcher;
using AgentStudio.Quota;
using AgentStudio.Registry;
using AgentStudio.Projects;

namespace AgentStudio.Operator;

/// <summary>Reads one family of existing facts for the decision stream.</summary>
public interface IOperatorDecisionFactSource
{
    /// <summary>The event types this source reports; a failed read keeps their published keys.</summary>
    IReadOnlyList<string> Types { get; }

    /// <summary>The current facts, or null while the source has nothing to read yet; null keeps their published keys.</summary>
    IReadOnlyList<OperatorDecisionFact>? Collect(DateTime nowUtc);
}

/// <summary>One installation-wide warning for scheduled review retries that have lost their driver.</summary>
public sealed class OperatorReviewRetryFactSource(
    Func<IReadOnlyList<ReviewInfrastructureRetryDue>> scheduled,
    Func<bool> tickFailed,
    IConfiguration configuration) : IOperatorDecisionFactSource
{
    private static readonly TimeSpan StartGrace =
        TimeSpan.FromSeconds(2 * ReviewInfrastructureRetryScheduler.DefaultIntervalSeconds);

    public IReadOnlyList<string> Types { get; } = [OperatorDecisionTypes.ReviewRetrySchedulerStalled];

    public IReadOnlyList<OperatorDecisionFact> Collect(DateTime nowUtc)
    {
        var disabled = !configuration.GetValue("Runner:ReviewInfrastructureRetry:Enabled", true);
        var failed = tickFailed();
        var affected = scheduled()
            .Where(retry => disabled || failed || nowUtc > retry.DueAtUtc + StartGrace)
            .OrderBy(retry => retry.DueAtUtc)
            .ToArray();
        if (affected.Length == 0) return [];

        var cards = affected.Select(retry => retry.TaskKey).Distinct(StringComparer.Ordinal).ToArray();
        var state = disabled ? "disabled" : failed ? "failing" : "not starting overdue retries";
        var parkLimit = AutoReviewStrandingWatchdog.RetryOverdueLimit(configuration);
        var deadlines = affected.Select(retry =>
            $"{retry.TaskKey} by {AutoReviewStrandingWatchdog.ParkOverdueRetryBy(retry.DueAtUtc, parkLimit):yyyy-MM-dd HH:mm} UTC");
        return [new OperatorDecisionFact(
            OperatorDecisionTypes.ReviewRetrySchedulerStalled,
            $"{OperatorDecisionTypes.ReviewRetrySchedulerStalled}|installation",
            null, null,
            $"Review retry scheduler is {state}; {cards.Length} affected card(s): {string.Join(", ", cards)}. "
            + $"The watchdog will park these cards for an operator decision: {string.Join("; ", deadlines)}.",
            affected[0].DueAtUtc <= nowUtc ? affected[0].DueAtUtc : nowUtc,
            [], cards, "Review retries are not starting")];
    }
}

/// <summary>
/// Completed deploys already metered by workspace quota, without a new event store.
/// The title names project, environment and release as the Deploy area does
/// (AGT-3987); the job id stays in the key only. <paramref name="jobLabels"/>
/// reads the Deploy area's names of a project's jobs by display name.
/// </summary>
public sealed class OperatorDeployFactSource(WorkspaceQuotaService quota, ProjectRegistry projects,
    Func<string, IReadOnlyDictionary<string, AgentStudio.Deploy.DeployJobLabel>>? jobLabels = null)
    : IOperatorDecisionFactSource
{
    public IReadOnlyList<string> Types { get; } = [OperatorDecisionTypes.DeployCompleted];

    public IReadOnlyList<OperatorDecisionFact> Collect(DateTime nowUtc)
    {
        var labels = new Dictionary<string, IReadOnlyDictionary<string, AgentStudio.Deploy.DeployJobLabel>>(StringComparer.OrdinalIgnoreCase);
        return quota.CompletedDeploys(nowUtc.AddDays(-7)).Select(deploy =>
        {
            var project = projects.FindById(deploy.ProjectId);
            var name = project?.DisplayName ?? deploy.ProjectId;
            if (!labels.TryGetValue(name, out var jobs)) labels[name] = jobs = Labels(name);
            return new OperatorDecisionFact(OperatorDecisionTypes.DeployCompleted,
                $"{OperatorDecisionTypes.DeployCompleted}|{deploy.ProjectId}|{deploy.JobId}|{deploy.CompletedAt:O}",
                project?.DisplayName, null, "Deployment completed.", deploy.CompletedAt!.Value, [])
            {
                Title = Title(name, jobs.GetValueOrDefault(deploy.JobId), deploy.EnvironmentId),
            };
        }).ToArray();
    }

    /// <summary><c>Shop · Production · 2026.11.2</c>; a job without a release record names its environment only.</summary>
    public static string Title(string project, AgentStudio.Deploy.DeployJobLabel? label, string environmentId)
        => label?.ReleaseVersion is { Length: > 0 } version
            ? $"{project} · {label.Environment} · {version}"
            : $"{project} · {label?.Environment ?? environmentId}";

    private IReadOnlyDictionary<string, AgentStudio.Deploy.DeployJobLabel> Labels(string project)
    {
        if (jobLabels is null) return new Dictionary<string, AgentStudio.Deploy.DeployJobLabel>();
        try { return jobLabels(project); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                          or System.Text.Json.JsonException or InvalidOperationException)
        {
            AgentStudio.Diagnostics.SilentCatch.Note(exception, "OperatorDeployFactSource: deploy state unreadable");
            return new Dictionary<string, AgentStudio.Deploy.DeployJobLabel>();
        }
    }
}

/// <summary>
/// Open environment incidents from the durable health state. Recovery clears the fact.
/// <paramref name="cardKey"/> turns the incident card's folder slug into the key the board shows.
/// </summary>
public sealed class OperatorEnvironmentIncidentFactSource(HealthCheckStore store, Func<IEnumerable<string>> projects,
    Func<string, string, string?>? cardKey = null)
    : IOperatorDecisionFactSource
{
    public IReadOnlyList<string> Types { get; } = [OperatorDecisionTypes.EnvironmentIncidentOpen];

    public IReadOnlyList<OperatorDecisionFact> Collect(DateTime nowUtc)
    {
        var facts = new List<OperatorDecisionFact>();
        foreach (var project in projects().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var state = store.Load(project);
            foreach (var environment in state.Environments.Values)
            {
                if (environment.OpenIncident is not { EndedAt: null } incident) continue;
                var failing = state.Checks.Values
                    .Select(check => check.OpenIncident)
                    .Where(check => check is { EndedAt: null } && check.EnvironmentId == incident.EnvironmentId)
                    .Select(check => $"{check!.CheckId}: {check.FirstError}")
                    .ToArray();
                var signal = failing.Length > 0 ? string.Join("; ", failing) : "Health check failing";
                var slug = System.Text.RegularExpressions.Regex.Replace(project.Trim().ToLowerInvariant(),
                    "[^a-z0-9]+", "-").Trim('-');
                var environmentUrl = "/#/projects/" + Uri.EscapeDataString(slug) + "/operations";
                var evidence = new List<OperatorEvidenceLink>
                {
                    new("environment operations", environmentUrl),
                };
                var card = incident.CardId is { } cardId ? cardKey?.Invoke(project, cardId) ?? cardId : null;
                if (card is not null)
                    evidence.Add(new("incident card", "/#/tasks/" + Uri.EscapeDataString(card)));
                facts.Add(new OperatorDecisionFact(OperatorDecisionTypes.EnvironmentIncidentOpen,
                    $"{OperatorDecisionTypes.EnvironmentIncidentOpen}|{project}|{incident.Id}",
                    project, card,
                    $"{incident.EnvironmentName} since {incident.StartedAt:yyyy-MM-dd HH:mm} UTC: {signal}",
                    incident.StartedAt.UtcDateTime, evidence,
                    Title: $"{project} · {incident.EnvironmentName} incident"));
            }
        }
        return facts;
    }
}

/// <summary>
/// Lanes, decision cards, quota waits and fallbacks from the published task
/// index. It reads the published generation only: a sweep never refreshes the
/// index, never starts a scan or a git process, and before the first
/// publication it reports nothing instead of an empty board.
/// </summary>
public sealed class OperatorTaskFactSource(
    Func<IReadOnlyList<TaskInfo>?> tasks,
    Func<TaskInfo, QuotaFallbackStatus?> fallbackOf) : IOperatorDecisionFactSource
{
    public IReadOnlyList<string> Types { get; } =
    [
        OperatorDecisionTypes.EscalationOpened, OperatorDecisionTypes.HumanReviewReady,
        OperatorDecisionTypes.DecisionCardOpened, OperatorDecisionTypes.QuotaExhausted,
        OperatorDecisionTypes.QuotaFallbackActive,
    ];

    public IReadOnlyList<OperatorDecisionFact>? Collect(DateTime nowUtc)
        => tasks() is { } published ? OperatorDecisionFacts.FromTasks(published, fallbackOf, includeLegacyQuotaWait: false) : null;
}

/// <summary>Open watcher stall cases (the watcher's own detector decides them).</summary>
public sealed class OperatorWatcherCaseFactSource(WatcherCaseStore cases, string? workspaceRoot) : IOperatorDecisionFactSource
{
    public IReadOnlyList<string> Types { get; } =
        [OperatorDecisionTypes.BackendStalled, OperatorDecisionTypes.BatchGateStuck,
            OperatorDecisionTypes.ProgressIdleWithReady];

    public IReadOnlyList<OperatorDecisionFact>? Collect(DateTime nowUtc)
        => string.IsNullOrWhiteSpace(workspaceRoot) ? [] : OperatorDecisionFacts.FromWatcherCases(cases.All(workspaceRoot));
}

/// <summary>Red and isolating batches, and quarantined batch records, from the batch gate store.</summary>
public sealed class OperatorBatchGateFactSource(BatchGateStore store, BatchGatePilotService? pilot = null) : IOperatorDecisionFactSource
{
    // A closed manifest never changes, and a published or abandoned batch
    // never changes phase again: each is read once per folder.
    private readonly Dictionary<string, BatchGateManifest> _manifests = new(StringComparer.Ordinal);
    private readonly HashSet<string> _settled = new(StringComparer.Ordinal);

    public IReadOnlyList<string> Types { get; } =
        [OperatorDecisionTypes.BatchRed, OperatorDecisionTypes.BatchIsolating, OperatorDecisionTypes.BatchRecordUnreadable,
         OperatorDecisionTypes.BatchRunningWithoutGate];

    public IReadOnlyList<OperatorDecisionFact>? Collect(DateTime nowUtc)
    {
        var batches = new List<(BatchGateManifest, BatchGateState?)>();
        foreach (var folder in store.ListManifestFolders())
        {
            if (_settled.Contains(folder)) continue;
            if (!_manifests.TryGetValue(folder, out var manifest))
            {
                // AGT-3681: an unreadable manifest is quarantined by the store
                // and reported below; it is read again on the next sweep.
                if (!store.TryReadManifestIn(folder, out manifest)) continue;
                _manifests[folder] = manifest;
            }
            var state = LatestState(folder);
            if (state?.Reason == BatchGateStore.StateUnreadableReason) continue;
            if (state?.Phase is BatchPhase.Published or BatchPhase.Abandoned)
            {
                _settled.Add(folder);
                _manifests.Remove(folder);
                continue;
            }
            batches.Add((manifest, state));
        }
        var facts = OperatorDecisionFacts.FromBatches(batches).ToList();
        facts.AddRange(OperatorDecisionFacts.FromQuarantinedBatchRecords(store.ListQuarantined()));
        if (pilot is not null)
            foreach (var (manifest, state) in batches)
            {
                if (state is null || !pilot.RunningWithoutFlight(manifest, state, new DateTimeOffset(nowUtc))) continue;
                var delay = pilot.RecoveryStatus(manifest.BatchId);
                var cards = manifest.Members.Select(member => member.TaskKey).ToArray();
                facts.Add(new OperatorDecisionFact(OperatorDecisionTypes.BatchRunningWithoutGate,
                    $"{OperatorDecisionTypes.BatchRunningWithoutGate}|{manifest.BatchId}|{state.RecordedAtUtc:O}",
                    manifest.Scope.Project, cards.Length == 1 ? cards[0] : null,
                    $"Batch running without a live gate: recovery has not run ({delay.Reason}). "
                        + $"Next automatic retry at {delay.NextRetryAtUtc:HH:mm:ss} UTC.",
                    state.RecordedAtUtc.UtcDateTime,
                    [new OperatorEvidenceLink("batch-gate-state", "/api/projects/"
                        + Uri.EscapeDataString(manifest.Scope.Project) + "/batch-gate/state")], cards));
            }
        return facts;
    }

    private BatchGateState? LatestState(string folder)
    {
        // Unknown cannot establish a red or isolating decision fact.
        var state = store.LatestState(_manifests[folder].BatchId);
        return state.Phase == BatchPhase.Unknown ? null : state;
    }
}

/// <summary>
/// AGT-3792: the gate host health state while the host is unreachable. One
/// installation-wide fact per outage, naming the waiting batch members and
/// what the owner can do.
/// </summary>
public sealed class OperatorGateHostFactSource(GateHostOutageMonitor monitor, BatchGateStore store)
    : IOperatorDecisionFactSource
{
    public IReadOnlyList<string> Types { get; } = [OperatorDecisionTypes.GateHostUnreachable];

    public IReadOnlyList<OperatorDecisionFact> Collect(DateTime nowUtc)
    {
        if (monitor.Current is not { } outage) return [];
        var waiting = store.ListPending()
            .OrderBy(item => item.Subject.EnqueueSequence)
            .Select(item => item.Subject.TaskKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return
        [
            new OperatorDecisionFact(OperatorDecisionTypes.GateHostUnreachable,
                $"{OperatorDecisionTypes.GateHostUnreachable}|{outage.SinceUtc.UtcDateTime:O}", null,
                waiting.Length == 1 ? waiting[0] : null,
                GateHostOutageText.Summary(outage, monitor.Host) + ". "
                    + GateHostOutageText.Detail(outage, monitor.Host, waiting),
                outage.SinceUtc.UtcDateTime,
                [new OperatorEvidenceLink("stalls", "/api/watcher/stalls")],
                waiting,
                Title: "Gate host unreachable"),
        ];
    }
}

/// <summary>Pending integration pushes past the batch gate's age alert.</summary>
public sealed class OperatorPendingPushFactSource(
    BatchIntegrationReservationService reservations,
    Func<string, string?> cardKeyOf) : IOperatorDecisionFactSource
{
    public IReadOnlyList<string> Types { get; } = [OperatorDecisionTypes.PushPendingOverdue];

    public IReadOnlyList<OperatorDecisionFact>? Collect(DateTime nowUtc)
        => reservations.ProjectsWithPending()
            .SelectMany(project => OperatorDecisionFacts.FromPendingPushes(
                project, reservations.GetWaits(project), cardKeyOf))
            .ToArray();
}

/// <summary>
/// Post Processing cards whose publication was refused (AGT-3695). It reads the
/// published task index and each Post Processing card's pipeline record; it
/// starts no scan and no git process.
/// </summary>
public sealed class OperatorBlockedPublicationFactSource(
    Func<IReadOnlyList<TaskInfo>?> tasks,
    Func<TaskInfo, PipelineStepExecution?> latestMergeOf) : IOperatorDecisionFactSource
{
    public IReadOnlyList<string> Types { get; } = [OperatorDecisionTypes.PushBlocked];

    public IReadOnlyList<OperatorDecisionFact>? Collect(DateTime nowUtc)
        => tasks() is { } published ? OperatorDecisionFacts.FromBlockedPublications(published, latestMergeOf) : null;
}

/// <summary>Provider limits the runners recorded per CLI.</summary>
public sealed class OperatorProviderLimitFactSource(ProviderLimitRegistry limits, AgentStudio.Cli.ModelSelectionLedger? selection = null) : IOperatorDecisionFactSource
{
    public IReadOnlyList<string> Types { get; } = [OperatorDecisionTypes.QuotaExhausted];

    public IReadOnlyList<OperatorDecisionFact>? Collect(DateTime nowUtc)
        => selection is null ? OperatorDecisionFacts.FromProviderLimits(limits.Active(nowUtc))
            : selection.Active().Where(entry => entry.Kind == "waiting" && entry.Rule != "pin"
                && !entry.NearbyResetWait)
                .Select(entry => new OperatorDecisionFact(OperatorDecisionTypes.QuotaExhausted,
                    "model-class-wait:" + entry.Id, entry.Project, entry.TaskKey, entry.Reason, entry.At.UtcDateTime,
                    [new OperatorEvidenceLink("Model routing", "/api/model-selection/events?eventId=" + entry.Id)]))
                .ToArray();
}

/// <summary>
/// AGT-3965: one fact per fast or balanced project whose develop moved
/// without a post-merge run reported within six hours. The occurrence is the
/// last reported tip, so a new report that leaves develop uncovered again is
/// a new occurrence, and a report that covers develop clears it.
/// </summary>
public sealed class OperatorPostMergeCoverageFactSource(Func<IReadOnlyList<PostMergeCoverage>> missing)
    : IOperatorDecisionFactSource
{
    public IReadOnlyList<string> Types { get; } = [OperatorDecisionTypes.PostMergeRunsMissing];

    public IReadOnlyList<OperatorDecisionFact> Collect(DateTime nowUtc)
        => missing().Select(coverage => new OperatorDecisionFact(
            OperatorDecisionTypes.PostMergeRunsMissing,
            $"{OperatorDecisionTypes.PostMergeRunsMissing}|{coverage.Project}|{coverage.LastReportedTipSha ?? "never"}",
            coverage.Project, null, coverage.Message,
            (coverage.LastReportedAtUtc + PostMergeCoveragePolicy.Limit)?.UtcDateTime ?? nowUtc,
            [new OperatorEvidenceLink("testing", "/api/projects/" + Uri.EscapeDataString(coverage.Project) + "/testing")],
            Title: "Post-merge runs missing")).ToArray();
}

/// <summary>
/// RT-1 (AGT-3988): one Needs you item per open post-merge incident whose
/// owner question was asked (two hours of incident age). The occurrence is the
/// project, the fingerprint and the incident start, so a repeated red receipt
/// of the same incident never adds a second item, and a green receipt that
/// resolves the incident clears it. The answer is applied through the
/// existing routes: a revert or fix-forward note on the repair card, or the
/// project's testing strategy, which stays an owner setting.
/// </summary>
public sealed class OperatorPostMergeRedIncidentFactSource(
    Func<IEnumerable<(string Project, AgentStudio.TaskServer.Contracts.PostMergeProjectState State)>> states,
    Func<string, string, string?>? cardKey = null) : IOperatorDecisionFactSource
{
    public IReadOnlyList<string> Types { get; } = [OperatorDecisionTypes.PostMergeRedIncident];

    public IReadOnlyList<OperatorDecisionFact> Collect(DateTime nowUtc)
    {
        var facts = new List<OperatorDecisionFact>();
        foreach (var (project, state) in states())
        foreach (var incident in state.Incidents ?? [])
        {
            if (incident.ResolvedAtUtc is not null || incident.OwnerQuestion is not { } question
                || incident.OwnerAskedAtUtc is not { } asked) continue;
            var card = cardKey?.Invoke(project, incident.RepairCardId) ?? incident.RepairCardId;
            var receipt = state.Runs.LastOrDefault(run => run.Id == question.RedRunId);
            var evidence = new List<OperatorEvidenceLink>
            {
                new("repair card", "/#/tasks/" + Uri.EscapeDataString(card)),
                new("latest red receipt", "/api/runner/post-merge/projects/" + Uri.EscapeDataString(project)),
                new("testing strategy", "/api/projects/" + Uri.EscapeDataString(project) + "/testing"),
            };
            var tests = incident.FailingTests.Count == 0 ? "unknown" : string.Join(", ", incident.FailingTests.Take(5))
                + (incident.FailingTests.Count > 5 ? $" and {incident.FailingTests.Count - 5} more" : "");
            var tip = question.RedTipSha is { Length: >= 12 } sha ? sha[..12] : question.RedTipSha ?? "unknown";
            var reason = $"Develop has been red for incident {incident.Fingerprint} since "
                + $"{incident.FirstRedAtUtc.UtcDateTime:yyyy-MM-dd HH:mm} UTC. Latest red receipt "
                + $"{question.RedRunId ?? "unknown"} at {tip}"
                + (receipt?.FinishedAtUtc is { } finished ? $" ({finished.UtcDateTime:yyyy-MM-dd HH:mm} UTC)" : "")
                + $". Failing tests: {tests}. Attributed merge: {incident.AttributedMerge ?? "none"}. "
                + "Options: " + string.Join("; ", question.Options.Select(option => OptionText(option, incident)))
                + $". Recommended: {OptionText(question.Recommended, incident)}, because {Lower(question.RecommendationReason)}"
                + " Studio moves no card and keeps the project's test placement until you answer.";
            facts.Add(new OperatorDecisionFact(OperatorDecisionTypes.PostMergeRedIncident,
                $"{OperatorDecisionTypes.PostMergeRedIncident}|{project}|{incident.Fingerprint}|{incident.FirstRedAtUtc:O}",
                project, card, reason, asked.UtcDateTime, evidence,
                Title: $"{project} · develop red for two hours"));
        }
        return facts;
    }

    private static string OptionText(string option, AgentStudio.TaskServer.Contracts.PostMergeIncident incident) => option switch
    {
        nameof(AgentStudio.OrchestrationDecisions.RedDevelopOwnerOption.RevertAttributedMerge) =>
            incident.AttributedMerge is { } merge
                ? $"revert the attributed merge {(merge.Length >= 12 ? merge[..12] : merge)}"
                : "revert the attributed merge (none attributed yet)",
        nameof(AgentStudio.OrchestrationDecisions.RedDevelopOwnerOption.KeepFixingForward) => "keep fixing forward",
        nameof(AgentStudio.OrchestrationDecisions.RedDevelopOwnerOption.SwitchProjectToStrict) =>
            "switch the project to strict until develop is green",
        _ => option,
    };

    private static string Lower(string sentence)
        => sentence.Length == 0 ? sentence : char.ToLowerInvariant(sentence[0]) + sentence[1..];
}
