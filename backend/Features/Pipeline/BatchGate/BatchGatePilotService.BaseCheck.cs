using System.Text.Json;

namespace AgentStudio.Pipeline;

/// <summary>
/// AGT-3433: the base check of a red batch candidate. Before it, every red
/// candidate without a recorded base verdict ran the full suite on the base:
/// on 2 November 2026 a guard violation found in 2.5 minutes was followed by
/// a one-hour base run of a tip whose full suite had just passed.
/// </summary>
public sealed partial class BatchGatePilotService
{
    /// <summary>
    /// How many first-parent commits below a base the pilot looks for a
    /// verified ancestor before it checks the base itself.
    /// </summary>
    internal const int VerdictCarryDepth = 20;

    /// <summary>What answered the base question of a red candidate, and what it cost.</summary>
    private sealed record BaseAnswer(bool Red, string Source, double Minutes);

    /// <summary>
    /// AGT-3433: the next batch on a published tip needs no base run. The
    /// verified candidate is that tip, so its passing verdict is the base
    /// verdict. Best effort: a failed write costs at most one base check.
    /// </summary>
    private void RecordPublishedBaseVerdict(BatchGateManifest manifest, BatchGateRunRecord run)
    {
        try
        {
            _store.RecordBaseVerdict(new BatchGateBaseVerdict(manifest.Scope.Project,
                run.CandidateSha, run.GateProfileDigest, "pass", null,
                $"batch {manifest.BatchId} verified and published this tip",
                run.EvidencePath, _time.GetUtcNow(),
                BatchGateBaseCheckSources.PublishedBatch, 0, run.CandidateSha));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "batch-gate-published-base-verdict-failed batch={BatchId} sha={Sha}",
                manifest.BatchId, run.CandidateSha);
        }
    }

    /// <summary>
    /// Answers whether the base of a red candidate is red, cheapest first: a
    /// recorded verdict on the base, a verified ancestor with no gated change
    /// since, the part of the suite that failed, and only then the whole
    /// suite. Null when the candidate is not a product red.
    /// </summary>
    private async Task<BaseAnswer?> CheckBaseAsync(BatchGateManifest manifest,
        BatchGateRunRecord run, BuildTestGateResult candidate, string repo,
        CancellationToken ct)
    {
        if (candidate.Verdict != BuildTestGateVerdict.Fail
            || candidate.FailureKind != BuildTestGateFailureKind.Code)
            return null;
        var recorded = _store.ReadBaseVerdict(manifest.Scope.Project,
                manifest.BaseSha, run.GateProfileDigest)
            ?? CarriedBaseVerdict(manifest, run.GateProfileDigest, repo);
        if (recorded is not null)
            return new BaseAnswer(recorded.Outcome == "fail",
                recorded.Source is BatchGateBaseCheckSources.PublishedBatch
                    or BatchGateBaseCheckSources.VerifiedAncestor
                    ? recorded.Source : BatchGateBaseCheckSources.RecordedVerdict, 0);
        if (BatchGateBaseCheckPolicy.Narrow(candidate) is { } narrow
            && await RunBaseCheckAsync(manifest, run, repo, narrow, ct)
                .ConfigureAwait(false) is { } narrowed)
            return narrowed;
        return await RunBaseCheckAsync(manifest, run, repo, null, ct).ConfigureAwait(false)
            ?? throw new IOException("The base verification produced no verdict.");
    }

    /// <summary>
    /// A passing verdict of a first-parent ancestor, carried to the base when
    /// every commit since changes no gated input. The walk stops at the first
    /// commit that does, or at the first recorded verdict.
    /// </summary>
    private BatchGateBaseVerdict? CarriedBaseVerdict(BatchGateManifest manifest,
        string digest, string repo)
    {
        var child = manifest.BaseSha;
        for (var depth = 0; depth < VerdictCarryDepth; depth++)
        {
            var parent = _git.GetFirstParent(repo, child);
            if (parent is null
                || !DocsOnlyDeliveryPolicy.IsDocsOnly(_git.ChangedPathsAgainstMergeBase(repo, parent, child)))
                return null;
            var verified = _store.ReadBaseVerdict(manifest.Scope.Project, parent, digest);
            if (verified is null)
            {
                child = parent;
                continue;
            }
            if (verified.Outcome != "pass"
                || !BatchGateBaseCheckPolicy.CarriesVerdict(
                    _git.IsAncestor(repo, parent, manifest.BaseSha),
                    _git.ChangedPathsAgainstMergeBase(repo, parent, manifest.BaseSha)))
                return null;
            var carried = new BatchGateBaseVerdict(manifest.Scope.Project,
                manifest.BaseSha, digest, "pass", null,
                $"verified ancestor {parent}; no gated input changed since",
                verified.EvidencePath, _time.GetUtcNow(),
                BatchGateBaseCheckSources.VerifiedAncestor, 0, parent);
            _store.RecordBaseVerdict(carried);
            return carried;
        }
        return null;
    }

    /// <summary>
    /// Runs the base gate: the whole suite when <paramref name="narrow"/> is
    /// null, otherwise only that part. A narrow red makes the base red and is
    /// recorded for later batches; a narrow green answers only this candidate
    /// and is never recorded as a passing base. Null when the narrow check
    /// found nothing to repeat on the base.
    /// </summary>
    private async Task<BaseAnswer?> RunBaseCheckAsync(BatchGateManifest manifest,
        BatchGateRunRecord run, string repo, GateNarrowCheck? narrow, CancellationToken ct)
    {
        var settings = _settings.Get(manifest.Scope.Project);
        if (GateProfile(settings.BuildProfile, repo) != run.GateProfileDigest)
            throw new InvalidDataException("Batch gate profile changed before base verification.");
        var started = _time.GetUtcNow();
        var result = await _gate.RunAsync(new BuildTestGateRequest(
                repo, manifest.BaseSha, "batch-base")
            {
                Project = manifest.Scope.Project,
                GateId = "batch-base",
                SubjectRef = manifest.BaseSha,
                IntegrationRef = manifest.BaseSha,
                RequiredTestLevel = TestExecutionLevels.Full,
                TestExecution = settings.TestExecution,
                DisableVerdictCache = true,
                NarrowCheck = narrow,
            }, null, settings.BuildProfile, PostStepMode.Fail,
            TimeSpan.FromMinutes(settings.BatchGate.GateTimeoutMinutes), ct)
            .ConfigureAwait(false);
        if (GateProfile(_settings.Get(manifest.Scope.Project).BuildProfile, repo)
            != run.GateProfileDigest)
            throw new InvalidDataException("Batch gate profile changed during base verification.");
        if (narrow is not null
            && result.Verdict is BuildTestGateVerdict.Skipped or BuildTestGateVerdict.NotApplicable)
        {
            _logger.LogInformation("batch-gate-narrow-base-check-empty batch={BatchId} check={Check} reason={Reason}",
                manifest.BatchId, narrow.Kind, result.Reason);
            return null;
        }
        if (!string.Equals(result.TestedSha, manifest.BaseSha,
                StringComparison.OrdinalIgnoreCase)
            || result.IsInfrastructureFailure)
            throw new IOException("The base verification did not produce an exact-SHA verdict.");
        var source = narrow?.Kind ?? BatchGateBaseCheckSources.FullSuite;
        var evidencePath = Path.Combine(_store.BatchDirectory(manifest.BatchId),
            "base-" + manifest.BaseSha + (narrow is null ? "" : "-" + narrow.Kind) + ".json");
        File.WriteAllText(evidencePath, JsonSerializer.Serialize(result));
        var red = result.Verdict != BuildTestGateVerdict.Ok;
        var minutes = result.DurationMs / 60_000d;
        if (narrow is null || red)
            _store.RecordBaseVerdict(new BatchGateBaseVerdict(manifest.Scope.Project,
                manifest.BaseSha, run.GateProfileDigest,
                red ? "fail" : "pass",
                result.Verdict == BuildTestGateVerdict.Fail
                    ? BuildTestGateRunner.DiagnosticFingerprint(result) : null,
                result.Reason, evidencePath, _time.GetUtcNow(), source, minutes));
        _store.RecordExecution(new BatchGateExecutionFact(
            manifest.Scope.Project, manifest.BatchId, null, manifest.BaseSha,
            result.Verdict.ToString(), Environment.MachineName,
            started, _time.GetUtcNow(), result.GateQueueWaitMs, 0, evidencePath,
            result.DurationMs, NarrowCheck: narrow?.Kind));
        return new BaseAnswer(red, source, minutes);
    }
}
