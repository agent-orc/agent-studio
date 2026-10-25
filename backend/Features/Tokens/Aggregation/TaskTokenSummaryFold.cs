namespace AgentStudio.Tokens;

/// <summary>
/// Pure fold from a priced usage ledger to one <see cref="TaskTokenSummary"/>
/// per job: the board card's token bubble, the task-detail call list, and
/// the durable task receipt. Entries without a job id are skipped.
/// </summary>
public static class TaskTokenSummaryFold
{
    public static Dictionary<string, TaskTokenSummary> PerJob(IReadOnlyList<PricedTokenEntry> ledger)
    {
        var perJob = new Dictionary<string, JobAccumulator>(StringComparer.Ordinal);
        foreach (var priced in ledger)
        {
            var usage = priced.Usage;
            if (usage is null || priced.Cost is null) continue;
            var jobId = priced.Entry.JobId;
            if (string.IsNullOrWhiteSpace(jobId)) continue;
            if (!perJob.TryGetValue(jobId, out var job))
            {
                job = new JobAccumulator();
                perJob[jobId] = job;
            }
            job.Add(priced.Entry, usage, priced.Cost);
        }

        var result = new Dictionary<string, TaskTokenSummary>(perJob.Count, StringComparer.Ordinal);
        foreach (var (jobId, job) in perJob)
            result[jobId] = job.ToSummary();
        return result;
    }

    private sealed class JobAccumulator
    {
        private int _calls;
        private long _input;
        private long _output;
        private long _cacheRead;
        private long _cacheCreate;
        private decimal _cost;
        private bool _anyUnpriced;
        private string? _lastAnyModel;
        private string? _lastAgentModel;
        private string? _lastAnyModelId;
        private string? _lastAgentModelId;
        private DateTime? _lastUpdate;
        private DateTime? _lastAnyUpdate;
        private DateTime? _lastAgentUpdate;
        private readonly List<TaskTokenCall> _entries = [];

        public void Add(OrchestratorLogEntry entry, OrchestratorTokenUsage usage, TokenCostEstimate cost)
        {
            _calls++;
            _input += usage.InputTokens;
            _output += usage.OutputTokens;
            _cacheRead += usage.CacheReadTokens;
            _cacheCreate += usage.CacheCreationTokens;
            _cost += cost.Total;
            if (!cost.ModelKnown) _anyUnpriced = true;

            var storedId = ModelIdentity.StoredId(usage.Model);
            var label = ModelIdentity.Label(usage.Model);
            if (entry.Ts > (_lastUpdate ?? DateTime.MinValue)) _lastUpdate = entry.Ts;
            if (!string.IsNullOrWhiteSpace(label))
            {
                if (entry.Ts > (_lastAnyUpdate ?? DateTime.MinValue))
                {
                    _lastAnyUpdate = entry.Ts;
                    _lastAnyModel = label;
                    _lastAnyModelId = storedId;
                }
                if (TokenParticipants.IsAgent(entry.ParticipantId)
                    && entry.Ts > (_lastAgentUpdate ?? DateTime.MinValue))
                {
                    _lastAgentUpdate = entry.Ts;
                    _lastAgentModel = label;
                    _lastAgentModelId = storedId;
                }
            }

            _entries.Add(new TaskTokenCall
            {
                Ts = entry.Ts,
                Model = string.IsNullOrWhiteSpace(storedId) ? null : storedId,
                DisplayModel = label,
                ThinkingLevel = Trimmed(usage.ThinkingLevel),
                ParticipantId = entry.ParticipantId,
                CliType = Trimmed(usage.CliType),
                Host = Trimmed(usage.Host),
                InputTokens = usage.InputTokens,
                OutputTokens = usage.OutputTokens,
                CacheReadTokens = usage.CacheReadTokens,
                CacheCreationTokens = usage.CacheCreationTokens,
                ReasoningTokens = usage.ReasoningTokens,
                InputIncludesCached = usage.InputIncludesCached,
                UsageNormalization = usage.UsageNormalization,
                PinnedModel = string.IsNullOrWhiteSpace(usage.PinnedModel)
                    ? null
                    : ModelIdentity.StoredId(usage.PinnedModel),
                ModelMismatch = usage.ModelMismatch,
                EstimatedApiCostUsd = cost.Total,
                ModelPriced = cost.ModelKnown,
            });
        }

        public TaskTokenSummary ToSummary() => new()
        {
            Calls = _calls,
            InputTokens = _input,
            OutputTokens = _output,
            CacheReadTokens = _cacheRead,
            CacheCreationTokens = _cacheCreate,
            TotalTokens = _input + _output + _cacheRead + _cacheCreate,
            EstimatedApiCostUsd = _cost,
            AllModelsPriced = !_anyUnpriced,
            // The newest agent call names the card's model; other participants only fill a gap.
            LastModel = _lastAgentModel ?? _lastAnyModel,
            LastModelId = _lastAgentModel is not null ? _lastAgentModelId : _lastAnyModelId,
            LastUpdate = _lastUpdate,
            Entries = _entries.OrderBy(call => call.Ts).ToList(),
            HasModelMismatch = _entries.Any(call => call.ModelMismatch),
        };

        private static string? Trimmed(string? value)
            => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
