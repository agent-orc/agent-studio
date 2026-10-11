namespace AgentStudio.Review;

/// <summary>
/// Structured result of the closing Result assessment (AGT-3046), persisted
/// next to status.md as <c>summary-assessment.json</c> and rendered in the
/// Result tab directly under the verdict.
/// </summary>
public sealed record SummaryAssessment
{
    public int Version { get; init; } = 1;

    /// <summary>One of <see cref="SummaryOutcomes"/>.</summary>
    public string Outcome { get; init; } = SummaryOutcomes.Delivered;

    public string? OutcomeReason { get; init; }

    /// <summary>The deterministic classification, kept even when the model's outcome won.</summary>
    public string PolicyOutcome { get; init; } = SummaryOutcomes.Delivered;

    /// <summary><c>model</c> when the model's structured block parsed, else <c>policy</c>.</summary>
    public string Source { get; init; } = SummaryAssessmentSources.Policy;

    /// <summary>Why the model block was not used; null when it was.</summary>
    public string? FallbackReason { get; init; }

    public string? Model { get; init; }

    public DateTime GeneratedAt { get; init; }

    public List<SummaryInconsistency> Inconsistencies { get; init; } = [];
}

/// <summary>Two card signals that cannot both be read at face value.</summary>
public sealed record SummaryInconsistency
{
    /// <summary>Stable id; policy kinds are listed in <see cref="SummaryInconsistencyKinds"/>.</summary>
    public string Kind { get; init; } = "";
    public string SignalA { get; init; } = "";
    public string SignalB { get; init; } = "";
    public string Explanation { get; init; } = "";
    public bool WorkMayBeLost { get; init; }
}

public static class SummaryOutcomes
{
    public const string Delivered = "delivered";
    public const string DeliveredViaOperatorMerge = "delivered-via-operator-merge";
    public const string ParkedOnDecision = "parked-on-decision";
    public const string Failed = "failed";
    public const string NoOp = "no-op";

    public static readonly string[] All = [Delivered, DeliveredViaOperatorMerge, ParkedOnDecision, Failed, NoOp];

    public static bool IsKnown(string? value) => value is not null && All.Contains(value, StringComparer.Ordinal);
}

public static class SummaryAssessmentSources
{
    public const string Model = "model";
    public const string Policy = "policy";
}

public static class SummaryInconsistencyKinds
{
    public const string FilesVersusNoCode = "files-vs-no-code";
    public const string FailureVersusCompleted = "failure-vs-completed";
    public const string EmptyCommitsVersusIntegrated = "empty-commits-vs-integrated";
    public const string SalvageNotIntegrated = "salvage-not-integrated";
}
