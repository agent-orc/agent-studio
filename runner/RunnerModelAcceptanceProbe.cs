using System.Globalization;
using System.Text.RegularExpressions;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>
/// Credential-bound model evidence. CLI discovery proves only that a model is
/// listed by the binary; a small real request proves this host login accepts it.
/// Every probe launch and launch rejection reserves the next value of a durable
/// sequence, so a server can tell a probe started after a rejection from a
/// delayed earlier advertisement without comparing clocks.
/// </summary>
internal sealed class RunnerModelAcceptanceProbe
{
    internal static RunnerModelAcceptanceProbe Shared { get; } = new();

    private readonly object _sync = new();
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private readonly Dictionary<string, ModelVerdict> _verdicts = new(StringComparer.Ordinal);
    private readonly List<(string Provider, string Model)> _requested = [];
    private const int MaxRequestedModels = 32;
    private static readonly Regex ModelId = new(
        "^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private string? _sequencePath;
    private long? _lastSequence;
    private Action<string> _log = _ => { };
    private Func<string, string, CancellationToken, Task<ProcessResult>>? _launch;
    private Func<string, string?> _generation = _ => null;
    private Func<DateTimeOffset> _clock = () => DateTimeOffset.UtcNow;

    internal void Configure(
        Func<string, string, CancellationToken, Task<ProcessResult>> launch,
        Func<string, string?> generation,
        Func<DateTimeOffset>? clock = null,
        string? sequencePath = null,
        Action<string>? log = null)
    {
        lock (_sync)
        {
            _launch = launch;
            _generation = generation;
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
            _sequencePath = sequencePath;
            _lastSequence = null;
            _log = log ?? (_ => { });
            _verdicts.Clear();
            _requested.Clear();
        }
    }

    /// <summary>
    /// A server asks for a configured model outside the curated list, for
    /// example an operator fallback override or a card's primary pin. Only a
    /// well-formed model-acceptance key is kept; the list is bounded.
    /// </summary>
    internal bool RequestProbe(string capabilityKey)
    {
        if (!CapabilityProtocol.TryParseModelAcceptance(capabilityKey, out var provider, out var model)
            || provider is not (CliSelection.ClaudeCli or CliSelection.CodexCli)
            || !ModelId.IsMatch(model))
            return false;
        lock (_sync)
        {
            if (_requested.Contains((provider, model))) return true;
            if (_requested.Count >= MaxRequestedModels) return false;
            _requested.Add((provider, model));
            return true;
        }
    }

    internal IReadOnlyList<(string Provider, string Model)> RequestedModels()
    {
        lock (_sync) return _requested.ToArray();
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
                    EvidenceId: current ? CapabilityProtocol.ModelProbeEvidence(verdict!.Sequence) : null,
                    CredentialGeneration: currentGeneration,
                    CredentialObservedAt: current ? verdict!.ObservedAt.UtcDateTime : null);
            }).ToArray();
        }
    }

    /// <summary>
    /// Records a launch rejection and returns its probe sequence. The run
    /// reports the sequence, and only a later-started successful probe clears it.
    /// </summary>
    internal long RecordRejection(string provider, string model)
    {
        var generation = _generation(provider);
        lock (_sync)
        {
            var sequence = NextSequence();
            _verdicts[CapabilityProtocol.ModelAcceptance(provider, model)] = new ModelVerdict(
                generation, "unavailable", "The provider refused this model for the host login.", _clock(),
                sequence);
            return sequence;
        }
    }

    private long NextSequence()
    {
        _lastSequence ??= ReadPersistedSequence();
        var sequence = _lastSequence.Value + 1;
        _lastSequence = sequence;
        if (_sequencePath is null) return sequence;
        var temporary = _sequencePath + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_sequencePath))!);
            File.WriteAllText(temporary, sequence.ToString(CultureInfo.InvariantCulture));
            File.Move(temporary, _sequencePath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A lost high-water mark can only make a later process reuse lower
            // sequences, which keeps a rejection held rather than clearing it.
            _log($"model probe sequence not persisted: {exception.Message}");
        }
        return sequence;
    }

    private long ReadPersistedSequence()
    {
        if (_sequencePath is null || !File.Exists(_sequencePath)) return 0;
        try
        {
            return long.TryParse(File.ReadAllText(_sequencePath).Trim(), NumberStyles.None,
                CultureInfo.InvariantCulture, out var persisted) ? persisted : 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log($"model probe sequence unreadable: {exception.Message}");
            return 0;
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
                long sequence = 0;
                lock (_sync)
                {
                    launch = _launch;
                    generation = _generation(provider);
                    cached = _verdicts.TryGetValue(CapabilityProtocol.ModelAcceptance(provider, model), out var prior)
                        && prior.Generation == generation
                        && (prior.Status != "unknown"
                            || _clock() - prior.ObservedAt < TimeSpan.FromMinutes(10));
                    // Reserved before launch: the verdict proves only what
                    // happened after every earlier sequence, including rejections.
                    if (!cached && launch is not null && generation is not null)
                        sequence = NextSequence();
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
                        && newer.Generation == generation
                        && (newer.Status != "unknown" || newer.Sequence > sequence)) continue;
                    _verdicts[CapabilityProtocol.ModelAcceptance(provider, model)] = new ModelVerdict(
                        generation, status,
                        status == "ready" ? "A real request was accepted for this model."
                            : status == "unavailable" ? "The provider refused this model for the host login."
                            : "The model acceptance probe could not verify this route.",
                        _clock(),
                        sequence);
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
        string? Generation, string Status, string Detail, DateTimeOffset ObservedAt, long Sequence);
}
