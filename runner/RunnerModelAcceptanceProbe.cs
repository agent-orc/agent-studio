using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>
/// Credential-bound model evidence. CLI discovery proves only that a model is
/// listed by the binary; a small real request proves this host login accepts it.
/// </summary>
internal sealed class RunnerModelAcceptanceProbe
{
    internal static RunnerModelAcceptanceProbe Shared { get; } = new();

    private readonly object _sync = new();
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private readonly Dictionary<string, ModelVerdict> _verdicts = new(StringComparer.Ordinal);
    private IReadOnlyList<(string Provider, string Model)> _configured = [];
    private DateTime? _serverWatermark;
    private Func<string, string, CancellationToken, Task<ProcessResult>>? _launch;
    private Func<string, string?> _generation = _ => null;
    private Func<DateTimeOffset> _clock = () => DateTimeOffset.UtcNow;

    internal void Configure(
        Func<string, string, CancellationToken, Task<ProcessResult>> launch,
        Func<string, string?> generation,
        Func<DateTimeOffset>? clock = null)
    {
        lock (_sync)
        {
            _launch = launch;
            _generation = generation;
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
            _verdicts.Clear();
            _configured = [];
            _serverWatermark = null;
        }
    }

    /// <summary>Configured and queued routes the server asked this host to prove.</summary>
    internal IReadOnlyList<(string Provider, string Model)> Configured
    {
        get { lock (_sync) return _configured; }
    }

    /// <summary>
    /// Adopts one capability response. Its server time orders later probes
    /// after every rejection the server had recorded, without trusting this
    /// host's clock. A ready verdict the server still refuses predates such a
    /// rejection, so it is probed again after the inconclusive retry interval.
    /// </summary>
    internal void ObserveServer(
        DateTime serverTime,
        IReadOnlyList<string>? probeKeys,
        IEnumerable<CapabilityHealthDto> capabilities)
    {
        lock (_sync)
        {
            var observed = serverTime.ToUniversalTime();
            if (_serverWatermark is not { } prior || observed > prior) _serverWatermark = observed;
            if (probeKeys is not null)
                _configured = probeKeys
                    .SelectMany(key => CapabilityProtocol.TryParseModelAcceptance(key, out var provider, out var model)
                        ? new[] { (provider, model) } : [])
                    .Distinct()
                    .Take(MaxConfiguredRoutes)
                    .ToArray();
            foreach (var capability in capabilities)
            {
                if (capability.AdvertisedStatus != "unavailable"
                    || !_verdicts.TryGetValue(capability.Key, out var verdict)
                    || verdict.Status != "ready"
                    || _clock() - verdict.ObservedAt < RetryAfter) continue;
                _verdicts.Remove(capability.Key);
            }
        }
    }

    internal IReadOnlyList<AdvertisedCapabilityDto> Snapshot(
        string provider, IReadOnlyList<string> models)
    {
        var currentGeneration = _generation(provider);
        lock (_sync)
        {
            return models.Select(model =>
            {
                _verdicts.TryGetValue(CapabilityProtocol.ModelAcceptance(provider, model), out var verdict);
                var current = verdict is not null && verdict.Generation == currentGeneration;
                return new AdvertisedCapabilityDto(
                    CapabilityProtocol.ModelAcceptance(provider, model), "model-acceptance",
                    current ? verdict!.Status : "unknown",
                    Identity: $"{provider}/{model}",
                    Detail: current ? verdict!.Detail : "Model acceptance has not been checked for this login.",
                    CredentialGeneration: currentGeneration,
                    CredentialObservedAt: current ? verdict!.ObservedAt.UtcDateTime : null,
                    ProbeServerWatermark: current ? verdict!.ServerWatermark : null);
            }).ToArray();
        }
    }

    internal void RecordRejection(string provider, string model)
    {
        var generation = _generation(provider);
        lock (_sync)
            _verdicts[CapabilityProtocol.ModelAcceptance(provider, model)] = new ModelVerdict(
                generation, "unavailable", "The provider refused this model for the host login.", _clock(),
                _serverWatermark);
    }

    internal async Task RefreshAsync(
        IEnumerable<(string Provider, string Model)> candidates, CancellationToken ct)
    {
        if (!await _refresh.WaitAsync(0, ct)) return;
        try
        {
            foreach (var (provider, model) in candidates.Distinct())
            {
                ct.ThrowIfCancellationRequested();
                Func<string, string, CancellationToken, Task<ProcessResult>>? launch;
                string? generation;
                DateTime? watermark;
                bool cached;
                lock (_sync)
                {
                    launch = _launch;
                    generation = _generation(provider);
                    watermark = _serverWatermark;
                    cached = _verdicts.TryGetValue(CapabilityProtocol.ModelAcceptance(provider, model), out var prior)
                        && prior.Generation == generation
                        && (prior.Status != "unknown"
                            || _clock() - prior.ObservedAt < RetryAfter);
                }
                if (cached || launch is null || generation is null) continue;
                ProcessResult result;
                try
                {
                    using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    bounded.CancelAfter(TimeSpan.FromSeconds(45));
                    result = await launch(provider, model, bounded.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    result = new ProcessResult(1, "", "Model acceptance probe timed out.");
                }
                catch (Exception exception) when (!ct.IsCancellationRequested)
                {
                    result = new ProcessResult(1, "",
                        $"Model acceptance probe failed: {exception.GetType().Name}.");
                }

                var status = result.ExitCode == 0 ? "ready" :
                    IsModelRejection(result, model) ? "unavailable" : "unknown";
                lock (_sync)
                {
                    if (_generation(provider) != generation) continue;
                    if (_verdicts.TryGetValue(CapabilityProtocol.ModelAcceptance(provider, model), out var newer)
                        && newer.Generation == generation && newer.Status != "unknown") continue;
                    _verdicts[CapabilityProtocol.ModelAcceptance(provider, model)] = new ModelVerdict(
                        generation, status,
                        status == "ready" ? "A real request was accepted for this model."
                            : status == "unavailable" ? "The provider refused this model for the host login."
                            : "The model acceptance probe could not verify this route.",
                        _clock(), watermark);
                }
            }
        }
        finally { _refresh.Release(); }
    }

    internal static bool IsModelRejection(ProcessResult result, string model)
        => result.ExitCode != 0
           && ProviderRequestRejectionClassifier.TryClassify(
               string.Join('\n', result.StdOut, result.StdErr), out var rejection)
           && ProviderRequestRejectionClassifier.IsModelUnavailable(rejection, model);

    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(10);
    private const int MaxConfiguredRoutes = 24;

    private sealed record ModelVerdict(
        string? Generation, string Status, string Detail, DateTimeOffset ObservedAt, DateTime? ServerWatermark);
}
