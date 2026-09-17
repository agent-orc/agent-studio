using System.Text.Json;

namespace AgentTaskboard.UpdateService;

public sealed class ReleasePreflightService
{
    private readonly IBackendProbe _backend;
    private readonly UpdateServiceOptions _options;
    private readonly UpdateStatusStore? _store;
    private readonly VerificationPreconditionService? _preconditions;

    public ReleasePreflightService(
        IBackendProbe backend,
        UpdateServiceOptions options,
        UpdateStatusStore? store = null,
        VerificationPreconditionService? preconditions = null)
    {
        _backend = backend;
        _options = options;
        _store = store;
        _preconditions = preconditions;
    }

    /// <summary>
    /// The release gate plus, unless the caller opts out, the AGT-2865
    /// verification preconditions read off the running instance.
    /// <paramref name="includeVerificationPreconditions"/> exists for the
    /// background status tick, which runs every
    /// <see cref="UpdateServiceOptions.ProbeIntervalSeconds"/> and has no
    /// business replaying the board query that often. The operator-facing
    /// <c>GET /update/preflight</c> and the orchestrator's phase-1 gate both
    /// evaluate them.
    /// </summary>
    public async Task<ReleaseComparison> EvaluateAsync(
        bool allowDowngrade,
        CancellationToken ct,
        bool includeVerificationPreconditions = true)
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
        else if (comparison.UpgradeInVerification)
        {
            comparison = comparison with { DivergenceExplanation = ExplainUpgradeInVerification(candidate) };
        }

        if (includeVerificationPreconditions && _preconditions is not null)
            comparison = WithPreconditions(comparison, await _preconditions.EvaluateAsync(ct));

        return comparison;
    }

    /// <summary>
    /// Folds the precondition verdicts into the comparison. A blocking
    /// failure is an ordinary preflight error, so every existing consumer -
    /// the FE banner, the run folder's <c>release-preflight.json</c>, the
    /// orchestrator's refusal message - surfaces it without new plumbing.
    /// </summary>
    public static ReleaseComparison WithPreconditions(
        ReleaseComparison comparison,
        IReadOnlyList<VerificationPreconditionResult> results)
    {
        var blocking = VerificationPreconditionPolicy.BlockingErrors(results);
        return comparison with
        {
            VerificationPreconditions = results,
            Errors = blocking.Count == 0 ? comparison.Errors : comparison.Errors.Concat(blocking).ToArray(),
            Allowed = comparison.Allowed && blocking.Count == 0,
        };
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

    /// <summary>
    /// Names the run that put the installation into "upgrade in verification":
    /// the backend already reports the candidate because the update run handed
    /// it the intended manifest at restart, and the checkout root still
    /// carries the previous release because the manifest is committed only
    /// after verification passes.
    /// </summary>
    private string? ExplainUpgradeInVerification(ReleaseManifest? candidate)
    {
        const string state = "upgrade in verification: the running backend already reports the candidate "
            + "and the checkout manifest is committed only after verification passes";
        if (_store is null || candidate is null || string.IsNullOrWhiteSpace(candidate.Tag)) return state;
        try
        {
            var run = _store.ReadHistory(50)
                .Where(h => string.Equals(h.IntendedTag, candidate.Tag, StringComparison.Ordinal))
                .OrderByDescending(h => h.FinishedAt ?? h.StartedAt)
                .FirstOrDefault();
            return run is null ? state : $"{state} (run {run.RunId}, status={run.Status})";
        }
        catch
        {
            return state;
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
