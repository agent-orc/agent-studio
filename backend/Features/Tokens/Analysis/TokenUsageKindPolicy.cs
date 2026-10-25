namespace AgentStudio.Tokens;

/// <summary>
/// Usage kinds of the card-centric token analysis (AGT-3267). Every token a
/// card spent lands in exactly one kind, so the per-kind columns of a card
/// sum to its total.
/// </summary>
public static class TokenUsageKinds
{
    public const string Coding = "coding";
    public const string FixRound = "fix-round";
    public const string ReviewAspect = "review-aspect";
    public const string ReviewGrade = "review-grade";
    public const string DeepReview = "deep-review";
    public const string Gate = "gate";
    public const string OrchestratorDecision = "orchestrator-decision";
    public const string Chat = "chat";
    public const string Enrichment = "enrichment";
    public const string Other = "other";

    public static readonly string[] All =
        [Coding, FixRound, ReviewAspect, ReviewGrade, DeepReview, Gate, OrchestratorDecision, Chat, Enrichment, Other];
}

/// <summary>
/// Pure classification of one recorded execution or ledger call into a
/// <see cref="TokenUsageKinds"/> value. Pipeline steps classify by step id
/// and kind; ledger calls without a step classify by participant and topic.
/// A coding run that is not the card's first run (a later attempt, or a
/// repeated run inside one attempt) is a fix round.
/// </summary>
public static class TokenUsageKindPolicy
{
    private static readonly HashSet<string> DeepReviewSteps = new(StringComparer.OrdinalIgnoreCase)
    {
        "post-analysis-model-review",
        "post-orchestrator-review",
        "post-concept-review",
        "post-concept-sight-review",
        "post-abort-review",
        "post-summary-assessment",
    };

    private static readonly HashSet<string> GateSteps = new(StringComparer.OrdinalIgnoreCase)
    {
        "post-build-test-gate",
        "post-lint-scss",
        "post-merge-into-develop",
        "post-merge-into-develop-push",
        "post-integrate-merge",
        "post-conflict-resolution",
        "post-worktree-containment",
        "post-ui-human-review-gate",
        "post-ui-visual-verdict",
        "post-ui-visual-capture",
        "pre-loop-guard",
    };

    private static readonly HashSet<string> EnrichmentSteps = new(StringComparer.OrdinalIgnoreCase)
    {
        "pre-prompt-enrichment",
        "pre-model-qualification",
        "pre-orchestrator-prep",
        "pre-reissue-open-items",
        "pre-ui-pipeline-routing",
    };

    private static readonly HashSet<string> EnrichmentTopics = new(StringComparer.OrdinalIgnoreCase)
    {
        "title-generation",
        "summary-generation",
        "prompt-enhancement",
        "commit-message",
        "task-spawner",
        "prompt-enrichment",
        "model-qualification",
        "qualification",
        "tagging",
    };

    private static readonly HashSet<string> DecisionTopics = new(StringComparer.OrdinalIgnoreCase)
    {
        "orchestrator-decision",
        "orchestrator-steer",
        "orchestrator-boot",
        "orchestrator-turn",
        "engine-decision",
        "engine-decision-late-call",
        "review-decision",
        "soft-reasoning",
        "failure-intervention",
        "task-receipt-orchestrator",
    };

    /// <param name="stepId">Pipeline step id.</param>
    /// <param name="kind">Pipeline step kind.</param>
    /// <param name="firstCodingRun">True for the card's first coding execution.</param>
    public static string ForStep(string? stepId, StepKind kind, bool firstCodingRun)
    {
        var id = stepId?.Trim() ?? "";
        if (kind == StepKind.Core) return firstCodingRun ? TokenUsageKinds.Coding : TokenUsageKinds.FixRound;
        if (string.Equals(id, "post-code-review-grade", StringComparison.OrdinalIgnoreCase))
            return TokenUsageKinds.ReviewGrade;
        if (DeepReviewSteps.Contains(id) || id.Contains("second-opinion", StringComparison.OrdinalIgnoreCase)
            || id.Contains("deep-review", StringComparison.OrdinalIgnoreCase))
            return TokenUsageKinds.DeepReview;
        if (id.StartsWith("aspect-", StringComparison.OrdinalIgnoreCase) || kind == StepKind.Aspect)
            return TokenUsageKinds.ReviewAspect;
        if (GateSteps.Contains(id) || id.EndsWith("-gate", StringComparison.OrdinalIgnoreCase))
            return TokenUsageKinds.Gate;
        if (EnrichmentSteps.Contains(id)) return TokenUsageKinds.Enrichment;
        if (string.Equals(id, "post-orchestrator-decision", StringComparison.OrdinalIgnoreCase)
            || id.Equals("post-failure-intervention", StringComparison.OrdinalIgnoreCase)
            || kind == StepKind.Orchestrator)
            return TokenUsageKinds.OrchestratorDecision;
        return TokenUsageKinds.Other;
    }

    /// <param name="participantId">Ledger participant (<c>agent:*</c>, <c>support:*</c>, <c>orchestrator:*</c>).</param>
    /// <param name="topic">Ledger topic.</param>
    /// <param name="firstCodingRun">True when an agent call belongs to the card's first coding run.</param>
    public static string ForLedger(string? participantId, string? topic, bool firstCodingRun)
    {
        var t = topic?.Trim() ?? "";
        if (t.Contains("chat", StringComparison.OrdinalIgnoreCase)) return TokenUsageKinds.Chat;
        if (EnrichmentTopics.Contains(t)) return TokenUsageKinds.Enrichment;
        if (DecisionTopics.Contains(t)) return TokenUsageKinds.OrchestratorDecision;
        if (t.StartsWith("aspect-", StringComparison.OrdinalIgnoreCase)) return TokenUsageKinds.ReviewAspect;
        if (t.Contains("grade", StringComparison.OrdinalIgnoreCase)) return TokenUsageKinds.ReviewGrade;
        if (t.Contains("drift", StringComparison.OrdinalIgnoreCase)
            || t.Contains("wiki", StringComparison.OrdinalIgnoreCase)
            || t.Contains("watcher", StringComparison.OrdinalIgnoreCase))
            return TokenUsageKinds.Other;
        if (TokenParticipants.IsOrchestrator(participantId)) return TokenUsageKinds.OrchestratorDecision;
        if (TokenParticipants.IsSupporting(participantId)) return TokenUsageKinds.ReviewAspect;
        return firstCodingRun ? TokenUsageKinds.Coding : TokenUsageKinds.FixRound;
    }
}

/// <summary>Outcome buckets of a card in the token analysis.</summary>
public static class CardOutcomes
{
    public const string Integrated = "integrated";
    public const string Completed = "completed";
    public const string Delivered = "delivered";
    public const string Escalated = "escalated";
    public const string Blocked = "blocked";
    public const string InProgress = "in-progress";
    public const string Open = "open";

    /// <summary>
    /// Pure outcome of a card: integration evidence first, then the lane.
    /// An archived card counts as completed.
    /// </summary>
    public static string Of(string? state, string? integrationStatus, bool hasBlocker)
    {
        if (string.Equals(integrationStatus, IntegrationStatuses.Integrated, StringComparison.OrdinalIgnoreCase))
            return Integrated;
        return state switch
        {
            TaskStates.Completed or TaskStates.Archive => Completed,
            TaskStates.HumanReview => Delivered,
            TaskStates.Escalated => Escalated,
            TaskStates.FailedPickup or TaskStates.CodeNotComplete => Blocked,
            TaskStates.Progress or TaskStates.AutoReview => hasBlocker ? Blocked : InProgress,
            _ => hasBlocker ? Blocked : Open,
        };
    }

    /// <summary>True while a run can still add tokens to the card.</summary>
    public static bool IsInFlight(string? state)
        => state is TaskStates.Progress or TaskStates.AutoReview;
}
