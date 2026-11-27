namespace AgentStudio.TaskServer.Contracts;

/// <summary>Exact-tip post-merge execution on a fenced gate worker.</summary>
public sealed record PostMergeGateWork(string Project, string Branch, string Kind, int GateSlots = 1);
public sealed record PostMergeHostAvailability(bool Gate, bool Coding);
public sealed record PostMergePlacement(string Executor, string? Reason = null, string? SubjectId = null);
public sealed record PostMergeWindowClosure(string TipSha, DateTimeOffset ClosedAtUtc, string Reason);
