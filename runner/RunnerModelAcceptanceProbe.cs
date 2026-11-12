using System.Text.RegularExpressions;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>
/// Credential-bound model evidence. CLI discovery proves only that a model is
/// listed by the binary; a small real request proves this host login accepts it.
/// </summary>
internal sealed class RunnerModelAcceptanceProbe
{
    internal static RunnerModelAcceptanceProbe Shared { get; } = new();
    internal static readonly TimeSpan CapacityTtl = TimeSpan.FromMinutes(15);

    private readonly object _sync = new();
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private readonly Dictionary<string, ModelVerdict> _verdicts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _invalidationSequences = new(StringComparer.Ordinal);
    private Func<string, string, CancellationToken, Task<ProcessResult>>? _launch;
    private Func<string, string?> _generation = _ => null;
    private Func<DateTimeOffset> _clock = () => DateTimeOffset.UtcNow;
    private IReadOnlyList<(string Provider, string Model)> _configured = [];
    private readonly Dictionary<string, string> _catalogueRevision = new(StringComparer.Ordinal);
    // Causal order for the server: a verdict written after the server saw a
    // non-ready entry carries a larger sequence than that entry did.
    private long _sequence;
    private static readonly Regex ProbeableModelId = new(
        @"^[A-Za-z0-9](?:[A-Za-z0-9._\-\[\]]{0,79})$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Models currently reachable through configured routes or review plans.</summary>
    internal IReadOnlyList<(string Provider, string Model)> ConfiguredModels
    {
        get { lock (_sync) return _configured; }
    }

    internal void UseConfiguredModels(IEnumerable<ConfiguredModelRouteDto>? routes)
    {
        var accepted = (routes ?? [])
            .Select(route => (Provider: CliSelection.NormalizeCliType(route.CliType), Model: route.Model?.Trim()))
            .Where(route => route.Provider is not null && IsProbeableModelId(route.Model))
            .Select(route => (route.Provider!, route.Model!))
            .Distinct()
            .ToArray();
        lock (_sync) _configured = accepted;
    }

    /// <summary>The model id reaches a CLI argument and a probe directory name.</summary>
    internal static bool IsProbeableModelId(string? model)
        => model is not null && ProbeableModelId.IsMatch(model) && !model.Contains("..", StringComparison.Ordinal);

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
            _invalidationSequences.Clear();
            _catalogueRevision.Clear();
        }
    }

    internal IReadOnlyList<AdvertisedCapabilityDto> Snapshot(
        string provider, IReadOnlyList<string> models)
    {
        var currentGeneration = _generation(provider);
        lock (_sync)
        {
            var horizon = _sequence;
            return models.Select(model =>
            {
                _verdicts.TryGetValue(CapabilityProtocol.ModelAcceptance(provider, model), out var verdict);
                var current = verdict is not null && verdict.Generation == currentGeneration
                    && (verdict.ExpiresAt is null || verdict.ExpiresAt > _clock());
                var status = current ? verdict!.Status : "unknown";
                return new AdvertisedCapabilityDto(
                    CapabilityProtocol.ModelAcceptance(provider, model), "model-acceptance",
                    status,
                    Identity: $"{provider}/{model}",
                    Detail: current ? verdict!.Detail : "Model acceptance has not been checked for this login.",
                    CredentialGeneration: currentGeneration,
                    CredentialObservedAt: current ? verdict!.ObservedAt.UtcDateTime : null,
                    ProbeSequence: status == "ready" ? verdict!.Sequence : horizon);
            }).ToArray();
        }
    }

    internal void RecordRejection(string provider, string model)
    {
        var generation = _generation(provider);
        lock (_sync)
            _verdicts[CapabilityProtocol.ModelAcceptance(provider, model)] = new ModelVerdict(
                generation, "unavailable", "The provider refused this model for the host login.", _clock(),
                ++_sequence);
    }

    internal void NoteCatalogueRefresh(string provider, string revision)
    {
        lock (_sync)
        {
            if (_catalogueRevision.TryGetValue(provider, out var previous)
                && previous != revision)
            {
                var prefix = $"model-acceptance:{provider.Trim().ToLowerInvariant()}:";
                foreach (var key in _verdicts.Where(item =>
                             item.Key.StartsWith(prefix, StringComparison.Ordinal)
                             && item.Value.Status == "unavailable"
                             && item.Value.ExpiresAt is null)
                         .Select(item => item.Key).ToArray())
                    _verdicts.Remove(key);
                _sequence++;
            }
            _catalogueRevision[provider] = revision;
        }
    }

    internal void RecordCapacity(string provider, string model, DateTimeOffset? retryAt = null)
    {
        var generation = _generation(provider);
        lock (_sync)
        {
            var now = _clock();
            var expiresAt = retryAt is { } hint && hint > now ? hint : now.Add(CapacityTtl);
            _verdicts[CapabilityProtocol.ModelAcceptance(provider, model)] = new ModelVerdict(
                generation, "unavailable", $"This model is at capacity until {expiresAt:O}.", now,
                ++_sequence, expiresAt);
        }
    }

    internal void Invalidate(string provider)
    {
        var prefix = $"model-acceptance:{provider.Trim().ToLowerInvariant()}:";
        lock (_sync)
        {
            foreach (var key in _verdicts.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
                _verdicts.Remove(key);
            _invalidationSequences[provider] = ++_sequence;
        }
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
                bool cached;
                long started;
                lock (_sync)
                {
                    started = _sequence;
                    launch = _launch;
                    generation = _generation(provider);
                    cached = _verdicts.TryGetValue(CapabilityProtocol.ModelAcceptance(provider, model), out var prior)
                        && prior.Generation == generation
                        && (prior.ExpiresAt is null || prior.ExpiresAt > _clock())
                        && (prior.Status == "unavailable"
                            || _clock() - prior.ObservedAt < (prior.Status == "ready"
                                ? generation?.StartsWith("daemon-session:", StringComparison.Ordinal) == true
                                    ? TimeSpan.FromHours(6)
                                    : TimeSpan.MaxValue
                                : TimeSpan.FromMinutes(10)));
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

                // Another host-local real probe (the provider auth canary) held
                // the shared lock, so this model was never asked. Recording that
                // as "unknown" would cache a non-verdict for ten minutes; leave
                // the route unprobed so the next refresh retries it.
                if (result.ExitCode != 0
                    && result.StdErr == ProviderRealRequestHostFlight.InProgress) continue;
                var status = result.ExitCode == 0 ? "ready" :
                    IsModelRejection(result, model) ? "unavailable" : "unknown";
                lock (_sync)
                {
                    if (_generation(provider) != generation) continue;
                    if (_invalidationSequences.GetValueOrDefault(provider) > started) continue;
                    // A launch rejection recorded while this probe ran is newer
                    // evidence; the probe result must not overwrite it.
                    if (_verdicts.TryGetValue(CapabilityProtocol.ModelAcceptance(provider, model), out var newer)
                        && newer.Generation == generation && newer.Sequence > started) continue;
                    _verdicts[CapabilityProtocol.ModelAcceptance(provider, model)] = new ModelVerdict(
                        generation, status,
                        status == "ready" ? "A real request was accepted for this model."
                            : status == "unavailable" ? "The provider refused this model for the host login."
                            : "The model acceptance probe could not verify this route.",
                        _clock(), ++_sequence);
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

    private sealed record ModelVerdict(
        string? Generation, string Status, string Detail, DateTimeOffset ObservedAt, long Sequence,
        DateTimeOffset? ExpiresAt = null);
}
