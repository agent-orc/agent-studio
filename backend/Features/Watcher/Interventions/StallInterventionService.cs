using System.Collections.Concurrent;

namespace AgentStudio.Watcher;

/// <summary>Counters of one intervention pass.</summary>
public sealed record StallInterventionPassResult(int Considered, int Submitted, int Applied, int Held, string? Skipped = null);

/// <summary>
/// AGT-3207: the AI Operator's subscription to the AGT-3206 stall events. Each
/// pass reads the open operational Watcher cases and the interventions whose
/// follow-up check is due, freezes their facts and submits them through
/// <see cref="AiOperatorDecisionGateway"/> as the <c>stall-intervention</c>
/// decider. On Monolith the decision returns at once and
/// <see cref="StallInterventionEffect"/> applies it here; on Engine the
/// engine's receipt is applied by the authority through the same effect.
/// One decision per subject and pass; the policy holds the rate limit per host
/// and hour and the loop protection.
/// </summary>
public sealed class StallInterventionService(
    StallInterventionStore store,
    WatcherCaseStore cases,
    StallInterventionFactsBuilder facts,
    StallInterventionEffect effect,
    AiOperatorDecisionGateway decisions,
    IConfiguration configuration,
    TimeProvider time,
    ILogger<StallInterventionService> logger)
{
    /// <summary>Bucket of an engine occurrence; one engine decision per subject and bucket.</summary>
    public static readonly TimeSpan EnginePeriod = TimeSpan.FromMinutes(5);

    private const int MaxDecisionsPerPass = 20;
    private const string ExpectedState = "stall-open";
    private readonly ConcurrentDictionary<string, StallInterventionDecisionView> _latest = new(StringComparer.Ordinal);

    /// <summary>The latest decision per subject, including holds and their reasons.</summary>
    public IReadOnlyList<StallInterventionDecisionView> LatestDecisions()
        => _latest.Values.OrderByDescending(item => item.DecidedAtUtc).ToArray();

    public async Task<StallInterventionPassResult> RunOnceAsync(CancellationToken ct = default)
    {
        var options = StallInterventionOptions.FromConfiguration(configuration);
        if (!options.Enabled) return new(0, 0, 0, 0, "disabled");
        var root = configuration["TaskRepository"];
        if (string.IsNullOrWhiteSpace(root)) return new(0, 0, 0, 0, "no-task-repository");
        var now = time.GetUtcNow().UtcDateTime;
        store.Prune(now);
        var stalls = cases.All(root).Where(item => item.Subject is not null
            && StallInterventionFactsBuilder.KindOf(item) is not null).ToArray();
        var open = stalls.Where(StallInterventionFactsBuilder.IsOpen).ToArray();
        var subjects = new HashSet<string>(StringComparer.Ordinal);
        int considered = 0, submitted = 0, applied = 0, held = 0;

        foreach (var record in store.All().Where(item => item.Status == StallInterventionStatuses.Applied
            && item.FollowUpDueUtc <= now).ToArray())
        {
            if (submitted >= MaxDecisionsPerPass || !subjects.Add(record.Subject)) continue;
            considered++;
            var watcherCase = stalls.FirstOrDefault(item => item.Fingerprint == record.Fingerprint);
            var outcome = await IsolatedAsync(record.Subject, () => DecideAsync(record.Project,
                facts.FollowUp(record, watcherCase, store.All(), options, now), $"follow-up:{record.Id}", now, ct));
            submitted += outcome.Submitted ? 1 : 0;
            applied += outcome.Applied ? 1 : 0;
            held += outcome.Held ? 1 : 0;
        }

        foreach (var item in open.OrderBy(item => item.SinceUtc ?? item.FirstSeenUtc))
        {
            if (submitted >= MaxDecisionsPerPass || !subjects.Add(item.Subject!)) continue;
            considered++;
            var outcome = await IsolatedAsync(item.Subject!, () => DecideAsync(item.Project,
                facts.Stall(item, open, store.All(), options, now), $"stall:{item.Id}", now, ct));
            submitted += outcome.Submitted ? 1 : 0;
            applied += outcome.Applied ? 1 : 0;
            held += outcome.Held ? 1 : 0;
        }
        return new(considered, submitted, applied, held);
    }

    public StallInterventionUndoResult Undo(string id, string actor) => effect.Undo(id, actor);

    /// <summary>One subject's failure (an unreadable fact, a failed write) must not stop the others.</summary>
    private async Task<(bool Submitted, bool Applied, bool Held)> IsolatedAsync(string subject,
        Func<Task<(bool Submitted, bool Applied, bool Held)>> decide)
    {
        try
        {
            return await decide();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "stall-intervention-subject-failed subject={Subject}", subject);
            return (false, false, false);
        }
    }

    private async Task<(bool Submitted, bool Applied, bool Held)> DecideAsync(string project,
        StallInterventionFacts stall, string subjectKey, DateTime now, CancellationToken ct)
    {
        var payload = new AiOperatorDecisionPayload(AiOperatorDeciders.StallIntervention, subjectKey,
            StallIntervention: stall);
        AiOperatorSubmission submission;
        try
        {
            submission = decisions.Submit(new AiOperatorDecisionRequest(project, stall.Subject, ExpectedState,
                payload, decisions.EngineDecides ? EnginePeriod : null, AuthorityAttemptId: stall.Subject));
        }
        catch (InvalidOperationException exception) when (exception.Message == "orchestration-idempotency-conflict")
        {
            // The engine already decided this subject in the current period.
            logger.LogDebug("stall-intervention-period-decided subject={Subject}", stall.Subject);
            return (false, false, true);
        }
        if (submission.WaitsForEngine) return (true, false, false);
        var decision = submission.MonolithDecision!;
        _latest[stall.Subject] = new StallInterventionDecisionView(stall.Subject, stall.Kind,
            decision.Disposition.ToString(), decision.Effect, decision.ReasonCode, decision.Detail, now);
        if (decision.Disposition == AiOperatorDisposition.Hold) return (true, false, true);
        var runId = $"monolith:{Guid.NewGuid():N}";
        await effect.ApplyAsync(new AiOperatorApplication(runId, project, stall.Subject, ExpectedState,
            $"stall-intervention:{subjectKey}:{now:yyyyMMddHHmm}:{decision.Effect}", payload, decision,
            stall.Subject, 0, 0), ct);
        return (true, true, false);
    }
}

/// <summary>Runs <see cref="StallInterventionService"/> on its cadence; kill switch <c>StallIntervention:Enabled</c>.</summary>
public sealed class StallInterventionHostedService(
    StallInterventionService interventions,
    IConfiguration configuration,
    ILogger<StallInterventionHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await interventions.RunOnceAsync(stoppingToken);
                if (result.Submitted > 0)
                    logger.LogInformation("stall-intervention-pass considered={Considered} submitted={Submitted} applied={Applied} held={Held}",
                        result.Considered, result.Submitted, result.Applied, result.Held);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "stall-intervention-pass-failed"); }
            var options = StallInterventionOptions.FromConfiguration(configuration);
            try { await Task.Delay(TimeSpan.FromSeconds(options.IntervalSeconds), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
