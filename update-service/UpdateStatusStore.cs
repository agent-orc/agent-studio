using System.Text.Json;

namespace AgentTaskboard.UpdateService;

/// <summary>
/// Single source of truth for what /update/status returns. All public mutation
/// goes through SetPhase / AppendHistory; reads are lock-free snapshots so the
/// HTTP endpoint never blocks on an in-progress orchestration.
/// </summary>
public sealed class UpdateStatusStore
{
    private readonly object _lock = new();
    private readonly string _historyFile;
    private readonly ILogger<UpdateStatusStore> _logger;
    private readonly string _serviceVersion;
    private readonly Func<string> _readProductVersion;

    private UpdateStatus _status;
    private bool _backendReachable;

    public UpdateStatusStore(
        string historyFile,
        string headLocal,
        Func<string> readProductVersion,
        ILogger<UpdateStatusStore> logger,
        string mode = "manual")
    {
        _historyFile = historyFile;
        _logger = logger;
        _readProductVersion = readProductVersion;
        _serviceVersion = typeof(UpdateStatusStore).Assembly.GetName().Version?.ToString(3) ?? "unknown";
        _status = new UpdateStatus(
            Phase: "idle",
            PhaseLabel: null,
            Message: null,
            CurrentRunId: null,
            StartedAt: null,
            FinishedAt: null,
            HeadLocal: headLocal,
            HeadOrigin: null,
            BehindBy: 0,
            PendingCommits: Array.Empty<CommitInfo>(),
            LastFetchAt: null,
            LastUpdateAt: null,
            LastSuccessAt: HydrateLastSuccess(),
            LastRunFinishedAt: null,
            LastRunHeadBefore: null,
            LastRunHeadAfter: null,
            IsRunning: false,
            BackendReachable: false,
            ServiceVersion: _serviceVersion,
            ProductVersion: SafeReadVersion(),
            Mode: NormalizeMode(mode),
            VerificationFailures: null,
            AutoRollbackEnabled: false
        );
    }

    private static string NormalizeMode(string? mode)
        => string.Equals(mode, "scheduled", StringComparison.OrdinalIgnoreCase) ? "scheduled" : "manual";

    private string SafeReadVersion()
    {
        try { return _readProductVersion(); } catch { return "unknown"; }
    }

    public UpdateStatus Get()
    {
        lock (_lock) return _status;
    }

    public void SetHead(string headLocal)
    {
        lock (_lock)
        {
            _status = _status with { HeadLocal = headLocal, ProductVersion = SafeReadVersion() };
        }
    }

    public void SetFetchResult(string headOrigin, int behindBy, IReadOnlyList<CommitInfo> pending)
    {
        lock (_lock)
        {
            _status = _status with
            {
                HeadOrigin = headOrigin,
                BehindBy = behindBy,
                PendingCommits = pending,
                LastFetchAt = DateTime.UtcNow,
                ProductVersion = SafeReadVersion(),
            };
        }
    }

    public void SetBackendReachable(bool reachable)
    {
        lock (_lock)
        {
            if (_backendReachable == reachable) return;
            _backendReachable = reachable;
            _status = _status with { BackendReachable = reachable };
        }
    }

    public void SetVersionTopology(RuntimeVersion runtime, VersionTopology topology)
    {
        lock (_lock)
        {
            var changed = _status.RunningVersion?.Version != runtime.Version
                || _status.MainVersion?.Commit != topology.Main.Commit
                || _status.DevelopVersion?.Commit != topology.Develop.Commit;
            _status = _status with
            {
                RunningVersion = runtime,
                MainVersion = topology.Main,
                DevelopVersion = topology.Develop,
                ProductVersion = runtime.Version,
            };
            if (changed)
            {
                _logger.LogInformation(
                    "update_version_topology_resolved running={RunningCommit} main={MainCommit} develop={DevelopCommit}",
                    runtime.Commit, topology.Main.Commit, topology.Develop.Commit);
            }
        }
    }

    public void SetReleaseComparison(ReleaseComparison comparison)
    {
        lock (_lock) _status = _status with { ReleaseComparison = comparison };
    }

    /// <summary>
    /// Phases a run can end on. Everything else implies "in-flight" and flips
    /// <c>IsRunning=true</c> so the FE block-modal stays mounted across the
    /// full pipeline. <c>degraded</c> (AGT-2862: backend up, frontend down)
    /// is terminal like the other two: the run is over and the operator
    /// decides what happens next, so the modal must come down.
    /// </summary>
    private static readonly string[] TerminalPhases = { "idle", "done", "failed", "degraded" };

    /// <summary>
    /// Phase transition. Phases that imply "in-flight" (anything that is not
    /// terminal, see <see cref="TerminalPhases"/>) flip <c>IsRunning=true</c>
    /// so the FE block-modal stays mounted across the full pipeline.
    /// </summary>
    public void SetPhase(
        string phase,
        string? message,
        string? runId,
        DateTime? startedAt,
        DateTime? finishedAt = null,
        DateTime? lastSuccessAt = null,
        DateTime? lastRunFinishedAt = null,
        string? lastRunHeadBefore = null,
        string? lastRunHeadAfter = null,
        IReadOnlyList<VerificationFailure>? verificationFailures = null,
        string? phaseLabel = null,
        bool? autoRollbackEnabled = null)
    {
        lock (_lock)
        {
            var running = !TerminalPhases.Contains(phase);
            _status = _status with
            {
                Phase = phase,
                PhaseLabel = phaseLabel ?? _status.PhaseLabel,
                Message = message,
                CurrentRunId = runId,
                StartedAt = startedAt,
                FinishedAt = finishedAt ?? _status.FinishedAt,
                IsRunning = running,
                LastUpdateAt = (phase != "idle" && TerminalPhases.Contains(phase)) ? DateTime.UtcNow : _status.LastUpdateAt,
                LastSuccessAt = lastSuccessAt ?? _status.LastSuccessAt,
                LastRunFinishedAt = lastRunFinishedAt ?? _status.LastRunFinishedAt,
                LastRunHeadBefore = lastRunHeadBefore ?? _status.LastRunHeadBefore,
                LastRunHeadAfter = lastRunHeadAfter ?? _status.LastRunHeadAfter,
                VerificationFailures = verificationFailures,
                AutoRollbackEnabled = autoRollbackEnabled ?? _status.AutoRollbackEnabled,
            };
        }
    }

    public void AppendHistory(UpdateHistoryEntry entry)
    {
        var line = JsonSerializer.Serialize(entry);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_historyFile)!);
            File.AppendAllText(_historyFile, line + Environment.NewLine);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "history append failed");
        }
    }

    public IReadOnlyList<UpdateHistoryEntry> ReadHistory(int max)
    {
        try
        {
            if (!File.Exists(_historyFile)) return Array.Empty<UpdateHistoryEntry>();
            var lines = File.ReadAllLines(_historyFile);
            var take = Math.Min(lines.Length, Math.Max(1, max));
            var slice = lines[^take..];
            var list = new List<UpdateHistoryEntry>(slice.Length);
            foreach (var l in slice)
            {
                if (string.IsNullOrWhiteSpace(l)) continue;
                try
                {
                    var entry = JsonSerializer.Deserialize<UpdateHistoryEntry>(l);
                    if (entry is not null) list.Add(entry);
                }
                catch (JsonException) { /* skip malformed line */ }
            }
            return list;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "history read failed");
            return Array.Empty<UpdateHistoryEntry>();
        }
    }

    private DateTime? HydrateLastSuccess()
    {
        if (!File.Exists(_historyFile)) return null;
        try
        {
            string? lastOk = null;
            foreach (var line in File.ReadAllLines(_historyFile))
            {
                if (line.Contains("\"Status\":\"ok\"") || line.Contains("\"status\":\"ok\""))
                    lastOk = line;
            }
            if (lastOk == null) return null;
            var entry = JsonSerializer.Deserialize<UpdateHistoryEntry>(lastOk);
            return entry?.FinishedAt;
        }
        catch
        {
            return null;
        }
    }
}
