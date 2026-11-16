using System.Text.Json;
using AgentStudio.Runner;
using AgentStudio.Tasks;
using AgentStudio.TaskServer.Contracts.BatchGate;

namespace AgentStudio.Pipeline;

public sealed partial class BatchGatePilotService
{
    /// <summary>
    /// A green batch whose flight stopped before publication publishes from its
    /// recorded passing run: an isolation child, and since AGT-3563 also a
    /// batch whose own flight died between its green verdict and the
    /// publication fence. A green batch whose admitted member left the queue
    /// is retired and returns the rest.
    /// </summary>
    private async Task RecoverGreenBatchesAsync(CancellationToken ct)
    {
        foreach (var green in _store.ListManifests().Where(manifest =>
                     _store.HasState(manifest.BatchId)
                     && _store.LatestState(manifest.BatchId).Phase == BatchPhase.Green))
        {
            try
            {
            ct.ThrowIfCancellationRequested();
            var child = green.ParentBatchId is not null;
            var admitted = child ? green.Members.Select(member => member.TaskKey).ToArray()
                : green.Members.Where(member => _store.TryReadReplay(green.BatchId, member.TaskKey)
                    is { Outcome: "admitted" }).Select(member => member.TaskKey).ToArray();
            var pending = _store.ListPending().Where(item => green.Members.Any(member =>
                    member.TaskKey == item.Subject.TaskKey
                    && member.RunAttempt == item.Subject.RunAttempt))
                .ToDictionary(item => item.Subject.TaskKey, StringComparer.Ordinal);
            var complete = admitted.Length > 0 && admitted.All(pending.ContainsKey);
            var childLostGeneration = child && (!complete || pending.Count != green.Members.Count
                || green.Members.Any(member => !Refresh(pending[member.TaskKey]).Subject.CurrentGeneration));
            BatchCoordinatorLease? lease;
            try { lease = _leases.TryAcquire(LeaseScope(green), child ? "green-isolation-recovery" : "green-batch-recovery"); }
            catch (IOException) { continue; }
            if (lease is null) continue;
            try
            {
                if (_store.LatestState(green.BatchId) is not { Phase: BatchPhase.Green } state) continue;
                if (childLostGeneration)
                {
                    // A green diagnostic child cannot publish without every
                    // immutable review generation. Retire it so its red parent
                    // can hand the still pending members to fallback recovery.
                    State(green, BatchPhase.Abandoned, state.CandidateSha, lease.Fence,
                        "isolation child lost a review generation before publication");
                    continue;
                }
                var run = _store.ListRuns(green.BatchId)
                    .Where(item => item.CandidateSha == state.CandidateSha)
                    .MaxBy(item => item.StartedAtUtc);
                var verdict = run is null ? null : _store.ReadVerdict(green.BatchId, run.BatchRunId);
                if (!complete || run is null || verdict?.Outcome != "pass")
                {
                    if (child) continue;
                    State(green, BatchPhase.Abandoned, state.CandidateSha, lease.Fence,
                        "green batch interrupted before publication; an admitted member left the queue");
                    ReturnToPending(green);
                    continue;
                }
                var outcomes = green.Members.ToDictionary(member => member.TaskKey,
                    member => _store.TryReadReplay(green.BatchId, member.TaskKey)?.Outcome, StringComparer.Ordinal);
                var assembly = new BatchGateAssembly(green.BatchId, green.MembershipDigest,
                    BatchGatePolicy.CandidateRef(green, run.CoordinatorFence), run.CandidateSha, admitted,
                    child ? [] : outcomes.Where(item => item.Value is "conflict" or "unattributable")
                        .Select(item => item.Key).ToArray(),
                    child ? [] : outcomes.Where(item => item.Value == "cascade-deferred")
                        .Select(item => item.Key).ToArray(), false);
                // Ejected members of a published batch leave through the
                // published-batch recovery of the same tick.
                await PublishGreenAsync(green, assembly, run, verdict, lease, pending,
                    pending[admitted[0]].RepositoryPath, new HashSet<string>(StringComparer.Ordinal), ct)
                    .ConfigureAwait(false);
            }
            finally { _leases.Release(lease); }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException
                                       or BatchPhaseTransitionRefusedException)
            {
                _logger.LogWarning(ex, "batch-gate-green-recovery-failed batch={BatchId}", green.BatchId);
            }
        }
    }

    private void DeriveIsolationFindings(BatchGateManifest parent)
        => DeriveIsolationFindings(parent,
            _store.ListManifests().Where(manifest => manifest.ParentBatchId == parent.BatchId));

    /// <summary>
    /// Records what the green and red children of <paramref name="parent"/>
    /// prove. AGT-3864: the result depends only on the children and their
    /// phases. A green or red child wrote its runs and verdict before it took
    /// that phase, and findings are write-once, so a parent whose children and
    /// child phases are unchanged since its last derivation in this process
    /// would record nothing new; with <paramref name="skipSettled"/> it is
    /// skipped. A restart starts with no memo and derives every parent once.
    /// </summary>
    private void DeriveIsolationFindings(BatchGateManifest parent,
        IEnumerable<BatchGateManifest> childManifests, bool skipSettled = false)
    {
        var phases = childManifests.Select(manifest => (Manifest: manifest, Phase: ChildPhase(manifest.BatchId)))
            .ToArray();
        var fingerprint = string.Join("\n", phases.Select(item => item.Manifest.BatchId + ":" + item.Phase));
        if (skipSettled && _derivedIsolation.TryGetValue(parent.BatchId, out var derived) && derived == fingerprint)
            return;
        var children = phases.Where(item => item.Phase is BatchPhase.Green or BatchPhase.Red).ToArray();
        var green = new HashSet<string>(StringComparer.Ordinal);
        foreach (var child in children.Where(item => item.Phase == BatchPhase.Green))
            foreach (var member in child.Manifest.Members)
            {
                green.Add(member.TaskKey);
                Record(member, "proven-green", Evidence(child.Manifest));
            }
        var red = children.Where(item => item.Phase == BatchPhase.Red).ToArray();
        if (red.Length > 0)
            foreach (var finding in _store.ListIsolationFindings(parent.Scope.Project)
                         .Where(item => item.ParentBatchId == parent.BatchId && item.Status == "proven-green"))
                green.Add(finding.TaskKey);
        // A red child without a readable verdict is derived again on the next tick.
        var settled = true;
        // AGT-3858: a red same-code re-gate takes the per-card path, so it
        // proves no member red here.
        foreach (var child in red.Where(item => item.Manifest.SameCodeRerunOfRunId is null))
        {
            // A red phase also records transport and cancellation failures. Only
            // an executed deterministic verdict can identify an offender.
            var run = _store.ListRuns(child.Manifest.BatchId).LastOrDefault();
            var verdict = run is null ? null : _store.ReadVerdict(child.Manifest.BatchId, run.BatchRunId);
            if (verdict is null) settled = false;
            if (verdict?.Classification != "DeterministicSuiteRed") continue;
            var unknown = child.Manifest.Members.Where(member => !green.Contains(member.TaskKey)).ToArray();
            if (unknown.Length == 1)
                Record(unknown[0], "proven-red", Evidence(child.Manifest));
        }
        if (settled) _derivedIsolation[parent.BatchId] = fingerprint;
        else _derivedIsolation.TryRemove(parent.BatchId, out _);

        void Record(BatchGateSubject member, string status,
            (string Path, string BatchId, string? RunId) evidence)
            => _store.RecordIsolationFinding(new BatchGateIsolationFinding(
                parent.Scope.Project, parent.BatchId, member.TaskKey, member.RunAttempt,
                member.ResultSha, parent.BaseSha, status, evidence.Path, _time.GetUtcNow(),
                evidence.BatchId, evidence.RunId));

        // AGT-3577: the run that decided the status, so the UI shows its failed tests.
        (string Path, string BatchId, string? RunId) Evidence(BatchGateManifest child)
        {
            var run = _store.ListRuns(child.BatchId).LastOrDefault();
            return run is null ? (string.Empty, child.BatchId, null)
                : (_store.ReadVerdict(child.BatchId, run.BatchRunId)?.EvidencePath ?? string.Empty,
                    child.BatchId, run.BatchRunId);
        }
    }

    // A published or abandoned batch never changes phase again, so its phase
    // is read once per process.
    private BatchPhase ChildPhase(string batchId)
    {
        if (_terminalChildPhases.TryGetValue(batchId, out var terminal)) return terminal;
        var phase = _store.LatestState(batchId).Phase;
        if (MonolithTransitionAdapter.BatchPhaseIsTerminal(phase)) _terminalChildPhases[batchId] = phase;
        return phase;
    }

    private void RecordIsolationSuperseded(BatchGateManifest parent, BatchGateSubject member)
        => _store.RecordIsolationFinding(new BatchGateIsolationFinding(
            parent.Scope.Project, parent.BatchId, member.TaskKey, member.RunAttempt,
            member.ResultSha, parent.BaseSha, "superseded", string.Empty, _time.GetUtcNow()));

    private BatchGateIsolationFinding? ProvenRed(BatchGatePendingRecord item)
        => _store.ListIsolationFindings(item.Subject.Project).LastOrDefault(finding =>
            finding.Status == "proven-red"
            && IsDeterministicFinding(finding)
            && finding.TaskKey == item.Subject.TaskKey
            && finding.RunAttempt == item.Subject.RunAttempt
            && string.Equals(finding.ResultSha, item.Subject.ResultSha,
                StringComparison.OrdinalIgnoreCase));

    private bool IsDeterministicFinding(BatchGateIsolationFinding finding)
        => finding.ProvingBatchId is { } batchId && finding.ProvingRunId is { } runId
            && _store.ReadVerdict(batchId, runId)?.Classification == "DeterministicSuiteRed";

    private bool ProvenGreen(BatchGatePendingRecord item, string baseSha)
        => _store.ListIsolationFindings(item.Subject.Project).Any(finding =>
            finding.Status == "proven-green"
            && finding.TaskKey == item.Subject.TaskKey
            && finding.RunAttempt == item.Subject.RunAttempt
            && string.Equals(finding.ResultSha, item.Subject.ResultSha,
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(finding.BaseSha, baseSha, StringComparison.OrdinalIgnoreCase));

    private async Task HandOffProvenRedsAsync(string project, CancellationToken ct)
    {
        foreach (var item in _store.ListPending().Where(item =>
                     string.Equals(item.Subject.Project, project, StringComparison.Ordinal)))
        {
            var finding = ProvenRed(item);
            if (finding is null) continue;
            var current = Refresh(item);
            if (!current.Subject.CurrentGeneration) continue;
            var task = _scanner.FindJob(item.Subject.TaskKey, item.WatchPath);
            if (task?.State != TaskStates.AutoReview) continue;
            var reason = FailureSummary(finding.EvidencePath);
            await EscalateFallbackAsync(task, item, reason, false, true, ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>AGT-3858: the failed tests of a red run, read from its last evidence record for the tested SHA.</summary>
    private static IReadOnlyList<string> RedRunFailedTests(string? evidencePath, string candidateSha)
    {
        if (evidencePath is null || !File.Exists(evidencePath)) return [];
        try
        {
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var gate = File.ReadLines(evidencePath).Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => JsonSerializer.Deserialize<BuildTestGateResult>(value, options))
                .LastOrDefault(item => item is not null
                    && (item.TestedSha is null || string.Equals(item.TestedSha, candidateSha,
                        StringComparison.OrdinalIgnoreCase)));
            return gate is null ? [] : BatchGateProbeClassifier.FailedTests(gate);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string FailureSummary(string evidencePath)
    {
        if (!File.Exists(evidencePath)) return "Batch isolation proved this delivery red.";
        try
        {
            var line = File.ReadLines(evidencePath).LastOrDefault(value => !string.IsNullOrWhiteSpace(value));
            var gate = line is null ? null : JsonSerializer.Deserialize<BuildTestGateResult>(line,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var failed = gate?.Output.Split('\n').Where(value =>
                value.Contains("Failed", StringComparison.OrdinalIgnoreCase)
                || value.Contains("Error", StringComparison.OrdinalIgnoreCase)).Take(12);
            var excerpt = failed is null ? string.Empty : string.Join("\n", failed);
            var tests = gate is null ? [] : BatchGateProbeClassifier.FailedTests(gate);
            return "Batch isolation proved this delivery red. "
                + (tests.Count == 0 ? string.Empty : "Failing tests: " + string.Join(", ", tests) + ". ")
                + gate?.Reason
                + (excerpt.Length == 0 ? string.Empty : "\n" + excerpt[..Math.Min(excerpt.Length, 2000)])
                + "\nEvidence: " + evidencePath;
        }
        catch (Exception)
        {
            return "Batch isolation proved this delivery red. Evidence: " + evidencePath;
        }
    }
}
