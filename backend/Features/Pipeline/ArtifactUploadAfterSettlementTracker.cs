namespace AgentStudio.Pipeline;

/// <summary>One RunAttempt whose artifact write was refused because it had already settled.</summary>
public sealed record SettledArtifactRejection(
    string AttemptId,
    string TaskKey,
    DateTime FirstRejectedAtUtc,
    DateTime LastRejectedAtUtc,
    int RejectedWrites);

/// <summary>
/// A runner whose artifact writes the Task Server refused after settlement for
/// <see cref="PipelineHealthConventions.ArtifactUploadAfterSettlementAttempts"/>
/// distinct RunAttempts within <see cref="PipelineHealthConventions.ArtifactUploadAfterSettlementWindow"/>.
/// </summary>
public sealed record ArtifactUploadAfterSettlement(
    string RunnerId,
    string? ReleaseId,
    string Project,
    string? WatchPath,
    IReadOnlyList<SettledArtifactRejection> Attempts,
    DateTime DetectedAtUtc);

/// <summary>Receiver of the artifact-upload-after-settlement alarm (implemented by the pipeline health sensor).</summary>
public interface IArtifactUploadAfterSettlementAlarm
{
    void ArtifactUploadsRejectedAfterSettlement(ArtifactUploadAfterSettlement rejection);
    void ArtifactUploadsRecovered(string runnerId, DateTime recoveredAtUtc);
}

/// <summary>What one observed artifact write means for the alarm.</summary>
public enum ArtifactUploadAfterSettlementSignal
{
    None,
    Alarm,
    Recovered,
}

/// <summary>
/// Counts, per runner, the distinct RunAttempts whose artifact writes were
/// refused because the attempt had already settled (AGT-3095). Release 0.9.4
/// settled before uploading, so every coding run of agent-runner-01 lost its
/// result files for more than a week, and each card only carried its own
/// <c>result_artifacts_partial</c> event.
///
/// <para>Deterministic over the timestamps it is given. Requests and files of
/// one attempt count once. Attempts whose last refusal is older than the window
/// drop out. The alarm fires once per runner; the next accepted artifact write
/// from that runner clears it and re-arms the tracker.</para>
/// </summary>
public sealed class ArtifactUploadAfterSettlementTracker
{
    private readonly object _sync = new();
    private readonly Dictionary<string, Runner> _runners = new(StringComparer.Ordinal);

    public (ArtifactUploadAfterSettlementSignal Signal, IReadOnlyList<SettledArtifactRejection> Attempts) ObserveRejected(
        string runnerId,
        string attemptId,
        string taskKey,
        DateTime nowUtc)
    {
        lock (_sync)
        {
            if (!_runners.TryGetValue(runnerId, out var runner))
            {
                runner = new Runner();
                _runners[runnerId] = runner;
            }

            var since = nowUtc - PipelineHealthConventions.ArtifactUploadAfterSettlementWindow;
            foreach (var stale in runner.Attempts.Where(pair => pair.Value.Rejection.LastRejectedAtUtc < since).ToArray())
                runner.Attempts.Remove(stale.Key);

            runner.Attempts[attemptId] = runner.Attempts.TryGetValue(attemptId, out var known)
                ? (known.Rejection with
                {
                    LastRejectedAtUtc = nowUtc,
                    RejectedWrites = known.Rejection.RejectedWrites + 1,
                }, known.Order)
                : (new SettledArtifactRejection(attemptId, taskKey, nowUtc, nowUtc, 1), runner.NextOrder++);

            if (runner.Alarmed
                || runner.Attempts.Count < PipelineHealthConventions.ArtifactUploadAfterSettlementAttempts)
            {
                return (ArtifactUploadAfterSettlementSignal.None, []);
            }

            runner.Alarmed = true;
            return (ArtifactUploadAfterSettlementSignal.Alarm, runner.Attempts.Values
                .OrderBy(attempt => attempt.Order)
                .Select(attempt => attempt.Rejection)
                .ToArray());
        }
    }

    public ArtifactUploadAfterSettlementSignal ObserveAccepted(string runnerId)
    {
        lock (_sync)
        {
            if (!_runners.Remove(runnerId, out var cleared)) return ArtifactUploadAfterSettlementSignal.None;
            return cleared.Alarmed ? ArtifactUploadAfterSettlementSignal.Recovered : ArtifactUploadAfterSettlementSignal.None;
        }
    }

    private sealed class Runner
    {
        public Dictionary<string, (SettledArtifactRejection Rejection, long Order)> Attempts { get; } =
            new(StringComparer.Ordinal);
        public long NextOrder { get; set; }
        public bool Alarmed { get; set; }
    }
}
