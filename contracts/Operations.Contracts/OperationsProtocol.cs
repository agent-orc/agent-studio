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

public static class OperationCatalogue
{
    // The first executable capability has no filesystem or process side effects.
    // Additional definitions require an executor and their own security evidence.
    public static readonly OperationDefinition HostInspect = new("host.inspect", 1, "host.inspect", "read-only",
        OperationsProtocol.Digest("{\"type\":\"object\",\"maxProperties\":0}"),
        OperationsProtocol.Digest("{\"type\":\"object\",\"required\":[\"os\",\"architecture\",\"processors\"]}"), 30, 4096);
    public static OperationDefinition? Find(string id, int version) =>
        id == HostInspect.Id && version == HostInspect.Version ? HostInspect : null;
}

public sealed record IssueOperationPermitRequest(string PrincipalId, string Actor, OperationSubject Subject,
    string OperationId, int Version, string InputDigest, string AgentId, string ResultAudience, string CorrelationId, DateTimeOffset Deadline);
public sealed record IssuedOperationPermitResponse(string Permit, DateTimeOffset ValidUntil);
