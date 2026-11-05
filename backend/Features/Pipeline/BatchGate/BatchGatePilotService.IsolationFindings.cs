using System.Text.Json;
using AgentStudio.Tasks;
using AgentStudio.TaskServer.Contracts.BatchGate;

namespace AgentStudio.Pipeline;

public sealed partial class BatchGatePilotService
{
    private async Task RecoverGreenIsolationChildrenAsync(CancellationToken ct)
    {
        foreach (var child in _store.ListManifests().Where(manifest =>
                     manifest.ParentBatchId is not null
                     && _store.LatestState(manifest.BatchId).Phase == BatchPhase.Green))
        {
            ct.ThrowIfCancellationRequested();
            var pending = _store.ListPending().Where(item => child.Members.Any(member =>
                    member.TaskKey == item.Subject.TaskKey
                    && member.RunAttempt == item.Subject.RunAttempt))
                .ToDictionary(item => item.Subject.TaskKey, StringComparer.Ordinal);
            if (pending.Count != child.Members.Count
                || child.Members.Any(member => !Refresh(pending[member.TaskKey]).Subject.CurrentGeneration))
                continue;
            BatchCoordinatorLease? lease;
            try { lease = _leases.TryAcquire(LeaseScope(child), "green-isolation-recovery"); }
            catch (IOException) { continue; }
            if (lease is null) continue;
            try
            {
                var run = _store.ListRuns(child.BatchId).LastOrDefault();
                if (run is null) continue;
                var verdict = _store.ReadVerdict(child.BatchId, run.BatchRunId);
                if (verdict?.Outcome != "pass") continue;
                var assembly = new BatchGateAssembly(child.BatchId, child.MembershipDigest,
                    BatchGatePolicy.CandidateRef(child, run.CoordinatorFence), run.CandidateSha,
                    child.Members.Select(member => member.TaskKey).ToArray(), [], [], false);
                await PublishGreenAsync(child, assembly, run, verdict, lease, pending,
                    pending.Values.First().RepositoryPath, new HashSet<string>(StringComparer.Ordinal), ct)
                    .ConfigureAwait(false);
            }
            finally { _leases.Release(lease); }
        }
    }

    private void DeriveIsolationFindings(BatchGateManifest parent)
    {
        var children = _store.ListManifests()
            .Where(manifest => manifest.ParentBatchId == parent.BatchId)
            .Select(manifest => (Manifest: manifest, State: _store.LatestState(manifest.BatchId)))
            .Where(item => item.State.Phase is BatchPhase.Green or BatchPhase.Red)
            .ToArray();
        var green = new HashSet<string>(StringComparer.Ordinal);
        foreach (var child in children.Where(item => item.State.Phase == BatchPhase.Green))
            foreach (var member in child.Manifest.Members)
            {
                green.Add(member.TaskKey);
                Record(member, "proven-green", Evidence(child.Manifest));
            }
        foreach (var finding in _store.ListIsolationFindings(parent.Scope.Project)
                     .Where(item => item.ParentBatchId == parent.BatchId && item.Status == "proven-green"))
            green.Add(finding.TaskKey);
        foreach (var child in children.Where(item => item.State.Phase == BatchPhase.Red))
        {
            var unknown = child.Manifest.Members.Where(member => !green.Contains(member.TaskKey)).ToArray();
            if (unknown.Length == 1)
                Record(unknown[0], "proven-red", Evidence(child.Manifest));
        }

        void Record(BatchGateSubject member, string status, string evidence)
            => _store.RecordIsolationFinding(new BatchGateIsolationFinding(
                parent.Scope.Project, parent.BatchId, member.TaskKey, member.RunAttempt,
                member.ResultSha, parent.BaseSha, status, evidence, _time.GetUtcNow()));

        string Evidence(BatchGateManifest child)
        {
            var run = _store.ListRuns(child.BatchId).LastOrDefault();
            return run is null ? string.Empty
                : _store.ReadVerdict(child.BatchId, run.BatchRunId)?.EvidencePath ?? string.Empty;
        }
    }

    private void RecordIsolationSuperseded(BatchGateManifest parent, BatchGateSubject member)
        => _store.RecordIsolationFinding(new BatchGateIsolationFinding(
            parent.Scope.Project, parent.BatchId, member.TaskKey, member.RunAttempt,
            member.ResultSha, parent.BaseSha, "superseded", string.Empty, _time.GetUtcNow()));

    private BatchGateIsolationFinding? ProvenRed(BatchGatePendingRecord item)
        => _store.ListIsolationFindings(item.Subject.Project).LastOrDefault(finding =>
            finding.Status == "proven-red"
            && finding.TaskKey == item.Subject.TaskKey
            && finding.RunAttempt == item.Subject.RunAttempt
            && string.Equals(finding.ResultSha, item.Subject.ResultSha,
                StringComparison.OrdinalIgnoreCase));

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
            return "Batch isolation proved this delivery red. " + gate?.Reason
                + (excerpt.Length == 0 ? string.Empty : "\n" + excerpt[..Math.Min(excerpt.Length, 2000)])
                + "\nEvidence: " + evidencePath;
        }
        catch (Exception)
        {
            return "Batch isolation proved this delivery red. Evidence: " + evidencePath;
        }
    }
}
