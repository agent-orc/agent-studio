using System.Text.RegularExpressions;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>
/// I8 due-date and reminder policy (docs/credentials-and-logins/index.html#renewal, Warnings;
/// D4 option A). Only issuer or operator expiry and an explicit rotation due date can raise a
/// reminder. An access-token expiry of a refreshable login is never a due date, and an unknown
/// date stays unknown. The Task Server clock is the only clock that sets urgency.
/// </summary>
public static class CredentialReminderPolicy
{
    /// <summary>Matches the registry boundary, which refuses observations further ahead.</summary>
    public static readonly TimeSpan ClockSkewTolerance = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan StaleObservationAge = TimeSpan.FromHours(24);

    public enum ReminderAction { None, Open, Escalate, Reschedule, Keep, Close }

    public sealed record ExistingReminder(DateTime DueAt, int ThresholdDays, bool Closed);

    public sealed record ReminderDecision(ReminderAction Action, int? ThresholdDays, DateTime? DueAt);

    public static CredentialDueDto Assess(CredentialRegistryRecordDto record, DateTime now)
    {
        var expiryKnown = record.ExpiryKnowledge is "issuer" or "operator" && record.ExpiresAt is not null;
        var expiryUnknown = record.ExpiryKnowledge == "unknown" || record.ExpiryKnowledge is "issuer" or "operator" && !expiryKnown;
        var candidates = new List<(string Subject, DateTime At)>();
        if (expiryKnown) candidates.Add(("expiry", record.ExpiresAt!.Value));
        if (record.RotationDueAt is { } rotation) candidates.Add(("rotation", rotation));
        if (candidates.Count == 0)
            return expiryUnknown
                ? new("unknown", null, null, null, false, true, record.NextProbeAt,
                    record.UnknownReasons.GetValueOrDefault("expiresAt") ?? "expiry-unknown")
                : new("none", null, null, null, false, false, null, null);
        var (subject, dueAt) = candidates.OrderBy(candidate => candidate.At).First();
        var remaining = dueAt - now;
        return new("known", subject, dueAt, Threshold(remaining), remaining <= TimeSpan.Zero, expiryUnknown,
            expiryUnknown ? record.NextProbeAt : null,
            expiryUnknown ? record.UnknownReasons.GetValueOrDefault("expiresAt") ?? "expiry-unknown" : null);
    }

    /// <summary>The most urgent applicable threshold, so late discovery emits only that one.</summary>
    public static int? Threshold(TimeSpan remaining)
    {
        foreach (var days in CredentialViewProtocol.ReminderThresholdDays.OrderBy(days => days))
            if (remaining <= TimeSpan.FromDays(days)) return days;
        return null;
    }

    /// <summary>
    /// One item per generation. For the same due date urgency only rises, so a clock that
    /// steps back cannot lower it or emit a second reminder; a changed due date reschedules.
    /// </summary>
    public static ReminderDecision Decide(CredentialDueDto due, ExistingReminder? existing)
    {
        if (due.ThresholdDays is not { } threshold || due.DueAt is not { } dueAt)
            return existing is { Closed: false }
                ? new(ReminderAction.Close, null, null)
                : new(ReminderAction.None, null, null);
        if (existing is null || existing.Closed) return new(ReminderAction.Open, threshold, dueAt);
        if (existing.DueAt != dueAt) return new(ReminderAction.Reschedule, threshold, dueAt);
        return threshold < existing.ThresholdDays
            ? new(ReminderAction.Escalate, threshold, dueAt)
            : new(ReminderAction.Keep, existing.ThresholdDays, dueAt);
    }

    /// <summary>
    /// <c>clock-skew</c> when the host observation or a verification time lies in the
    /// server's future; <c>stale</c> when the metadata was not observed within a day.
    /// </summary>
    public static string EvidenceQuality(CredentialRegistryRecordDto record, DateTime observedAt, DateTime now)
    {
        var horizon = now + ClockSkewTolerance;
        if (observedAt > horizon || record.LastVerifiedAt > horizon || record.LastRealSuccessAt > horizon
            || record.DiscoveredAt > horizon)
            return "clock-skew";
        return now - observedAt > StaleObservationAge ? "stale" : "ok";
    }
}

/// <summary>
/// Separates login failure from provider incident, quota, network and pending diagnosis.
/// Evidence order follows the Dossier: a missing effective source or confirmed invalid
/// login first, then trustworthy quota and network responses, then incident correlation.
/// </summary>
public static class CredentialStatusPolicy
{
    public sealed record Circuit(string Outcome, DateTime? NextRetryAt);

    /// <summary>One runner unit's provider-auth advertisement for this host and provider.</summary>
    public sealed record Unit(string? HealthOutcome, string? ServiceAvailability, string? Generation, DateTime? LimitedUntil);

    public sealed record Classification(string Status, string? Detail, DateTime? NextRetryAt, string? ServiceAvailability);

    public static Classification Classify(CredentialRegistryRecordDto record, Circuit? binding, Circuit? service,
        IReadOnlyList<Unit> units)
    {
        var current = units.Where(unit => unit.Generation is null || unit.Generation == record.Generation).ToArray();
        var availability = current.Select(unit => unit.ServiceAvailability)
            .OrderBy(value => value switch { "unavailable" => 0, "limited" => 1, "available" => 2, _ => 3 })
            .FirstOrDefault();
        bool Reports(string outcome) => current.Any(unit => unit.HealthOutcome == outcome);
        if (record.Locator.EffectiveSource == "absent")
            return new(CredentialViewProtocol.Statuses.CredentialInvalid, "effective-source-missing", null, availability);
        if (binding?.Outcome == "credential_invalid" || Reports("credential_invalid"))
            return new(CredentialViewProtocol.Statuses.CredentialInvalid, "login-confirmed-invalid", null, availability);
        if (Reports("quota_exhausted"))
            return new(CredentialViewProtocol.Statuses.QuotaExhausted, "provider-limit",
                current.Select(unit => unit.LimitedUntil).Max(), availability);
        if (Reports("network_failure"))
            return new(CredentialViewProtocol.Statuses.NetworkFailure, "connectivity", null, availability);
        if (service?.Outcome == "provider_incident" || Reports("provider_incident"))
            return new(CredentialViewProtocol.Statuses.ProviderIncident, "retry-scheduled", service?.NextRetryAt, availability);
        if (binding?.Outcome == "indeterminate" || Reports("indeterminate"))
            return new(CredentialViewProtocol.Statuses.DiagnosisPending, "evidence-inconclusive", binding?.NextRetryAt, availability);
        if (Reports("healthy")) return new(CredentialViewProtocol.Statuses.Healthy, null, null, availability);
        return record.LastOutcome switch
        {
            "healthy" => new(CredentialViewProtocol.Statuses.Healthy, null, null, availability),
            "credential_invalid" => new(CredentialViewProtocol.Statuses.CredentialInvalid, "login-confirmed-invalid", null, availability),
            "provider_incident" => new(CredentialViewProtocol.Statuses.ProviderIncident, "retry-scheduled", null, availability),
            "quota_exhausted" => new(CredentialViewProtocol.Statuses.QuotaExhausted, "provider-limit", null, availability),
            "network_failure" => new(CredentialViewProtocol.Statuses.NetworkFailure, "connectivity", null, availability),
            "indeterminate" => new(CredentialViewProtocol.Statuses.DiagnosisPending, "evidence-inconclusive", null, availability),
            _ => new(CredentialViewProtocol.Statuses.NotVerified, null, null, availability),
        };
    }
}

/// <summary>
/// Role-scoped actions (docs/credentials-and-logins/index.html#surface). Viewers see health
/// only. Operators renew provider logins through the existing host-owned sign-in flows (I4).
/// Repository scopes, principals, peers and administration keys need the owner, who is the
/// installation administrator. Secrets have no reveal, download or copy action.
/// </summary>
public static partial class CredentialActionPolicy
{
    private static readonly IReadOnlyDictionary<string, string> StudioRenewalLabels = new Dictionary<string, string>
    {
        ["R1"] = "Renew Claude token on this host",
        ["R2"] = "Sign in to Claude on this host",
        ["R3"] = "Sign in to Codex on this host",
    };

    private static readonly IReadOnlySet<string> AdministratorMethods = new HashSet<string>(["R4", "R5", "R6", "R7", "R9"]);

    public static string NormalizeRole(string? role) => role switch
    {
        StudioUserRoles.Owner => StudioUserRoles.Owner,
        StudioUserRoles.Operator => StudioUserRoles.Operator,
        _ => StudioUserRoles.Viewer,
    };

    public static bool MayReadLocators(string role) => NormalizeRole(role) is StudioUserRoles.Owner or StudioUserRoles.Operator;

    public static IReadOnlyList<CredentialViewActionDto> For(string role, CredentialRegistryRecordDto record,
        string status, bool reminderOpen, string? runbookHref)
    {
        role = NormalizeRole(role);
        if (role == StudioUserRoles.Viewer) return [];
        var invalid = status == CredentialViewProtocol.Statuses.CredentialInvalid;
        var actions = new List<CredentialViewActionDto>();
        if (AdministratorMethods.Contains(record.RenewalMethod))
        {
            if (!invalid && !reminderOpen) return [];
            if (role != StudioUserRoles.Owner)
                return [new("requires-administrator", "An administrator renews this credential", false)];
            if (runbookHref is not null) actions.Add(new("runbook", "Open rotation runbook", invalid, Href: runbookHref));
            return actions;
        }
        if (StudioRenewalLabels.TryGetValue(record.RenewalMethod, out var label) && (invalid || reminderOpen))
            actions.Add(new("renew", label, invalid, record.RenewalMethod));
        if (runbookHref is not null && (invalid || reminderOpen || status == CredentialViewProtocol.Statuses.DiagnosisPending))
            actions.Add(new("runbook", status == CredentialViewProtocol.Statuses.DiagnosisPending
                ? "Open diagnosis runbook" : "Open renewal runbook", false, Href: runbookHref));
        return actions;
    }

    /// <summary>Only repository documentation paths become links; anything else stays text.</summary>
    public static string? SafeRunbookHref(string? runbookId)
        => runbookId is not null && !runbookId.Contains("..", StringComparison.Ordinal) && SafeDocPath().IsMatch(runbookId)
            ? runbookId : null;

    [GeneratedRegex(@"^docs/[A-Za-z0-9/_.-]+\.(?:md|html)(?:#[A-Za-z0-9_-]+)?$")]
    private static partial Regex SafeDocPath();
}
