using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>One current reminder per host credential. The generation fences a renewal.</summary>
public static class CredentialReminderPolicy
{
    public static CredentialReminderDto? Evaluate(CredentialRegistryRecordDto record, DateTime now)
    {
        if (record.LastOutcome == "provider_incident") return null;
        if (record.LastVerifiedAt > now.AddMinutes(2) || record.DiscoveredAt > now.AddMinutes(2))
            return null;
        var due = new[]
        {
            record.ExpiryKnowledge is "issuer" or "operator" ? record.ExpiresAt : null,
            record.RotationDueAt,
        }.Where(date => date is not null).Min();
        if (due is null || due > now.AddDays(14)) return null;
        var threshold = due <= now.AddDays(1) ? 1 : due <= now.AddDays(7) ? 7 : 14;
        return new CredentialReminderDto(
            $"credential:{record.InstallationId}:{record.HostId}:{record.CredentialId}:{record.Generation}",
            threshold, due.Value,
            record.ExpiresAt == due ? "expiry" : "rotation");
    }
}

public sealed record CredentialReminderDto(string Id, int ThresholdDays, DateTime DueAt, string Reason);

/// <summary>Presentation projection excludes locators, fingerprints, account identifiers and values.</summary>
public sealed record CredentialViewDto(
    string InstallationId, string HostId, string CredentialId, string Generation,
    string? Supersedes, string Provider, string Kind, string SourceLabel,
    string EffectiveSource, IReadOnlyList<string> Scopes,
    string Owner, DateTime? LastVerifiedAt, string ExpiryKnowledge, DateTime? ExpiresAt,
    DateTime? RotationDueAt, string Outcome, DateTime? NextProbeAt,
    string EvidenceQuality, IReadOnlyList<string> EvidenceRefs, string? RunbookId, CredentialReminderDto? Reminder,
    string? RenewalAction);

public static class CredentialViewPolicy
{
    public static CredentialViewDto Project(CredentialRegistryRecordDto record, DateTime now)
    {
        var clockSkew = record.LastVerifiedAt > now.AddMinutes(2) || record.DiscoveredAt > now.AddMinutes(2);
        var action = !clockSkew && record.LastOutcome == "credential_invalid" ? record.Kind switch
        {
            "codex_chatgpt_login" => "codex-sign-in",
            "claude_native_login" or "claude_oauth_token" => "claude-sign-in",
            _ => null,
        } : null;
        return new CredentialViewDto(
            record.InstallationId, record.HostId, record.CredentialId, record.Generation,
            record.Supersedes, record.Provider, record.Kind, record.Locator.Adapter,
            record.Locator.EffectiveSource, record.Scopes,
            record.Owner, record.LastVerifiedAt, record.ExpiryKnowledge, record.ExpiresAt,
            record.RotationDueAt, record.LastOutcome, record.NextProbeAt,
            clockSkew ? "clock-skew" : "current",
            record.EvidenceRefs, record.RunbookId, CredentialReminderPolicy.Evaluate(record, now), action);
    }
}
