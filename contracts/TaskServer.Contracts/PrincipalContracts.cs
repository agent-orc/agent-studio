namespace AgentStudio.TaskServer.Contracts;

public static class TaskServerPrincipalKinds
{
    public const string Studio = "studio";
    public const string Engine = "engine";
    public const string Runner = "runner";
}

public static class TaskServerScopes
{
    public const string TasksRead = "tasks:read";
    public const string TasksWrite = "tasks:write";
    public const string RunsClaim = "runs:claim";
    public const string RunsWrite = "runs:write";
    public const string ReviewsClaim = "reviews:claim";
    public const string ReviewsWrite = "reviews:write";
    public const string OrchestrationClaim = "orchestration:claim";
    public const string OrchestrationWrite = "orchestration:write";
    public const string Management = "management";
    public const string EventsSubscribe = "events:subscribe";
    public const string EventsWrite = "events:write";
}

public sealed record CreatePrincipalRequest(
    string PrincipalId,
    string Kind,
    IReadOnlyList<string>? Scopes = null,
    string? RunnerId = null);

public sealed record RotatePrincipalRequest(int? OverlapSeconds = null);

public sealed record PrincipalDto(
    string PrincipalId,
    string Kind,
    IReadOnlyList<string> Scopes,
    string? RunnerId,
    DateTime CreatedAt,
    DateTime? RevokedAt,
    DateTime? LastSeenAt);

public sealed record IssuedPrincipalCredential(
    PrincipalDto Principal,
    string Credential,
    DateTime CreatedAt,
    DateTime? PreviousCredentialValidUntil = null);
