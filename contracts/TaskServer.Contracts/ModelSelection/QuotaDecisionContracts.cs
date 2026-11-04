namespace AgentStudio.Cli;

// Moved from backend/Features/Cli/Quota (MS-5) so the shared model-selection
// planner owns the quota and routing facts it decides on. Namespaces and
// shapes are unchanged; the backend services that load these facts stay in
// the monolith.

/// <summary>Per-CLI, per-window usage caps as the planner reads them.</summary>
public interface IQuotaCapTable
{
    /// <summary>The cap for one window; <see cref="QuotaCapDefaults.CapPct"/> when none is configured.</summary>
    int GetCap(string cliType, string windowLabel);
}

public static class QuotaCapDefaults
{
    /// <summary>Default cap when a CLI window has no configured entry.</summary>
    public const int CapPct = 95;
}

/// <summary>
/// Result of evaluating a quota snapshot against the configured caps. When
/// <see cref="Blocked"/> is false, the other fields are not meaningful and
/// callers should not log a "blocked" reason.
/// </summary>
public sealed record CapEvaluation(
    bool Blocked,
    string? CliType = null,
    string? WindowLabel = null,
    int CapPct = 0,
    double UsedPct = 0d,
    bool Suspicious = false,
    string? SuspiciousReason = null,
    // AGT-2055: set when the block is a forward-looking projection ("this
    // window will cross the cap before it resets") rather than an already-
    // breached cap. Callers use this to pre-empt the wall instead of reacting
    // to it. <see cref="ResetAt"/> carries the offending window's reset time
    // so a "next reset HH:mm" hint can be surfaced at the waiting slot.
    bool Projected = false,
    DateTime? ResetAt = null)
{
    public static readonly CapEvaluation NotBlocked = new(false);

    public string DescribeReason()
    {
        if (!Blocked) return "ok";
        if (Suspicious)
            return $"{CliType} quota snapshot unconfirmed ({SuspiciousReason ?? "suspicious glitch"}); holding launch until a re-probe confirms";
        return Projected
            ? $"{CliType} {WindowLabel} projected to reach the {CapPct}% cap before reset (now {UsedPct:0.#}%)"
            : $"{CliType} {WindowLabel} at {UsedPct:0.#}% (cap {CapPct}%)";
    }
}

/// <summary>
/// Numbers behind one window's projection, carried into the load-distribution
/// log/event so the operator sees the reasoning (burn rate, remaining budget,
/// remaining time) - "gute Transparenz dieses Themas Lastverteilung".
/// </summary>
public sealed record QuotaProjection(
    string WindowLabel,
    double CurrentUsedPct,
    double ProjectedUsedPct,
    double BurnRatePctPerHour,
    double HoursRemaining,
    int CapPct,
    DateTime? ResetAt,
    bool BreachesBeforeReset,
    DateTime? AssumedStartAt = null,
    double? ElapsedFraction = null);

public sealed record QuotaProjectionWarning(
    string WindowLabel,
    string Reason,
    DateTime ResetAt,
    DateTime AssumedStartAt,
    double ElapsedFraction,
    double CurrentUsedPct,
    double ProjectedUsedPct);

/// <summary>Runtime operator preference to conserve one provider before its hard cap.</summary>
public sealed record CliFallbackPreference(
    string CliType,
    bool Active,
    DateTime? EnabledAt,
    DateTime? ExpiresAt);

public sealed record ResolvedCliQuotaWaitPolicy(
    bool Enabled,
    int ThresholdMinutes,
    string Source,
    bool? ProjectEnabled,
    int? ProjectThresholdMinutes,
    bool GlobalEnabled,
    int GlobalThresholdMinutes);

/// <summary>Workspace primary/fallback profile for one CLI (<c>cli-model-routing.json</c>).</summary>
public sealed record CliModelRouteProfile
{
    public string CliType { get; init; } = "";
    public string? PrimaryModel { get; init; }
    public string? PrimaryThinkingLevel { get; init; }
    public string? FallbackCliType { get; init; }
    public string? FallbackModel { get; init; }
    public string? FallbackThinkingLevel { get; init; }
    /// <summary>
    /// True when the effective-profile read filled the fallback fields from
    /// the equivalence catalogue because the operator configured none. Always
    /// false on a persisted profile - this is a read-time display marker,
    /// never written to <c>cli-model-routing.json</c>.
    /// </summary>
    public bool IsFallbackDerived { get; init; }
    public string? CatalogueVersion { get; init; }
}

public sealed record CliRouteDecision(
    string CliType,
    string? Model,
    string? ThinkingLevel,
    bool IsFallback,
    string? Reason,
    CapEvaluation PrimaryCap,
    string? FallbackReason = null,
    string? FallbackSource = null,
    string? CatalogueVersion = null,
    string? CapabilityClass = null);

/// <summary>One route projected from Token Economy's routing and price catalogues.</summary>
public sealed record ModelEquivalenceRoute(
    string FromCliType,
    string FromModel,
    string? FromThinkingLevel,
    string ToCliType,
    string ToModel,
    string? ToThinkingLevel,
    string CatalogueVersion,
    decimal? FromInputPerMTok,
    decimal? FromOutputPerMTok,
    decimal? ToInputPerMTok,
    decimal? ToOutputPerMTok,
    string CapabilityClass);

/// <summary>The Token Economy equivalence catalogue as the planner reads it.</summary>
public interface IModelEquivalenceCatalog
{
    string Version { get; }
    (string Model, string? ThinkingLevel)? TryGetEquivalent(
        string fromCliType, string? fromModel, string? fromThinkingLevel, string toCliType);
    ModelEquivalenceRoute? TryGetRoute(
        string fromCliType, string? fromModel, string? fromThinkingLevel, string toCliType);
    IReadOnlyList<ModelEquivalenceRoute> Routes { get; }
    bool SameCapabilityClass(string? fromModel, string? toModel);
}
