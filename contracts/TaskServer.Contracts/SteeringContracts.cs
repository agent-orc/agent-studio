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
