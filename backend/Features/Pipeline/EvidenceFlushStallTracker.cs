namespace AgentStudio.Pipeline;

/// <summary>A workspace repository whose evidence flush has failed without a success since <see cref="FirstFailedAtUtc"/>.</summary>
public sealed record EvidenceFlushStall(
    string GitRoot,
    string Error,
    DateTime FirstFailedAtUtc,
    DateTime LastFailedAtUtc,
    int ConsecutiveFailures)
{
    public TimeSpan FailingFor => LastFailedAtUtc - FirstFailedAtUtc;
}

/// <summary>Receiver of the evidence-flush stall alarm (implemented by the pipeline health sensor).</summary>
public interface IEvidenceFlushAlarm
{
    void EvidenceFlushStalled(EvidenceFlushStall stall);
    void EvidenceFlushRecovered(string gitRoot, DateTime recoveredAtUtc);
}

/// <summary>What one observed flush outcome means for the alarm.</summary>
public enum EvidenceFlushStallSignal
{
    None,
    Stalled,
    Recovered,
}

/// <summary>
/// Remembers, per workspace repository, since when evidence flushes have been
/// failing, and turns that into the <c>evidence-flush-stalled</c> alarm once the
/// failure has repeated for longer than the threshold (AGT-3000). The
/// 2026-09-27 stale <c>index.lock</c> produced 36 hours of
/// <c>workspace-evidence-flush-failed</c> warnings and no alarm; a warning line
/// is not a signal anyone watches.
///
/// <para>Deterministic over the timestamps it is given. A success (commit or
/// nothing-to-commit) resets the repository; the first success after a stall
/// reports <see cref="EvidenceFlushStallSignal.Recovered"/> so the alarm clears.</para>
/// </summary>
public sealed class EvidenceFlushStallTracker
{
    public static readonly TimeSpan DefaultThreshold = TimeSpan.FromMinutes(15);

    private readonly TimeSpan _threshold;
    private readonly object _sync = new();
    private readonly Dictionary<string, Failing> _failing = new(StringComparer.OrdinalIgnoreCase);

    public EvidenceFlushStallTracker(TimeSpan? threshold = null)
    {
        _threshold = threshold ?? DefaultThreshold;
    }

    public (EvidenceFlushStallSignal Signal, EvidenceFlushStall? Stall) Observe(
        string gitRoot,
        bool success,
        string? error,
        DateTime nowUtc)
    {
        lock (_sync)
        {
            if (success)
            {
                if (!_failing.Remove(gitRoot, out var cleared)) return (EvidenceFlushStallSignal.None, null);
                return cleared.Alarmed
                    ? (EvidenceFlushStallSignal.Recovered, null)
                    : (EvidenceFlushStallSignal.None, null);
            }

            if (!_failing.TryGetValue(gitRoot, out var state))
            {
                state = new Failing(nowUtc);
                _failing[gitRoot] = state;
            }
            state.Count++;
            state.LastAtUtc = nowUtc;
            state.LastError = string.IsNullOrWhiteSpace(error) ? "unknown" : error.Trim();

            // "More than 15 minutes": a failure exactly at the threshold is not yet a stall.
            if (nowUtc - state.FirstAtUtc <= _threshold) return (EvidenceFlushStallSignal.None, null);
            state.Alarmed = true;
            return (EvidenceFlushStallSignal.Stalled, new EvidenceFlushStall(
                gitRoot, state.LastError, state.FirstAtUtc, nowUtc, state.Count));
        }
    }

    private sealed class Failing(DateTime firstAtUtc)
    {
        public DateTime FirstAtUtc { get; } = firstAtUtc;
        public DateTime LastAtUtc { get; set; } = firstAtUtc;
        public int Count { get; set; }
        public string LastError { get; set; } = "unknown";
        public bool Alarmed { get; set; }
    }
}
