using System.Text.Json;

namespace AgentTaskboard.UpdateService;

public sealed class ReleasePreflightService
{
    private readonly IBackendProbe _backend;
    private readonly UpdateServiceOptions _options;
    private readonly UpdateStatusStore? _store;

    public ReleasePreflightService(IBackendProbe backend, UpdateServiceOptions options, UpdateStatusStore? store = null)
    {
        _backend = backend;
        _options = options;
        _store = store;
    }

    public async Task<ReleaseComparison> EvaluateAsync(bool allowDowngrade, CancellationToken ct)
    {
        var running = ToManifest(await _backend.ReadRuntimeVersionAsync(ct));
        var installed = ReadFile(Path.Combine(_options.StableCheckoutDir, _options.BuildManifestFile));
        // Migration from a pre-contract installation has no on-disk manifest.
        // The boot-captured runtime identity is the only truthful rollback
        // anchor, so preserve it as the installed identity for this first hop.
        if (installed is null && running?.Legacy == true)
            installed = running;
        var candidate = ReadFile(_options.CandidateManifestFile);
        var approved = File.Exists(_options.ApprovedTagFile)
            ? File.ReadAllText(_options.ApprovedTagFile).Trim()
            : null;
        var comparison = StableReleaseContract.Compare(
            running, installed, candidate, approved, _options.ReleaseMetadataOffline, allowDowngrade);

        const string divergenceMarker = "running identity diverges from the installed manifest";
        if (!comparison.Allowed && comparison.Errors.Any(e => e.Contains(divergenceMarker, StringComparison.Ordinal)))
        {
            var explanation = ExplainDivergence(installed);
            if (explanation is not null)
            {
                var enriched = comparison.Errors
                    .Select(e => e.Contains(divergenceMarker, StringComparison.Ordinal) ? $"{e} ({explanation})" : e)
                    .ToArray();
                comparison = comparison with { Errors = enriched, DivergenceExplanation = explanation };
            }
        }
        return comparison;
    }

    /// <summary>
    /// Finds the most recent run in history whose intended tag matches what
    /// is currently installed, so a divergence refusal names the run that
    /// caused it instead of forcing the operator to dig through run folders.
    /// </summary>
    private string? ExplainDivergence(ReleaseManifest? installed)
    {
        if (_store is null || installed is null || string.IsNullOrWhiteSpace(installed.Tag)) return null;
        try
        {
            var culprit = _store.ReadHistory(50)
                .Where(h => string.Equals(h.IntendedTag, installed.Tag, StringComparison.Ordinal))
                .OrderByDescending(h => h.FinishedAt ?? h.StartedAt)
                .FirstOrDefault();
            if (culprit is null) return null;

            return culprit.Status == "ok"
                ? $"run {culprit.RunId} (finished {culprit.FinishedAt:O}) installed {installed.Tag}, but the backend process is still reporting an older identity; restart the backend to pick up the installed manifest"
                : $"run {culprit.RunId} (finished {culprit.FinishedAt:O}, status={culprit.Status}) intended to install {installed.Tag} and did not finish cleanly: {culprit.Error ?? "no error recorded"}";
        }
        catch
        {
            return null;
        }
    }

    public static ReleaseManifest? ToManifest(RuntimeVersion? runtime)
    {
        if (runtime is null) return null;
        return new ReleaseManifest(
            1, "Agent Studio", runtime.Tag ?? "untagged", runtime.Version,
            runtime.Commit, runtime.Dirty, runtime.BuiltAt,
            runtime.Integrity ?? "unverified",
            runtime.CodingAgentRunner!, runtime.CodingAgentChat!, runtime.Legacy);
    }

    private static ReleaseManifest? ReadFile(string path) =>
        File.Exists(path) ? ReadJson(File.ReadAllText(path)) : null;

    private static ReleaseManifest? ReadJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return StableReleaseContract.Read(json); }
        catch (JsonException) { return null; }
    }
}
