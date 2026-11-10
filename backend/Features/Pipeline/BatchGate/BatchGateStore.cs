using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.Diagnostics;
using Serilog;
using AgentStudio.TaskServer.Contracts.BatchGate;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Pipeline;

public sealed record BatchGateState(
    string BatchId, string MembershipDigest, BatchPhase Phase,
    string? CandidateSha, long CoordinatorFence, DateTimeOffset RecordedAtUtc,
    string? Reason = null, string? BatchRunId = null,
    long? RefMutationFence = null,
    string? BaseMoveToSha = null, string? BaseMoveOutcome = null,
    string? SuccessorBatchId = null,
    IReadOnlyDictionary<string, string>? BaseMoveMemberReasons = null,
    string? CausedByTaskKey = null);

public sealed record BatchGateWithdrawal(string BatchId, string TaskKey,
    string Actor, string Reason, DateTimeOffset AtUtc);

public sealed record BatchGateRunRecord(
    string BatchRunId, string BatchId, string MembershipDigest, string BaseSha,
    string CandidateSha, string GateProfile, string GateProfileDigest,
    string Host, long CoordinatorFence, IReadOnlyList<string> Commands,
    DateTimeOffset StartedAtUtc, string EvidencePath);

/// <summary>
/// One candidate verdict. On a red candidate <see cref="BaseCheck"/> names
/// what answered the base question (a <see cref="BatchGateBaseCheckSources"/>
/// or <see cref="GateNarrowCheckKinds"/> value) and
/// <see cref="BaseCheckMinutes"/> what that answer cost; both are null when no
/// base check was needed.
/// </summary>
public sealed record BatchGateRunVerdict(
    string BatchRunId, string BatchId, string MembershipDigest,
    string TestedCandidateSha, string GateProfileDigest,
    string Outcome, string Classification, string EvidencePath,
    DateTimeOffset CompletedAtUtc, string? BaseCheck = null,
    double? BaseCheckMinutes = null);

public sealed record BatchGatePublication(
    string BatchId, string MembershipDigest, string BatchRunId,
    string PreTipSha, string TestedCandidateSha, string VerifiedRemoteSha,
    long CoordinatorFence, long RefMutationFence, DateTimeOffset VerifiedAtUtc);

/// <summary>
/// One member's replay onto the candidate. <see cref="Strategy"/> is
/// <c>linear</c> (identity or rebase mapping, also every record written before
/// AGT-3104) or <c>merge</c>: the untouched result commit merged into the
/// candidate, attributed to that merge commit. <see cref="Reason"/> names why an
/// <c>unattributable</c> member was ejected.
/// </summary>
public sealed record BatchGateReplayRecord(
    string BatchId, string MembershipDigest, string TaskKey,
    string OriginalResultSha, string TipBeforeSha, string? TipAfterSha,
    IReadOnlyList<AgentStudio.Git.RebasedCommitReplacement> Replacements,
    IReadOnlyList<string> ConflictedFiles, string Outcome,
    DateTimeOffset RecordedAtUtc, string? Strategy = null, string? Reason = null);

public sealed record BatchGateMemberRecord(
    string TaskKey, string RunAttempt, long DeliveryEpoch, string OriginalResultSha,
    IReadOnlyList<string> ReplacementShas, string BatchId, string MembershipDigest,
    string BaseSha, string TestedCandidateSha, string GateProfileDigest,
    string BatchRunId, string Verdict, string EvidencePath,
    DateTimeOffset RecordedAtUtc);

public sealed record BatchGateReleaseResult(bool Allowed, string? FailureCode);

public sealed record BatchGatePendingRecord(
    string ReviewAttemptId, BatchGateSubject Subject, string RepositoryPath,
    string? WatchPath, string JobFolderPath, string IntegrationStrategy,
    string PipelineType, DateTimeOffset EnqueuedAtUtc);

public sealed record BatchGateExecutionFact(
    string Project, string? BatchId, string? BatchRunId,
    string TestedSha, string Outcome, string Host,
    DateTimeOffset StartedAtUtc, DateTimeOffset CompletedAtUtc,
    long QueueWaitMs, double OverloadMinutes, string EvidencePath,
    long? DurationMs = null, IReadOnlyList<string>? MemberKeys = null,
    string? NarrowCheck = null, string? EvidenceGateRunId = null);

/// <summary>
/// Review-side verification of one batch-deferred review: the builds and
/// selected tests its plan ran with aspect <c>review-verification</c>.
/// </summary>
public sealed record BatchGateReviewVerificationFact(
    string Project, string TaskKey, string ReviewAttemptId, string TestedSha,
    string Outcome, double CandidateMinutes, double BaselineMinutes,
    int CountedCommands, DateTimeOffset RecordedAtUtc);

/// <summary>
/// A base verdict any later batch on the same base and gate profile reuses.
/// Only a full answer is recorded as <c>pass</c>: a published batch, a
/// verified ancestor with no gated change since (<see cref="VerifiedSha"/>),
/// or the whole suite. A narrow check is recorded only when it is red; a red
/// part makes the base red. <see cref="Source"/> is null on records written
/// before AGT-3433, which all came from the whole suite.
/// </summary>
public sealed record BatchGateBaseVerdict(
    string Project, string BaseSha, string GateProfileDigest,
    string Outcome, string? FailureFingerprint, string Reason,
    string EvidencePath, DateTimeOffset RecordedAtUtc,
    string? Source = null, double? Minutes = null, string? VerifiedSha = null,
    bool Provisional = false, IReadOnlyList<string>? FailedTests = null);

/// <summary>
/// AGT-3434: the suites one card ran in the per-task fallback path, written
/// once per review generation when that path settles it. Per-task gates count
/// every recorded execution of the generation (merged candidate, tip verdict
/// it triggered, own-base retest); the publish gate counts when the merge into
/// the integration branch ran its suite instead of reusing the per-task verdict.
/// </summary>
/// <param name="Outcome"><c>published</c>, <c>publish-failed</c>, <c>escalated</c> or <c>merge-round</c>.</param>
public sealed record BatchGateFallbackSuiteFact(
    string Project, string TaskKey, string ReviewAttemptId, string Outcome,
    int PerTaskGateRuns, int PublishGateRuns, bool PublishVerdictReused,
    DateTimeOffset RecordedAtUtc)
{
    public int Suites => PerTaskGateRuns + PublishGateRuns;
}

/// <summary>
/// One member's isolation outcome under a red parent batch. AGT-3577:
/// <see cref="ProvingBatchId"/> and <see cref="ProvingRunId"/> name the child
/// batch run whose verdict decided the status; records written before AGT-3577
/// carry null and the state route resolves them from the evidence path.
/// </summary>
public sealed record BatchGateIsolationFinding(
    string Project, string ParentBatchId, string TaskKey, string RunAttempt,
    string ResultSha, string BaseSha, string Status, string EvidencePath,
    DateTimeOffset RecordedAtUtc, string? ProvingBatchId = null, string? ProvingRunId = null);

/// <summary>Immutable first candidate outcome for an original batch manifest.</summary>
public sealed record BatchFirstCandidateFact(
    string BatchId, string Project, DateTimeOffset AtUtc, int Size,
    double MaxRisk, IReadOnlyList<string> Areas, string OverlapGroup,
    bool Red, string Classification);

/// <summary>Cost of suites used to isolate a red candidate; one fact per parent.</summary>
public sealed record BatchIsolationCostFact(
    string BatchId, int SuiteRuns, double HostMinutes, DateTimeOffset AtUtc);

/// <summary>
/// Durable batch authority. A closed manifest is written once before assembly;
/// all later facts are append-only files carrying its digest. CreateNew and
/// durable flush prevent retries from silently changing an existing fact.
/// </summary>
public sealed partial class BatchGateStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private readonly string _root;
    private readonly HashSet<string> _missingStateWarnings = new(StringComparer.Ordinal);
    private readonly object _failureRankingLock = new();
    private sealed class FailureRankingProjection
    {
        public List<BatchTestFailure> Failures { get; } = [];
        public List<BatchGateExecutionFact> UnreadableFacts { get; } = [];
    }
    private readonly Dictionary<string, FailureRankingProjection> _failureRanking = new(StringComparer.Ordinal);
    private readonly Func<BatchGateExecutionFact, IReadOnlyList<BatchTestFailure>> _readFailureEvidence;

    public void RecordFirstCandidate(BatchFirstCandidateFact fact)
    {
        var manifest = ReadManifest(fact.BatchId);
        if (manifest.ParentBatchId is not null || manifest.Diagnostic
            || manifest.Scope.Project != fact.Project || fact.Size != manifest.Members.Count
            || fact.Size < 1 || fact.MaxRisk is < 0 or > 1)
            throw new InvalidDataException("First candidate fact does not match its batch.");
        var folder = Path.Combine(_root, "first-candidates");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, SafeName(fact.BatchId) + ".json");
        if (!File.Exists(path)) WriteOnce(path, fact);
    }

    public void RecordIsolationCost(BatchIsolationCostFact fact)
    {
        if (fact.SuiteRuns < 0 || fact.HostMinutes < 0)
            throw new ArgumentOutOfRangeException(nameof(fact));
        var folder = Path.Combine(_root, "isolation-costs");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, SafeName(fact.BatchId) + ".json");
        if (!File.Exists(path)) WriteOnce(path, fact);
    }

    public IReadOnlyList<BatchPlanObservation> ListPlanObservations(string project)
    {
        var folder = Path.Combine(_root, "first-candidates");
        if (!Directory.Exists(folder)) return [];
        return Directory.EnumerateFiles(folder, "*.json")
            .Select(Read<BatchFirstCandidateFact>)
            .Where(fact => string.Equals(fact.Project, project, StringComparison.Ordinal))
            .Select(fact =>
            {
                var path = Path.Combine(_root, "isolation-costs", SafeName(fact.BatchId) + ".json");
                var cost = File.Exists(path) ? Read<BatchIsolationCostFact>(path) : null;
                return new BatchPlanObservation(fact.BatchId, fact.AtUtc, fact.Size,
                    fact.MaxRisk, fact.Areas, fact.OverlapGroup, fact.Red,
                    cost?.SuiteRuns ?? 0, cost?.HostMinutes ?? 0,
                    fact.Classification);
            }).OrderBy(fact => fact.AtUtc).ToArray();
    }

    public void RecordIsolationFinding(BatchGateIsolationFinding finding)
    {
        var folder = Path.Combine(_root, "isolation-findings", SafeName(finding.Project));
        Directory.CreateDirectory(folder);
        var identity = string.Join("\n", finding.ParentBatchId, finding.TaskKey,
            finding.RunAttempt, finding.ResultSha, finding.BaseSha, finding.Status);
        var name = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        var path = Path.Combine(folder, name + ".json");
        if (!File.Exists(path)) WriteOnce(path, finding);
    }

    public IReadOnlyList<BatchGateIsolationFinding> ListIsolationFindings(string project)
    {
        var folder = Path.Combine(_root, "isolation-findings", SafeName(project));
        return Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.json").Select(Read<BatchGateIsolationFinding>)
                .OrderBy(finding => finding.RecordedAtUtc).ToArray()
            : [];
    }

    /// <summary>
    /// AGT-3577: the evidence file of the newest isolation finding of a member
    /// under a parent batch, or null. Only a file inside the batch store is
    /// returned, so a recorded path can never expose another file.
    /// </summary>
    public string? IsolationEvidencePath(string project, string parentBatchId, string taskKey)
    {
        var finding = ListIsolationFindings(project).LastOrDefault(item =>
            item.ParentBatchId == parentBatchId && item.TaskKey == taskKey);
        return finding is null ? null : StoredEvidenceFile(finding.EvidencePath);
    }

    /// <summary>The full path when <paramref name="evidencePath"/> is an existing file inside the batch store, else null.</summary>
    public string? StoredEvidenceFile(string evidencePath)
    {
        if (string.IsNullOrWhiteSpace(evidencePath) || !Path.IsPathRooted(evidencePath)) return null;
        var root = Path.GetFullPath(_root) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(evidencePath);
        return path.StartsWith(root, StringComparison.Ordinal) && File.Exists(path) ? path : null;
    }

    public BatchGateStore(string? root = null)
    {
        _root = root ?? LocalStateRoot.Combine("agentstudio", "batch-gates");
        _readFailureEvidence = BatchGateFailureRanking.ReadEvidence;
    }

    internal BatchGateStore(string root,
        Func<BatchGateExecutionFact, IReadOnlyList<BatchTestFailure>> readFailureEvidence) : this(root)
    {
        _readFailureEvidence = readFailureEvidence;
    }

    public string BatchDirectory(string batchId)
        => Path.Combine(_root, SafeName(batchId));

    public BatchGateBaseVerdict? ReadBaseVerdict(string project, string sha, string digest)
    {
        var path = BaseVerdictPath(project, sha, digest);
        return File.Exists(path) ? Read<BatchGateBaseVerdict>(path) : null;
    }

    public IReadOnlyList<BatchGateBaseVerdict> ListBaseVerdicts(string project)
    {
        var folder = Path.Combine(_root, "base-verdicts", SafeName(project));
        if (!Directory.Exists(folder)) return [];
        return Directory.EnumerateFiles(folder, "*.json")
            .Select(path => Read<BatchGateBaseVerdict>(path))
            .Where(verdict => verdict is not null
                && string.Equals(verdict.Project, project, StringComparison.Ordinal))
            .Select(verdict => verdict!)
            .ToArray();
    }

    public void RecordBaseVerdict(BatchGateBaseVerdict verdict)
    {
        var path = BaseVerdictPath(verdict.Project, verdict.BaseSha, verdict.GateProfileDigest);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) { WriteOnce(path, verdict); return; }
        if (!Read<BatchGateBaseVerdict>(path).Provisional || verdict.Provisional) return;
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(file, verdict, Json);
            file.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }

    public void MarkBaseVerdictProvisional(BatchGateBaseVerdict verdict)
    {
        var path = BaseVerdictPath(verdict.Project, verdict.BaseSha, verdict.GateProfileDigest);
        var current = Read<BatchGateBaseVerdict>(path);
        if (current.Provisional || current.RecordedAtUtc != verdict.RecordedAtUtc) return;
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(file, current with { Provisional = true }, Json);
            file.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }

    private string BaseVerdictPath(string project, string sha, string digest)
        => Path.Combine(_root, "base-verdicts", SafeName(project),
            SafeName(sha) + "-" + SafeName(digest) + ".json");

    public void AppendPlanDecision(string project, BatchPlanDecision decision)
    {
        var folder = Path.Combine(_root, "plans");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, SafeName(project) + ".jsonl");
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(stream, decision, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        stream.WriteByte((byte)'\n');
        stream.Flush(flushToDisk: true);
        var latest = Path.Combine(folder, SafeName(project) + ".latest.json");
        var temporary = latest + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(file, decision, Json);
            file.Flush(flushToDisk: true);
        }
        File.Move(temporary, latest, overwrite: true);
    }

    public BatchPlanDecision? LatestPlanDecision(string project)
    {
        var path = Path.Combine(_root, "plans", SafeName(project) + ".latest.json");
        if (!File.Exists(path)) return null;
        return Read<BatchPlanDecision>(path);
    }

    public void RequestCloseNow(string project)
    {
        var folder = Path.Combine(_root, "requests");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, SafeName(project) + ".close"),
            DateTimeOffset.UtcNow.ToString("O"));
    }

    public bool HasCloseNow(string project)
        => File.Exists(Path.Combine(_root, "requests", SafeName(project) + ".close"));

    public void ClearCloseNow(string project)
    {
        var path = Path.Combine(_root, "requests", SafeName(project) + ".close");
        if (File.Exists(path)) File.Delete(path);
    }

    public void RequestEject(string project, string taskKey)
    {
        var folder = Path.Combine(_root, "requests", "eject", SafeName(project));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, SafeName(taskKey)), DateTimeOffset.UtcNow.ToString("O"));
    }

    public void RecordWithdrawal(BatchGateWithdrawal withdrawal)
    {
        var manifest = ReadManifest(withdrawal.BatchId);
        if (!manifest.Members.Any(member => member.TaskKey == withdrawal.TaskKey))
            throw new InvalidDataException("The withdrawn card is not a batch member.");
        var folder = Path.Combine(BatchDirectory(withdrawal.BatchId), "withdrawals");
        Directory.CreateDirectory(folder);
        WriteOnce(Path.Combine(folder, Guid.NewGuid().ToString("N") + ".json"), withdrawal);
    }

    public IReadOnlyList<BatchGateWithdrawal> ListWithdrawals(string batchId)
    {
        var folder = Path.Combine(BatchDirectory(batchId), "withdrawals");
        return Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.json").Select(Read<BatchGateWithdrawal>).ToArray()
            : [];
    }

    public bool HasEject(string project, string taskKey)
        => File.Exists(Path.Combine(_root, "requests", "eject", SafeName(project), SafeName(taskKey)));

    public void ClearEject(string project, string taskKey)
        => File.Delete(Path.Combine(_root, "requests", "eject", SafeName(project), SafeName(taskKey)));

    public void RecordAdvice(string batchId, BatchAdvisorView advice)
        => WriteOnce(Path.Combine(BatchDirectory(batchId), "advisor.json"), advice);

    public BatchGatePendingRecord Enqueue(BatchGatePendingRecord pending)
    {
        if (string.IsNullOrWhiteSpace(pending.ReviewAttemptId)
            || string.IsNullOrWhiteSpace(pending.RepositoryPath)
            || pending.Subject.ReviewCompletedAtUtc == default)
            throw new InvalidDataException("The pending batch subject is incomplete.");
        var folder = Path.Combine(_root, "pending");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, SafeName(pending.ReviewAttemptId) + ".json");
        if (File.Exists(path))
        {
            var previous = Read<BatchGatePendingRecord>(path);
            if (previous.Subject.TaskKey != pending.Subject.TaskKey
                || previous.Subject.ResultSha != pending.Subject.ResultSha
                || previous.Subject.RunAttempt != pending.Subject.RunAttempt)
                throw new InvalidDataException("A pending review attempt changed identity.");
            return previous;
        }
        // The sequence is durable and monotonically assigned under one exclusive
        // file lock, including after a process restart.
        using var guard = new FileStream(Path.Combine(folder, "enqueue.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (File.Exists(path))
        {
            var previous = Read<BatchGatePendingRecord>(path);
            if (previous.Subject.TaskKey != pending.Subject.TaskKey
                || previous.Subject.ResultSha != pending.Subject.ResultSha
                || previous.Subject.RunAttempt != pending.Subject.RunAttempt)
                throw new InvalidDataException("A pending review attempt changed identity.");
            return previous;
        }
        var sequencePath = Path.Combine(folder, "sequence");
        var prior = File.Exists(sequencePath)
            ? long.Parse(File.ReadAllText(sequencePath), System.Globalization.CultureInfo.InvariantCulture)
            : 0;
        prior = Math.Max(prior, Directory.EnumerateFiles(folder, "*.json")
            .Select(file => Read<BatchGatePendingRecord>(file).Subject.EnqueueSequence)
            .DefaultIfEmpty(0).Max());
        var next = checked(prior + 1);
        var sequenced = pending with { Subject = pending.Subject with { EnqueueSequence = next } };
        WriteOnce(path, sequenced);
        File.WriteAllText(sequencePath, next.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return sequenced;
    }

    public BatchGatePendingRecord? ReadPending(string reviewAttemptId)
    {
        var path = Path.Combine(_root, "pending", SafeName(reviewAttemptId) + ".json");
        return File.Exists(path) ? Read<BatchGatePendingRecord>(path) : null;
    }

    public IReadOnlyList<BatchGatePendingRecord> ListPending()
    {
        var folder = Path.Combine(_root, "pending");
        if (!Directory.Exists(folder)) return [];
        return Directory.EnumerateFiles(folder, "*.json")
            .Where(path => !File.Exists(path + ".resolved"))
            .Select(Read<BatchGatePendingRecord>)
            .OrderBy(item => item.Subject.EnqueueSequence)
            .ToArray();
    }

    public IReadOnlyList<BatchGatePendingRecord> ListAllPending()
    {
        var folder = Path.Combine(_root, "pending");
        return Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.json")
                .Select(Read<BatchGatePendingRecord>).ToArray()
            : [];
    }

    /// <summary>
    /// Every readable closed manifest in formation order: close time, then
    /// batch id; an unreadable one is quarantined and skipped (AGT-3681). Batch
    /// folders are named by hash, so their enumeration order carries no meaning
    /// and no caller may depend on it (AGT-3710).
    /// </summary>
    public IReadOnlyList<BatchGateManifest> ListManifests()
    {
        var manifests = new List<BatchGateManifest>();
        foreach (var folder in ListManifestFolders())
            if (TryReadManifestIn(folder, out var manifest)) manifests.Add(manifest);
        return InFormationOrder(manifests);
    }

    private static BatchGateManifest[] InFormationOrder(IEnumerable<BatchGateManifest> manifests)
        => manifests.OrderBy(manifest => manifest.ClosedAtUtc)
            .ThenBy(manifest => manifest.BatchId, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// Folders that hold a closed manifest. A manifest is written once, so a
    /// caller that polls can read each folder's manifest once and cache it.
    /// </summary>
    public IReadOnlyList<string> ListManifestFolders()
        => !Directory.Exists(_root) ? []
            : Directory.EnumerateDirectories(_root)
                .Where(dir => File.Exists(Path.Combine(dir, "manifest.json")))
                .Order(StringComparer.Ordinal).ToArray();

    public BatchGateManifest ReadManifestIn(string folder)
        => ReadRecord<BatchGateManifest>(Path.Combine(folder, "manifest.json"),
            BatchGateRecordKinds.Manifest, null);

    /// <summary>False, with the record quarantined, when the folder's manifest does not read.</summary>
    public bool TryReadManifestIn(string folder, out BatchGateManifest manifest)
        => TryReadRecord(Path.Combine(folder, "manifest.json"), BatchGateRecordKinds.Manifest, null, out manifest);

    public IReadOnlyList<BatchGateRunRecord> ListRuns(string batchId)
    {
        var folder = Path.Combine(BatchDirectory(batchId), "runs");
        return Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.json")
                .Where(path => !path.EndsWith(".verdict.json", StringComparison.Ordinal))
                .Select(path => TryReadRecord<BatchGateRunRecord>(path, BatchGateRecordKinds.Run, batchId,
                    out var run) ? run : null)
                .OfType<BatchGateRunRecord>().ToArray()
            : [];
    }

    public BatchGateRunVerdict? ReadVerdict(string batchId, string runId)
    {
        var path = Path.Combine(BatchDirectory(batchId), "runs",
            SafeName(runId) + ".verdict.json");
        return File.Exists(path) ? Read<BatchGateRunVerdict>(path) : null;
    }

    public BatchGatePublication? ReadPublication(string batchId)
    {
        var path = Path.Combine(BatchDirectory(batchId), "publication.json");
        return File.Exists(path) ? Read<BatchGatePublication>(path) : null;
    }

    public BatchGateReplayRecord? TryReadReplay(string batchId, string taskKey)
    {
        var path = Path.Combine(BatchDirectory(batchId), "replays", SafeName(taskKey) + ".json");
        return File.Exists(path) ? Read<BatchGateReplayRecord>(path) : null;
    }

    public BatchGateMemberRecord? TryReadMember(string batchId, string taskKey, string runId)
    {
        var path = Path.Combine(BatchDirectory(batchId), "members",
            SafeName(taskKey), SafeName(runId) + ".json");
        return File.Exists(path) ? Read<BatchGateMemberRecord>(path) : null;
    }

    public void RecordExecution(BatchGateExecutionFact fact)
    {
        lock (_failureRankingLock)
        {
            // Initialization scans persisted executions under this lock. Keep the
            // write and projection update together so that scan cannot include
            // this fact before the writer adds its evidence to the projection.
            var folder = Path.Combine(_root, "executions");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".json");
            WriteOnce(path, fact);
            if (_failureRanking.TryGetValue(fact.Project, out var projection))
                AppendFailureEvidence(projection, fact);
        }
    }

    public BatchTestFailureReport FailedTestRanking(string project, DateTimeOffset now, int windowDays = 7)
    {
        if (windowDays is < 1 or > 90) throw new ArgumentOutOfRangeException(nameof(windowDays));
        lock (_failureRankingLock)
        {
            var projection = FailureProjection(project);
            projection.Failures.RemoveAll(failure => failure.AtUtc < now.AddDays(-90));
            return new BatchTestFailureReport(project, windowDays,
                BatchGateFailureRanking.Rank(projection.Failures, now, windowDays),
                projection.UnreadableFacts.Count(fact => fact.CompletedAtUtc >= now.AddDays(-windowDays)
                    && fact.CompletedAtUtc <= now));
        }
    }

    /// <summary>
    /// AGT-3577: the failed tests of one batch run, read from the same cached
    /// projection as the failed-test ranking. It leaves unreadable evidence to
    /// the ranking's retry, so the polled state route reads no evidence file
    /// once the projection exists.
    /// </summary>
    public IReadOnlyList<BatchTestFailure> RunFailures(string project, string batchId, string batchRunId)
    {
        lock (_failureRankingLock)
            return FailureProjection(project, retryUnreadable: false).Failures
                .Where(failure => failure.BatchId == batchId && failure.RunId == batchRunId).ToArray();
    }

    // Callers hold _failureRankingLock.
    private FailureRankingProjection FailureProjection(string project, bool retryUnreadable = true)
    {
        if (!_failureRanking.TryGetValue(project, out var projection))
        {
            projection = new FailureRankingProjection();
            foreach (var fact in ListExecutions(project).Where(fact => fact.BatchId is not null))
                AppendFailureEvidence(projection, fact);
            _failureRanking[project] = projection;
        }
        else if (retryUnreadable && projection.UnreadableFacts.Count > 0)
        {
            var retry = projection.UnreadableFacts.ToArray();
            projection.UnreadableFacts.Clear();
            foreach (var fact in retry) AppendFailureEvidence(projection, fact);
        }
        return projection;
    }

    private void AppendFailureEvidence(FailureRankingProjection projection, BatchGateExecutionFact fact)
    {
        try { projection.Failures.AddRange(_readFailureEvidence(WithMembers(fact))); }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            projection.UnreadableFacts.Add(fact);
            SilentCatch.Note(exception, "Batch gate failure ranking will retry unreadable evidence");
        }
    }

    private BatchGateExecutionFact WithMembers(BatchGateExecutionFact fact)
    {
        if (fact.MemberKeys is { Count: > 0 } || fact.BatchId is null) return fact;
        try
        {
            return fact with { MemberKeys = ReadManifest(fact.BatchId).Members
                .Select(member => member.TaskKey).ToArray() };
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
        {
            SilentCatch.Note(exception, "Batch gate failure ranking could not read a historical manifest");
            return fact;
        }
    }

    public IReadOnlyList<BatchGateExecutionFact> ListExecutions(string project)
    {
        return ListAllExecutions()
            .Where(fact => string.Equals(fact.Project, project, StringComparison.Ordinal))
            .ToArray();
    }

    public IReadOnlyList<BatchGateExecutionFact> ListAllExecutions()
    {
        var folder = Path.Combine(_root, "executions");
        return Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.json")
                .Select(Read<BatchGateExecutionFact>)
                .ToArray()
            : [];
    }

    /// <summary>Writes the review generation's fact once; false when it already exists.</summary>
    public bool RecordFallbackSuites(BatchGateFallbackSuiteFact fact)
    {
        var folder = Path.Combine(_root, "fallback-suites");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, SafeName(fact.ReviewAttemptId) + ".json");
        if (File.Exists(path)) return false;
        try
        {
            WriteOnce(path, fact);
            return true;
        }
        catch (IOException) when (File.Exists(path))
        {
            // A concurrent settlement of the same generation won the write.
            return false;
        }
    }

    public IReadOnlyList<BatchGateFallbackSuiteFact> ListFallbackSuites(string project)
    {
        var folder = Path.Combine(_root, "fallback-suites");
        return Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.json")
                .Select(Read<BatchGateFallbackSuiteFact>)
                .Where(fact => string.Equals(fact.Project, project, StringComparison.Ordinal))
                .ToArray()
            : [];
    }

    /// <summary>Writes the review attempt's fact once; false when it already exists.</summary>
    public bool RecordReviewVerification(BatchGateReviewVerificationFact fact)
    {
        var folder = Path.Combine(_root, "review-verification");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, SafeName(fact.ReviewAttemptId) + ".json");
        if (File.Exists(path)) return false;
        try
        {
            WriteOnce(path, fact);
            return true;
        }
        catch (IOException) when (File.Exists(path))
        {
            // A concurrent replay of the same settlement won the write.
            return false;
        }
    }

    public IReadOnlyList<BatchGateReviewVerificationFact> ListReviewVerifications(string project)
    {
        var folder = Path.Combine(_root, "review-verification");
        return Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.json")
                .Select(Read<BatchGateReviewVerificationFact>)
                .Where(fact => string.Equals(fact.Project, project, StringComparison.Ordinal))
                .ToArray()
            : [];
    }

    /// <summary>Formed or in-flight manifests in formation order, as <see cref="ListManifests"/>.</summary>
    public IReadOnlyList<BatchGateManifest> ListRecoverableManifests()
        => ListManifests().Where(IsRecoverable).ToArray();

    private bool IsRecoverable(BatchGateManifest manifest)
    {
        var state = LatestState(manifest.BatchId);
        var phase = state.Phase;
        // An unreadable state row is reported by the quarantine, not as missing state.
        if (phase == BatchPhase.Unknown && state.Reason != StateUnreadableReason)
        {
            lock (_missingStateWarnings)
            {
                if (_missingStateWarnings.Add(manifest.BatchId))
                    Log.Warning("Batch gate manifest {BatchId} has no state rows; recovery will skip it", manifest.BatchId);
            }
            return false;
        }
        return phase is BatchPhase.Formed or BatchPhase.Assembling
            or BatchPhase.Assembled or BatchPhase.Running;
    }

    // A pause stays in the append-only history, but an unpublished paused batch
    // must hand its members back; the pilot resolves each one on its next tick.
    public IReadOnlyList<BatchGateManifest> ListPausedManifests()
        => ListManifests()
            .Where(manifest => LatestState(manifest.BatchId).Phase == BatchPhase.Paused
                && ReadPublication(manifest.BatchId) is null)
            .ToArray();

    public void ResolvePending(string reviewAttemptId, string outcome)
    {
        var path = Path.Combine(_root, "pending", SafeName(reviewAttemptId) + ".json");
        if (!File.Exists(path)) throw new FileNotFoundException("Pending batch member is missing.", path);
        var resolved = path + ".resolved";
        if (!File.Exists(resolved)) WriteOnce(resolved, new { outcome, atUtc = DateTimeOffset.UtcNow });
    }

    public void CloseManifest(BatchGateManifest manifest)
    {
        if (manifest.Members.Count == 0 || manifest.Members.Count > 8
            || manifest.MembershipDigest != BatchGatePolicy.MembershipDigest(
                manifest.BaseSha, manifest.Scope, manifest.Members))
            throw new InvalidDataException("The closed batch manifest has an invalid membership digest.");
        var dir = BatchDirectory(manifest.BatchId);
        Directory.CreateDirectory(dir);
        var stateFolder = Path.Combine(dir, "state");
        Directory.CreateDirectory(stateFolder);
        var firstStatePath = Path.Combine(stateFolder, "00000001.json");
        if (!File.Exists(firstStatePath))
            WriteOnce(firstStatePath, new BatchGateState(
                manifest.BatchId, manifest.MembershipDigest, BatchPhase.Formed,
                null, 0, DateTimeOffset.UtcNow));
        else if (!File.Exists(Path.Combine(dir, "manifest.json")))
        {
            var existing = Read<BatchGateState>(firstStatePath);
            if (existing.BatchId != manifest.BatchId
                || existing.MembershipDigest != manifest.MembershipDigest
                || existing.Phase != BatchPhase.Formed)
                throw new InvalidDataException("The initial batch state does not match its manifest.");
        }
        WriteOnce(Path.Combine(dir, "manifest.json"), manifest);
        lock (_deliveryIndexLock)
        {
            if (_deliveryIndex is not null) AddToDeliveryIndex(manifest);
        }
    }

    public BatchGateManifest ReadManifest(string batchId)
        => ReadRecord<BatchGateManifest>(Path.Combine(BatchDirectory(batchId), "manifest.json"),
            BatchGateRecordKinds.Manifest, batchId);

    public void AppendState(BatchGateState state)
    {
        var manifest = ReadManifest(state.BatchId);
        if (state.MembershipDigest != manifest.MembershipDigest
            || state.CandidateSha is { } sha && !IsSha(sha))
            throw new InvalidDataException("Batch state does not match its closed manifest.");
        var folder = Path.Combine(BatchDirectory(state.BatchId), "state");
        Directory.CreateDirectory(folder);
        var number = Directory.EnumerateFiles(folder, "*.json").Count() + 1;
        WriteOnce(Path.Combine(folder, $"{number:D8}.json"), state);
    }

    public BatchGateState LatestState(string batchId)
    {
        var folder = Path.Combine(BatchDirectory(batchId), "state");
        var path = Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.json")
                .OrderBy(x => x, StringComparer.Ordinal).LastOrDefault()
            : null;
        if (path is not null)
        {
            if (TryReadRecord<BatchGateState>(path, BatchGateRecordKinds.State, batchId, out var state))
                return state;
            // The newest row decides the phase; an older readable row could
            // name a phase the batch already left, so the batch reads Unknown.
            var manifestPath = Path.Combine(BatchDirectory(batchId), "manifest.json");
            var digest = File.Exists(manifestPath)
                && TryReadRecord<BatchGateManifest>(manifestPath, BatchGateRecordKinds.Manifest, batchId, out var owner)
                ? owner.MembershipDigest : string.Empty;
            return new BatchGateState(batchId, digest, BatchPhase.Unknown, null, 0,
                new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero), StateUnreadableReason);
        }
        var manifestPath = Path.Combine(BatchDirectory(batchId), "manifest.json");
        var manifest = File.Exists(manifestPath)
            && TryReadRecord<BatchGateManifest>(manifestPath, BatchGateRecordKinds.Manifest, batchId, out var owner)
            ? owner : null;
        return new BatchGateState(batchId, manifest?.MembershipDigest ?? string.Empty,
            BatchPhase.Unknown, null, 0, manifest?.ClosedAtUtc ?? DateTimeOffset.MinValue);
    }

    public void RecordRun(BatchGateRunRecord run)
    {
        var manifest = ReadManifest(run.BatchId);
        var state = LatestState(run.BatchId);
        if (run.MembershipDigest != manifest.MembershipDigest
            || run.BaseSha != manifest.BaseSha
            || run.GateProfile != manifest.Scope.GateProfile
            || run.GateProfileDigest != manifest.Scope.GateProfileDigest
            || !IsSha(run.CandidateSha)
            || run.CoordinatorFence <= 0 || run.Commands.Count == 0
            || string.IsNullOrWhiteSpace(run.Host)
            || state.Phase != BatchPhase.Assembled
            || state.CandidateSha != run.CandidateSha
            || state.CoordinatorFence != run.CoordinatorFence
            || string.IsNullOrWhiteSpace(run.EvidencePath))
            throw new InvalidDataException("Batch run identity is incomplete or mismatched.");
        var folder = Path.Combine(BatchDirectory(run.BatchId), "runs");
        Directory.CreateDirectory(folder);
        WriteOnce(Path.Combine(folder, SafeName(run.BatchRunId) + ".json"), run);
    }

    public BatchGateRunRecord ReadRun(string batchId, string runId)
        => ReadRecord<BatchGateRunRecord>(Path.Combine(BatchDirectory(batchId), "runs", SafeName(runId) + ".json"),
            BatchGateRecordKinds.Run, batchId);

    public void RecordReplay(BatchGateReplayRecord replay)
    {
        var manifest = ReadManifest(replay.BatchId);
        var member = manifest.Members.SingleOrDefault(x => x.TaskKey == replay.TaskKey);
        if (replay.MembershipDigest != manifest.MembershipDigest
            || member is null || replay.OriginalResultSha != member.ResultSha
            || !IsSha(replay.TipBeforeSha)
            || replay.TipAfterSha is { } after && !IsSha(after))
            throw new InvalidDataException("Batch replay identity does not match the manifest.");
        // A merged member maps its untouched result to exactly one candidate
        // merge commit; nothing else of the delivery was rewritten.
        if (replay.Strategy == BatchReplayStrategies.Merge && replay.Outcome == "admitted"
            && (replay.Replacements.Count != 1
                || !string.Equals(replay.Replacements[0].OriginalSha, replay.OriginalResultSha,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(replay.Replacements[0].RebasedSha, replay.TipAfterSha,
                    StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Batch merge replay must map the result SHA to its merge commit.");
        var folder = Path.Combine(BatchDirectory(replay.BatchId), "replays");
        Directory.CreateDirectory(folder);
        WriteOnce(Path.Combine(folder, SafeName(replay.TaskKey) + ".json"), replay);
    }

    public BatchGateReplayRecord ReadReplay(string batchId, string taskKey)
        => Read<BatchGateReplayRecord>(Path.Combine(BatchDirectory(batchId),
            "replays", SafeName(taskKey) + ".json"));

    public void RecordVerdict(BatchGateRunVerdict verdict)
    {
        var run = ReadRun(verdict.BatchId, verdict.BatchRunId);
        if (verdict.MembershipDigest != run.MembershipDigest
            || verdict.TestedCandidateSha != run.CandidateSha
            || verdict.GateProfileDigest != run.GateProfileDigest
            || !string.Equals(verdict.EvidencePath, run.EvidencePath, StringComparison.Ordinal)
            || !File.Exists(run.EvidencePath))
            throw new InvalidDataException("Batch verdict does not match its recorded run.");
        WriteOnce(Path.Combine(BatchDirectory(verdict.BatchId), "runs",
            SafeName(verdict.BatchRunId) + ".verdict.json"), verdict);
    }

    public void RecordPublication(BatchGatePublication publication)
    {
        var manifest = ReadManifest(publication.BatchId);
        var run = ReadRun(publication.BatchId, publication.BatchRunId);
        var verdict = Read<BatchGateRunVerdict>(Path.Combine(BatchDirectory(publication.BatchId),
            "runs", SafeName(publication.BatchRunId) + ".verdict.json"));
        if (publication.MembershipDigest != run.MembershipDigest
            || publication.PreTipSha != run.BaseSha
            || publication.TestedCandidateSha != run.CandidateSha
            || publication.VerifiedRemoteSha != run.CandidateSha
            || publication.CoordinatorFence < run.CoordinatorFence
            || publication.RefMutationFence <= 0 || verdict.Outcome != "pass")
            throw new InvalidDataException("Untested or unverified batch publication.");
        if (manifest.StackedOnBatchId is { } predecessorId
            && !BatchStackingPolicy.CanPublish(ReadManifest(predecessorId),
                LatestState(predecessorId), ReadPublication(predecessorId), manifest))
            throw new InvalidDataException("Stacked predecessor has no verified publication.");
        WriteOnce(Path.Combine(BatchDirectory(publication.BatchId), "publication.json"), publication);
    }

    public void RecordMember(BatchGateMemberRecord record)
    {
        var manifest = ReadManifest(record.BatchId);
        var member = manifest.Members.SingleOrDefault(x => x.TaskKey == record.TaskKey);
        var run = ReadRun(record.BatchId, record.BatchRunId);
        var replay = ReadReplay(record.BatchId, record.TaskKey);
        if (member is null || record.RunAttempt != member.RunAttempt
            || record.DeliveryEpoch != member.DeliveryEpoch
            || !string.Equals(record.OriginalResultSha, member.ResultSha, StringComparison.OrdinalIgnoreCase)
            || record.MembershipDigest != manifest.MembershipDigest
            || record.BaseSha != manifest.BaseSha
            || record.TestedCandidateSha != run.CandidateSha
            || record.GateProfileDigest != run.GateProfileDigest
            || !string.Equals(record.EvidencePath, run.EvidencePath, StringComparison.Ordinal)
            || record.ReplacementShas.Count == 0
            || record.ReplacementShas.Any(sha => !IsSha(sha))
            || replay.Outcome != "admitted"
            || replay.TipAfterSha is null
            || !string.Equals(record.ReplacementShas[^1], replay.TipAfterSha,
                StringComparison.OrdinalIgnoreCase)
            || !record.ReplacementShas.SequenceEqual(
                replay.Replacements.Select(x => x.RebasedSha),
                StringComparer.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(record.EvidencePath)
            || !File.Exists(record.EvidencePath))
            throw new InvalidDataException("batch-gate-evidence-missing");
        var folder = Path.Combine(BatchDirectory(record.BatchId), "members", SafeName(record.TaskKey));
        Directory.CreateDirectory(folder);
        WriteOnce(Path.Combine(folder, SafeName(record.BatchRunId) + ".json"), record);
    }

    public bool CanRelease(BatchGateSubject current, string batchId, string runId)
    {
        try
        {
            var manifest = ReadManifest(batchId);
            var run = ReadRun(batchId, runId);
            var path = Path.Combine(BatchDirectory(batchId), "members",
                SafeName(current.TaskKey), SafeName(runId) + ".json");
            var record = Read<BatchGateMemberRecord>(path);
            var verdict = Read<BatchGateRunVerdict>(Path.Combine(BatchDirectory(batchId),
                "runs", SafeName(runId) + ".verdict.json"));
            var publication = Read<BatchGatePublication>(Path.Combine(BatchDirectory(batchId),
                "publication.json"));
            var frozen = manifest.Members.Single(member => member.TaskKey == current.TaskKey);
            var replay = ReadReplay(batchId, current.TaskKey);
            return current.CurrentGeneration
                && current.ResultRef == frozen.ResultRef
                && current.FencingToken == frozen.FencingToken
                && current.DeliveryEpoch == frozen.DeliveryEpoch
                && record.TaskKey == current.TaskKey
                && record.RunAttempt == current.RunAttempt
                && record.DeliveryEpoch == current.DeliveryEpoch
                && record.OriginalResultSha == current.ResultSha
                && record.MembershipDigest == manifest.MembershipDigest
                && record.BaseSha == manifest.BaseSha
                && record.TestedCandidateSha == run.CandidateSha
                && record.GateProfileDigest == run.GateProfileDigest
                && record.BatchRunId == run.BatchRunId
                && record.ReplacementShas.Count > 0
                && record.ReplacementShas.All(IsSha)
                && replay.Outcome == "admitted"
                && replay.OriginalResultSha == record.OriginalResultSha
                && string.Equals(record.ReplacementShas[^1], replay.TipAfterSha,
                    StringComparison.OrdinalIgnoreCase)
                && record.ReplacementShas.SequenceEqual(
                    replay.Replacements.Select(item => item.RebasedSha),
                    StringComparer.OrdinalIgnoreCase)
                && record.Verdict == "pass"
                && string.Equals(record.EvidencePath, run.EvidencePath, StringComparison.Ordinal)
                && File.Exists(record.EvidencePath)
                && verdict.Outcome == "pass"
                && verdict.EvidencePath == run.EvidencePath
                && verdict.TestedCandidateSha == run.CandidateSha
                && verdict.GateProfileDigest == run.GateProfileDigest
                && publication.BatchRunId == runId
                && publication.MembershipDigest == manifest.MembershipDigest
                && publication.TestedCandidateSha == run.CandidateSha
                && publication.VerifiedRemoteSha == run.CandidateSha;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or InvalidOperationException)
        {
            return false;
        }
    }

    public BatchGateReleaseResult CheckRelease(BatchGateSubject current, string batchId, string runId)
        => CanRelease(current, batchId, runId)
            ? new BatchGateReleaseResult(true, null)
            : new BatchGateReleaseResult(false, "batch-gate-evidence-missing");

    private static void WriteOnce<T>(string path, T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew,
                       FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static T Read<T>(string path)
        => JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), Json)
            ?? throw new InvalidDataException($"Missing {typeof(T).Name} at {path}.");

    private static string SafeName(string value)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool IsSha(string value)
        => value is { Length: 40 or 64 } && value.All(Uri.IsHexDigit);
}
