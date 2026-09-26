namespace AgentStudio.Tasks;

/// <summary>Wire contract for the additive task core route.</summary>
public sealed record TaskCoreResponse
{
    public string State { get; init; } = "ready";
    public string ProjectId { get; init; } = "";
    public string ProjectName { get; init; } = "";
    public string Id { get; init; } = "";
    public string TaskKey { get; init; } = "";
    public string? Key { get; init; }
    public string Title { get; init; } = "";
    public string Kind { get; init; } = "task";
    public string TaskType { get; init; } = "chore";
    public string Lane { get; init; } = "";
    public string? ArchiveState { get; init; }
    public DateTime EnteredLaneAt { get; init; }
    public string? Phase { get; init; }
    public DateTime? PhaseEnteredAt { get; init; }
    public int Order { get; init; }
    public string Mode { get; init; } = "coding";
    public bool Released { get; init; }
    public bool PendingIntent { get; init; }
    public TaskCorePins Pins { get; init; } = new();
    public TaskCoreActions Actions { get; init; } = new();
    public TaskCoreBlocking Blocking { get; init; } = new();
    public TaskCoreRuntimeSnapshot Runtime { get; init; } = new();
    public string RuntimeVersion { get; init; } = "";
    public TaskCoreText StatusSummary { get; init; } = TaskCoreText.Missing;
    public TaskCorePromptResponse Prompt { get; init; } = new();
    public TaskCoreTimelineResponse Timeline { get; init; } = new();
    public long CoreVersion { get; init; }
}

public sealed record TaskCorePins
{
    public string? Model { get; init; }
    public bool ModelExplicit { get; init; }
    public string? ThinkingLevel { get; init; }
    public bool ThinkingLevelExplicit { get; init; }
    public string? CliType { get; init; }
    public string? ContextMode { get; init; }
    public bool? UseOwnSession { get; init; }
    public bool AllowWebAccess { get; init; }
    public bool NoBranchExpected { get; init; }
}

public sealed record TaskCoreActions(bool CanEdit = false, bool CanMove = false,
    bool CanDelete = false, bool CanContinue = false);

public sealed record TaskCoreBlocking
{
    public string? BlockerType { get; init; }
    public string? BlockerCondition { get; init; }
    public string? BlockerStatus { get; init; }
    public string? BlockerDescription { get; init; }
    public TaskCoreOutcomeIssue? OutcomeIssue { get; init; }
    public string? NeedsInput { get; init; }
    public bool DependencyBlocked { get; init; }
    public string DependencyState { get; init; } = "ready";
    public IReadOnlyList<TaskCoreDependency> Dependencies { get; init; } = [];
    public IReadOnlyList<string> DependsOn { get; init; } = [];
    public IReadOnlyList<string> BlockedBy { get; init; } = [];
}

public sealed record TaskCorePromptResponse(
    string State = "missing", string? Text = null, long OriginalBytes = 0,
    string? Hash = null, string? Cursor = null, string? ContinuationUrl = null);

public sealed record TaskCoreTimelineResponse(
    string State = "missing", IReadOnlyList<TaskCoreEvent>? Events = null,
    long OriginalBytes = 0, string? Hash = null, string? Cursor = null,
    string? ContinuationUrl = null);
