namespace AgentStudio.TaskServer.Contracts;

public enum ModelAcceptanceAdmission
{
    Accepted,
    Legacy,
    Pending,
    Unavailable,
    Refused,
}

/// <summary>Rolling-upgrade boundary for named-model claims on one runner instance.</summary>
public static class ModelAcceptanceAdmissionPolicy
{
    public static readonly TimeSpan UnknownGrace = TimeSpan.FromMinutes(2);

    public static ModelAcceptanceAdmission Evaluate(
        string? model, bool protocolAdvertised, string? status, DateTime? freshUntil,
        bool rejected, DateTime instanceStartedAt, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(model)) return ModelAcceptanceAdmission.Accepted;
        // A launch refusal is authoritative even if the host is rolled back.
        if (rejected) return ModelAcceptanceAdmission.Unavailable;
        if (!protocolAdvertised) return ModelAcceptanceAdmission.Legacy;
        if (status == "ready" && freshUntil > now) return ModelAcceptanceAdmission.Accepted;
        if (status == "unavailable") return ModelAcceptanceAdmission.Unavailable;
        return now - instanceStartedAt < UnknownGrace
            ? ModelAcceptanceAdmission.Pending : ModelAcceptanceAdmission.Refused;
    }

    public static string Refusal(string host, string capabilityKey, ModelAcceptanceAdmission admission)
        => admission switch
        {
            ModelAcceptanceAdmission.Pending =>
                $"Host {host} is verifying {capabilityKey}; retry after its startup probe.",
            ModelAcceptanceAdmission.Unavailable =>
                $"Host model unavailable on {host}: {capabilityKey}. Repair the host login or model access.",
            _ => $"Host capability missing on {host}: {capabilityKey}. Upgrade or repair the host probe.",
        };
}
