namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Runner-host enrolment (Dossier AGT-W63 I03, decision D3 option A): one host
/// identity in one runner-host family, with separately credentialed role
/// services and one bounded coding/review resource envelope. The Task Server
/// owns the enrolment; drain stays on the existing host-admission authority.
/// </summary>
public static class HostRoles
{
    public const string Coding = "coding";
    public const string Review = "review";

    public static readonly IReadOnlyList<string> All = [Coding, Review];
}

public static class HostEnrolmentStatuses
{
    public const string Enrolled = "enrolled";
    public const string Removed = "removed";
}

/// <summary>Typed reasons a claim is refused before any lease or attempt is minted.</summary>
public static class HostAdmissionReasons
{
    public const string CapabilityMissing = "capability-missing";
    public const string CapabilityStale = "capability-stale";
    public const string ProviderLoginMissing = "provider-login-missing";
    public const string RepositoryProofFailed = "repository-proof-failed";
    public const string CapabilityUnavailable = "capability-unavailable";
    public const string CapabilityDraining = "capability-draining";
    public const string HostDraining = "host-draining";
    public const string HostRemoved = "host-removed";
    public const string PrincipalNotEnrolled = "principal-not-enrolled";
    public const string RoleNotEnrolled = "role-not-enrolled";
    public const string SlotBudgetFull = "slot-budget-full";
    public const string RoleSlotBudgetFull = "role-slot-budget-full";

    /// <summary>Classifies a capability refusal by the capability family it concerns.</summary>
    public static string ForCapability(string capabilityKey, string failure)
    {
        if (failure is CapabilityStale or CapabilityDraining) return failure;
        if (capabilityKey.StartsWith("provider-auth:", StringComparison.Ordinal)) return ProviderLoginMissing;
        if (capabilityKey is CapabilityProtocol.RepositoryAccess
            or CapabilityProtocol.GitFetch
            or CapabilityProtocol.GitPush
            or CapabilityProtocol.GitWorkflowPush
            or CapabilityProtocol.RepositoryFileSystem)
            return RepositoryProofFailed;
        return failure;
    }
}

/// <summary>
/// Shared resource envelope. <see cref="TotalSlots"/> is conserved across both
/// roles; each role cap is a sub-limit. It never changes project parallelism.
/// </summary>
public sealed record HostEnvelopeDto(int TotalSlots, int CodingSlots, int ReviewSlots)
{
    public int RoleSlots(string role)
        => role == HostRoles.Coding ? CodingSlots : role == HostRoles.Review ? ReviewSlots : 0;
}

public sealed record HostRolePrincipalDto(string Role, string PrincipalId);

public sealed record EnrolHostRequest(
    string HostClass,
    IReadOnlyList<HostRolePrincipalDto> Roles,
    HostEnvelopeDto Envelope,
    long ExpectedGeneration,
    string? Reason = null);

public sealed record RemoveHostRequest(long ExpectedGeneration, string Reason);

public sealed record HostEnrolmentDto(
    string HostId,
    string HostClass,
    string Status,
    long Generation,
    IReadOnlyList<HostRolePrincipalDto> Roles,
    HostEnvelopeDto Envelope,
    DateTime EnrolledAt,
    DateTime UpdatedAt,
    DateTime? RemovedAt);

public sealed record HostEnvelopeAdmission(bool Admitted, string? Reason, string? Message)
{
    public static readonly HostEnvelopeAdmission Open = new(true, null, null);

    public static HostEnvelopeAdmission Refused(string reason, string message) => new(false, reason, message);
}

public static class HostEnrolmentPolicy
{
    public const int MaxTotalSlots = 64;

    /// <summary>Returns a validation error, or null when the desired enrolment is coherent.</summary>
    public static string? Validate(string hostId, EnrolHostRequest request)
    {
        if (!ValidIdentifier(hostId)) return "Host id must be 1-64 characters of [A-Za-z0-9._-].";
        if (!ValidIdentifier(request.HostClass?.Trim().ToLowerInvariant()))
            return "Host class must be 1-64 characters of [A-Za-z0-9._-].";
        if (request.ExpectedGeneration < 0) return "Expected generation cannot be negative.";
        var roles = request.Roles ?? [];
        if (roles.Count == 0) return "At least one role service is required.";
        if (roles.Any(role => !HostRoles.All.Contains(role.Role, StringComparer.Ordinal)))
            return "Role must be coding or review.";
        if (roles.Select(role => role.Role).Distinct(StringComparer.Ordinal).Count() != roles.Count)
            return "Each role may be enrolled once per host.";
        if (roles.Any(role => string.IsNullOrWhiteSpace(role.PrincipalId)))
            return "Each role needs its own service principal.";
        if (roles.Select(role => role.PrincipalId.Trim()).Distinct(StringComparer.Ordinal).Count() != roles.Count)
            return "Role services must use distinct principals; a shared credential would merge their scopes.";
        var envelope = request.Envelope;
        if (envelope is null) return "A resource envelope is required.";
        if (envelope.TotalSlots is < 1 or > MaxTotalSlots)
            return $"Envelope totalSlots must be between 1 and {MaxTotalSlots}.";
        if (envelope.CodingSlots < 0 || envelope.ReviewSlots < 0)
            return "Role slot caps cannot be negative.";
        if (envelope.CodingSlots > envelope.TotalSlots || envelope.ReviewSlots > envelope.TotalSlots)
            return "A role slot cap cannot exceed the shared envelope.";
        foreach (var role in HostRoles.All)
        {
            var enrolled = roles.Any(item => item.Role == role);
            var slots = envelope.RoleSlots(role);
            if (enrolled && slots == 0) return $"Enrolled role '{role}' needs at least one slot.";
            if (!enrolled && slots > 0) return $"Role '{role}' has slots but no enrolled principal.";
        }
        return null;
    }

    /// <summary>
    /// Pure claim-time admission for an enrolled host. Occupancy counts are the
    /// live coding leases and review attempts held on this host.
    /// </summary>
    public static HostEnvelopeAdmission Decide(
        HostEnrolmentDto? enrolment,
        string role,
        string principalId,
        int occupiedCoding,
        int occupiedReview)
    {
        if (enrolment is null) return HostEnvelopeAdmission.Open;
        if (enrolment.Status == HostEnrolmentStatuses.Removed)
            return HostEnvelopeAdmission.Refused(
                HostAdmissionReasons.HostRemoved,
                $"Host '{enrolment.HostId}' was removed at generation {enrolment.Generation}.");
        var binding = enrolment.Roles.FirstOrDefault(item => item.Role == role);
        if (binding is null)
            return HostEnvelopeAdmission.Refused(
                HostAdmissionReasons.RoleNotEnrolled,
                $"Host '{enrolment.HostId}' has no enrolled {role} service.");
        if (!string.Equals(binding.PrincipalId, principalId, StringComparison.Ordinal))
            return HostEnvelopeAdmission.Refused(
                HostAdmissionReasons.PrincipalNotEnrolled,
                $"Principal '{principalId}' is not the enrolled {role} service of host '{enrolment.HostId}'.");
        var occupied = occupiedCoding + occupiedReview;
        if (occupied >= enrolment.Envelope.TotalSlots)
            return HostEnvelopeAdmission.Refused(
                HostAdmissionReasons.SlotBudgetFull,
                $"Host '{enrolment.HostId}' shared slot budget is full ({occupied}/{enrolment.Envelope.TotalSlots}).");
        var roleOccupied = role == HostRoles.Coding ? occupiedCoding : occupiedReview;
        var roleCap = enrolment.Envelope.RoleSlots(role);
        if (roleOccupied >= roleCap)
            return HostEnvelopeAdmission.Refused(
                HostAdmissionReasons.RoleSlotBudgetFull,
                $"Host '{enrolment.HostId}' {role} slot budget is full ({roleOccupied}/{roleCap}).");
        return HostEnvelopeAdmission.Open;
    }

    private static bool ValidIdentifier(string? value)
        => !string.IsNullOrEmpty(value)
           && value.Length <= 64
           && value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-');
}
