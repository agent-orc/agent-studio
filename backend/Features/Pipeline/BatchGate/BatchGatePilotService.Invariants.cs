using System.Text.Json;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Runner;

namespace AgentStudio.Pipeline;

/// <summary>One invariant finding of the batch gate state (AGT-3563).</summary>
public sealed record BatchGateInvariantView(
    string Kind, string BatchId, string? Phase, string? TaskKey,
    string? ReviewAttemptId, string Detail, string Repair);

/// <summary>The last invariant check of one project.</summary>
public sealed record BatchGateInvariantReport(
    string Project, DateTimeOffset CheckedAtUtc, IReadOnlyList<BatchGateInvariantView> Findings);

public sealed partial class BatchGatePilotService
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, BatchGateInvariantReport>
        _invariantReports = new(StringComparer.OrdinalIgnoreCase);

    private sealed record InvariantAssessment(BatchGateManifest Manifest, BatchPhase? Phase,
        IReadOnlyList<BatchMemberClaim> Claims, IReadOnlyList<BatchInvariantFinding> Findings);

    /// <summary>
    /// Start of every tick: a closed manifest without a phase record gets its
    /// first record (formed, every member claimed), and a record whose state
    /// history entry a crash left out gets that entry. Later steps of the tick
    /// read every manifest's phase, so this runs first.
    /// </summary>
    internal void ReconcilePhaseRecords()
    {
        foreach (var folder in _store.ListManifestFolders())
        {
            try
            {
                var repair = _store.ReconcilePhaseRecordIn(folder, _time.GetUtcNow());
                if (repair != BatchPhaseRepair.None)
                    _logger.LogWarning("batch-gate-phase-record-repair folder={Folder} repair={Repair}",
                        Path.GetFileName(folder), repair);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException
                or InvalidOperationException)
            {
                _logger.LogWarning(ex, "batch-gate-phase-record-reconcile-failed folder={Folder}",
                    Path.GetFileName(folder));
            }
        }
    }

    /// <summary>
    /// After the interrupted-flight recovery: completes or retires orphaned
    /// formed batches and returns members that a terminal batch still owns.
    /// Each repair holds the batch's coordinator lease, so a live flight on the
    /// scope is never repaired underneath.
    /// </summary>
    internal void RepairBatchInvariants()
    {
        foreach (var assessment in AssessInvariants(_store.ListManifests(), _store.ListPending()))
        {
            if (assessment.Findings.All(finding => finding.Repair == BatchInvariantRepair.None)) continue;
            var manifest = assessment.Manifest;
            BatchCoordinatorLease? lease;
            try { lease = _leases.TryAcquire(LeaseScope(manifest), "batch-invariant-repair"); }
            catch (IOException) { continue; }
            if (lease is null) continue;
            try
            {
                // The facts may have changed while the lease was free; the
                // scope's lease is now this repair's own.
                var current = AssessInvariants([_store.ReadManifest(manifest.BatchId)], _store.ListPending(),
                    leaseIsLiveness: false).SingleOrDefault();
                if (current is not null) Repair(current, lease);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException
                or InvalidOperationException)
            {
                _logger.LogWarning(ex, "batch-gate-invariant-repair-failed batch={BatchId}", manifest.BatchId);
            }
            finally { _leases.Release(lease); }
        }
    }

    private void Repair(InvariantAssessment assessment, BatchCoordinatorLease lease)
    {
        var manifest = assessment.Manifest;
        var repairs = assessment.Findings.Select(finding => finding.Repair).ToHashSet();
        foreach (var finding in assessment.Findings.Where(finding => finding.Repair != BatchInvariantRepair.None))
            _logger.LogWarning(
                "batch-gate-invariant-repair batch={BatchId} kind={Kind} task={TaskKey} repair={Repair} detail={Detail}",
                manifest.BatchId, finding.Kind, finding.TaskKey, finding.Repair, finding.Detail);
        var detail = string.Join("; ", assessment.Findings.Select(finding =>
            finding.TaskKey is null ? finding.Detail : $"{finding.TaskKey}: {finding.Detail}"));
        if (repairs.Contains(BatchInvariantRepair.InitializeFormed))
            _store.InitializeFormed(manifest, _time.GetUtcNow());
        else if (repairs.Contains(BatchInvariantRepair.AbandonBatch))
        {
            State(manifest, BatchPhase.Abandoned, null, lease.Fence, "invariant-repair: " + detail);
            ReturnToPending(manifest);
        }
        else if (repairs.Contains(BatchInvariantRepair.PauseBatch))
            State(manifest, BatchPhase.Paused, _store.LatestState(manifest.BatchId).CandidateSha,
                lease.Fence, "isolation-interrupted: " + detail);
        else if (repairs.Contains(BatchInvariantRepair.ReleaseMember))
            ReturnToPending(manifest);
        else if (repairs.Contains(BatchInvariantRepair.ClaimMember))
            foreach (var finding in assessment.Findings.Where(item => item.Repair == BatchInvariantRepair.ClaimMember))
                ClaimMember(manifest, assessment.Claims.Single(claim => claim.TaskKey == finding.TaskKey));
    }

    /// <summary>Writes the ownership marker formation did not reach; only over the member's queued marker.</summary>
    private void ClaimMember(BatchGateManifest manifest, BatchMemberClaim claim)
    {
        var pending = ClaimedPending(manifest, claim, _store.ListPending());
        if (pending is null) return;
        var folder = MemberFolder(pending);
        if (Ownership(BatchGateOwnershipStore.Read(folder), pending.ReviewAttemptId, manifest.BatchId)
            != BatchMemberOwnership.Queued) return;
        BatchGateOwnershipStore.Write(folder, new BatchGateOwnership(pending.ReviewAttemptId,
            manifest.Members.Single(member => member.TaskKey == claim.TaskKey), manifest.BatchId));
    }

    /// <summary>
    /// Runs the invariant check now and keeps it as the project's state. The
    /// on-demand route waits for a running tick so it never reads one half done.
    /// </summary>
    public async Task<BatchGateInvariantReport> CheckInvariantsAsync(string project, CancellationToken ct)
    {
        if (!await _tick.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false))
            return _invariantReports.GetValueOrDefault(project)
                ?? new BatchGateInvariantReport(project, default, []);
        try
        {
            RecordInvariants();
            return _invariantReports.GetValueOrDefault(project)
                ?? new BatchGateInvariantReport(project, _time.GetUtcNow(), []);
        }
        finally { _tick.Release(); }
    }

    /// <summary>The project's last invariant check, or null before the first tick.</summary>
    public BatchGateInvariantReport? LastInvariantReport(string project)
        => _invariantReports.GetValueOrDefault(project);

    /// <summary>End of every tick: one report per project, an empty one for a project without findings.</summary>
    internal void RecordInvariants()
    {
        var now = _time.GetUtcNow();
        var manifests = _store.ListManifests();
        var assessments = AssessInvariants(manifests, _store.ListPending());
        var projects = manifests.Select(manifest => manifest.Scope.Project)
            .Concat(_invariantReports.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var project in projects)
        {
            var findings = assessments
                .Where(item => string.Equals(item.Manifest.Scope.Project, project, StringComparison.OrdinalIgnoreCase))
                .SelectMany(item => item.Findings.Select(finding => View(item, finding)))
                .ToArray();
            // A finding that persists is logged once, when it appears.
            var known = _invariantReports.GetValueOrDefault(project)?.Findings ?? [];
            foreach (var finding in findings.Except(known))
                _logger.LogWarning(
                    "batch-gate-invariant kind={Kind} batch={BatchId} phase={Phase} task={TaskKey} detail={Detail}",
                    finding.Kind, finding.BatchId, finding.Phase, finding.TaskKey, finding.Detail);
            _invariantReports[project] = new BatchGateInvariantReport(project, now, findings);
        }
    }

    private static BatchGateInvariantView View(InvariantAssessment assessment, BatchInvariantFinding finding)
        => new(Kebab(finding.Kind.ToString()), finding.BatchId,
            assessment.Phase?.ToString().ToLowerInvariant(), finding.TaskKey,
            assessment.Claims.FirstOrDefault(claim => claim.TaskKey == finding.TaskKey)?.ReviewAttemptId,
            finding.Detail, Kebab(finding.Repair.ToString()));

    private static string Kebab(string name)
        => string.Concat(name.Select((letter, index) => char.IsUpper(letter)
            ? (index > 0 ? "-" : "") + char.ToLowerInvariant(letter) : letter.ToString()));

    /// <summary>
    /// The batches with findings. Settled history is cheap: a member whose
    /// queued record is resolved needs no ownership read, and the lease
    /// liveness is read only for a batch that has a finding.
    /// </summary>
    private IReadOnlyList<InvariantAssessment> AssessInvariants(IReadOnlyList<BatchGateManifest> manifests,
        IReadOnlyList<BatchGatePendingRecord> pending, bool leaseIsLiveness = true)
    {
        var assessments = new List<InvariantAssessment>();
        foreach (var manifest in manifests)
        {
            try
            {
                if (Assess(manifest, pending, leaseIsLiveness) is { } assessment) assessments.Add(assessment);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException
                or InvalidOperationException)
            {
                _logger.LogWarning(ex, "batch-gate-invariant-check-failed batch={BatchId}", manifest.BatchId);
            }
        }
        return assessments;
    }

    private InvariantAssessment? Assess(BatchGateManifest manifest, IReadOnlyList<BatchGatePendingRecord> pending,
        bool leaseIsLiveness)
    {
        // A quarantined phase record is the quarantine's attention item, not an invariant breach.
        if (!_store.TryReadPhaseRecord(manifest.BatchId, out var record)) return null;
        BatchPhase? phase = record?.State.Phase
            ?? (_store.HasState(manifest.BatchId) ? _store.LatestState(manifest.BatchId).Phase : null);
        if (phase is not (null or BatchPhase.Formed or BatchPhase.Abandoned
            or BatchPhase.Red or BatchPhase.Published)) return null;
        IReadOnlyList<BatchMemberClaim> claims = record?.Members ?? manifest.Members
            .Select(member => new BatchMemberClaim(member.TaskKey, member.RunAttempt, null,
                BatchMemberDisposition.Claimed)).ToArray();
        // A settled batch without a queued member has nothing to check.
        if (phase is not (null or BatchPhase.Formed)
            && claims.All(claim => ClaimedPending(manifest, claim, pending) is null)) return null;
        if (record is null && phase is { } legacy)
            claims = MonolithTransitionAdapter.BatchMemberDispositions(legacy, claims, ReplayOutcomesOf(manifest));
        var members = claims.Select(claim =>
        {
            var queued = ClaimedPending(manifest, claim, pending);
            if (queued is null)
                return new BatchInvariantMemberFact(claim.TaskKey, claim.ReviewAttemptId,
                    claim.Disposition, false, BatchMemberOwnership.None);
            var ownership = Ownership(BatchGateOwnershipStore.Read(MemberFolder(queued)),
                queued.ReviewAttemptId, manifest.BatchId);
            var live = FallbackInFlight(queued.ReviewAttemptId)
                || phase == BatchPhase.Published && _store.ReadPublication(manifest.BatchId) is { } publication
                    && EngineDecisionPending(
                        $"batch-result:{manifest.BatchId}:{publication.BatchRunId}:{claim.TaskKey}");
            return new BatchInvariantMemberFact(claim.TaskKey, queued.ReviewAttemptId,
                claim.Disposition, true, ownership, live);
        }).ToArray();
        var facts = new BatchInvariantFacts(manifest.BatchId, phase, manifest.Diagnostic, false, members);
        if (MonolithTransitionAdapter.BatchGateInvariants(facts).Count == 0) return null;
        var findings = MonolithTransitionAdapter.BatchGateInvariants(
            facts with { Live = BatchLive(manifest, leaseIsLiveness) });
        return findings.Count == 0 ? null : new InvariantAssessment(manifest, phase, claims, findings);
    }

    private IReadOnlyDictionary<string, string> ReplayOutcomesOf(BatchGateManifest manifest)
        => manifest.Members
            .Select(member => (member.TaskKey, Replay: _store.TryReadReplay(manifest.BatchId, member.TaskKey)))
            .Where(item => item.Replay is not null)
            .ToDictionary(item => item.TaskKey, item => item.Replay!.Outcome, StringComparer.Ordinal);

    /// <summary>
    /// The member's unresolved queued record. A claim without a review attempt
    /// (a batch closed before AGT-3563) matches by run attempt and only a record
    /// queued before the batch closed, so a later review is not its member.
    /// </summary>
    private static BatchGatePendingRecord? ClaimedPending(BatchGateManifest manifest, BatchMemberClaim claim,
        IReadOnlyList<BatchGatePendingRecord> pending)
        => claim.ReviewAttemptId is { } reviewAttemptId
            ? pending.FirstOrDefault(item => item.ReviewAttemptId == reviewAttemptId)
            : pending.Where(item => item.Subject.TaskKey == claim.TaskKey
                    && item.Subject.RunAttempt == claim.RunAttempt
                    && item.EnqueuedAtUtc <= manifest.ClosedAtUtc)
                .MaxBy(item => item.Subject.EnqueueSequence);

    private string MemberFolder(BatchGatePendingRecord pending)
        => _scanner.FindJob(pending.Subject.TaskKey, pending.WatchPath)?.FolderPath ?? pending.JobFolderPath;

    private static BatchMemberOwnership Ownership(BatchGateOwnership? marker, string reviewAttemptId, string batchId)
    {
        if (marker is null) return BatchMemberOwnership.None;
        if (marker.ReviewAttemptId != reviewAttemptId) return BatchMemberOwnership.Elsewhere;
        if (marker.BatchId == batchId)
            return marker.BatchRunId is null ? BatchMemberOwnership.ThisBatch : BatchMemberOwnership.ReleasedByThisBatch;
        return marker is { BatchId: null, FallbackGateActive: false, FallbackTestedSha: null,
            FallbackIntegrated: false, ContainedInSha: null }
            ? BatchMemberOwnership.Queued : BatchMemberOwnership.Elsewhere;
    }

    /// <summary>
    /// A batch is live while a flight of this host runs it or its parent, or
    /// (with <paramref name="leaseIsLiveness"/>) while any coordinator holds
    /// its scope's lease. A repair holding that lease itself passes false.
    /// </summary>
    private bool BatchLive(BatchGateManifest manifest, bool leaseIsLiveness)
    {
        lock (_runningStacks)
        lock (_runningBatches)
        {
            if (_startedBatchIds.Contains(manifest.BatchId)
                || manifest.ParentBatchId is { } parent && _startedBatchIds.Contains(parent)
                || _assemblingBatchIds.Contains(manifest.BatchId))
                return true;
        }
        return leaseIsLiveness && _leases.IsBusy(LeaseScope(manifest));
    }
}
