namespace AgentStudio.Diagnostics;

/// <summary>
/// Why the ADR-0031 phase-6 db-touch sentinel (<c>POST /api/_internal/probe</c>)
/// is open or closed on this instance. The reason is part of the endpoint's
/// answer so an operator reading a probe response knows which gate let it in.
/// </summary>
public enum InternalProbeGateReason
{
    /// <summary><c>UpdateService:ProbeEnabled=false</c>: the operator closed it explicitly.</summary>
    ExplicitlyDisabled,

    /// <summary><c>UpdateService:ProbeEnabled=true</c>: the operator opened it explicitly.</summary>
    ExplicitlyEnabled,

    /// <summary><c>Environment:IsDev=true</c>: a dev checkout has it open by definition.</summary>
    DevEnvironment,

    /// <summary><c>DevTools:UpdateStableEnabled=true</c>: the legacy gate, kept for compatibility.</summary>
    DevToolsUpdateStable,

    /// <summary>
    /// The Stable update contract is installed (the approved-tag marker
    /// exists), so the Update Service owns this instance and its verification
    /// matrix must be able to run.
    /// </summary>
    UpdateContractInstalled,

    /// <summary>Nothing opens it: a plain production instance.</summary>
    NoGate
}

/// <summary>Gate verdict plus the reason that produced it.</summary>
public sealed record InternalProbeGateDecision(bool Enabled, InternalProbeGateReason Reason);

/// <summary>
/// Pure gate policy for the db-touch sentinel.
///
/// AGT-2865: the sentinel used to depend on <c>DevTools:UpdateStableEnabled</c>,
/// a flag whose other consumer is the DevTools SSE stream that runs
/// <c>update-stable.sh</c>. A default Stable installation has neither that flag
/// nor <c>Environment:IsDev</c>, so the Update Service's own phase-6 check
/// could never pass there, and the run only found out after it had already
/// stopped, restored, rebuilt and restarted the stack (run 0649a4e0,
/// 17.09.2026, v0.6.0 -> v0.7.0).
///
/// The sentinel therefore has its own gate, <c>UpdateService:ProbeEnabled</c>,
/// which defaults to open exactly where the Update Service contract is
/// installed - i.e. where the approved-tag marker
/// (<c>&lt;TaskRepository&gt;/.metadata/stable-approved-tag</c>) exists. The
/// DevTools SSE stream keeps its own flag and is not widened by any of this:
/// no default here opens that stream on Stable.
/// </summary>
public static class InternalProbeGate
{
    /// <summary>Explicit per-instance switch. Unset means "follow the update contract".</summary>
    public const string ProbeEnabledKey = "UpdateService:ProbeEnabled";

    /// <summary>Optional absolute override for the approved-tag marker path.</summary>
    public const string ApprovedTagFileKey = "UpdateService:ApprovedTagFile";

    /// <summary>Workspace-relative location of the approved-tag marker.</summary>
    public const string ApprovedTagRelativePath = ".metadata/stable-approved-tag";

    /// <summary>
    /// The decision, in precedence order. An explicit value always wins so an
    /// operator can close the sentinel on a host that does carry the contract,
    /// and open it on a host that does not carry it yet.
    /// </summary>
    public static InternalProbeGateDecision Decide(
        bool? probeEnabled,
        bool isDev,
        bool devToolsUpdateStableEnabled,
        bool updateContractInstalled)
    {
        if (probeEnabled == false) return new InternalProbeGateDecision(false, InternalProbeGateReason.ExplicitlyDisabled);
        if (probeEnabled == true) return new InternalProbeGateDecision(true, InternalProbeGateReason.ExplicitlyEnabled);
        if (isDev) return new InternalProbeGateDecision(true, InternalProbeGateReason.DevEnvironment);
        if (devToolsUpdateStableEnabled) return new InternalProbeGateDecision(true, InternalProbeGateReason.DevToolsUpdateStable);
        if (updateContractInstalled) return new InternalProbeGateDecision(true, InternalProbeGateReason.UpdateContractInstalled);
        return new InternalProbeGateDecision(false, InternalProbeGateReason.NoGate);
    }

    /// <summary>
    /// Reads the four inputs off configuration. Nothing is cached: the flag
    /// lives in <c>appsettings.Local.json</c>, which is loaded with
    /// <c>reloadOnChange</c>, and an operator who sets it mid-incident expects
    /// the next request to answer.
    /// </summary>
    public static InternalProbeGateDecision Decide(IConfiguration config) => Decide(
        ReadOptionalBool(config, ProbeEnabledKey),
        config.GetValue<bool>("Environment:IsDev"),
        config.GetValue<bool>("DevTools:UpdateStableEnabled"),
        UpdateContractInstalled(config));

    /// <summary>
    /// Absolute path of the approved-tag marker, or null when neither an
    /// explicit override nor a workspace root is configured.
    /// </summary>
    public static string? ApprovedTagFile(IConfiguration config)
    {
        var configured = config[ApprovedTagFileKey];
        if (!string.IsNullOrWhiteSpace(configured)) return configured;

        var workspace = config["TaskRepository"];
        return string.IsNullOrWhiteSpace(workspace)
            ? null
            : Path.Combine(workspace, ApprovedTagRelativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>True when this instance carries the Stable update contract.</summary>
    public static bool UpdateContractInstalled(IConfiguration config)
    {
        var path = ApprovedTagFile(config);
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { return File.Exists(path); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Tri-state flag read. Absent or unparsable is "unset", which hands the
    /// decision to the contract default rather than silently closing the gate.
    /// </summary>
    private static bool? ReadOptionalBool(IConfiguration config, string key)
    {
        var raw = config[key]?.Trim();
        if (string.IsNullOrEmpty(raw)) return null;
        if (bool.TryParse(raw, out var parsed)) return parsed;
        if (raw == "1") return true;
        if (raw == "0") return false;
        return null;
    }
}
