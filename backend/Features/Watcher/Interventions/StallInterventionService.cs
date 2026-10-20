using System.Collections.Concurrent;

namespace AgentStudio.Watcher;

/// <summary>Counters of one intervention pass.</summary>
public sealed record StallInterventionPassResult(int Considered, int Submitted, int Applied, int Held, string? Skipped = null);

/// <summary>
/// AGT-3207: the AI Operator's subscription to the AGT-3206 stall events. Each
/// pass settles interrupted write-ahead records, checks the interventions
/// whose follow-up is due, restores lowered admissions that are still in
/// force once the host is calm, and looks at the open actionable stall cases
/// (observe-only kinds such as lane stalls are never submitted). The pure
/// policy is evaluated in process first; only a decision that acts, proposes
/// or asks counts toward the per-pass budget and reaches the engine. On
/// Monolith the decision is applied here through
/// <see cref="StallInterventionEffect"/>; on Engine the engine's receipt is
/// applied by the authority through the same effect. One decision per
/// subject and pass; the policy holds the rate limits and the loop protection.
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

    /// <summary>Decisions per pass that act, propose or ask; held decisions do not count.</summary>
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
        foreach (var record in store.All().Where(item => item.Status == StallInterventionStatuses.Applying
            && now - item.DecidedAtUtc >= StallInterventionEffect.InterruptedAfter).ToArray())
            await IsolatedAsync(record.Subject, () => Task.FromResult(Recovered(record)));

        var stalls = cases.All(root).Where(item => item.Subject is not null
            && StallInterventionFactsBuilder.KindOf(item) is not null).ToArray();
        var actionable = stalls.Where(item => StallInterventionFactsBuilder.IsActionable(item)
            && StallKinds.Priority(StallInterventionFactsBuilder.KindOf(item)) >= 0).ToArray();
        var subjects = new HashSet<string>(StringComparer.Ordinal);
        var counts = new PassCounts();

        foreach (var record in store.All().Where(item => item.Status == StallInterventionStatuses.Applied
            && item.FollowUpDueUtc <= now).OrderBy(item => item.FollowUpDueUtc).ToArray())
        {
            if (counts.Submitted >= MaxDecisionsPerPass) break;
            if (!subjects.Add(record.Subject)) continue;
            var watcherCase = stalls.FirstOrDefault(item => item.Fingerprint == record.Fingerprint);
            counts.Add(await IsolatedAsync(record.Subject, () => DecideAsync(record.Project,
                facts.FollowUp(record, watcherCase, store.All(), options, now), $"follow-up:{record.Id}", now, ct)));
        }

        // A lowered admission stays in force after its own follow-up settled
        // (for example as an escalation): restore it once the host is calm.
        // While the host's overload case is open, that case decides.
        foreach (var record in store.All().Where(item => item.AdmissionInForce
            && !StallInterventionStatuses.Pending.Contains(item.Status)).ToArray())
        {
            if (counts.Submitted >= MaxDecisionsPerPass) break;
            if (actionable.Any(item => item.Subject == record.Subject) || !subjects.Add(record.Subject)) continue;
            var reading = facts.AdmissionOf(record, options, now);
            if (reading is null) continue;
            if (reading.Admission != record.TargetAdmission)
            {
                effect.ReleaseLowering(record, $"admission reads {reading.Admission?.ToString() ?? "unknown"}, "
                    + $"the AI Operator had set {record.TargetAdmission}");
                continue;
            }
            var overloadOpen = stalls.Any(item => item.Subject == record.Subject && StallInterventionFactsBuilder.IsOpen(item)
                && StallInterventionFactsBuilder.KindOf(item) is StallKinds.HostOverload or StallKinds.MemoryPressure);
            counts.Add(await IsolatedAsync(record.Subject, () => DecideAsync(record.Project,
                facts.Restore(record, reading, overloadOpen, store.All(), options, now), $"restore:{record.Id}", now, ct)));
        }

        foreach (var item in actionable.OrderBy(item => StallKinds.Priority(StallInterventionFactsBuilder.KindOf(item)))
            .ThenBy(item => item.SinceUtc ?? item.FirstSeenUtc))
        {
            if (counts.Submitted >= MaxDecisionsPerPass) break;
            if (!subjects.Add(item.Subject!)) continue;
            counts.Add(await IsolatedAsync(item.Subject!, () => DecideAsync(item.Project,
                facts.Stall(item, stalls.Where(StallInterventionFactsBuilder.IsOpen).ToArray(), store.All(), options, now),
                $"stall:{item.Id}", now, ct)));
        }
        return new(counts.Considered, counts.Submitted, counts.Applied, counts.Held);
    }

    public StallInterventionUndoResult Undo(string id, string actor) => effect.Undo(id, actor);

    private Outcome Recovered(StallInterventionRecord record)
    {
        var recovered = effect.RecoverInterrupted(record);
        logger.LogWarning("stall-intervention-interrupted id={Id} action={Action} settled={Status}",
            record.Id, record.Action, recovered.Status);
        return new Outcome(false, false, false);
    }

    /// <summary>One subject's failure (an unreadable fact, a failed write) must not stop the others.</summary>
    private async Task<Outcome> IsolatedAsync(string subject, Func<Task<Outcome>> decide)
    {
        try
        {
            return await decide();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "stall-intervention-subject-failed subject={Subject}", subject);
            return new Outcome(false, false, false);
        }
    }

    private async Task<Outcome> DecideAsync(string project, StallInterventionFacts stall, string subjectKey,
        DateTime now, CancellationToken ct)
    {
        var payload = new AiOperatorDecisionPayload(AiOperatorDeciders.StallIntervention, subjectKey,
            StallIntervention: stall);
        var request = new AiOperatorDecisionRequest(project, stall.Subject, ExpectedState,
            payload, decisions.EngineDecides ? EnginePeriod : null, AuthorityAttemptId: stall.Subject);
        // The pure policy runs in process first: a held decision changes
        // nothing and is never sent to the engine.
        var decision = decisions.EngineDecides ? decisions.Preview(payload) : decisions.Submit(request).MonolithDecision!;
        _latest[stall.Subject] = new StallInterventionDecisionView(stall.Subject, stall.Kind,
            decision.Disposition.ToString(), decision.Effect, decision.ReasonCode, decision.Detail, now, project);
        if (decision.Disposition == AiOperatorDisposition.Hold) return new Outcome(false, false, true);
        var action = decision.Intervention?.Action ?? decision.Effect;
        var writeKey = StallInterventionEffect.WriteKey(stall, action,
            StallInterventionEffect.Observes(StallInterventionOptions.FromConfiguration(configuration), action));
        if (store.FindByWriteKey(writeKey) is not null) return new Outcome(false, false, true);
        if (decisions.EngineDecides)
        {
            try
            {
                decisions.Submit(request);
            }
            catch (InvalidOperationException exception) when (exception.Message == "orchestration-idempotency-conflict")
            {
                // The engine already decided this subject in the current period.
                logger.LogDebug("stall-intervention-period-decided subject={Subject}", stall.Subject);
                return new Outcome(false, false, true);
            }
            return new Outcome(true, false, false);
        }
        await effect.ApplyAsync(new AiOperatorApplication($"monolith:{Guid.NewGuid():N}", project, stall.Subject,
            ExpectedState, writeKey, payload, decision, stall.Subject, 0, 0), ct);
        return new Outcome(true, true, false);
    }

    private readonly record struct Outcome(bool Submitted, bool Applied, bool Held);

    private sealed class PassCounts
    {
        public int Considered { get; private set; }
        public int Submitted { get; private set; }
        public int Applied { get; private set; }
        public int Held { get; private set; }

        public void Add(Outcome outcome)
        {
            Considered++;
            Submitted += outcome.Submitted ? 1 : 0;
            Applied += outcome.Applied ? 1 : 0;
            Held += outcome.Held ? 1 : 0;
        }
    }
}

/// <summary>Runs <see cref="StallInterventionService"/> on its cadence; <c>StallIntervention:Mode</c> off stops it.</summary>
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
