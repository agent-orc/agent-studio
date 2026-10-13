using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Versioned runbook data (docs/credentials-and-logins/runbooks.json). A step is an
/// allowlisted adapter opcode; a document can never supply shell text to execute.
/// </summary>
public static class CredentialRunbookProtocol
{
    public const int CatalogueSchemaVersion = 1;
    public const string CatalogueRecordClass = "proposed-runbook-catalogue";

    public static readonly string[] ReceiptFields =
    [
        "operationId", "stepId", "actorId", "hostId", "credentialId", "expectedGeneration",
        "observedGeneration", "outcome", "evidenceRefs", "occurredAt",
    ];

    public static readonly IReadOnlyDictionary<string, CredentialRunbookStepDefinition> Steps =
        new CredentialRunbookStepDefinition[]
        {
            new("discover_effective_source", CredentialRunbookStepEffect.Observe, CredentialRunbookStepOwner.TaskServer),
            new("classify_provider_access", CredentialRunbookStepEffect.Observe, CredentialRunbookStepOwner.TaskServer),
            new("correlate_incident", CredentialRunbookStepEffect.Observe, CredentialRunbookStepOwner.TaskServer),
            new("discover_repository_binding", CredentialRunbookStepEffect.Observe, CredentialRunbookStepOwner.TaskServer),
            new("probe_exact_origin", CredentialRunbookStepEffect.Observe, CredentialRunbookStepOwner.Host),
            new("verify_real_request", CredentialRunbookStepEffect.Observe, CredentialRunbookStepOwner.TaskServer, Proves: true),
            new("verify_recovery_canary", CredentialRunbookStepEffect.Observe, CredentialRunbookStepOwner.TaskServer, Proves: true),
            new("verify_repository_access", CredentialRunbookStepEffect.Observe, CredentialRunbookStepOwner.Host, Proves: true),
            new("pause_affected_claims", CredentialRunbookStepEffect.Hold, CredentialRunbookStepOwner.TaskServer),
            new("schedule_bounded_retry", CredentialRunbookStepEffect.Hold, CredentialRunbookStepOwner.TaskServer),
            new("begin_host_login", CredentialRunbookStepEffect.Issue, CredentialRunbookStepOwner.TaskServer,
                HumanBoundary: CredentialRunbookHumanBoundaries.BrowserConsent),
            new("generate_host_key", CredentialRunbookStepEffect.Issue, CredentialRunbookStepOwner.Host),
            new("register_public_key", CredentialRunbookStepEffect.Issue, CredentialRunbookStepOwner.Human,
                HumanBoundary: CredentialRunbookHumanBoundaries.RepositoryAdministration),
            new("stage_local_secret", CredentialRunbookStepEffect.Stage, CredentialRunbookStepOwner.Host),
            new("install_generation", CredentialRunbookStepEffect.Install, CredentialRunbookStepOwner.Host),
            new("reconcile_units_safely", CredentialRunbookStepEffect.Install, CredentialRunbookStepOwner.Host),
            new("switch_repository_identity", CredentialRunbookStepEffect.Install, CredentialRunbookStepOwner.Host),
            new("advertise_generation", CredentialRunbookStepEffect.Advertise, CredentialRunbookStepOwner.TaskServer),
            new("retire_old_generation", CredentialRunbookStepEffect.Revoke, CredentialRunbookStepOwner.Host),
        }.ToDictionary(step => step.StepId, StringComparer.Ordinal);

    /// <summary>Predicate fields map to typed facts; anything else rejects the catalogue.</summary>
    public static readonly IReadOnlyDictionary<string, Func<CredentialIncidentFacts, object?>> Fields =
        new Dictionary<string, Func<CredentialIncidentFacts, object?>>(StringComparer.Ordinal)
        {
            ["credential.kind"] = facts => facts.CredentialKind,
            ["evidence.refreshSessionSharedAcrossHosts"] = facts => facts.RefreshSessionSharedAcrossHosts,
            ["probe.outcome"] = facts => facts.ProbeOutcome,
            ["request.normalizedCode"] = facts => facts.RequestNormalizedCode,
            ["incident.corroboration"] = facts => facts.IncidentCorroboration,
            ["repository.purpose"] = facts => facts.RepositoryPurpose,
            ["repository.requiredAccessVerified"] = facts => facts.RepositoryRequiredAccessVerified,
        };

    public static readonly IReadOnlySet<string> Kinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "refresh_rotation_conflict", "provider_incident_unauthorized", "repository_scope_missing",
    };

    public static readonly IReadOnlySet<string> Outcomes = new HashSet<string>(StringComparer.Ordinal)
    {
        "healthy", "credential_invalid", "provider_incident", "quota_exhausted", "network_failure",
        "indeterminate", "not_verified",
    };

    public static readonly IReadOnlySet<string> Corroborations = new HashSet<string>(StringComparer.Ordinal)
    {
        "official-applicable", "independent-known-good-match", "none",
    };
}

public static class CredentialRunbookHumanBoundaries
{
    public const string BrowserConsent = "browser-consent";
    public const string RepositoryAdministration = "repository-administration";
}

public enum CredentialRunbookStepEffect { Observe, Hold, Issue, Stage, Install, Advertise, Revoke }

/// <summary>Who may produce a step receipt: a Task Server adapter, a host adapter or an explicit human.</summary>
public enum CredentialRunbookStepOwner { TaskServer, Host, Human }

public sealed record CredentialRunbookStepDefinition(
    string StepId,
    CredentialRunbookStepEffect Effect,
    CredentialRunbookStepOwner Owner,
    string? HumanBoundary = null,
    bool Proves = false)
{
    /// <summary>Issue, stage, install and revoke change credential custody and need explicit repair intent.</summary>
    public bool ChangesCustody => Effect is CredentialRunbookStepEffect.Issue or CredentialRunbookStepEffect.Stage
        or CredentialRunbookStepEffect.Install or CredentialRunbookStepEffect.Revoke;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CredentialRunbookCatalogueDto(
    int SchemaVersion, string RecordClass, IReadOnlyList<CredentialRunbookDto> Runbooks);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CredentialRunbookDto(
    string Id,
    string IncidentDate,
    string Kind,
    IReadOnlyList<string> CredentialKinds,
    string ReportedEvent,
    IReadOnlyList<string> Discrimination,
    string HistoricalFix,
    IReadOnlyList<string> Unknowns,
    string TargetOutcome,
    string OperatorAction,
    IReadOnlyList<string> HumanSteps,
    IReadOnlyList<string> PlatformSteps,
    IReadOnlyList<string> Verification,
    IReadOnlyList<string> Rollback,
    string ActionPolicy,
    int Version,
    CredentialRunbookProvenanceDto Provenance,
    IReadOnlyList<string> Preconditions,
    string StepExecution,
    IReadOnlyList<string> ReceiptFields,
    CredentialRunbookMatchDto Match,
    CredentialRunbookExecutionPolicyDto ExecutionPolicy);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CredentialRunbookProvenanceDto(
    string Type, string SourceTaskKey, string ReportedAt, bool IndependentlyReproduced);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CredentialRunbookMatchDto(IReadOnlyList<CredentialRunbookPredicateDto> All);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CredentialRunbookPredicateDto(string Field, string Operator, JsonElement Value);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CredentialRunbookExecutionPolicyDto(
    bool GenerationFenceRequired,
    bool DurableIdempotencyRequired,
    bool SecretValuesAllowedInReceipts,
    string Authorization,
    string OnUnknownPredicate);

/// <summary>Typed predicate inputs. Null means not observed, never false.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CredentialIncidentFacts(
    string? CredentialKind = null,
    bool? RefreshSessionSharedAcrossHosts = null,
    string? ProbeOutcome = null,
    string? RequestNormalizedCode = null,
    string? IncidentCorroboration = null,
    string? RepositoryPurpose = null,
    bool? RepositoryRequiredAccessVerified = null);

/// <summary>Typed recovery reaction selected with a runbook.</summary>
public sealed record CredentialRecoveryPolicy(
    string OperatorItem,
    string HoldScope,
    IReadOnlyList<int> RetrySeconds,
    IReadOnlyList<string> HumanBoundaries,
    bool CredentialRotationAllowed,
    bool SignInAdvice,
    string? OperatorAction);

public static class CredentialRunbookModes
{
    public const string Repair = "repair";
    public const string DiagnosisOnly = "diagnosis-only";
    public const string None = "none";
}

public sealed record CredentialRunbookSelection(
    string Mode,
    string? RunbookId,
    int? RunbookVersion,
    CredentialRecoveryPolicy Policy,
    IReadOnlyList<string> UnknownFields,
    string Reason);

public static class CredentialRunbookIntents
{
    public const string Diagnose = "diagnose";
    public const string Repair = "repair";
}

public static class CredentialRunbookStates
{
    public const string Requested = "requested";
    public const string AwaitingHuman = "awaiting-human";
    public const string AwaitingHost = "awaiting-host";
    public const string AwaitingRecovery = "awaiting-recovery";
    public const string AwaitingRepair = "awaiting-repair";
    public const string DiagnosisOnly = "diagnosis-only";
    public const string Complete = "complete";
    public const string Failed = "failed";
    public const string RecoveryRequired = "recovery-required";

    public static bool IsTerminal(string state)
        => state is Complete or Failed or RecoveryRequired or DiagnosisOnly;
}

public static class CredentialRunbookStepOutcomes
{
    public const string Started = "started";
    public const string AwaitingHuman = "awaiting-human";
    public const string AwaitingHost = "awaiting-host";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BeginCredentialRunbookRequest(
    string InstallationId,
    string HostId,
    string CredentialId,
    string ExpectedGeneration,
    string IdempotencyKey,
    string Intent,
    CredentialIncidentFacts Facts,
    string? IncidentRef = null,
    IReadOnlyList<string>? EvidenceRefs = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ContinueCredentialRunbookRequest(string Intent);

/// <summary>An explicit host or human receipt for the current step only.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RecordCredentialRunbookStepRequest(
    string StepId,
    string Outcome,
    string? ObservedGeneration,
    IReadOnlyList<string> EvidenceRefs);

public sealed record CredentialRunbookOperationDto(
    string OperationId,
    string RunbookId,
    int RunbookVersion,
    string SelectionMode,
    string Intent,
    string InstallationId,
    string HostId,
    string CredentialId,
    string ExpectedGeneration,
    string ActorId,
    string IdempotencyKey,
    string? IncidentRef,
    IReadOnlyList<string> EvidenceRefs,
    CredentialIncidentFacts Facts,
    CredentialRecoveryPolicy Policy,
    string State,
    string? CurrentStep,
    string? Detail,
    string? LinkedRenewalKey,
    string? LinkedRenewalOperationId,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>Property names are exactly the runbook receiptFields; no secret field exists.</summary>
public sealed record CredentialRunbookStepReceiptDto(
    string OperationId,
    string StepId,
    string ActorId,
    string HostId,
    string CredentialId,
    string ExpectedGeneration,
    string? ObservedGeneration,
    string Outcome,
    IReadOnlyList<string> EvidenceRefs,
    DateTime OccurredAt);

public sealed record CredentialRunbookRunDto(
    CredentialRunbookSelection Selection,
    CredentialRunbookOperationDto? Operation,
    IReadOnlyList<CredentialRunbookStepReceiptDto> Receipts);
