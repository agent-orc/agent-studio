namespace AgentTaskboard.UpdateService;

/// <summary>
/// Boot-time configuration. Defaults are tuned for the local dev/stable
/// devspace layout; everything is overridable via env vars / appsettings
/// (see Program.cs Bind call).
/// </summary>
public sealed class UpdateServiceOptions
{
    public string ListenUrl { get; set; } = "http://127.0.0.1:5039;http://[::1]:5039";
    public string StableCheckoutDir { get; set; } = @"C:\Projects\agent-taskboard-devspace\agent-taskboard-stable";
    public string DevspaceDir { get; set; } = @"C:\Projects\agent-taskboard-devspace";
    public string UpdateScript { get; set; } = "update-stable.sh";
    public string StopScript { get; set; } = "stop-stable.sh";
    public string StartScript { get; set; } = "start-stable.sh";
    public string BashPath { get; set; } = @"C:\Program Files\Git\bin\bash.exe";
    public string BackendUrl { get; set; } = "http://127.0.0.1:5031";
    public string BackendClientId { get; set; } = "stable-restart-watcher";
    public string HistoryFile { get; set; } = @"C:\Projects\agent-taskboard-workspace\logs\stable-updates.jsonl";

    /// <summary>
    /// ADR-0031: per-run folder root. Each run gets its own subdirectory
    /// containing pre/post snapshots, verification.jsonl, captured stdout/
    /// stderr from each phase, and a human-readable summary.md.
    /// </summary>
    public string RunsDirectory { get; set; } = @"C:\Projects\agent-taskboard-workspace\logs\update-service-runs";

    public string VersionFile { get; set; } = @"C:\Projects\agent-taskboard-devspace\agent-taskboard-stable\VERSION";
    public bool RequireReleaseManifest { get; set; } = false;
    public string BuildManifestFile { get; set; } = "build-manifest.json";
    public string CandidateManifestFile { get; set; } = @"C:\Projects\agent-taskboard-workspace\.metadata\stable-candidate-manifest.json";
    public string ApprovedTagFile { get; set; } = @"C:\Projects\agent-taskboard-workspace\.metadata\stable-approved-tag";
    /// <summary>
    /// Set by the outer updater when release-channel refresh could not reach
    /// the registry and the preflight is deliberately using cached immutable
    /// manifests plus a cached approved tag. This is explicit state, never
    /// inferred from file timestamps or cache presence.
    /// </summary>
    public bool ReleaseMetadataOffline { get; set; } = false;

    public int ProbeIntervalSeconds { get; set; } = 30;
    public int HealthWaitSeconds { get; set; } = 180;

    /// <summary>
    /// Ceiling for the phase-5 restart health wait, used instead of
    /// <see cref="HealthWaitSeconds"/> when waiting for the freshly-restarted
    /// backend to start listening. A cold compile (many changed commits since
    /// the last Stable build) can take roughly 3 minutes before dotnet starts
    /// listening on port 5031; 10 minutes gives headroom without letting a
    /// genuinely dead process hang the run forever.
    /// </summary>
    public int RestartHealthWaitSeconds { get; set; } = 600;

    /// <summary>
    /// Loopback origin the frontend dev server listens on after restart.
    /// Fix for the "frontend left down after a Stable update" incident: the
    /// orchestrator waits for this port to accept connections before the run
    /// is allowed to reach phase=done.
    ///
    /// AGT-2862: the value is the IPv4 loopback because that is the address
    /// the dev server is now told to bind (<c>host: 127.0.0.1</c> in
    /// <c>frontend/angular.json</c>). The probe does not trust that alone -
    /// it also tries <c>localhost</c> and <c>[::1]</c> on the same port
    /// before calling the frontend down, because which family
    /// <c>ng serve</c> ends up on depends on the host's name resolution.
    /// Raising <see cref="FrontendWaitSeconds"/> is not a fix for a probe
    /// that is looking at the wrong address.
    /// </summary>
    public string FrontendUrl { get; set; } = "http://127.0.0.1:4011";
    public int FrontendWaitSeconds { get; set; } = 120;

    /// <summary>
    /// Controls whether behind-origin notifications may apply an update on
    /// their own. "manual" still allows explicit manual triggers; "scheduled"
    /// allows scheduled/API triggers to run the apply pipeline.
    /// </summary>
    public string Mode { get; set; } = "manual";

    /// <summary>
    /// ADR-0031: opt-in. When true (env: ATP_UPDATE_AUTO_ROLLBACK=1), a
    /// failed verification triggers an automatic git reset + restart +
    /// re-verify cycle. Default off so verification failures stay loud and
    /// the operator stays in control.
    /// </summary>
    public bool AutoRollback { get; set; } = false;

    /// <summary>
    /// ADR-0031: how long the FE keeps showing the completion toast for the
    /// last successful run. The wire field is `lastRunFinishedAt`; the FE
    /// computes "within the last N seconds" against it.
    /// </summary>
    public int DoneLingerSeconds { get; set; } = 60;

    public string? TriggerToken { get; set; } = null;
}
