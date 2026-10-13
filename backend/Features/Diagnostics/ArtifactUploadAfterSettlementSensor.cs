using AgentStudio.Pipeline;
using AgentStudio.Runner;

namespace AgentStudio.Diagnostics;

/// <summary>
/// Feeds artifact writes refused after settlement into
/// <see cref="ArtifactUploadAfterSettlementTracker"/> and forwards its alarm and
/// recovery to the pipeline health feed (AGT-3095). The release id comes from
/// the same capability registry that feeds the host-release drift view.
/// </summary>
public sealed class ArtifactUploadAfterSettlementSensor
{
    private readonly ArtifactUploadAfterSettlementTracker _tracker = new();
    private readonly TimeProvider _time;
    private readonly IArtifactUploadAfterSettlementAlarm? _alarm;
    private readonly V1ReviewExecutorRegistry? _registry;

    public ArtifactUploadAfterSettlementSensor(
        TimeProvider time,
        IArtifactUploadAfterSettlementAlarm? alarm = null,
        V1ReviewExecutorRegistry? registry = null)
    {
        _time = time;
        _alarm = alarm;
        _registry = registry;
    }

    public void Rejected(string runnerId, string attemptId, string taskKey, string project, string? watchPath)
    {
        if (string.IsNullOrWhiteSpace(runnerId) || string.IsNullOrWhiteSpace(attemptId)) return;
        var now = _time.GetUtcNow().UtcDateTime;
        var (signal, attempts) = _tracker.ObserveRejected(runnerId, attemptId, taskKey, now);
        if (signal != ArtifactUploadAfterSettlementSignal.Alarm) return;
        _alarm?.ArtifactUploadsRejectedAfterSettlement(new ArtifactUploadAfterSettlement(
            runnerId, ReleaseOf(runnerId), project, watchPath, attempts, now));
    }

    public void Accepted(string? runnerId)
    {
        if (string.IsNullOrWhiteSpace(runnerId)) return;
        if (_tracker.ObserveAccepted(runnerId) == ArtifactUploadAfterSettlementSignal.Recovered)
            _alarm?.ArtifactUploadsRecovered(runnerId, _time.GetUtcNow().UtcDateTime);
    }

    private string? ReleaseOf(string runnerId)
        => _registry?.ListCapabilitySnapshots()
            .FirstOrDefault(snapshot => string.Equals(snapshot.RunnerId, runnerId, StringComparison.Ordinal)
                && snapshot.Release is not null)
            ?.Release!.ReleaseId;
}
