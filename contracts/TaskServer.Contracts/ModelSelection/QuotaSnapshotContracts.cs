using System.Text.Json.Serialization;

namespace AgentStudio.Shared;

// Moved from backend/Shared/Models (MS-5) so the shared model-selection
// planner can read quota facts in the monolith, Task Server and runner. The
// namespace is unchanged; the JSON shape is unchanged.

/// <summary>
/// One quota window for a CLI subscription (e.g. monthly premium requests,
/// 5-hour sliding window, or weekly limit). All numeric fields are nullable
/// because each CLI exposes a different subset.
/// </summary>
public record QuotaWindow
{
    /// <summary>"Premium requests" / "5-hour" / "Weekly" / etc.</summary>
    public string Label { get; init; } = "";
    /// <summary>Percentage used, 0..100+. May exceed 100 when over-quota.</summary>
    public double? UsedPct { get; init; }
    /// <summary>Absolute used count when known.</summary>
    public double? Used { get; init; }
    /// <summary>Absolute plan limit when known.</summary>
    public double? Limit { get; init; }
    /// <summary>"requests" / "tokens" / "%".</summary>
    public string? Unit { get; init; }
    /// <summary>UTC timestamp when this window resets, when computable.</summary>
    public DateTime? ResetAt { get; init; }
    /// <summary>
    /// Start of the active window as established by the first trusted snapshot.
    /// Projection keeps this anchor until its expected reset has actually passed,
    /// so a later parser glitch cannot move the start forward and inflate burn rate.
    /// </summary>
    public DateTime? ObservedStartAt { get; init; }
    /// <summary>Why this window must not currently be used for projection.</summary>
    public string? ProjectionSuspiciousReason { get; init; }
    /// <summary>Original human-readable reset string from the CLI ("3:40am (Europe/Berlin)" / "Mar 1").</summary>
    public string? ResetLabel { get; init; }
}

/// <summary>
/// A single CLI's quota state at a point in time. <see cref="Error"/> is set
/// when probing failed; consumers should still display <see cref="Plan"/> and
/// any partial windows that did parse.
/// </summary>
public record QuotaSnapshot
{
    public string CliType { get; init; } = "";
    /// <summary>Opaque digest identifying a host subscription, when known.</summary>
    public string? SubscriptionKey { get; init; }
    public DateTime FetchedAt { get; init; } = DateTime.UtcNow;
    /// <summary>
    /// Version reported by the CLI's <c>--version</c> command for this probe.
    /// Keeping it on the quota snapshot makes parser drift attributable without
    /// requiring a second operator-side reproduction.
    /// </summary>
    public string? CliVersion { get; init; }
    /// <summary>
    /// UTC time of the most recent failed probe. When this is set,
    /// <see cref="FetchedAt"/>, <see cref="Plan"/>, and <see cref="Windows"/>
    /// still describe the last good reading.
    /// </summary>
    public DateTime? ProbeFailedAt { get; init; }
    /// <summary>"Pro" / "Pro+" / "Plus" / "Free" — null when unknown.</summary>
    public string? Plan { get; init; }
    public List<QuotaWindow> Windows { get; init; } = [];
    /// <summary>How the data was sourced: "/usage" / "/status" / "footer" / "banner".</summary>
    public string? Source { get; init; }
    /// <summary>Truncated raw snapshot for debugging in the UI.</summary>
    public string? RawSample { get; init; }
    /// <summary>Set when probing failed; <see cref="Plan"/>/<see cref="Windows"/> may still hold partial data.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// True when this snapshot is not yet trusted: either a single probe showed
    /// an implausible downward jump that no reset explains and a confirmation
    /// re-probe has not agreed yet, or a live launch died with a usage-limit
    /// error that contradicts these numbers (AGT-2064). Admission treats a
    /// suspicious snapshot conservatively - it keeps blocking until a re-probe
    /// confirms - so a transient glitch can never open the launch gate on a CLI
    /// that is really at its limit.
    /// </summary>
    public bool Suspicious { get; init; }

    /// <summary>Human-readable reason a snapshot was flagged <see cref="Suspicious"/>.</summary>
    public string? SuspiciousReason { get; init; }
}

public sealed record ModelFallbackInfo(
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("to")] string To,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("cliType")] string? CliType = null,
    [property: JsonPropertyName("thinkingLevel")] string? ThinkingLevel = null,
    [property: JsonPropertyName("window")] string? Window = null,
    [property: JsonPropertyName("usedPct")] double? UsedPct = null,
    [property: JsonPropertyName("catalogueVersion")] string? CatalogueVersion = null);
