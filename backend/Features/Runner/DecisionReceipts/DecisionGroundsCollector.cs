using System.Globalization;
using System.Text.Json;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.Tokens;

namespace AgentStudio.Runner;

/// <summary>The settled coding completion as the host observed it.</summary>
public sealed record CodingCompletionObservation(
    string OccurrenceId,
    TaskInfo Task,
    string AttemptId,
    long Fence,
    long Epoch,
    string Outcome,
    string? TargetState,
    string? ResultSha,
    string? BaseSha,
    string? ResultRef,
    bool HasSettledEnvelope,
    TransitionDecision Deterministic,
    DateTime ObservedAt);

/// <summary>
/// Host side of AGT-W73 OD-S1: collects typed facts with source references
/// for the coding-completion shadow profile and freezes them with the
/// occurrence. It reads stores the monolith already owns (attempt authority,
/// the card token receipt, earlier decision receipts) and contains no
/// transition rule; the engine freezes and decides from this input.
/// </summary>
public sealed class DecisionGroundsCollector(
    IConfiguration configuration,
    ITokenPricing pricing,
    AttemptAuthorityService authority,
    MonolithOrchestrationStore orchestration,
    OrchestratorDecisionStore decisions)
{
    public const string Profile = "CodingCompletion";

    public DecisionShadowInputV1 FreezeCodingCompletion(CodingCompletionObservation observed)
    {
        var choice = DecisionGroundsPolicy.CodingCompletionChoice(observed.Deterministic);
        return decisions.FreezeInputs(new DecisionShadowInputV1(CodingCompletion(observed),
            choice.Action, choice.Target, observed.Deterministic.Reason));
    }

    public DecisionGroundsInputV1 CodingCompletion(CodingCompletionObservation observed)
    {
        var task = observed.Task;
        var taskKey = task.TaskKey ?? task.Id;
        var projection = authority.GetTaskProjection(taskKey);
        var accepted = projection.ReviewAttempts
            .Where(review => review.Outcome is ReviewTerminalOutcome.Pass or ReviewTerminalOutcome.IntegrationBranchDefect)
            .OrderBy(review => review.TerminalAt ?? review.CreatedAt).ToArray();
        var verifiedTrees = accepted.Select(review => review.TestedResultSha ?? review.Subject.ExpectedResultSha)
            .Where(sha => !string.IsNullOrWhiteSpace(sha)).Select(sha => sha!).Distinct().ToArray();
        var acceptedRuns = accepted.Select(review => review.SourceRunAttemptId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rounds = projection.RunAttempts.Select(run => new RunLedgerRoundInputV1(
            run.AttemptId, run.LastFence, run.CreatedAt, run.TerminalAt, run.TerminalOutcome,
            run.ResultEnvelope?.BaseSha, run.ResultSha ?? run.ResultEnvelope?.ResultSha,
            acceptedRuns.Contains(run.AttemptId),
            IsExhaustion(run.TerminalOutcome) || IsExhaustion(run.TerminalReason))).ToArray();
        var rows = (task.TokenSummary ?? PersistedTokenSummary(task.FolderPath))?.Entries
            .Select(LedgerRow).ToArray() ?? [];
        var ledger = CardRunLedgerPolicy.Build(rows, rounds, verifiedTrees, (row, usage) =>
        {
            var estimate = pricing.Estimate(row.Model, usage.UncachedInput, usage.Output,
                usage.CachedInput, usage.CacheWrite, row.At);
            return estimate.ModelKnown ? new(DecisionPriceStatuses.Priced, estimate.Total)
                : new(estimate.Status == TokenEconomy.PriceStatus.NoPriceForDate
                    ? DecisionPriceStatuses.NoPriceForDate : DecisionPriceStatuses.UnknownModel, null);
        });

        var at = observed.ObservedAt;
        var attemptRef = new GroundsSourceRefV1("run-attempt", $"attempt:{observed.AttemptId}");
        var envelopeRef = new GroundsSourceRefV1("result-envelope", $"attempt:{observed.AttemptId}/envelope",
            observed.ResultSha);
        var ledgerRef = new GroundsSourceRefV1("token-receipt", $"task:{task.Id}/token-summary");
        var authorityRef = new GroundsSourceRefV1("attempt-authority", $"task:{taskKey}/attempts");
        var priceReliability = ledger.Ledger.PriceStatus == DecisionPriceStatuses.Priced
            ? GroundsReliability.Estimated : GroundsReliability.Unreliable;
        var lastReview = projection.ReviewAttempts.Where(review => review.Outcome is not null)
            .OrderBy(review => review.TerminalAt ?? review.CreatedAt).LastOrDefault();
        var facts = new List<GroundsFactV1>
        {
            Fact("run.outcome", "truth", "Typed run outcome", observed.Outcome, null, attemptRef, at, required: true),
            Fact("attempt.fence", "truth", "Attempt fence", observed.Fence.ToString(CultureInfo.InvariantCulture),
                null, attemptRef, at, required: true),
            Fact("attempt.epoch", "truth", "Authority epoch", observed.Epoch.ToString(CultureInfo.InvariantCulture),
                null, attemptRef, at, required: true),
            Fact("card.lane", "truth", "Current card lane", task.State, null,
                new("task", $"task:{task.Id}"), at, required: true),
            Fact("transition.proposed-next", "truth", "Lane the deterministic decision moves to",
                observed.TargetState ?? "unchanged", null, attemptRef, at, required: true),
            Fact("result.sha", "truth", "Result tree SHA", observed.ResultSha ?? "missing", null, envelopeRef, at,
                required: true, reliability: observed.ResultSha is null ? GroundsReliability.Unreliable : null),
            Fact("result.base-sha", "truth", "Start tree SHA", observed.BaseSha ?? "missing", null, envelopeRef, at,
                reliability: observed.BaseSha is null ? GroundsReliability.Unreliable : null),
            Fact("result.envelope", "truth", "Settled immutable result envelope",
                observed.HasSettledEnvelope ? "settled" : "missing", null, envelopeRef, at, required: true),
            Fact("result.ref", "truth", "Immutable result ref", observed.ResultRef ?? "missing", null, envelopeRef, at),
            Fact("review.last-outcome", "quality", "Latest review outcome",
                lastReview?.Outcome?.ToString() ?? "none", null,
                lastReview is null ? authorityRef : new("review-attempt", $"review:{lastReview.AttemptId}"),
                lastReview?.TerminalAt ?? at),
            Fact("ledger.tokens", "consumption", "Deduplicated card tokens",
                ledger.Ledger.Tokens.Total.ToString(CultureInfo.InvariantCulture), "tokens", ledgerRef, at),
            Fact("ledger.priced-usd", "consumption", "Priced card subtotal",
                ledger.Ledger.PricedSubtotalUsd.ToString("0.0000", CultureInfo.InvariantCulture), "USD",
                ledgerRef, at, reliability: priceReliability),
            Fact("ledger.price-status", "consumption", "Card price status", ledger.Ledger.PriceStatus, null,
                ledgerRef, at),
            Fact("ledger.unpriced-rows", "consumption", "Rows without catalog price",
                ledger.Ledger.UnpricedRows.ToString(CultureInfo.InvariantCulture), "rows", ledgerRef, at),
            Fact("ledger.excluded-duplicates", "consumption", "Duplicate usage rows excluded",
                ledger.Ledger.ExcludedDuplicateRows.ToString(CultureInfo.InvariantCulture), "rows", ledgerRef, at),
            Fact("progress.rounds-without-progress", "progress", "Trailing rounds without progress",
                ledger.Progress.RoundsWithoutProgress.ToString(CultureInfo.InvariantCulture), "rounds",
                authorityRef, at),
            Fact("progress.exhaustions", "progress", "Run-limit exhaustions in this delivery",
                ledger.Progress.ExhaustionsInDelivery.ToString(CultureInfo.InvariantCulture), "rounds",
                authorityRef, at),
            Fact("progress.last-verified-tree", "progress", "Last verified tree SHA",
                ledger.Progress.LastVerifiedTreeSha ?? "none", null, authorityRef, at),
        };

        var prior = decisions.ReceiptsForTask(task.Id)
            .Where(receipt => receipt.OccurrenceId != observed.OccurrenceId)
            .Select(receipt => new GroundsPriorDecisionV1(receipt.ReceiptId, receipt.OccurrenceId,
                receipt.Chosen.FirstOrDefault()?.Action ?? DecisionActionKinds.Ask, receipt.Prediction,
                orchestration.GetOccurrence(receipt.OccurrenceId)?.MonolithReceipt?.Action is { } applied
                    ? "applied:" + applied : null,
                receipt.EndedAt)).ToArray();

        return new DecisionGroundsInputV1(
            observed.OccurrenceId,
            new GroundsTriggerV1("step-transition", Profile, at),
            new GroundsScopeV1("card", task.ProjectName, task.Id, observed.AttemptId, observed.Fence,
                observed.Epoch, null),
            "core",
            observed.TargetState,
            $"orchestration-decisions:{OrchestrationRolloutStore.LibraryVersion}/{DecisionReceiptPolicy.PromptVersion}",
            Route(configuration),
            facts,
            DecisionGroundsPolicy.CodingCompletionAlternatives(observed.Deterministic, observed.HasSettledEnvelope),
            prior,
            ledger.Ledger,
            ledger.Progress,
            [
                new("quota.window-head", "Quota headroom is not collected for the OD-S1 shadow profile."),
                new("host.load-band", "Host load and slot occupancy are not collected for the OD-S1 shadow profile."),
                new("result.diff-summary", "The changed-path summary is not part of the settled completion request."),
                new("test.evidence", "Test evidence arrives with the review; the latest review outcome stands in."),
            ]);
    }

    /// <summary>
    /// Configured deciding route. Runner-host custody is the default (AGT-W63);
    /// an engine credential is a separate follow-up decision.
    /// </summary>
    public static DecisionModelRouteV1 Route(IConfiguration configuration)
    {
        var section = configuration.GetSection("Orchestration:DecisionModel");
        return new(section["Cli"] ?? "codex", section["Model"] ?? "gpt-6-sol",
            section["ThinkingLevel"] ?? "medium", DecisionModelCustody.RunnerHostJob,
            Blank(section["AlternateCli"]), Blank(section["AlternateModel"]), Blank(section["AlternateThinkingLevel"]));
    }

    internal static RunLedgerRowV1 LedgerRow(TaskTokenCall call)
    {
        var participant = call.ParticipantId;
        var usage = TokenLedgerDuplicates.UsageIdentity(call);
        string? runId = null;
        if (participant?.StartsWith(TokenUsageHost.RemoteRunnerParticipantPrefix, StringComparison.Ordinal) == true)
        {
            runId = participant[TokenUsageHost.RemoteRunnerParticipantPrefix.Length..];
            var marker = runId.IndexOf(":usage:", StringComparison.Ordinal);
            if (marker >= 0) runId = runId[..marker];
        }
        // Rows written before input semantics were recorded may still carry a
        // raw OpenAI total; StoredUsageNormalization owns that recognition.
        var legacyRaw = !ReferenceEquals(StoredUsageNormalization.Normalize(call), call);
        return new(usage, runId, call.Ts, call.Model, call.ThinkingLevel, call.InputTokens,
            call.CacheReadTokens, call.CacheCreationTokens, call.OutputTokens, legacyRaw, participant);
    }

    /// <summary>
    /// The scanned card does not carry its token receipt; the remote receipt
    /// writer persists it in task.json under <c>tokenSummary</c>.
    /// </summary>
    private static TaskTokenSummary? PersistedTokenSummary(string? folderPath)
    {
        var path = string.IsNullOrWhiteSpace(folderPath) ? null : Path.Combine(folderPath, "task.json");
        if (path is null || !File.Exists(path)) return null;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.TryGetProperty("tokenSummary", out var summary)
            && summary.ValueKind == JsonValueKind.Object
            ? summary.Deserialize<TaskTokenSummary>(TaskJsonFile.ReadOpts) : null;
    }

    private static GroundsFactV1 Fact(string id, string block, string name, string value, string? unit,
        GroundsSourceRefV1 source, DateTime observedAt, bool required = false, string? reliability = null)
        => new(id, block, name, value, unit, source, observedAt, "complete",
            reliability ?? GroundsReliability.Reliable, required);

    private static bool IsExhaustion(string? value)
        => value?.Contains("timeout", StringComparison.OrdinalIgnoreCase) == true
            || value?.Contains("budget", StringComparison.OrdinalIgnoreCase) == true;

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
