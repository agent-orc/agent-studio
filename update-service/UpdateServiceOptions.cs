using AgentTaskboard.UpdateService.Installation;

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

    /// <summary>
    /// Shared secret for every state-changing route (header
    /// <c>X-Update-Token</c>, env <c>ATP_UPDATE_TOKEN</c>). When unset, the
    /// token is read from <see cref="TriggerTokenFile"/>, and the first start
    /// generates it there. The service never accepts a mutation without one.
    /// </summary>
    public string? TriggerToken { get; set; } = null;

    /// <summary>
    /// Owner-only token file (env <c>ATP_UPDATE_TOKEN_FILE</c>). Defaults to
    /// <c>.update-token</c> in the service content root, beside appsettings.json.
    /// The Studio backend reads the same file.
    /// </summary>
    public string? TriggerTokenFile { get; set; } = null;

    /// <summary>
    /// Studio origins granted CORS, separated by <c>;</c> (env
    /// <c>ATP_UPDATE_ALLOWED_ORIGINS</c>). Defaults to the loopback dev (4010)
    /// and Stable (4011) frontends. An empty value grants no origin, for a
    /// Studio that calls the service server-to-server. Credentials are never
    /// allowed.
    /// </summary>
    public string AllowedOrigins { get; set; } =
        "http://127.0.0.1:4010;http://localhost:4010;http://127.0.0.1:4011;http://localhost:4011";

    public IReadOnlyList<string> AllowedOriginList()
        => AllowedOrigins.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(origin => origin.TrimEnd('/'))
            .Where(origin => origin != "*")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// AGT-2947: the placement this service manages. Only
    /// <see cref="InstallationPlacement.DevStableCheckout"/> is applied; any
    /// other placement belongs to its own updater (installed Compose uses
    /// update-docker.sh) and triggers are refused.
    /// </summary>
    public InstallationPlacement Placement { get; set; } = InstallationPlacement.DevStableCheckout;
}
