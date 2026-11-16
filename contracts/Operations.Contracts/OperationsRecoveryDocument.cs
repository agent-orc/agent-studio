namespace AgentStudio.Operations.Contracts;

/// <summary>Checks retained after a host restore until every remote authority has been reviewed.</summary>
public sealed record OperationsRecoveryItem(string Kind, string Id, bool Complete, string[] OldPrincipalIds);

public sealed record OperationsRecoveryDocument(string BackupId, string FencedAt,
    OperationsRecoveryItem[] Items);
