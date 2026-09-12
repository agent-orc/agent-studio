namespace AgentStudio.Docs;

/// <summary>
/// The shared, dependency-free contract of a Workbench decision (AGT-2375).
/// Both the read side (<see cref="WorkbenchCatalogueService"/>, when it projects
/// a stored <c>decision</c> receipt) and the write side
/// (<see cref="WorkbenchDecisionService"/>) validate through exactly these
/// rules, so a receipt that was accepted on write can never be rejected on the
/// next read.
/// </summary>
public static class WorkbenchDecisionContracts
{
    /// <summary>
    /// Operation ids are client-generated idempotency keys. They are echoed into
    /// the durable receipt, so they must stay filename- and log-safe.
    /// </summary>
    public static bool SafeOperationId(string? value) =>
        value is { Length: >= 8 and <= 128 }
        && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    /// <summary>
    /// Lanes a decision draft may target. A Workbench decision hands work to the
    /// intake, never straight into execution, so the draft cannot name a running
    /// or reviewing lane.
    /// </summary>
    private static readonly string[] AllowedInitialLanes =
        [TaskStates.Backlog, TaskStates.Preparation, TaskStates.Ready];

    private static readonly string[] AllowedDecisionKinds = ["single", "multi", "confirm"];

    /// <summary>
    /// File-scoped content wins over repository provenance. A HEAD-only caller
    /// keeps the legacy fallback, while a caller that names the descriptor +
    /// HTML fingerprint is unaffected by commits to other files.
    /// </summary>
    public static string? StalenessError(
        string? expectedRevision,
        string? expectedFingerprint,
        string? currentRevision,
        string? currentFingerprint)
    {
        if (expectedRevision == null && expectedFingerprint == null)
            return "A decision must name the revision or fingerprint it was taken on.";
        if (expectedFingerprint != null)
            return expectedFingerprint == currentFingerprint
                ? null
                : "The Dossier content changed since the decision was taken.";
        return expectedRevision == currentRevision
            ? null
            : "The Dossier revision changed since the decision was taken.";
    }

    public static string? ValidateResponses(
        IReadOnlyList<WorkbenchDecisionResponse>? responses,
        bool allowPartial = false)
    {
        if (responses == null || responses.Count > 100)
            return "responses must contain at most 100 decision points.";
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var response in responses)
        {
            if (!SafeMarkupId(response.DecisionId) || !ids.Add(response.DecisionId))
                return "responses contain a malformed or duplicate decisionId.";
            if (!AllowedDecisionKinds.Contains(response.Kind, StringComparer.Ordinal))
                return $"response kind '{response.Kind}' is invalid.";
            if ((!allowPartial && response.SelectedOptionIds.Count == 0)
                || response.SelectedOptionIds.Count > 100
                || response.SelectedOptionIds.Any(optionId => !SafeMarkupId(optionId))
                || response.SelectedOptionIds.Distinct(StringComparer.Ordinal).Count()
                    != response.SelectedOptionIds.Count)
                return $"response '{response.DecisionId}' needs unique, safe selected option ids.";
            if (response.Kind is "single" or "confirm"
                && response.SelectedOptionIds.Count != 1
                && !(allowPartial && response.SelectedOptionIds.Count == 0))
                return $"response '{response.DecisionId}' requires exactly one selected option.";
            if (response.Comment is { Length: > 20_000 })
                return $"response '{response.DecisionId}' comment is too long.";
        }
        return null;
    }

    private static bool SafeMarkupId(string? value) =>
        value is { Length: >= 1 and <= 80 }
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    /// <summary>
    /// Returns null when the draft is a well-formed task request, otherwise a
    /// single human-readable reason. The draft is only ever a *request*: the
    /// decision service never creates the card itself (the client owns creation
    /// through the existing task API), so validation here is about bounding the
    /// text that gets written into the durable receipt.
    /// </summary>
    public static string? ValidateTaskDraft(WorkbenchTaskDraft? draft)
    {
        if (draft == null)
            return "task draft is missing.";
        if (string.IsNullOrWhiteSpace(draft.Title) || draft.Title.Trim().Length > 240)
            return "task.title is required and must be at most 240 characters.";
        if (string.IsNullOrWhiteSpace(draft.Goal) || draft.Goal.Trim().Length > 20_000)
            return "task.goal is required and must be at most 20000 characters.";
        if (draft.AcceptanceCriteria.Count is 0 or > 100
            || draft.AcceptanceCriteria.Any(item =>
                string.IsNullOrWhiteSpace(item) || item.Trim().Length > 2000))
            return "task.acceptanceCriteria needs 1-100 non-empty bounded items.";
        if (draft.EvidenceLinks.Count > 100
            || draft.EvidenceLinks.Any(item =>
                string.IsNullOrWhiteSpace(item) || item.Trim().Length > 2000))
            return "task.evidenceLinks contains an invalid item.";
        if (draft.RelatedTaskKeys.Count > 100
            || draft.RelatedTaskKeys.Any(item =>
                string.IsNullOrWhiteSpace(item) || item.Trim().Length > 100))
            return "task.relatedTaskKeys contains an invalid item.";
        if (draft.ChosenOption is { Length: > 2000 })
            return "task.chosenOption is too long.";
        if (!AllowedInitialLanes.Contains(draft.InitialLane, StringComparer.Ordinal))
            return "task.initialLane is invalid.";
        if (!TaskModes.IsValid(draft.Mode))
            return "task.mode is invalid.";
        if (!TaskTypes.All.Contains(draft.TaskType, StringComparer.Ordinal))
            return "task.taskType is invalid.";
        return null;
    }
}

public sealed record WorkbenchDecisionResponse
{
    public string DecisionId { get; init; } = "";
    public string Kind { get; init; } = "";
    public List<string> SelectedOptionIds { get; init; } = [];
    public string? Comment { get; init; }
}

/// <summary>
/// The task a feature decision asks for. It is stored verbatim in the decision
/// receipt and handed back to the caller; the backend does not turn it into a
/// card (see <see cref="WorkbenchDecisionService"/>).
/// </summary>
public sealed record WorkbenchTaskDraft
{
    public string Title { get; init; } = "";
    public string Goal { get; init; } = "";
    public List<string> AcceptanceCriteria { get; init; } = [];
    public List<string> EvidenceLinks { get; init; } = [];
    public string? ChosenOption { get; init; }
    public List<string> RelatedTaskKeys { get; init; } = [];
    public string? TargetProject { get; init; }
    public string InitialLane { get; init; } = TaskStates.Preparation;
    public string Mode { get; init; } = TaskModes.Coding;
    public string TaskType { get; init; } = TaskTypes.Feature;
}
