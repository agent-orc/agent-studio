using System.Globalization;
using System.Text.Json;
using AgentStudio.OrchestrationDecisions;

namespace AgentStudio.Pipeline;

/// <summary>
/// AGT-3563: the versioned phase and claim record of one batch. It is the
/// commit point of every phase change: <see cref="Version"/> is also the number
/// of the matching state history entry, and <see cref="Members"/> holds each
/// claimed member's disposition keyed by its immutable review attempt.
/// </summary>
public sealed record BatchPhaseRecord(
    string BatchId, string MembershipDigest, long Version, BatchGateState State,
    IReadOnlyList<BatchMemberClaim> Members);

public sealed record BatchPhaseTransitionResult(bool Accepted, string? Refusal, BatchPhaseRecord? Record);

public enum BatchPhaseRepair { None, HistoryWritten, FirstRecordWritten }

/// <summary>A phase change the transition rule refused; nothing was written.</summary>
public sealed class BatchPhaseTransitionRefusedException(string batchId, BatchPhase? from,
    BatchPhase to, string? refusal)
    : InvalidOperationException($"Batch {batchId} refused {from?.ToString() ?? "none"} -> {to}: {refusal}.")
{
    public string? Refusal { get; } = refusal;
}

public sealed partial class BatchGateStore
{
    private const string PhaseRecordName = "phase.json";
    private const int PhaseLockAttempts = 1000;
    private static readonly TimeSpan PhaseLockRetry = TimeSpan.FromMilliseconds(10);

    public BatchPhaseRecord? ReadPhaseRecord(string batchId)
    {
        var path = Path.Combine(BatchDirectory(batchId), PhaseRecordName);
        return File.Exists(path) ? Read<BatchPhaseRecord>(path) : null;
    }

    /// <summary>True once the batch has a phase record or a state history entry.</summary>
    public bool HasState(string batchId)
        => File.Exists(Path.Combine(BatchDirectory(batchId), PhaseRecordName))
           || HighestHistoryNumber(BatchDirectory(batchId)) > 0;

    /// <summary>
    /// Compare-and-swap of the batch phase record under a cross-process lock.
    /// The transition rule checks the successor, the coordinator fence and,
    /// when given, <paramref name="expectedVersion"/>. An accepted change
    /// writes the record first and the state history entry with the same
    /// number second; a crash between them leaves a record whose entry the
    /// next change or <see cref="ReconcilePhaseRecordIn"/> writes.
    /// </summary>
    public BatchPhaseTransitionResult TryTransition(BatchGateState state, long? expectedVersion = null)
    {
        var manifest = ReadManifest(state.BatchId);
        if (state.MembershipDigest != manifest.MembershipDigest
            || state.CandidateSha is { } sha && !IsSha(sha))
            throw new InvalidDataException("Batch state does not match its closed manifest.");
        var directory = BatchDirectory(state.BatchId);
        using var guard = PhaseGuard(directory);
        var current = CurrentRecord(manifest, directory);
        if (current is not null) WriteMissingHistory(directory, current);
        var replays = state.Phase is BatchPhase.Published or BatchPhase.Abandoned
            ? ReplayOutcomes(manifest) : new Dictionary<string, string>(StringComparer.Ordinal);
        var decision = AgentStudio.Runner.MonolithTransitionAdapter.BatchPhaseTransition(
            new BatchPhaseTransitionFacts(current?.State.Phase, current?.Version ?? 0,
                current?.State.CoordinatorFence ?? 0, state.Phase, state.CoordinatorFence,
                expectedVersion, current?.Members ?? Claims(manifest), replays));
        if (!decision.Accepted) return new(false, decision.Refusal, current);
        var record = new BatchPhaseRecord(state.BatchId, state.MembershipDigest,
            decision.Version, state, decision.Members);
        WriteRecord(Path.Combine(directory, PhaseRecordName), record);
        WriteMissingHistory(directory, record);
        return new(true, null, record);
    }

    /// <summary>
    /// The first phase record of a closed manifest whose formation stopped
    /// after the manifest write. False when the batch already has a state.
    /// </summary>
    public bool InitializeFormed(BatchGateManifest manifest, DateTimeOffset nowUtc)
        => TryTransition(new BatchGateState(manifest.BatchId, manifest.MembershipDigest,
            BatchPhase.Formed, null, 0, nowUtc), expectedVersion: 0).Accepted;

    /// <summary>
    /// Start-of-tick repair of one batch folder: a record whose state history
    /// entry a crash left out gets that entry, and a closed manifest without
    /// any state gets its first record. Settled folders cost two file checks
    /// and take no lock.
    /// </summary>
    public BatchPhaseRepair ReconcilePhaseRecordIn(string folder, DateTimeOffset nowUtc)
    {
        var path = Path.Combine(folder, PhaseRecordName);
        if (File.Exists(path))
        {
            if (File.Exists(HistoryPath(folder, Read<BatchPhaseRecord>(path).Version)))
                return BatchPhaseRepair.None;
            using var guard = PhaseGuard(folder);
            return WriteMissingHistory(folder, Read<BatchPhaseRecord>(path))
                ? BatchPhaseRepair.HistoryWritten : BatchPhaseRepair.None;
        }
        if (HighestHistoryNumber(folder) > 0) return BatchPhaseRepair.None;
        return InitializeFormed(ReadManifestIn(folder), nowUtc)
            ? BatchPhaseRepair.FirstRecordWritten : BatchPhaseRepair.None;
    }

    /// <summary>
    /// The record of a batch, or the one its legacy state history implies:
    /// batches closed before AGT-3563 have no record until their next change.
    /// A history entry beyond the record (written by an older host) wins, so a
    /// new entry never reuses its number.
    /// </summary>
    private BatchPhaseRecord? CurrentRecord(BatchGateManifest manifest, string directory)
    {
        var record = ReadPhaseRecord(manifest.BatchId);
        var highest = HighestHistoryNumber(directory);
        if (record is not null && record.Version >= highest) return record;
        if (highest == 0) return null;
        var latest = Read<BatchGateState>(HistoryPath(directory, highest));
        var members = record?.Members ?? AgentStudio.Runner.MonolithTransitionAdapter.BatchMemberDispositions(
            latest.Phase, Claims(manifest), ReplayOutcomes(manifest));
        return new BatchPhaseRecord(manifest.BatchId, manifest.MembershipDigest, highest, latest, members);
    }

    /// <summary>
    /// Each member with the review attempt of its queued record. A member that
    /// is not queued (a manifest closed by a test fixture, or a record gone
    /// since) keeps a null key and is matched by task key and run attempt.
    /// </summary>
    private IReadOnlyList<BatchMemberClaim> Claims(BatchGateManifest manifest)
    {
        var pending = ListPending();
        return manifest.Members.Select(member => new BatchMemberClaim(member.TaskKey, member.RunAttempt,
            pending.Where(item => item.Subject.TaskKey == member.TaskKey
                    && item.Subject.RunAttempt == member.RunAttempt
                    && string.Equals(item.Subject.ResultSha, member.ResultSha, StringComparison.OrdinalIgnoreCase))
                .MaxBy(item => item.Subject.EnqueueSequence)?.ReviewAttemptId,
            BatchMemberDisposition.Claimed)).ToArray();
    }

    private IReadOnlyDictionary<string, string> ReplayOutcomes(BatchGateManifest manifest)
        => manifest.Members
            .Select(member => (member.TaskKey, Replay: TryReadReplay(manifest.BatchId, member.TaskKey)))
            .Where(item => item.Replay is not null)
            .ToDictionary(item => item.TaskKey, item => item.Replay!.Outcome, StringComparer.Ordinal);

    private static bool WriteMissingHistory(string directory, BatchPhaseRecord record)
    {
        var path = HistoryPath(directory, record.Version);
        if (File.Exists(path)) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WriteOnce(path, record.State);
        return true;
    }

    private static string HistoryPath(string directory, long number)
        => Path.Combine(directory, "state", number.ToString("D8", CultureInfo.InvariantCulture) + ".json");

    private static long HighestHistoryNumber(string directory)
    {
        var folder = Path.Combine(directory, "state");
        if (!Directory.Exists(folder)) return 0;
        return Directory.EnumerateFiles(folder, "*.json")
            .Select(path => long.TryParse(Path.GetFileNameWithoutExtension(path), NumberStyles.None,
                CultureInfo.InvariantCulture, out var number) ? number : 0)
            .DefaultIfEmpty(0).Max();
    }

    private static void WriteRecord(string path, BatchPhaseRecord record)
    {
        BatchGateDurableWrites.Starting(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, record, Json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        BatchGateDurableWrites.Completed(path);
    }

    /// <summary>
    /// One writer per batch across processes. FileShare.None takes an
    /// exclusive lock on the lock file, so a contended open fails and retries.
    /// </summary>
    private static FileStream PhaseGuard(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "phase.lock");
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (attempt < PhaseLockAttempts)
            {
                Thread.Sleep(PhaseLockRetry);
            }
        }
    }
}
