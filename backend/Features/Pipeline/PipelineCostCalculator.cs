

namespace AgentStudio.Pipeline;

/// <summary>
/// One model/reason pair that prevented historical price resolution. The run
/// count lets aggregate UIs distinguish an unavailable amount from a priced
/// subtotal without treating either state as zero dollars.
/// </summary>
public sealed record PipelinePricingGap(
    string ModelId,
    string Reason,
    int AffectedRuns);

/// <summary>
/// Per-step cost (USD) breakdown for one step's recorded token usage.
/// <see cref="ModelKnown"/> is false when the historical resolver has no
/// price for the model and run date. <see cref="PricingGaps"/> carries the
/// exact model id and resolver reason for an honest unavailable-price state.
/// </summary>
public sealed record PipelineStepCost(
    string StepId,
    StepKind Kind,
    string? Model,
    string? TokenUsageSource,
    bool ModelKnown,
    IReadOnlyList<PipelinePricingGap> PricingGaps,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheCreationTokens,
    long TotalTokens,
    decimal InputCostUsd,
    decimal OutputCostUsd,
    decimal CacheReadCostUsd,
    decimal CacheCreationCostUsd,
    decimal CostUsd);

/// <summary>
/// Cost summary for one task's whole pipeline run: per-step rows plus the
/// task total (sum across all pre-steps + the core run + all post-steps),
/// which is the single number the Overview "task total" line shows.
/// </summary>
public sealed record PipelineCostSummary(
    IReadOnlyList<PipelineStepCost> Steps,
    long TotalInputTokens,
    long TotalOutputTokens,
    long TotalCacheReadTokens,
    long TotalCacheCreationTokens,
    long TotalTokens,
    decimal TotalInputCostUsd,
    decimal TotalOutputCostUsd,
    decimal TotalCacheReadCostUsd,
    decimal TotalCacheCreationCostUsd,
    decimal TotalCostUsd,
    bool AnyModelUnknown,
    int UnpricedRuns,
    IReadOnlyList<PipelinePricingGap> PricingGaps);

/// <summary>
/// Token + cost rollup for a single model, summed across the steps that ran
/// on it. A run uses several models (the core agent model, the aspect
/// reviewer's Haiku, an orchestrator decision model), so the Overview RUNS
/// view groups a run's step tokens by model into these rows.
/// <see cref="ModelKnown"/> is false when the historical resolver has no
/// price for at least one contributing run.
/// </summary>
public sealed record PipelineModelTokenUsage(
    string Model,
    bool ModelKnown,
    int UnpricedRuns,
    IReadOnlyList<PipelinePricingGap> PricingGaps,
    int Steps,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheCreationTokens,
    long TotalTokens,
    decimal CostUsd,
    /// <summary>
    /// Effective reasoning level the model ran at for these tokens, or null
    /// when the recorded data carries no level (legacy rows, models without a
    /// level dimension). Model and level together are one identity, so a run
    /// that used the same model at two levels yields two rows (AGT-2811).
    /// </summary>
    string? ThinkingLevel = null);

/// <summary>
/// One pipeline run (a <see cref="PipelineExecutionRecord"/> attempt) with
/// its tokens grouped per model. <see cref="Current"/> marks a run that is
/// still live (no completion stamp), never merely the newest: the panel
/// already sorts newest-first, so position carries recency and a finished
/// newest run gets no marker. Older runs come from
/// <see cref="PipelineExecutionRecord.PreviousAttempts"/>.
/// </summary>
public sealed record PipelineRunTokenUsage(
    int Attempt,
    bool Current,
    DateTime StartedAt,
    DateTime? CompletedAt,
    IReadOnlyList<PipelineModelTokenUsage> Models,
    long TotalTokens,
    decimal TotalCostUsd,
    bool AnyModelUnknown,
    IReadOnlyList<PipelinePricingGap> PricingGaps,
    bool TokenUsageAvailable);

/// <summary>
/// Per-model token usage for one task across every run: a per-run breakdown
/// plus a grand total that sums each model over all runs. Powers the
/// Overview "RUNS - tokens by model" surface (per-run cards plus a visually
/// distinct lifetime total row).
/// </summary>
public sealed record PipelineModelUsageSummary(
    IReadOnlyList<PipelineRunTokenUsage> Runs,
    IReadOnlyList<PipelineModelTokenUsage> TotalByModel,
    long TotalTokens,
    decimal TotalCostUsd,
    bool AnyModelUnknown,
    int UnpricedRuns,
    IReadOnlyList<PipelinePricingGap> PricingGaps,
    int MissingTokenRuns);

/// <summary>
/// Derives per-step and task-total cost from an already-recorded
/// <see cref="PipelineExecutionRecord"/> using the single price table in
/// <see cref="TokenPricing"/>. Pure and cheap: a task has a handful of
/// steps, so this runs on read without a disk scan. Project-level
/// aggregation across many tasks goes through a separate cached path so
/// the Overview poll never triggers an O(N) price scan.
/// </summary>
public static class PipelineCostCalculator
{
    public static PipelineCostSummary Summarize(PipelineExecutionRecord? record)
    {
        if (record == null || record.Steps.Count == 0)
        {
            return new PipelineCostSummary(
                Array.Empty<PipelineStepCost>(),
                0, 0, 0, 0, 0,
                0m, 0m, 0m, 0m, 0m,
                false, 0, Array.Empty<PipelinePricingGap>());
        }

        var steps = new List<PipelineStepCost>(record.Steps.Count);
        long totalInput = 0;
        long totalOutput = 0;
        long totalCacheRead = 0;
        long totalCacheCreation = 0;
        long totalTokens = 0;
        decimal totalInputCost = 0m;
        decimal totalOutputCost = 0m;
        decimal totalCacheReadCost = 0m;
        decimal totalCacheCreationCost = 0m;
        decimal totalCost = 0m;
        var anyUnknown = false;

        foreach (var s in record.Steps)
        {
            var est = TokenPricing.Estimate(
                s.Model, s.InputTokens, s.OutputTokens, s.CacheReadTokens, s.CacheCreationTokens,
                record.StartedAt);
            var stepTokens = s.InputTokens + s.OutputTokens + s.CacheReadTokens + s.CacheCreationTokens;
            // Only a step that actually consumed tokens but has no resolved
            // historical price should flag a gap; a 0-token tool step is not a
            // pricing gap.
            if (stepTokens > 0 && !est.ModelKnown) anyUnknown = true;

            steps.Add(new PipelineStepCost(
                StepId: s.StepId,
                Kind: s.Kind,
                Model: s.Model,
                TokenUsageSource: s.TokenUsageSource,
                ModelKnown: est.ModelKnown,
                PricingGaps: PricingGapsFor(est, stepTokens, s.Model),
                InputTokens: s.InputTokens,
                OutputTokens: s.OutputTokens,
                CacheReadTokens: s.CacheReadTokens,
                CacheCreationTokens: s.CacheCreationTokens,
                TotalTokens: stepTokens,
                InputCostUsd: Round(est.InputUsd),
                OutputCostUsd: Round(est.OutputUsd),
                CacheReadCostUsd: Round(est.CacheReadUsd),
                CacheCreationCostUsd: Round(est.CacheWriteUsd),
                CostUsd: Round(est.Total)));

            totalInput += s.InputTokens;
            totalOutput += s.OutputTokens;
            totalCacheRead += s.CacheReadTokens;
            totalCacheCreation += s.CacheCreationTokens;
            totalTokens += stepTokens;
            totalInputCost += est.InputUsd;
            totalOutputCost += est.OutputUsd;
            totalCacheReadCost += est.CacheReadUsd;
            totalCacheCreationCost += est.CacheWriteUsd;
            totalCost += est.Total;
        }

        return new PipelineCostSummary(
            steps,
            totalInput,
            totalOutput,
            totalCacheRead,
            totalCacheCreation,
            totalTokens,
            Round(totalInputCost),
            Round(totalOutputCost),
            Round(totalCacheReadCost),
            Round(totalCacheCreationCost),
            Round(totalCost),
            anyUnknown,
            anyUnknown ? 1 : 0,
            MergePricingGaps(steps.SelectMany(step => step.PricingGaps), oneRun: true));
    }

    /// <summary>
    /// Replaces the cost rows for read-projected remote steps with the
    /// canonical per-call token-ledger values. Local rows remain derived from
    /// <paramref name="record"/> exactly as before. This keeps historical
    /// per-call pricing and call counts out of <c>pipeline-execution.json</c>
    /// while making the task total reconcile with the ledger-backed Task tab.
    /// </summary>
    public static PipelineCostSummary SummarizeWithLedger(
        PipelineExecutionRecord? record,
        IReadOnlyDictionary<string, IReadOnlyList<TaskTokenCall>> ledgerCalls)
    {
        var baseline = Summarize(record);
        if (ledgerCalls.Count == 0) return baseline;

        var steps = baseline.Steps
            .Select(step => ledgerCalls.TryGetValue(step.StepId, out var calls) && calls.Count > 0
                ? CostFromLedger(step, calls)
                : step)
            .ToList();
        return new PipelineCostSummary(
            steps,
            steps.Sum(step => step.InputTokens),
            steps.Sum(step => step.OutputTokens),
            steps.Sum(step => step.CacheReadTokens),
            steps.Sum(step => step.CacheCreationTokens),
            steps.Sum(step => step.TotalTokens),
            Round(steps.Sum(step => step.InputCostUsd)),
            Round(steps.Sum(step => step.OutputCostUsd)),
            Round(steps.Sum(step => step.CacheReadCostUsd)),
            Round(steps.Sum(step => step.CacheCreationCostUsd)),
            Round(steps.Sum(step => step.CostUsd)),
            steps.Any(step => step.TotalTokens > 0 && !step.ModelKnown),
            steps.Any(step => step.TotalTokens > 0 && !step.ModelKnown) ? 1 : 0,
            MergePricingGaps(steps.SelectMany(step => step.PricingGaps), oneRun: true));
    }

    private static PipelineStepCost CostFromLedger(
        PipelineStepCost baseline,
        IReadOnlyList<TaskTokenCall> calls)
    {
        long input = 0;
        long output = 0;
        long cacheRead = 0;
        long cacheCreation = 0;
        decimal inputCost = 0;
        decimal outputCost = 0;
        decimal cacheReadCost = 0;
        decimal cacheCreationCost = 0;
        var allPriced = true;
        var pricingGaps = new List<PipelinePricingGap>();

        foreach (var call in calls)
        {
            input += call.InputTokens;
            output += call.OutputTokens;
            cacheRead += call.CacheReadTokens;
            cacheCreation += call.CacheCreationTokens;

            var estimate = TokenPricing.Estimate(
                call.Model,
                call.InputTokens,
                call.OutputTokens,
                call.CacheReadTokens,
                call.CacheCreationTokens,
                call.Ts);
            var priceResolved = call.ModelPriced || estimate.ModelKnown;
            allPriced &= priceResolved;
            if (!priceResolved)
            {
                pricingGaps.AddRange(PricingGapsFor(
                    estimate,
                    call.InputTokens + call.OutputTokens
                        + call.CacheReadTokens + call.CacheCreationTokens,
                    call.Model));
            }
            if (estimate.ModelKnown && estimate.Total > 0)
            {
                // Preserve a historical ledger amount when one was recorded.
                // If an older ledger row was unpriced but a newer catalogue
                // version now resolves the same run date, use that historical
                // catalogue estimate so rollout removes the missing-price state.
                var scale = call.ModelPriced
                    ? call.EstimatedApiCostUsd / estimate.Total
                    : 1m;
                inputCost += estimate.InputUsd * scale;
                outputCost += estimate.OutputUsd * scale;
                cacheReadCost += estimate.CacheReadUsd * scale;
                cacheCreationCost += estimate.CacheWriteUsd * scale;
                continue;
            }

            // The task ledger has already priced this historical call. If its
            // display model no longer resolves back to a catalogue id, preserve
            // the authoritative total and distribute it by token share so the
            // four visible components still sum exactly to the row.
            var tokens = call.InputTokens + call.OutputTokens
                + call.CacheReadTokens + call.CacheCreationTokens;
            if (call.ModelPriced && tokens > 0)
            {
                inputCost += call.EstimatedApiCostUsd * call.InputTokens / tokens;
                outputCost += call.EstimatedApiCostUsd * call.OutputTokens / tokens;
                cacheReadCost += call.EstimatedApiCostUsd * call.CacheReadTokens / tokens;
                cacheCreationCost += call.EstimatedApiCostUsd * call.CacheCreationTokens / tokens;
            }
        }

        return baseline with
        {
            TokenUsageSource =
                $"Remote token ledger · {calls.Count} call{(calls.Count == 1 ? "" : "s")}",
            ModelKnown = allPriced,
            PricingGaps = MergePricingGaps(pricingGaps, oneRun: true),
            InputTokens = input,
            OutputTokens = output,
            CacheReadTokens = cacheRead,
            CacheCreationTokens = cacheCreation,
            TotalTokens = input + output + cacheRead + cacheCreation,
            InputCostUsd = Round(inputCost),
            OutputCostUsd = Round(outputCost),
            CacheReadCostUsd = Round(cacheReadCost),
            CacheCreationCostUsd = Round(cacheCreationCost),
            CostUsd = Round(inputCost + outputCost + cacheReadCost + cacheCreationCost),
        };
    }

    /// <summary>
    /// Groups a task's recorded tokens by model, per run and across all runs.
    /// Runs are the current <paramref name="record"/> plus its flattened
    /// <see cref="PipelineExecutionRecord.PreviousAttempts"/>, ordered oldest
    /// first so the UI reads Run #1 -> latest top to bottom. Cost is computed
    /// on the per-model summed tokens (pricing is linear, so summing tokens
    /// then estimating equals estimating per step then summing). Steps with
    /// zero tokens are ignored; a null / empty model collapses to "unknown".
    /// </summary>
    public static PipelineModelUsageSummary SummarizeByModel(PipelineExecutionRecord? record)
    {
        if (record == null)
        {
            return new PipelineModelUsageSummary(
                Array.Empty<PipelineRunTokenUsage>(),
                Array.Empty<PipelineModelTokenUsage>(),
                0, 0m, false, 0, Array.Empty<PipelinePricingGap>(), 0);
        }

        // Oldest first: archived attempts (newest-first on disk) ascending by
        // attempt, then the live record last.
        var runs = new List<PipelineRunTokenUsage>();
        foreach (var prev in record.PreviousAttempts.OrderBy(p => p.Attempt))
        {
            runs.Add(BuildRun(prev, current: false));
        }
        // The live record still carries a CompletedAt stamp once the attempt
        // finished, so recency alone must not light the Current marker.
        runs.Add(BuildRun(record, current: record.CompletedAt == null));

        return BuildModelSummary(runs);
    }

    /// <summary>
    /// Projects the same chronological session rows as the task's RUNS badge,
    /// then attributes the canonical task token ledger into those run windows.
    /// This avoids treating a pipeline attempt as a CLI run and keeps missing
    /// telemetry distinct from a genuine recorded zero.
    /// </summary>
    public static PipelineModelUsageSummary SummarizeByModel(
        PipelineExecutionRecord? record,
        IReadOnlyList<SessionEvent> sessionEvents,
        TaskTokenSummary? taskTokens)
    {
        if (sessionEvents.Count == 0)
            return SummarizeByModel(record);

        var calls = CanonicalCalls(taskTokens);
        if (calls.Count == 0)
            calls = PipelineCalls(record);

        var callsByRun = Enumerable.Range(0, sessionEvents.Count)
            .Select(_ => new List<TaskTokenCall>())
            .ToList();
        foreach (var call in calls.OrderBy(call => call.Ts))
        {
            callsByRun[ResolveRunIndex(call.Ts, sessionEvents)].Add(call);
        }

        var runs = new List<PipelineRunTokenUsage>(sessionEvents.Count);
        for (var index = 0; index < sessionEvents.Count; index++)
        {
            var session = sessionEvents[index];
            var runCalls = callsByRun[index];
            var models = GroupCallsByModel(runCalls, session.Model, session.ThinkingLevel);
            runs.Add(new PipelineRunTokenUsage(
                Attempt: index + 1,
                Current: index == sessionEvents.Count - 1 && session.FinishedAt == null,
                StartedAt: session.Ts,
                CompletedAt: session.FinishedAt,
                Models: models,
                TotalTokens: models.Sum(model => model.TotalTokens),
                TotalCostUsd: Round(models.Sum(model => model.CostUsd)),
                AnyModelUnknown: models.Any(model => model.TotalTokens > 0 && !model.ModelKnown),
                PricingGaps: MergePricingGaps(
                    models.SelectMany(model => model.PricingGaps), oneRun: true),
                TokenUsageAvailable: runCalls.Count > 0));
        }

        return BuildModelSummary(runs);
    }

    private static PipelineModelUsageSummary BuildModelSummary(
        IReadOnlyList<PipelineRunTokenUsage> runs)
    {
        // Identity is (model, level): the lifetime rollup must not merge the
        // same model run at two reasoning levels into one row (AGT-2811).
        var totalByModel = runs
            .SelectMany(r => r.Models)
            .GroupBy(m => IdentityKey(m.Model, m.ThinkingLevel))
            .Select(g => new PipelineModelTokenUsage(
                g.First().Model,
                g.All(m => m.ModelKnown),
                g.Sum(m => m.UnpricedRuns),
                MergePricingGaps(g.SelectMany(m => m.PricingGaps)),
                g.Sum(m => m.Steps),
                g.Sum(m => m.InputTokens),
                g.Sum(m => m.OutputTokens),
                g.Sum(m => m.CacheReadTokens),
                g.Sum(m => m.CacheCreationTokens),
                g.Sum(m => m.TotalTokens),
                Round(g.Sum(m => m.CostUsd)),
                g.First().ThinkingLevel))
            .OrderByDescending(m => m.TotalTokens)
            .ThenBy(m => m.Model, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.ThinkingLevel ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToList();

        long totalTokens = totalByModel.Sum(m => m.TotalTokens);
        decimal totalCost = Round(totalByModel.Sum(m => m.CostUsd));
        bool anyUnknown = totalByModel.Any(m => m.TotalTokens > 0 && !m.ModelKnown);
        var unpricedRuns = runs.Count(run => run.AnyModelUnknown);
        var pricingGaps = MergePricingGaps(runs.SelectMany(run => run.PricingGaps));

        return new PipelineModelUsageSummary(
            runs,
            totalByModel,
            totalTokens,
            totalCost,
            anyUnknown,
            unpricedRuns,
            pricingGaps,
            runs.Count(run => !run.TokenUsageAvailable));
    }

    private static PipelineRunTokenUsage BuildRun(PipelineExecutionRecord run, bool current)
    {
        var models = GroupByModel(run.Steps, run.StartedAt);
        return new PipelineRunTokenUsage(
            Attempt: run.Attempt,
            Current: current,
            StartedAt: run.StartedAt,
            CompletedAt: run.CompletedAt,
            Models: models,
            TotalTokens: models.Sum(m => m.TotalTokens),
            TotalCostUsd: Round(models.Sum(m => m.CostUsd)),
            AnyModelUnknown: models.Any(m => m.TotalTokens > 0 && !m.ModelKnown),
            PricingGaps: MergePricingGaps(
                models.SelectMany(model => model.PricingGaps), oneRun: true),
            TokenUsageAvailable: true);
    }

    private static List<TaskTokenCall> CanonicalCalls(TaskTokenSummary? summary)
    {
        if (summary == null)
            return [];

        var calls = summary.Entries.ToList();
        var input = calls.Sum(call => call.InputTokens);
        var output = calls.Sum(call => call.OutputTokens);
        var cacheRead = calls.Sum(call => call.CacheReadTokens);
        var cacheCreation = calls.Sum(call => call.CacheCreationTokens);
        var residualInput = Math.Max(0, summary.InputTokens - input);
        var residualOutput = Math.Max(0, summary.OutputTokens - output);
        var residualCacheRead = Math.Max(0, summary.CacheReadTokens - cacheRead);
        var residualCacheCreation = Math.Max(0, summary.CacheCreationTokens - cacheCreation);
        var hasResidual = residualInput + residualOutput + residualCacheRead + residualCacheCreation > 0;
        var hasRecordedZero = calls.Count == 0 && summary.Calls > 0;
        if (hasResidual || hasRecordedZero)
        {
            calls.Add(new TaskTokenCall
            {
                Ts = summary.LastUpdate ?? default,
                Model = summary.LastModel,
                InputTokens = residualInput,
                OutputTokens = residualOutput,
                CacheReadTokens = residualCacheRead,
                CacheCreationTokens = residualCacheCreation,
                EstimatedApiCostUsd = Math.Max(
                    0m,
                    summary.EstimatedApiCostUsd - calls.Sum(call => call.EstimatedApiCostUsd)),
                ModelPriced = summary.AllModelsPriced,
            });
        }

        return calls;
    }

    private static List<TaskTokenCall> PipelineCalls(PipelineExecutionRecord? record)
    {
        if (record == null)
            return [];

        var records = record.PreviousAttempts
            .OrderBy(attempt => attempt.Attempt)
            .Append(record);
        return records
            .SelectMany(run => run.Steps.Select(step => (Run: run, Step: step)))
            .Where(item => item.Step.InputTokens + item.Step.OutputTokens
                + item.Step.CacheReadTokens + item.Step.CacheCreationTokens > 0)
            .Select(item => new TaskTokenCall
            {
                Ts = item.Step.StartedAt ?? item.Run.StartedAt,
                Model = item.Step.Model,
                ThinkingLevel = item.Step.ThinkingLevel,
                InputTokens = item.Step.InputTokens,
                OutputTokens = item.Step.OutputTokens,
                CacheReadTokens = item.Step.CacheReadTokens,
                CacheCreationTokens = item.Step.CacheCreationTokens,
            })
            .ToList();
    }

    private static int ResolveRunIndex(
        DateTime recordedAt,
        IReadOnlyList<SessionEvent> sessions)
    {
        if (recordedAt == default)
            return sessions.Count - 1;

        var index = 0;
        for (var candidate = 1; candidate < sessions.Count; candidate++)
        {
            if (recordedAt < sessions[candidate].Ts)
                break;
            index = candidate;
        }
        return index;
    }

    private static IReadOnlyList<PipelineModelTokenUsage> GroupCallsByModel(
        IReadOnlyList<TaskTokenCall> calls,
        string? runModel,
        string? runThinkingLevel = null)
    {
        var byModel = new List<PipelineModelTokenUsage>();
        var groups = calls
            .Select(call => (
                Call: call,
                Model: ResolveCallModel(call, runModel),
                Level: ResolveCallThinkingLevel(call, runModel, runThinkingLevel)))
            .GroupBy(item => IdentityKey(item.Model, item.Level));

        foreach (var group in groups)
        {
            long input = 0;
            long output = 0;
            long cacheRead = 0;
            long cacheCreation = 0;
            decimal cost = 0m;
            var modelKnown = true;
            var gaps = new List<PipelinePricingGap>();
            var model = group.First().Model;
            foreach (var (call, _, _) in group)
            {
                input += call.InputTokens;
                output += call.OutputTokens;
                cacheRead += call.CacheReadTokens;
                cacheCreation += call.CacheCreationTokens;
                var tokens = call.InputTokens + call.OutputTokens
                    + call.CacheReadTokens + call.CacheCreationTokens;
                var estimate = TokenPricing.Estimate(
                    model,
                    call.InputTokens,
                    call.OutputTokens,
                    call.CacheReadTokens,
                    call.CacheCreationTokens,
                    call.Ts == default ? null : call.Ts);
                var priceResolved = tokens == 0 || call.ModelPriced || estimate.ModelKnown;
                modelKnown &= priceResolved;
                if (!priceResolved)
                    gaps.AddRange(PricingGapsFor(estimate, tokens, model));
                cost += call.ModelPriced ? call.EstimatedApiCostUsd : estimate.Total;
            }

            byModel.Add(new PipelineModelTokenUsage(
                Model: model,
                ModelKnown: modelKnown,
                UnpricedRuns: modelKnown ? 0 : 1,
                PricingGaps: MergePricingGaps(gaps, oneRun: true),
                Steps: group.Count(),
                InputTokens: input,
                OutputTokens: output,
                CacheReadTokens: cacheRead,
                CacheCreationTokens: cacheCreation,
                TotalTokens: input + output + cacheRead + cacheCreation,
                CostUsd: Round(cost),
                ThinkingLevel: group.First().Level));
        }

        return byModel
            .OrderByDescending(model => model.TotalTokens)
            .ThenBy(model => model.Model, StringComparer.OrdinalIgnoreCase)
            .ThenBy(model => model.ThinkingLevel ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Canonical model id for a ledger call, falling back to the run's recorded model.</summary>
    private static string ResolveCallModel(TaskTokenCall call, string? runModel)
        => string.IsNullOrWhiteSpace(call.Model)
            ? string.IsNullOrWhiteSpace(runModel) ? "unknown" : runModel.Trim()
            : call.Model.Trim();

    /// <summary>
    /// The reasoning level for one ledger call. Prefers the level recorded on
    /// the call itself. Falls back to the run's own recorded level only when
    /// the call ran on the run's recorded model - that pair is what the
    /// run-start session event persisted, so it is recorded data rather than a
    /// guess. Every other case stays null ("level unknown").
    /// </summary>
    private static string? ResolveCallThinkingLevel(
        TaskTokenCall call,
        string? runModel,
        string? runThinkingLevel)
    {
        if (!string.IsNullOrWhiteSpace(call.ThinkingLevel)) return call.ThinkingLevel.Trim();
        if (string.IsNullOrWhiteSpace(runThinkingLevel)) return null;
        var model = ResolveCallModel(call, runModel);
        return string.Equals(model, runModel?.Trim(), StringComparison.OrdinalIgnoreCase)
            ? runThinkingLevel.Trim()
            : null;
    }

    /// <summary>
    /// Case-insensitive grouping key for one (model, reasoning level) identity.
    /// A missing level is its own bucket, never merged into a levelled one.
    /// </summary>
    private static string IdentityKey(string model, string? thinkingLevel)
        => $"{model.Trim().ToLowerInvariant()}\u0001{thinkingLevel?.Trim().ToLowerInvariant() ?? string.Empty}";

    // Sum a flat list of steps into per-model rows, busiest model first.
    private static IReadOnlyList<PipelineModelTokenUsage> GroupByModel(
        IEnumerable<PipelineStepExecution> steps,
        DateTime recordedAt)
    {
        var byModel = new List<PipelineModelTokenUsage>();
        // Steps already record their own reasoning level, so the (model, level)
        // identity comes straight out of the execution record (AGT-2811).
        var groups = steps
            .Where(s => s.InputTokens + s.OutputTokens + s.CacheReadTokens + s.CacheCreationTokens > 0)
            .Select(s => (
                Step: s,
                Model: string.IsNullOrWhiteSpace(s.Model) ? "unknown" : s.Model!.Trim(),
                Level: string.IsNullOrWhiteSpace(s.ThinkingLevel) ? null : s.ThinkingLevel!.Trim()))
            .GroupBy(item => IdentityKey(item.Model, item.Level));

        foreach (var g in groups)
        {
            var model = g.First().Model;
            long input = g.Sum(item => item.Step.InputTokens);
            long output = g.Sum(item => item.Step.OutputTokens);
            long cacheRead = g.Sum(item => item.Step.CacheReadTokens);
            long cacheCreation = g.Sum(item => item.Step.CacheCreationTokens);
            var est = TokenPricing.Estimate(model, input, output, cacheRead, cacheCreation, recordedAt);

            byModel.Add(new PipelineModelTokenUsage(
                Model: model,
                ModelKnown: est.ModelKnown,
                UnpricedRuns: est.ModelKnown ? 0 : 1,
                PricingGaps: PricingGapsFor(
                    est, input + output + cacheRead + cacheCreation, model),
                Steps: g.Count(),
                InputTokens: input,
                OutputTokens: output,
                CacheReadTokens: cacheRead,
                CacheCreationTokens: cacheCreation,
                TotalTokens: input + output + cacheRead + cacheCreation,
                CostUsd: Round(est.Total),
                ThinkingLevel: g.First().Level));
        }

        return byModel
            .OrderByDescending(m => m.TotalTokens)
            .ThenBy(m => m.Model, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<PipelinePricingGap> PricingGapsFor(
        TokenCostEstimate estimate,
        long totalTokens,
        string? displayModel)
    {
        if (totalTokens <= 0 || estimate.ModelKnown)
            return Array.Empty<PipelinePricingGap>();
        var modelId = !string.IsNullOrWhiteSpace(displayModel)
            ? displayModel.Trim()
            : string.IsNullOrWhiteSpace(estimate.ModelId) ? "unknown" : estimate.ModelId.Trim();
        return [new PipelinePricingGap(modelId, estimate.Status.ToString(), 1)];
    }

    private static IReadOnlyList<PipelinePricingGap> MergePricingGaps(
        IEnumerable<PipelinePricingGap> gaps,
        bool oneRun = false)
        => gaps
            .GroupBy(gap => (gap.ModelId, gap.Reason))
            .Select(group => new PipelinePricingGap(
                group.Key.ModelId,
                group.Key.Reason,
                oneRun ? 1 : group.Sum(gap => gap.AffectedRuns)))
            .OrderBy(gap => gap.ModelId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(gap => gap.Reason, StringComparer.Ordinal)
            .ToList();

    // Costs are fractions of a cent for a single task; keep 6 dp so the
    // sub-cent detail survives the round-trip and the UI decides display
    // precision.
    private static decimal Round(decimal value) => Math.Round(value, 6, MidpointRounding.AwayFromZero);
}
