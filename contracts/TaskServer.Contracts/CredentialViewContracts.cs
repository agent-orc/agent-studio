namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// I8 Execution Hosts credential view (docs/credentials-and-logins/index.html#surface,
/// D4 option A). A role-scoped, metadata-only projection of the credential registry,
/// fleet health circuits and generation-keyed expiry or rotation reminders. No field
/// carries a secret value, and no route offers reveal, download or copy.
/// </summary>
public static class CredentialViewProtocol
{
    /// <summary>
    /// Studio role the trusted Studio edge acts for when the human session lives at the
    /// edge rather than at the Task Server. The edge strips any client-supplied copy.
    /// </summary>
    public const string ActingRoleHeader = "X-Studio-Acting-Role";

    /// <summary>Reminder thresholds in days before the expiry or rotation due date.</summary>
    public static readonly IReadOnlyList<int> ReminderThresholdDays = [14, 7, 1];

    public static class Statuses
    {
        public const string Healthy = "healthy";
        public const string CredentialInvalid = "credential_invalid";
        public const string ProviderIncident = "provider_incident";
        public const string QuotaExhausted = "quota_exhausted";
        public const string NetworkFailure = "network_failure";
        public const string DiagnosisPending = "diagnosis_pending";
        public const string NotVerified = "not_verified";
    }
}

/// <summary>
/// Expiry or rotation knowledge for the current generation. <c>Knowledge</c> is
/// <c>known</c>, <c>none</c> (a credential-kind guarantee) or <c>unknown</c>; an
/// unknown date is never rendered as healthy. An auto-refreshable access-token
/// expiry is never a due date.
/// </summary>
public sealed record CredentialDueDto(
    string Knowledge,
    string? Subject,
    DateTime? DueAt,
    int? ThresholdDays,
    bool Overdue,
    bool ExpiryUnknown,
    DateTime? ReviewDueAt,
    string? UnknownReason);

/// <summary>One operator item per credential generation; urgency only rises.</summary>
public sealed record CredentialReminderDto(
    string InstallationId,
    string HostId,
    string CredentialId,
    string Generation,
    string Subject,
    DateTime DueAt,
    int ThresholdDays,
    DateTime OpenedAt,
    DateTime UpdatedAt,
    DateTime? ClosedAt,
    string? CloseReason);

/// <summary>An open I3 renewal or diagnosis item for this credential binding.</summary>
public sealed record CredentialHealthItemRefDto(string Kind, DateTime OpenedAt, string? RunbookHref);

/// <summary>
/// Server-authorized action for the calling role. <c>Kind</c> is <c>renew</c>
/// (an existing host-owned sign-in flow; <c>Method</c> is R1, R2 or R3),
/// <c>runbook</c> (a safe repository documentation link) or
/// <c>requires-administrator</c> (informational, no control).
/// </summary>
public sealed record CredentialViewActionDto(
    string Kind,
    string Label,
    bool Primary,
    string? Method = null,
    string? Href = null);

public sealed record CredentialViewRowDto(
    string InstallationId,
    string HostId,
    string CredentialId,
    string Kind,
    string Provider,
    string RenewalMethod,
    string Adapter,
    string EffectiveSource,
    string? LocalRef,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<string> Services,
    string Owner,
    string Generation,
    string? Supersedes,
    DateTime? LastVerifiedAt,
    DateTime? LastRealSuccessAt,
    DateTime ObservedAt,
    string Status,
    string? StatusDetail,
    DateTime? NextRetryAt,
    string? ServiceAvailability,
    string EvidenceQuality,
    CredentialDueDto Due,
    CredentialReminderDto? Reminder,
    CredentialHealthItemRefDto? HealthItem,
    string? RunbookHref,
    IReadOnlyList<string> EvidenceRefs,
    IReadOnlyList<CredentialViewActionDto> Actions);

/// <summary><c>Role</c> is the resolved Studio role: owner, operator or viewer.</summary>
public sealed record CredentialViewDto(
    string Role,
    DateTime GeneratedAt,
    IReadOnlyList<CredentialViewRowDto> Rows);
