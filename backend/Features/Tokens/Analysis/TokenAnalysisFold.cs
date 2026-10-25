using System.Globalization;

namespace AgentStudio.Tokens;

/// <summary>Everything the analysis knows about one card.</summary>
public sealed record TokenAnalysisJobInput(
    string JobId,
    TaskInfo? Task,
    PipelineExecutionRecord? Log,
    IReadOnlyList<OrchestratorLogEntry> Ledger);

/// <summary>
/// One priced usage fact of a card: a pipeline step execution or a ledger
/// call, already classified. Structural items carry a run's duration and
/// result with zero tokens because ledger calls price that run.
/// </summary>
public sealed record TokenAnalysisItem(
    DateTime At,
    DateTime? EndAt,
    string Kind,
    string Label,
    int Attempt,
    int Slot,
    string ModelId,
    string Model,
    string Level,
    string Cli,
    string Host,
    long Input,
    long Output,
    long CacheRead,
    long CacheWrite,
    long? Reasoning,
    decimal CostUsd,
    bool Priced,
    long? DurationMs,
    string? Result,
    string Source)
{
    public long Total => Input + Output + CacheRead + CacheWrite;
}

/// <summary>
/// Pure card-centric token analysis (AGT-3267). Reads the merged usage
/// ledger (bus rows, durable receipts, chat fallback) and the pipeline
/// execution log of each card and counts every token once:
/// <list type="bullet">
///   <item>Agent ledger calls price the coding run they fall into; that run's
///   logged counters become a structural item (duration and result only).</item>
///   <item>An orchestrator or supporting ledger call that equals a measured
///   decision, aspect, drift, or analysis execution is priced by the
///   execution, which knows its step; the call is dropped
///   (<see cref="ProjectPipelineCostService.ReconcileReceipts"/>).</item>
///   <item>Every other ledger call stays, classified by participant and topic.</item>
/// </list>
/// The first coding execution of a card is <see cref="TokenUsageKinds.Coding"/>;
/// every later one is a <see cref="TokenUsageKinds.FixRound"/>.
/// </summary>
public static class TokenAnalysisFold
{
    public const string Unknown = "unknown";
    public const string SourcePipeline = "pipeline";
    public const string SourceLedger = "ledger";
    public const string SourceStructure = "pipeline-run";

    public const string Disclaimer =
        "Theoretical cost at list prices; subscription plans bill differently. Reasoning tokens are a reported subset of output.";

    public static IReadOnlyList<TokenAnalysisItem> Items(ITokenPricing pricing, TokenAnalysisJobInput job)
    {
        var items = new List<TokenAnalysisItem>();
        var attempts = Attempts(job.Log);
        var slots = CoreSlots(pricing, attempts);
        var ledger = job.Ledger
            .Where(entry => entry.TokenUsage is { } usage && UsageTotal(usage) > 0)
            .OrderBy(entry => entry.Ts)
            .ToList();

        // Ledger calls of the coding family price the run they fall into.
        var agentCalls = new List<(OrchestratorLogEntry Entry, int Slot)>();
        var otherCalls = new List<OrchestratorLogEntry>();
        foreach (var entry in ledger)
        {
            var kind = TokenUsageKindPolicy.ForLedger(entry.ParticipantId, entry.Topic, firstCodingRun: true);
            if (kind == TokenUsageKinds.Coding) agentCalls.Add((entry, SlotOf(slots, entry.Ts)));
            else otherCalls.Add(entry);
        }
        var backedSlots = agentCalls.Select(call => call.Slot).ToHashSet();

        for (var i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            var kind = i == 0 ? TokenUsageKinds.Coding : TokenUsageKinds.FixRound;
            var run = slot.Run;
            var backed = backedSlots.Contains(i);
            items.Add(StepItem(pricing, slot.Step, run, kind, slot.Attempt, i, slot.StartedAt,
                backed ? SourceStructure : SourcePipeline, keepTokens: !backed));
        }
        foreach (var (entry, slot) in agentCalls)
        {
            var kind = slots.Count == 0 || slot <= 0 ? TokenUsageKinds.Coding : TokenUsageKinds.FixRound;
            var attempt = slots.Count == 0 ? AttemptOf(attempts, entry.Ts) : slots[slot].Attempt;
            items.Add(LedgerItem(pricing, entry, kind, attempt, slots.Count == 0 ? 0 : slot));
        }

        // Measured non-coding executions own their step; equal ledger calls are dropped.
        var measured = new List<(PipelineStepExecution Step, PipelineStepRunSummary Run, int Attempt, DateTime AttemptAt)>();
        foreach (var (attempt, index) in attempts.Select((attempt, index) => (attempt, index)))
        {
            foreach (var step in attempt.Steps.Where(step => step.Kind != StepKind.Core))
            {
                foreach (var run in StepCostMeasurement.Executions(pricing, step))
                    measured.Add((step, run, index + 1, attempt.StartedAt));
            }
        }
        var measuredKeys = measured
            .Where(m => Family(m.Step.Kind) is not null && StepCostMeasurement.Tokens(m.Run) > 0)
            .Select(m => Key(job.JobId, Family(m.Step.Kind)!.Value, m.Run.Model,
                m.Run.InputTokens, m.Run.OutputTokens, m.Run.CacheReadTokens, m.Run.CacheCreationTokens))
            .ToList();
        var ledgerKeys = otherCalls
            .Select(entry => LedgerFamily(entry.ParticipantId) is { } family
                ? Key(job.JobId, family, entry.TokenUsage!.Model, entry.TokenUsage.InputTokens,
                    entry.TokenUsage.OutputTokens, entry.TokenUsage.CacheReadTokens,
                    entry.TokenUsage.CacheCreationTokens)
                : (ProjectPipelineCostService.ReceiptMatch?)null)
            .ToList();
        var reconciled = ProjectPipelineCostService.ReconcileReceipts(ledgerKeys, measuredKeys);

        foreach (var (step, run, attempt, attemptAt) in measured)
        {
            if (StepCostMeasurement.Tokens(run) <= 0 && run.CostBasis != StepCostBasis.Model
                && run.DurationMs <= 0) continue;
            var kind = TokenUsageKindPolicy.ForStep(step.StepId, step.Kind, firstCodingRun: false);
            items.Add(StepItem(pricing, step, run, kind, attempt, -1, attemptAt, SourcePipeline, keepTokens: true));
        }
        for (var i = 0; i < otherCalls.Count; i++)
        {
            if (reconciled.Contains(i)) continue;
            var entry = otherCalls[i];
            var kind = TokenUsageKindPolicy.ForLedger(entry.ParticipantId, entry.Topic, firstCodingRun: true);
            items.Add(LedgerItem(pricing, entry, kind, AttemptOf(attempts, entry.Ts), -1));
        }

        return items.OrderBy(item => item.At).ToList();
    }

    /// <summary>Ledger rows without a card (project chat, orchestrator boot).</summary>
    public static IReadOnlyList<TokenAnalysisItem> RemainderItems(
        ITokenPricing pricing, IEnumerable<OrchestratorLogEntry> entries)
        => entries
            .Where(entry => entry.TokenUsage is { } usage && UsageTotal(usage) > 0)
            .Select(entry => LedgerItem(pricing, entry,
                TokenUsageKindPolicy.ForLedger(entry.ParticipantId, entry.Topic, firstCodingRun: true), 1, -1))
            .ToList();

    public static TokenAnalysisCard? Card(
        string project, TokenAnalysisJobInput job, IReadOnlyList<TokenAnalysisItem> items,
        DateTime fromUtc, DateTime toUtc)
    {
        var inRange = items.Where(item => item.At >= fromUtc && item.At < toUtc).ToList();
        var slices = Slices(inRange);
        if (slices.Count == 0) return null;
        var task = job.Task;
        var coreRuns = items.Where(item => item.Kind is TokenUsageKinds.Coding or TokenUsageKinds.FixRound)
            .Select(item => item.Slot).Distinct().Count();
        var attempts = job.Log is null ? 1 : 1 + job.Log.PreviousAttempts.Count;
        var live = job.Log is { CompletedAt: null } log
                   && log.Steps.Any(step => step.Status == PipelineStepStatus.Running);
        var hasBlocker = task is not null && (task.BlockedBy.Count > 0 || task.ParkedBlocker is not null);
        return new TokenAnalysisCard(
            project,
            job.JobId,
            string.IsNullOrWhiteSpace(task?.Key) ? NullIfBlank(task?.TaskKey) : task.Key,
            string.IsNullOrWhiteSpace(task?.Title) ? job.JobId : task.Title,
            task?.State,
            task?.TaskType ?? Unknown,
            task?.Tags ?? [],
            CardOutcomes.Of(task?.State, task?.Integration?.Status, hasBlocker),
            Math.Max(coreRuns, attempts),
            CardOutcomes.IsInFlight(task?.State) || live,
            inRange.Min(item => item.At),
            inRange.Max(item => item.EndAt ?? item.At),
            slices.Sum(SliceTotal),
            slices.Sum(slice => slice.CostUsd),
            slices.All(slice => slice.Priced),
            slices);
    }

    /// <summary>Usage cube of the items; zero-token structural items contribute nothing.</summary>
    public static IReadOnlyList<TokenAnalysisSlice> Slices(IEnumerable<TokenAnalysisItem> items)
        => items
            .Where(item => item.Total > 0)
            .GroupBy(item => (item.Kind, item.Model, item.Level, item.Cli, item.Host,
                Day: item.At.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))
            .Select(group => new TokenAnalysisSlice(
                group.Key.Kind, group.Key.Model, group.Key.Level, group.Key.Cli, group.Key.Host, group.Key.Day,
                group.Sum(item => item.Input),
                group.Sum(item => item.Output),
                group.Sum(item => item.CacheRead),
                group.Sum(item => item.CacheWrite),
                group.Any(item => item.Reasoning is not null) ? group.Sum(item => item.Reasoning ?? 0) : null,
                group.Sum(item => item.CostUsd),
                group.All(item => item.Priced),
                group.Count()))
            .OrderBy(slice => slice.Day, StringComparer.Ordinal)
            .ThenBy(slice => slice.Kind, StringComparer.Ordinal)
            .ThenBy(slice => slice.Model, StringComparer.Ordinal)
            .ToList();

    public static long SliceTotal(TokenAnalysisSlice slice)
        => slice.Input + slice.Output + slice.CacheRead + slice.CacheWrite;

    /// <summary>
    /// Drill-down timeline: the ledger calls of one coding run fold into one
    /// event per model identity, which also carries the run's logged duration
    /// and result. Every other item is its own event.
    /// </summary>
    public static IReadOnlyList<TokenAnalysisEvent> Events(IReadOnlyList<TokenAnalysisItem> items)
    {
        var events = new List<TokenAnalysisEvent>();
        var structure = items.Where(item => item.Source == SourceStructure)
            .GroupBy(item => item.Slot)
            .ToDictionary(group => group.Key, group => group.ToList());
        var runCalls = items
            .Where(item => item.Source == SourceLedger && item.Kind is TokenUsageKinds.Coding or TokenUsageKinds.FixRound)
            .GroupBy(item => (item.Slot, item.Attempt, item.Kind, item.Model, item.Level, item.Cli, item.Host))
            .OrderBy(group => group.Min(item => item.At))
            .ToList();
        var structureUsed = new HashSet<int>();
        foreach (var group in runCalls)
        {
            var calls = group.ToList();
            structure.TryGetValue(group.Key.Slot, out var runs);
            var carriesRun = runs is not null && structureUsed.Add(group.Key.Slot);
            var at = calls.Min(item => item.At);
            var endAt = calls.Max(item => item.EndAt ?? item.At);
            if (carriesRun)
            {
                at = runs!.Min(item => item.At) < at ? runs!.Min(item => item.At) : at;
                var runEnd = runs!.Max(item => item.EndAt ?? item.At);
                endAt = runEnd > endAt ? runEnd : endAt;
            }
            events.Add(new TokenAnalysisEvent(
                at, endAt, group.Key.Kind,
                carriesRun ? runs![0].Label : PipelineCatalogue.CoreAgentRunStepId,
                group.Key.Attempt, group.Key.Model, group.Key.Level, group.Key.Cli, group.Key.Host,
                calls.Sum(item => item.Input), calls.Sum(item => item.Output),
                calls.Sum(item => item.CacheRead), calls.Sum(item => item.CacheWrite),
                calls.Any(item => item.Reasoning is not null) ? calls.Sum(item => item.Reasoning ?? 0) : null,
                calls.Sum(item => item.Total), calls.Sum(item => item.CostUsd), calls.All(item => item.Priced),
                carriesRun ? runs!.Sum(item => item.DurationMs ?? 0) : null,
                carriesRun ? runs![^1].Result : null,
                calls.Count, SourceLedger));
        }
        foreach (var item in items)
        {
            if (item.Source == SourceLedger && item.Kind is TokenUsageKinds.Coding or TokenUsageKinds.FixRound) continue;
            if (item.Source == SourceStructure && structureUsed.Contains(item.Slot)) continue;
            events.Add(new TokenAnalysisEvent(
                item.At, item.EndAt, item.Kind, item.Label, item.Attempt, item.Model, item.Level, item.Cli, item.Host,
                item.Input, item.Output, item.CacheRead, item.CacheWrite, item.Reasoning, item.Total,
                item.CostUsd, item.Priced, item.DurationMs, item.Result, 1, item.Source));
        }
        return events.OrderBy(e => e.At).ThenBy(e => e.Label, StringComparer.Ordinal).ToList();
    }

    private sealed record CoreSlot(int Attempt, DateTime StartedAt, PipelineStepExecution Step, PipelineStepRunSummary Run);

    private static List<PipelineExecutionRecord> Attempts(PipelineExecutionRecord? log)
    {
        if (log is null) return [];
        var attempts = log.PreviousAttempts.OrderBy(attempt => attempt.Attempt).ThenBy(attempt => attempt.StartedAt).ToList();
        attempts.Add(log);
        return attempts;
    }

    private static List<CoreSlot> CoreSlots(ITokenPricing pricing, IReadOnlyList<PipelineExecutionRecord> attempts)
    {
        var slots = new List<CoreSlot>();
        for (var index = 0; index < attempts.Count; index++)
        {
            var attempt = attempts[index];
            foreach (var step in attempt.Steps.Where(step => step.Kind == StepKind.Core))
            {
                foreach (var run in StepCostMeasurement.Executions(pricing, step))
                    slots.Add(new CoreSlot(index + 1, run.StartedAt ?? step.StartedAt ?? attempt.StartedAt, step, run));
            }
        }
        return slots.OrderBy(slot => slot.StartedAt).ToList();
    }

    /// <summary>Run window owning a call: the last run started at or before it, else the first.</summary>
    private static int SlotOf(IReadOnlyList<CoreSlot> slots, DateTime at)
    {
        if (slots.Count == 0) return 0;
        var owner = 0;
        for (var i = 0; i < slots.Count; i++)
        {
            if (slots[i].StartedAt > at) break;
            owner = i;
        }
        return owner;
    }

    private static int AttemptOf(IReadOnlyList<PipelineExecutionRecord> attempts, DateTime at)
    {
        var owner = 1;
        for (var i = 0; i < attempts.Count; i++)
        {
            if (attempts[i].StartedAt > at) break;
            owner = i + 1;
        }
        return owner;
    }

    private static StepKind? Family(StepKind kind) => kind switch
    {
        StepKind.Orchestrator => StepKind.Orchestrator,
        StepKind.Aspect or StepKind.Drift or StepKind.Analysis => StepKind.Aspect,
        _ => null,
    };

    private static StepKind? LedgerFamily(string? participantId)
        => TokenParticipants.IsOrchestrator(participantId) ? StepKind.Orchestrator
            : TokenParticipants.IsSupporting(participantId) ? StepKind.Aspect
            : null;

    private static ProjectPipelineCostService.ReceiptMatch Key(
        string jobId, StepKind family, string? model, long input, long output, long cacheRead, long cacheWrite)
        => new(jobId, family, ModelIdentity.RegistryId(model ?? string.Empty), input, output, cacheRead, cacheWrite);

    private static TokenAnalysisItem StepItem(
        ITokenPricing pricing, PipelineStepExecution step, PipelineStepRunSummary run, string kind,
        int attempt, int slot, DateTime fallbackAt, string source, bool keepTokens)
    {
        var at = (run.StartedAt ?? step.StartedAt ?? run.CompletedAt ?? fallbackAt).ToUniversalTime();
        var input = keepTokens ? run.InputTokens : 0;
        var output = keepTokens ? run.OutputTokens : 0;
        var cacheRead = keepTokens ? run.CacheReadTokens : 0;
        var cacheWrite = keepTokens ? run.CacheCreationTokens : 0;
        var (cost, priced) = Price(pricing, run.Model, input, output, cacheRead, cacheWrite, at);
        var host = !string.IsNullOrWhiteSpace(step.ExecutionHostId) ? step.ExecutionHostId!.Trim()
            : string.Equals(step.ExecutionLocation, "remote", StringComparison.OrdinalIgnoreCase)
                ? TokenUsageHost.UnrecordedRemote : TokenUsageHost.Local;
        return new TokenAnalysisItem(
            at, run.CompletedAt?.ToUniversalTime(), kind, step.StepId, attempt, slot,
            ModelIdentity.StoredId(run.Model) ?? Unknown,
            ModelIdentity.Label(run.Model) ?? Unknown,
            Level(run.ThinkingLevel),
            Cli(null, null, run.Model),
            host,
            input, output, cacheRead, cacheWrite, null, cost, priced,
            run.DurationMs > 0 ? run.DurationMs : null,
            string.IsNullOrWhiteSpace(run.Verdict) ? run.Status.ToString().ToLowerInvariant() : run.Verdict,
            source);
    }

    private static TokenAnalysisItem LedgerItem(
        ITokenPricing pricing, OrchestratorLogEntry entry, string kind, int attempt, int slot)
    {
        var usage = entry.TokenUsage!;
        var at = entry.Ts.ToUniversalTime();
        var (cost, priced) = Price(pricing, usage.Model, usage.InputTokens, usage.OutputTokens,
            usage.CacheReadTokens, usage.CacheCreationTokens, at);
        return new TokenAnalysisItem(
            at, null, kind,
            string.IsNullOrWhiteSpace(entry.Topic) ? entry.ParticipantId ?? Unknown : entry.Topic,
            attempt, slot,
            ModelIdentity.StoredId(usage.Model) ?? Unknown,
            ModelIdentity.Label(usage.Model) ?? Unknown,
            Level(usage.ThinkingLevel),
            Cli(usage.CliType, entry.ParticipantId, usage.Model),
            TokenUsageHost.Resolve(usage.Host, entry.ParticipantId),
            usage.InputTokens, usage.OutputTokens, usage.CacheReadTokens, usage.CacheCreationTokens,
            usage.ReasoningTokens, cost, priced, null, null, SourceLedger);
    }

    private static (decimal Cost, bool Priced) Price(
        ITokenPricing pricing, string? model, long input, long output, long cacheRead, long cacheWrite, DateTime at)
    {
        if (input + output + cacheRead + cacheWrite <= 0) return (0m, true);
        var estimate = pricing.Estimate(model, input, output, cacheRead, cacheWrite, at);
        return estimate.ModelKnown ? (estimate.Total, true) : (0m, false);
    }

    private static string Level(string? level)
        => string.IsNullOrWhiteSpace(level) ? Unknown : level.Trim().ToLowerInvariant();

    /// <summary>CLI of a call: recorded value, then the agent participant, then the model's CLI.</summary>
    public static string Cli(string? cli, string? participantId, string? model)
    {
        if (!string.IsNullOrWhiteSpace(cli)) return cli.Trim().ToLowerInvariant();
        if (TokenParticipants.IsAgent(participantId))
        {
            var slug = participantId!.Trim()["agent:".Length..].Split(':')[0];
            if (CliTypes.IsValid(slug)) return CliTypes.Normalize(slug);
        }
        var fromModel = ModelIdentity.Resolve(model)?.Cli;
        return string.IsNullOrWhiteSpace(fromModel) ? Unknown : fromModel.Trim().ToLowerInvariant();
    }

    private static long UsageTotal(OrchestratorTokenUsage usage)
        => (long)usage.InputTokens + usage.OutputTokens + usage.CacheReadTokens + usage.CacheCreationTokens;

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
