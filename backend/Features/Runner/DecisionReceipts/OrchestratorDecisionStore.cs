using System.Text.Json;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.Tokens;

namespace AgentStudio.Runner;

/// <summary>
/// Durable AGT-W73 OD-S1 records under <c>&lt;TaskRepository&gt;/.orchestrator-decisions</c>:
/// the grounds input frozen when an occurrence is created, the claimable
/// shadow model jobs that runner hosts execute with their own provider
/// credentials, and one immutable decision receipt per occurrence. The first
/// stored input and receipt of an occurrence win; replays return them.
/// </summary>
public sealed class OrchestratorDecisionStore
{
    public const int MaxCallsPerCard = DecisionCostCapPolicy.MaxCallsPerCard;
    public const decimal MaxPricedUsdPerCard = DecisionCostCapPolicy.MaxPricedUsdPerCard;
    public const string SupersededByReceipt = "superseded-by-receipt";
    public static readonly TimeSpan ClaimTtl = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions Json = DecisionGroundsPolicy.Json;
    private readonly object _gate = new();
    private readonly string? _directory;
    private readonly TimeProvider _time;
    private readonly ITokenPricing _pricing;
    private readonly Dictionary<string, DecisionShadowInputV1> _inputs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JobRecord> _jobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OrchestratorDecisionReceiptV1> _receipts = new(StringComparer.Ordinal);

    public OrchestratorDecisionStore(IConfiguration configuration, TimeProvider time, ITokenPricing pricing)
        : this(string.IsNullOrWhiteSpace(configuration["TaskRepository"]) ? null
            : Path.Combine(configuration["TaskRepository"]!, ".orchestrator-decisions"), time, pricing) { }

    /// <param name="directory">Null keeps the records in memory, like the occurrence journal.</param>
    public OrchestratorDecisionStore(string? directory, TimeProvider time, ITokenPricing pricing)
    {
        _directory = directory;
        _time = time;
        _pricing = pricing;
        if (directory is null) return;
        foreach (var input in Load<DecisionShadowInputV1>("inputs")) _inputs[input.Grounds.OccurrenceId] = input;
        foreach (var job in Load<JobRecord>("jobs")) _jobs[job.Dto.JobId] = job;
        foreach (var receipt in Load<OrchestratorDecisionReceiptV1>("receipts")) _receipts[receipt.OccurrenceId] = receipt;
    }

    public DecisionShadowInputV1 FreezeInputs(DecisionShadowInputV1 input)
    {
        var occurrenceId = input.Grounds.OccurrenceId;
        ArgumentException.ThrowIfNullOrWhiteSpace(occurrenceId);
        lock (_gate)
        {
            if (_inputs.TryGetValue(occurrenceId, out var existing)) return existing;
            Save("inputs", occurrenceId, input);
            _inputs.Add(occurrenceId, input);
            return input;
        }
    }

    public DecisionShadowInputV1? GetInputs(string occurrenceId)
    {
        lock (_gate) return _inputs.GetValueOrDefault(occurrenceId);
    }

    /// <summary>Occurrences whose frozen grounds still wait for a receipt, oldest first.</summary>
    public IReadOnlyList<string> PendingReceipts(int limit)
    {
        lock (_gate) return _inputs.Values.Where(input => !_receipts.ContainsKey(input.Grounds.OccurrenceId))
            .OrderBy(input => input.Grounds.Trigger.ObservedAt)
            .ThenBy(input => input.Grounds.OccurrenceId, StringComparer.Ordinal)
            .Take(Math.Clamp(limit, 1, 100)).Select(input => input.Grounds.OccurrenceId).ToArray();
    }

    public OrchestratorDecisionJobDto CreateJob(OrchestratorDecisionJobRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Cli);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        if (request.Attempt is < 1 or > 2) throw new ArgumentException("A decision allows one primary and one alternate attempt.");
        lock (_gate)
        {
            var input = _inputs.GetValueOrDefault(request.OccurrenceId)?.Grounds
                ?? throw new KeyNotFoundException("No decision grounds were frozen for this occurrence.");
            // An occurrence without a card has null scope ids; the engine sends them as empty strings.
            if ((input.Scope.TaskId ?? string.Empty) != request.TaskId
                || (input.Scope.ProjectId ?? string.Empty) != request.ProjectId)
                throw new InvalidOperationException("decision-job-scope-mismatch");
            var existing = _jobs.Values.FirstOrDefault(job => job.Dto.OccurrenceId == request.OccurrenceId
                && job.Dto.Attempt == request.Attempt);
            if (existing is not null)
            {
                if (existing.GroundsDigest != request.GroundsDigest || existing.Dto.Model != request.Model
                    || existing.Dto.Cli != request.Cli)
                    throw new InvalidOperationException("decision-job-idempotency-conflict");
                return existing.Dto;
            }
            var now = _time.GetUtcNow().UtcDateTime;
            var (maxInput, maxOutput) = DecisionCostCapPolicy.Envelope(request.MaxInputTokens, request.MaxOutputTokens);
            var estimatedUsd = EstimatedEnvelopePrice(request.Model, maxInput, maxOutput, now);
            var capReason = DecisionCostCapPolicy.CallEnvelopeReason(maxInput, maxOutput);
            if (capReason is null && DecisionCostCapPolicy.ChargesCard(input.Scope))
                capReason = DecisionCostCapPolicy.CardReason(
                    Spend(_jobs.Values.Where(job => job.Dto.TaskId == request.TaskId)), estimatedUsd);
            else if (capReason is null)
            {
                var day = DecisionCostCapPolicy.HostDayKey(input.Scope, now);
                var hostDay = _jobs.Values.Where(job => _inputs.GetValueOrDefault(job.Dto.OccurrenceId)?.Grounds is { } grounds
                    && !DecisionCostCapPolicy.ChargesCard(grounds.Scope)
                    && DecisionCostCapPolicy.HostDayKey(grounds.Scope, job.Dto.CreatedAt) == day).ToArray();
                bool Scheduled(JobRecord job) => DecisionCostCapPolicy.IsScheduled(_inputs[job.Dto.OccurrenceId].Grounds.Trigger);
                capReason = DecisionCostCapPolicy.HostDayReason(Spend(hostDay.Where(Scheduled)),
                    Spend(hostDay.Where(job => !Scheduled(job))), DecisionCostCapPolicy.IsScheduled(input.Trigger),
                    maxInput, maxOutput, estimatedUsd);
            }
            var record = new JobRecord
            {
                Dto = new($"odj_{Guid.NewGuid():N}", request.OccurrenceId, request.ProjectId, request.TaskId,
                    request.Attempt, capReason is null ? DecisionJobStatuses.Pending : DecisionJobStatuses.CapExhausted,
                    input.ModelRoute.Custody, request.Cli, request.Model, request.ThinkingLevel, now, null, null,
                    null, capReason),
                GroundsDigest = request.GroundsDigest,
                Prompt = request.Prompt,
                MaxInputTokens = maxInput,
                MaxOutputTokens = maxOutput,
            };
            Save("jobs", record.Dto.JobId, record);
            _jobs.Add(record.Dto.JobId, record);
            return record.Dto;
        }
    }

    public OrchestratorDecisionJobDto? GetJob(string jobId)
    {
        lock (_gate) return _jobs.GetValueOrDefault(jobId)?.Dto;
    }

    public OrchestratorDecisionJobClaimResponse Claim(OrchestratorDecisionJobClaimRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RunnerId);
        lock (_gate)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            var next = _jobs.Values
                .Where(job => job.Dto.Status == DecisionJobStatuses.Pending
                    || job.Dto.Status == DecisionJobStatuses.Claimed && job.ClaimExpiresAt <= now)
                .OrderBy(job => job.Dto.CreatedAt).ThenBy(job => job.Dto.JobId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (next is null) return new(DecisionJobClaimStatuses.Empty);
            if (next.ClaimToken is not null && next.Dto.RunnerId is not null)
                next.ReplacedClaims.Add(new() { ClaimToken = next.ClaimToken, RunnerId = next.Dto.RunnerId });
            next.ClaimToken = Guid.NewGuid().ToString("N");
            next.ClaimExpiresAt = now + ClaimTtl;
            next.Dto = next.Dto with { Status = DecisionJobStatuses.Claimed, ClaimedAt = now,
                RunnerId = request.RunnerId };
            Save("jobs", next.Dto.JobId, next);
            return new(DecisionJobClaimStatuses.Claimed, new(next.Dto.JobId, next.ClaimToken, next.Prompt,
                next.Dto.Cli, next.Dto.Model, next.Dto.ThinkingLevel, (int)ClaimTtl.TotalSeconds - 60,
                next.ClaimExpiresAt.Value, Envelope(next).Input, Envelope(next).Output));
        }
    }

    /// <summary>
    /// False for a stale claim. A repeated completion of the same claim is
    /// accepted once. A completion that arrives after a receipt closed the
    /// claimed job still records its usage and price, so spend is not lost.
    /// </summary>
    public bool Complete(OrchestratorDecisionJobCompletion completion)
    {
        lock (_gate)
        {
            var job = _jobs.GetValueOrDefault(completion.JobId);
            if (job is null)
                return false;
            if (job.ClaimToken != completion.ClaimToken || job.Dto.RunnerId != completion.RunnerId)
            {
                var replaced = job.ReplacedClaims.FirstOrDefault(claim => claim.ClaimToken == completion.ClaimToken
                    && claim.RunnerId == completion.RunnerId);
                if (replaced is null) return false;
                if (replaced.Result is not null) return true;
                var oldUsage = CardRunLedgerPolicy.Deduplicate(completion.Usage);
                if (oldUsage.Tokens.Total == 0) return false;
                var oldModel = string.IsNullOrWhiteSpace(completion.Model) ? job.Dto.Model : completion.Model.Trim();
                replaced.Result = new(false, completion.ResponseText, job.Dto.Cli, oldModel,
                    job.Dto.ThinkingLevel, oldUsage.Tokens, Price(oldModel, oldUsage.Tokens, completion.EndedAt),
                    oldUsage.ProviderCallIds, oldUsage.ExcludedDuplicateFrames, completion.StartedAt,
                    completion.EndedAt, completion.RunnerId, "replaced-claim", completion.TransportFailure);
                Save("jobs", job.Dto.JobId, job);
                return true;
            }
            var late = job.Dto.Status == DecisionJobStatuses.Failed && job.Dto.Error == SupersededByReceipt
                && job.Dto.Result is null;
            if (DecisionJobStatuses.IsTerminal(job.Dto.Status) && !late) return true;
            if (job.Dto.Status != DecisionJobStatuses.Claimed && !late) return false;
            var usage = CardRunLedgerPolicy.Deduplicate(completion.Usage);
            var model = string.IsNullOrWhiteSpace(completion.Model) ? job.Dto.Model : completion.Model.Trim();
            var (maxInput, maxOutput) = Envelope(job);
            var overrun = DecisionCostCapPolicy.MeasuredOverrunReason(usage.Tokens, maxInput, maxOutput);
            var success = completion.Success && !string.IsNullOrWhiteSpace(completion.ResponseText) && overrun is null;
            var error = success ? null : overrun ?? completion.Error ?? "empty-response";
            job.Dto = job.Dto with
            {
                Status = success && !late ? DecisionJobStatuses.Completed : DecisionJobStatuses.Failed,
                Error = late ? SupersededByReceipt : error,
                Result = new(success && !late, completion.ResponseText, job.Dto.Cli, model, job.Dto.ThinkingLevel,
                    usage.Tokens, Price(model, usage.Tokens, completion.EndedAt), usage.ProviderCallIds,
                    usage.ExcludedDuplicateFrames, completion.StartedAt, completion.EndedAt,
                    completion.RunnerId, error, completion.TransportFailure),
            };
            job.LateAfterReceipt = late;
            Save("jobs", job.Dto.JobId, job);
            return true;
        }
    }

    /// <summary>Billed calls of an occurrence that completed after its receipt was stored.</summary>
    public IReadOnlyList<DecisionLateCallV1> LateCalls(string occurrenceId)
    {
        lock (_gate) return LateCallsLocked(occurrenceId);
    }

    private IReadOnlyList<DecisionLateCallV1> LateCallsLocked(string occurrenceId)
        => _jobs.Values.Where(job => job.Dto.OccurrenceId == occurrenceId)
            .SelectMany(job => job.ReplacedClaims.Where(claim => claim.Result is not null)
                .Select(claim => (job.Dto.JobId, Result: claim.Result!))
                .Concat(job.LateAfterReceipt && job.Dto.Result is not null
                    ? [(job.Dto.JobId, job.Dto.Result)] : []))
            .OrderBy(item => item.Result.EndedAt).ThenBy(item => item.JobId, StringComparer.Ordinal)
            .Select(item => new DecisionLateCallV1(item.JobId, item.Result.HostId, item.Result.Model,
                item.Result.Tokens, item.Result.Price, item.Result.ProviderCallIds,
                item.Result.EndedAt, item.Result.Error))
            .ToArray();

    /// <summary>
    /// Stores the receipt once per occurrence after checking that it was built
    /// from the frozen input. A replayed or late duplicate returns the stored one.
    /// </summary>
    public OrchestratorDecisionReceiptV1 PutReceipt(OrchestratorDecisionReceiptV1 receipt)
    {
        lock (_gate)
        {
            if (_receipts.TryGetValue(receipt.OccurrenceId, out var stored)) return stored;
            var input = _inputs.GetValueOrDefault(receipt.OccurrenceId)?.Grounds
                ?? throw new KeyNotFoundException("No decision grounds were frozen for this occurrence.");
            var expected = DecisionGroundsPolicy.Freeze(input).ContentDigest;
            if (receipt.GroundsDigest != expected || receipt.Grounds.ContentDigest != expected
                || !DecisionGroundsPolicy.IsIntact(receipt.Grounds))
                throw new InvalidOperationException("decision-receipt-grounds-mismatch");
            if (receipt.ReceiptId != DecisionReceiptPolicy.ReceiptId(receipt.OccurrenceId, expected)
                || receipt.EvidenceRef != DecisionReceiptPolicy.EvidenceRef(receipt.ReceiptId))
                throw new InvalidOperationException("decision-receipt-identity-mismatch");
            Save("receipts", receipt.ReceiptId, receipt);
            _receipts.Add(receipt.OccurrenceId, receipt);
            // A receipt written after an expired wait closes the occurrence; an
            // unclaimed or still running job must not spend a later call.
            foreach (var job in _jobs.Values.Where(job => job.Dto.OccurrenceId == receipt.OccurrenceId
                         && !DecisionJobStatuses.IsTerminal(job.Dto.Status)))
            {
                job.Dto = job.Dto with { Status = DecisionJobStatuses.Failed, Error = SupersededByReceipt };
                Save("jobs", job.Dto.JobId, job);
            }
            return receipt;
        }
    }

    public OrchestratorDecisionReceiptV1? GetReceipt(string receiptId)
    {
        lock (_gate) return _receipts.Values.FirstOrDefault(item => item.ReceiptId == receiptId);
    }

    public IReadOnlyList<OrchestratorDecisionReceiptV1> ReceiptsForTask(string taskId)
    {
        lock (_gate) return _receipts.Values.Where(item => item.Scope.TaskId == taskId)
            .OrderBy(item => item.EndedAt).ThenBy(item => item.ReceiptId, StringComparer.Ordinal).ToArray();
    }

    public IReadOnlyList<OrchestratorDecisionReceiptSummary> ListReceipts(string? taskId, int limit)
    {
        lock (_gate) return _receipts.Values
            .Where(item => taskId is null || item.Scope.TaskId == taskId)
            .OrderByDescending(item => item.EndedAt).ThenBy(item => item.ReceiptId, StringComparer.Ordinal)
            .Take(Math.Clamp(limit, 1, 100))
            .Select(item => Summary(item, LateCallsLocked(item.OccurrenceId))).ToArray();
    }

    public OrchestratorDecisionReceiptSummary Summary(OrchestratorDecisionReceiptV1 receipt)
    {
        lock (_gate) return Summary(receipt, LateCallsLocked(receipt.OccurrenceId));
    }

    /// <summary>Spend columns add late calls to the immutable receipt call; unknown prices stay explicit.</summary>
    public static OrchestratorDecisionReceiptSummary Summary(OrchestratorDecisionReceiptV1 receipt,
        IReadOnlyList<DecisionLateCallV1> lateCalls)
    {
        var prices = lateCalls.Select(call => call.Price).Prepend(receipt.Call.Price)
            .Where(price => price.Status != DecisionPriceStatuses.NoUsage).ToArray();
        var priceStatus = prices.Length == 0 ? receipt.Call.Price.Status
            : prices.All(price => price.Status == DecisionPriceStatuses.Priced) ? DecisionPriceStatuses.Priced
            : prices.Any(price => price.Status == DecisionPriceStatuses.Priced) ? DecisionPriceStatuses.Partial
            : prices[0].Status;
        decimal? usd = prices.Any(price => price.Status == DecisionPriceStatuses.Priced)
            ? prices.Sum(price => price.Status == DecisionPriceStatuses.Priced ? price.EstimatedUsd ?? 0 : 0)
            : null;
        return new(receipt.ReceiptId, receipt.OccurrenceId, receipt.Scope.TaskId, receipt.Trigger.Profile,
            receipt.Mode, string.Join(", ", receipt.Chosen.Where(item => !item.Outcome.StartsWith("rejected:",
                StringComparison.Ordinal)).Select(item => $"{item.Action}({item.Target})")),
            receipt.DeterministicAction, receipt.MatchesDeterministic, receipt.Call.Status, receipt.Call.Model,
            receipt.Call.ThinkingLevel, receipt.Call.Custody,
            receipt.Call.Tokens.Total + lateCalls.Sum(call => call.Tokens.Total), priceStatus, usd,
            receipt.EvidenceRef, receipt.EndedAt, lateCalls.Count);
    }

    /// <summary>
    /// A call counts once a runner claimed it or while it may still be claimed.
    /// Settled calls add measured usage; open calls reserve their envelope.
    /// </summary>
    private DecisionSpendSnapshot Spend(IEnumerable<JobRecord> jobs)
    {
        var counted = jobs.Where(job => job.Dto.ClaimedAt is not null
            || job.Dto.Status is DecisionJobStatuses.Pending or DecisionJobStatuses.Claimed).ToArray();
        var tokens = counted.Aggregate(DecisionTokenUsageV1.Zero, (total, job) => total.Add(
            job.Dto.Result?.Tokens ?? (DecisionJobStatuses.IsTerminal(job.Dto.Status) ? DecisionTokenUsageV1.Zero
                : new DecisionTokenUsageV1(Envelope(job).Input, 0, 0, Envelope(job).Output)))
            .Add(job.ReplacedClaims.Aggregate(DecisionTokenUsageV1.Zero, (attempts, claim) => attempts.Add(
                claim.Result?.Tokens ?? (DecisionJobStatuses.IsTerminal(job.Dto.Status)
                    ? DecisionTokenUsageV1.Zero
                    : new DecisionTokenUsageV1(Envelope(job).Input, 0, 0, Envelope(job).Output))))));
        var usd = counted.Sum(job => job.Dto.Result is { } result
                ? result.Price.Status == DecisionPriceStatuses.Priced ? result.Price.EstimatedUsd ?? 0 : 0
                : DecisionJobStatuses.IsTerminal(job.Dto.Status) ? 0
                : EstimatedEnvelopePrice(job.Dto.Model, Envelope(job).Input, Envelope(job).Output,
                    job.Dto.CreatedAt))
            + counted.Sum(job => job.ReplacedClaims.Sum(claim =>
                claim.Result is { } result
                    ? result.Price.Status == DecisionPriceStatuses.Priced ? result.Price.EstimatedUsd ?? 0 : 0
                    : DecisionJobStatuses.IsTerminal(job.Dto.Status) ? 0
                    : EstimatedEnvelopePrice(job.Dto.Model, Envelope(job).Input, Envelope(job).Output,
                        job.Dto.CreatedAt)));
        return new(counted.Length + counted.Sum(job => job.ReplacedClaims.Count), tokens, usd);
    }

    private decimal EstimatedEnvelopePrice(string model, int input, int output, DateTime at)
    {
        var estimate = _pricing.Estimate(model, input, output, 0, 0, at);
        return estimate.ModelKnown ? estimate.Total : 0;
    }

    /// <summary>Records written before the envelope was stored use the per-call cap.</summary>
    private static (int Input, int Output) Envelope(JobRecord job)
        => DecisionCostCapPolicy.Envelope(job.MaxInputTokens, job.MaxOutputTokens);

    private DecisionPriceV1 Price(string model, DecisionTokenUsageV1 tokens, DateTime at)
    {
        if (tokens.Total == 0) return new(DecisionPriceStatuses.NoUsage, null);
        var estimate = _pricing.Estimate(model, tokens.UncachedInput, tokens.Output, tokens.CachedInput,
            tokens.CacheWrite, at);
        return estimate.ModelKnown
            ? new(DecisionPriceStatuses.Priced, estimate.Total, estimate.PriceBasis is { } basis
                ? $"{basis.Source ?? "catalog"} valid from {basis.ValidFrom:yyyy-MM-dd}" : null)
            : new(estimate.Status == TokenEconomy.PriceStatus.NoPriceForDate
                ? DecisionPriceStatuses.NoPriceForDate : DecisionPriceStatuses.UnknownModel, null);
    }

    private IEnumerable<T> Load<T>(string kind)
    {
        var directory = Path.Combine(_directory!, kind);
        if (!Directory.Exists(directory)) yield break;
        foreach (var file in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
            yield return JsonSerializer.Deserialize<T>(File.ReadAllText(file), Json)
                ?? throw new InvalidDataException($"Orchestrator decision record is empty: {file}");
    }

    private void Save<T>(string kind, string id, T value)
    {
        if (_directory is null) return;
        var directory = Path.Combine(_directory, kind);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, id + ".json");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, value, Json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public sealed class JobRecord
    {
        public OrchestratorDecisionJobDto Dto { get; set; } = null!;
        public string GroundsDigest { get; set; } = string.Empty;
        public string Prompt { get; set; } = string.Empty;
        public string? ClaimToken { get; set; }
        public DateTime? ClaimExpiresAt { get; set; }
        public int MaxInputTokens { get; set; }
        public int MaxOutputTokens { get; set; }
        public bool LateAfterReceipt { get; set; }
        public List<ReplacedClaim> ReplacedClaims { get; set; } = [];
    }

    public sealed class ReplacedClaim
    {
        public string ClaimToken { get; set; } = string.Empty;
        public string RunnerId { get; set; } = string.Empty;
        public OrchestratorDecisionJobResult? Result { get; set; }
    }
}
