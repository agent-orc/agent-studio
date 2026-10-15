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
    public const int MaxCallsPerCard = 24;
    public const decimal MaxPricedUsdPerCard = 5m;
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
            if (input.Scope.TaskId != request.TaskId || input.Scope.ProjectId != request.ProjectId)
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
            // A call counts once a runner claimed it or while it may still be claimed.
            var cardJobs = _jobs.Values.Where(job => job.Dto.TaskId == request.TaskId
                && (job.Dto.ClaimedAt is not null
                    || job.Dto.Status is DecisionJobStatuses.Pending or DecisionJobStatuses.Claimed)).ToArray();
            var spent = cardJobs.Sum(job => job.Dto.Result?.Price is { Status: DecisionPriceStatuses.Priced } price
                ? price.EstimatedUsd ?? 0 : 0);
            var capReason = cardJobs.Length >= MaxCallsPerCard
                ? $"card call cap of {MaxCallsPerCard} decision calls reached"
                : spent >= MaxPricedUsdPerCard ? $"card priced decision spend cap of {MaxPricedUsdPerCard} USD reached"
                : request.MaxInputTokens > DecisionGroundsPolicy.MaxCallInputTokens
                    || request.MaxOutputTokens > DecisionGroundsPolicy.MaxCallOutputTokens
                    ? "requested per-call token envelope exceeds the decision cap" : null;
            var now = _time.GetUtcNow().UtcDateTime;
            var record = new JobRecord
            {
                Dto = new($"odj_{Guid.NewGuid():N}", request.OccurrenceId, request.ProjectId, request.TaskId,
                    request.Attempt, capReason is null ? DecisionJobStatuses.Pending : DecisionJobStatuses.CapExhausted,
                    input.ModelRoute.Custody, request.Cli, request.Model, request.ThinkingLevel, now, null, null,
                    null, capReason),
                GroundsDigest = request.GroundsDigest,
                Prompt = request.Prompt,
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
            next.ClaimToken = Guid.NewGuid().ToString("N");
            next.ClaimExpiresAt = now + ClaimTtl;
            next.Dto = next.Dto with { Status = DecisionJobStatuses.Claimed, ClaimedAt = now,
                RunnerId = request.RunnerId };
            Save("jobs", next.Dto.JobId, next);
            return new(DecisionJobClaimStatuses.Claimed, new(next.Dto.JobId, next.ClaimToken, next.Prompt,
                next.Dto.Cli, next.Dto.Model, next.Dto.ThinkingLevel, (int)ClaimTtl.TotalSeconds - 60,
                next.ClaimExpiresAt.Value));
        }
    }

    /// <summary>False for a stale claim. A repeated completion of the same claim is accepted once.</summary>
    public bool Complete(OrchestratorDecisionJobCompletion completion)
    {
        lock (_gate)
        {
            var job = _jobs.GetValueOrDefault(completion.JobId);
            if (job is null || job.ClaimToken != completion.ClaimToken || job.Dto.RunnerId != completion.RunnerId)
                return false;
            if (DecisionJobStatuses.IsTerminal(job.Dto.Status)) return true;
            if (job.Dto.Status != DecisionJobStatuses.Claimed) return false;
            var usage = CardRunLedgerPolicy.Deduplicate(completion.Usage);
            var model = string.IsNullOrWhiteSpace(completion.Model) ? job.Dto.Model : completion.Model.Trim();
            var success = completion.Success && !string.IsNullOrWhiteSpace(completion.ResponseText);
            job.Dto = job.Dto with
            {
                Status = success ? DecisionJobStatuses.Completed : DecisionJobStatuses.Failed,
                Error = success ? null : completion.Error ?? "empty-response",
                Result = new(success, completion.ResponseText, job.Dto.Cli, model, job.Dto.ThinkingLevel,
                    usage.Tokens, Price(model, usage.Tokens, completion.EndedAt), usage.ProviderCallIds,
                    usage.ExcludedDuplicateFrames, completion.StartedAt, completion.EndedAt,
                    completion.RunnerId, completion.Error, completion.TransportFailure),
            };
            Save("jobs", job.Dto.JobId, job);
            return true;
        }
    }

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
                job.Dto = job.Dto with { Status = DecisionJobStatuses.Failed, Error = "superseded-by-receipt" };
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
            .Select(Summary).ToArray();
    }

    public static OrchestratorDecisionReceiptSummary Summary(OrchestratorDecisionReceiptV1 receipt)
        => new(receipt.ReceiptId, receipt.OccurrenceId, receipt.Scope.TaskId, receipt.Trigger.Profile,
            receipt.Mode, string.Join(", ", receipt.Chosen.Where(item => !item.Outcome.StartsWith("rejected:",
                StringComparison.Ordinal)).Select(item => $"{item.Action}({item.Target})")),
            receipt.DeterministicAction, receipt.MatchesDeterministic, receipt.Call.Status, receipt.Call.Model,
            receipt.Call.ThinkingLevel, receipt.Call.Custody, receipt.Call.Tokens.Total, receipt.Call.Price.Status,
            receipt.Call.Price.EstimatedUsd, receipt.EvidenceRef, receipt.EndedAt);

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
    }
}
