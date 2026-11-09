using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentStudio.Operations.Contracts;

public static class OperationsProtocol
{
    public const int Version = 1;
    public const string Header = "X-Operations-Protocol";
    public const string Audience = "operations-server";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string Digest(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string Digest<T>(T value) => Digest(JsonSerializer.Serialize(value, Json));
}

public sealed record OperationDefinition(string Id, int Version, string Scope, string SideEffect,
    string InputSchemaDigest, string OutputSchemaDigest, int TimeoutSeconds, int MaxOutputBytes);
public sealed record OperationSubject(string TaskId, string RunOrReviewId, long Fence);
public sealed record OperationCommand(string IdempotencyKey, string Actor, string AgentId,
    string OperationId, int Version, JsonElement Input, string InputDigest,
    DateTimeOffset Deadline, string CorrelationId, OperationSubject? Subject = null, string? Permit = null);
public sealed record AgentCapability(string AgentId, string BootId, string HostClass,
    string[] Roots, string[] Tools, Dictionary<string, int> Operations, int Capacity, int ProtocolMin, int ProtocolMax);
public sealed record AgentRegistration(AgentCapability Capability, DateTimeOffset LastSeen);
public sealed record AgentPollRequest(string BootId);
public sealed record AttemptLeaseRequest(string BootId, long Fence);
public sealed record OperationEvent(long Cursor, DateTimeOffset At, string Kind, string Digest);
public sealed record OperationArtifact(string Name, string Sha256, long Bytes);
public sealed record OperationResult(string Outcome, string SubjectDigest, string ExitClassification,
    JsonElement Output, OperationArtifact[] Artifacts, bool CleanupConfirmed, DateTimeOffset StartedAt, DateTimeOffset FinishedAt);
public sealed record AttemptReportRequest(string BootId, long Fence, OperationResult Result);
public sealed record OperationAttempt(string Id, string PrincipalId, OperationCommand Command,
    string AgentId, string? BootId, long Fence, DateTimeOffset? LeaseUntil, string State,
    OperationEvent[] Events, OperationResult? Result = null, string? ResultDigest = null,
    OperationResult? LateResult = null, string? LateResultDigest = null);
public sealed record PermitIntrospectionRequest(string Permit, string PrincipalId, string Actor,
    OperationSubject Subject, string OperationId, int Version, string InputDigest, string AgentId,
    string ResultAudience, string CorrelationId, DateTimeOffset Deadline);
public sealed record PermitIntrospectionResponse(bool Active, DateTimeOffset ValidUntil);
public sealed record OperationError(string Code);
public sealed record HostJournalEntry(string AttemptId, string Actor, string AgentId,
    string OperationId, string InputDigest, DateTimeOffset At, string Outcome);
public sealed record HostRevocationList(long Version, string Sha256, string Base64Krl);

public static class OperationCatalogue
{
    // The first executable capability has no filesystem or process side effects.
    // Additional definitions require an executor and their own security evidence.
    public static readonly OperationDefinition HostInspect = new("host.inspect", 1, "host.inspect", "read-only",
        OperationsProtocol.Digest("{\"type\":\"object\",\"maxProperties\":0}"),
        OperationsProtocol.Digest("{\"type\":\"object\",\"required\":[\"os\",\"architecture\",\"processors\"]}"), 30, 4096);

    // AGT-3207: the AI Operator's bounded restart. The input names one service
    // from the agent's local allowlist; the agent maps it to a fixed compose
    // target, validates the configuration first and never accepts a path,
    // command or argument from the caller.
    public static readonly OperationDefinition ServiceRestart = new("service.restart", 1, "service.restart", "bounded-restart",
        OperationsProtocol.Digest("{\"type\":\"object\",\"required\":[\"service\"],\"maxProperties\":1}"),
        OperationsProtocol.Digest("{\"type\":\"object\",\"required\":[\"service\",\"restarted\",\"configurationValid\"]}"), 180, 4096);

    public static readonly OperationDefinition HostDrain = new("host.drain", 1, "host.drain", "claim-barrier",
        OperationsProtocol.Digest("{\"type\":\"object\",\"required\":[\"role\"],\"maxProperties\":1}"),
        OperationsProtocol.Digest("{\"type\":\"object\",\"required\":[\"role\",\"drained\"]}"), 60, 4096);
    public static readonly OperationDefinition HostResume = new("host.resume", 1, "host.resume", "claim-barrier",
        OperationsProtocol.Digest("{\"type\":\"object\",\"required\":[\"role\"],\"maxProperties\":1}"),
        OperationsProtocol.Digest("{\"type\":\"object\",\"required\":[\"role\",\"drained\"]}"), 60, 4096);
    public static readonly OperationDefinition HostLogsCollect = new("host.logs.collect", 1, "host.logs.collect", "bounded-read",
        OperationsProtocol.Digest("{\"type\":\"object\",\"required\":[\"source\"],\"maxProperties\":1}"),
        OperationsProtocol.Digest("{\"type\":\"object\",\"required\":[\"source\",\"text\"]}"), 30, 16384);
    public static readonly OperationDefinition HostConfigurationApply = new("host.configuration.apply", 1, "host.configuration.apply", "reviewed-file-copy",
        OperationsProtocol.Digest("{\"type\":\"object\",\"required\":[\"configuration\",\"sha256\"],\"maxProperties\":2}"),
        OperationsProtocol.Digest("{\"type\":\"object\",\"required\":[\"configuration\",\"applied\"]}"), 60, 4096);

    public static readonly IReadOnlyList<OperationDefinition> All = [HostInspect, ServiceRestart, HostDrain,
        HostResume, HostLogsCollect, HostConfigurationApply];

    public static OperationDefinition? Find(string id, int version) =>
        All.FirstOrDefault(definition => definition.Id == id && definition.Version == version);

    /// <summary>An allowlisted service name: lower-case letters, digits and dashes.</summary>
    public static bool ValidServiceName(string? value) =>
        value is { Length: >= 2 and <= 63 } && value[0] is >= 'a' and <= 'z'
        && value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    public static bool ValidSha256(string? value) => value is { Length: 64 }
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public sealed record IssueOperationPermitRequest(string PrincipalId, string Actor, OperationSubject Subject,
    string OperationId, int Version, string InputDigest, string AgentId, string ResultAudience, string CorrelationId, DateTimeOffset Deadline);
public sealed record IssuedOperationPermitResponse(string Permit, DateTimeOffset ValidUntil);

/// <summary>
/// Security model SM-2: the control plane's kill switch revokes the
/// Operations Server principals that belong to operator agents. With
/// <see cref="AllOperators"/> every principal that names an operator agent is
/// revoked; otherwise those of <see cref="OperatorAgentIds"/>. Queued
/// operations of a revoked principal are cancelled and running ones asked to stop.
/// </summary>
public sealed record OperatorPrincipalRevocationRequest(string[] OperatorAgentIds, bool AllOperators, string CorrelationId);
public sealed record OperatorPrincipalRevocationResponse(string[] RevokedPrincipalIds, int CancelledAttempts);

public static class OperatorPrincipalRevocation
{
    public const string Path = "api/operations/v1/principals/revoke-operators";
    /// <summary>The scope of the control plane principal that may revoke operator principals.</summary>
    public const string Scope = "principals.revoke";
}
