using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

public static class HostQuotaCapabilityPolicy
{
    public static bool Holds(RunnerCapabilitySnapshotDto host, string provider)
        => host.Capabilities.Any(capability => capability.Key == "provider-auth:" + provider);

    public static bool Healthy(RunnerCapabilitySnapshotDto host, string provider, DateTime nowUtc)
        => host.Status == "active" && host.HostAdmission.AdmissionState == "open"
           && host.LastSeenAt > nowUtc.AddMinutes(-3)
           && host.Capabilities.Any(capability => capability.Key == "provider-auth:" + provider
               && capability.IsFresh && capability.AdvertisedStatus == "ready"
               && capability.HealthState == CapabilityHealthStates.Healthy
               && capability.HealthOutcome is not ("credential_invalid" or "provider_incident" or "indeterminate"));
}
