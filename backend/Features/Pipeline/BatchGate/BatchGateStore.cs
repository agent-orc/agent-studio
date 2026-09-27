using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentStudio.Pipeline;

public sealed record BatchGateState(
    string BatchId, string MembershipDigest, BatchPhase Phase,
    string? CandidateSha, long CoordinatorFence, DateTimeOffset RecordedAtUtc,
    string? Reason = null);

public sealed record BatchGateRunRecord(
    string BatchRunId, string BatchId, string MembershipDigest, string BaseSha,
    string CandidateSha, string GateProfile, string GateProfileDigest,
    string Host, long CoordinatorFence, IReadOnlyList<string> Commands,
    DateTimeOffset StartedAtUtc, string EvidencePath);

public sealed record BatchGateRunVerdict(
    string BatchRunId, string BatchId, string MembershipDigest,
    string TestedCandidateSha, string GateProfileDigest,
    string Outcome, string Classification, string EvidencePath,
    DateTimeOffset CompletedAtUtc);

public sealed record BatchGatePublication(
    string BatchId, string MembershipDigest, string BatchRunId,
    string PreTipSha, string TestedCandidateSha, string VerifiedRemoteSha,
    long CoordinatorFence, long RefMutationFence, DateTimeOffset VerifiedAtUtc);

public sealed record BatchGateReplayRecord(
    string BatchId, string MembershipDigest, string TaskKey,
    string OriginalResultSha, string TipBeforeSha, string? TipAfterSha,
    IReadOnlyList<AgentStudio.Git.RebasedCommitReplacement> Replacements,
    IReadOnlyList<string> ConflictedFiles, string Outcome,
    DateTimeOffset RecordedAtUtc);

public sealed record BatchGateMemberRecord(
    string TaskKey, string RunAttempt, long DeliveryEpoch, string OriginalResultSha,
    IReadOnlyList<string> ReplacementShas, string BatchId, string MembershipDigest,
    string BaseSha, string TestedCandidateSha, string GateProfileDigest,
    string BatchRunId, string Verdict, string EvidencePath,
    DateTimeOffset RecordedAtUtc);

public sealed record BatchGateReleaseResult(bool Allowed, string? FailureCode);

/// <summary>
/// Durable batch authority. A closed manifest is written once before assembly;
/// all later facts are append-only files carrying its digest. CreateNew and
/// durable flush prevent retries from silently changing an existing fact.
/// </summary>
public sealed class BatchGateStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private readonly string _root;

    public BatchGateStore(string? root = null)
    {
        var data = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _root = root ?? Path.Combine(data, "agentstudio", "batch-gates");
    }

    public string BatchDirectory(string batchId)
        => Path.Combine(_root, SafeName(batchId));

    public void CloseManifest(BatchGateManifest manifest)
    {
        if (manifest.Members.Count == 0 || manifest.Members.Count > 8
            || manifest.MembershipDigest != BatchGatePolicy.MembershipDigest(
                manifest.BaseSha, manifest.Scope, manifest.Members))
            throw new InvalidDataException("The closed batch manifest has an invalid membership digest.");
        var dir = BatchDirectory(manifest.BatchId);
        Directory.CreateDirectory(dir);
        WriteOnce(Path.Combine(dir, "manifest.json"), manifest);
        AppendState(new BatchGateState(
            manifest.BatchId, manifest.MembershipDigest, BatchPhase.Formed,
            null, 0, DateTimeOffset.UtcNow));
    }

    public BatchGateManifest ReadManifest(string batchId)
        => Read<BatchGateManifest>(Path.Combine(BatchDirectory(batchId), "manifest.json"));

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
        var path = Directory.EnumerateFiles(folder, "*.json")
            .OrderBy(x => x, StringComparer.Ordinal).Last();
        return Read<BatchGateState>(path);
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
        => Read<BatchGateRunRecord>(Path.Combine(BatchDirectory(batchId), "runs", SafeName(runId) + ".json"));

    public void RecordReplay(BatchGateReplayRecord replay)
    {
        var manifest = ReadManifest(replay.BatchId);
        var member = manifest.Members.SingleOrDefault(x => x.TaskKey == replay.TaskKey);
        if (replay.MembershipDigest != manifest.MembershipDigest
            || member is null || replay.OriginalResultSha != member.ResultSha
            || !IsSha(replay.TipBeforeSha)
            || replay.TipAfterSha is { } after && !IsSha(after))
            throw new InvalidDataException("Batch replay identity does not match the manifest.");
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
            || string.IsNullOrWhiteSpace(verdict.EvidencePath))
            throw new InvalidDataException("Batch verdict does not match its recorded run.");
        WriteOnce(Path.Combine(BatchDirectory(verdict.BatchId), "runs",
            SafeName(verdict.BatchRunId) + ".verdict.json"), verdict);
    }

    public void RecordPublication(BatchGatePublication publication)
    {
        var run = ReadRun(publication.BatchId, publication.BatchRunId);
        var verdict = Read<BatchGateRunVerdict>(Path.Combine(BatchDirectory(publication.BatchId),
            "runs", SafeName(publication.BatchRunId) + ".verdict.json"));
        if (publication.MembershipDigest != run.MembershipDigest
            || publication.PreTipSha != run.BaseSha
            || publication.TestedCandidateSha != run.CandidateSha
            || publication.VerifiedRemoteSha != run.CandidateSha
            || publication.CoordinatorFence != run.CoordinatorFence
            || publication.RefMutationFence <= 0 || verdict.Outcome != "pass")
            throw new InvalidDataException("Untested or unverified batch publication.");
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
            || record.ReplacementShas.Count == 0
            || record.ReplacementShas.Any(sha => !IsSha(sha))
            || replay.Outcome != "admitted"
            || replay.TipAfterSha is null
            || !record.ReplacementShas.SequenceEqual(
                replay.Replacements.Select(x => x.RebasedSha),
                StringComparer.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(record.EvidencePath))
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
            return current.CurrentGeneration
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
                && record.Verdict == "pass"
                && verdict.Outcome == "pass"
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
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static T Read<T>(string path)
        => JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), Json)
            ?? throw new InvalidDataException($"Missing {typeof(T).Name} at {path}.");

    private static string SafeName(string value)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool IsSha(string value)
        => value is { Length: 40 or 64 } && value.All(Uri.IsHexDigit);
}
