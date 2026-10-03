namespace AgentStudio.TaskServer.Contracts;

/// <summary>A versioned engine decision. Generation is the task's last issued run fence.</summary>
public sealed record SteeringActionRequest(
    int ContractVersion,
    string CommandId,
    string Action,
    long ExpectedTaskVersion,
    long ExpectedGeneration,
    string Reason);

public sealed record SteeringActionReceipt(
    string CommandId,
    string ProjectId,
    string TaskId,
    string Action,
    long ExpectedTaskVersion,
    long ExpectedGeneration,
    long ResultTaskVersion,
    string ResultState,
    string Actor,
    string Reason,
    DateTime AcceptedAt);

/// <summary>One ordered continuation round. ExpectedTaskVersion is a compare-and-swap token.</summary>
public sealed record ContinuationIntentRequest(
    int ContractVersion,
    string CommandId,
    long ExpectedTaskVersion,
    string Prompt,
    string? Model,
    string? CliType,
    string? ThinkingLevel,
    string Mode,
    string Reason);

/// <summary>The immutable acceptance receipt returned on every identical retry.</summary>
public sealed record ContinuationIntentReceipt(
    string CommandId,
    string ProjectId,
    string TaskId,
    long ExpectedTaskVersion,
    long ResultTaskVersion,
    long Round,
    long PromptRevision,
    long SpecRevision,
    string Actor,
    string Reason,
    DateTime AcceptedAt,
    string PolicyVersion,
    bool ExplicitSelection);

/// <summary>Read projection for a later task UI, including the exact claiming run.</summary>
public sealed record ContinuationIntentProjection(
    ContinuationIntentReceipt Receipt,
    string Status,
    string Prompt,
    string? Model,
    string? CliType,
    string? ThinkingLevel,
    string Mode,
    string? RunId,
    long? Fence,
    DateTime? ConsumedAt,
    ContinuationSelectionMask? Selection = null);

/// <summary>Distinguishes explicit route fields from values snapshotted for the receipt.</summary>
public sealed record ContinuationSelectionMask(bool Model, bool CliType, bool ThinkingLevel);
