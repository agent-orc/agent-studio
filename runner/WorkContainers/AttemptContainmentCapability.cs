using AgentStudio.TaskServer.Contracts;

namespace AgentRunner.WorkContainers;

/// <summary>
/// SM-11: the <c>attempt-containment</c> capability of a daemon. Ready means
/// the host runs the role contained (or still runs the process executor before
/// its canary verdict); unavailable means contained mode is enforced and a
/// prerequisite is missing, which the Task Server's capability admission turns
/// into a refused claim on every claim plane.
/// </summary>
public static class AttemptContainmentCapability
{
    public const string Category = "containment";

    public static AttemptContainmentDecision Decide(
        RunnerOptions options, Func<string, string?>? read = null, TimeProvider? clock = null)
    {
        read ??= Environment.GetEnvironmentVariable;
        WorkContainerSettings settings;
        try
        {
            settings = WorkContainerSettings.FromEnvironment(read, WorkContainerSettings.HostCores(),
                HostMemory.Read().TotalBytes, Path.Combine(options.StateDir, "work-containers"));
        }
        catch (InvalidOperationException ex)
        {
            // Without readable settings there is no canary verdict to enforce.
            return new AttemptContainmentDecision(ContainmentVerdict.Process, false, "settings-invalid: " + ex.Message);
        }
        var role = options.Role is "review" or "gate" ? options.Role : "coding";
        return AttemptContainmentPolicy.Decide(
            AttemptContainmentHost.Gather(settings, role, options.ZonePolicyStatePath, clock ?? TimeProvider.System));
    }

    public static AdvertisedCapabilityDto Describe(AttemptContainmentDecision decision)
        => new(
            Key: CapabilityProtocol.AttemptContainment,
            Category: Category,
            Status: decision.Verdict == ContainmentVerdict.Refuse ? "unavailable" : "ready",
            Identity: decision.Verdict.ToString().ToLowerInvariant(),
            Detail: (decision.Enforced ? "enforced; " : "not enforced; ") + decision.Reason);

    /// <summary>The capability key a claim requires, or none before enforcement.</summary>
    public static IEnumerable<string> Requirement(RunnerOptions options)
        => Decide(options).Enforced ? [CapabilityProtocol.AttemptContainment] : [];
}
