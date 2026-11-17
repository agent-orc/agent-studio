using AgentStudio.Clients;
using AgentStudio.Projects;
using AgentStudio.Security;

namespace AgentStudio.Deploy;

/// <summary>
/// Reachability of a run host: <see cref="Reachable"/> within
/// <see cref="RunHostDirectory.ReachableWithin"/> of its last contact,
/// <see cref="Unreachable"/> after it, <see cref="NoContact"/> before its first
/// one, and <see cref="NotEnrolled"/> for a declared host id without a run host
/// identity.
/// </summary>
public static class RunHostStates
{
    public const string Reachable = "reachable";
    public const string Unreachable = "unreachable";
    public const string NoContact = "no-contact";
    public const string NotEnrolled = "not-enrolled";
}

/// <summary>One run host as the Hosts view and the Deploy area show it.</summary>
public sealed record RunHostStatus(
    string Id,
    string Name,
    string State,
    IReadOnlyList<string> Projects,
    DateTime? LastSeenAt,
    DateTime? LastClaimAt,
    DateTime? LastReportAt);

/// <summary>
/// The run hosts of the installation: enrolled identities holding an active
/// runner.deploy credential. A run host sends no claim heartbeat, so its
/// liveness is its last deploy claim, report or run-target poll (F4).
/// </summary>
public sealed class RunHostDirectory(AccessSecurityStore security, ClientIdentityStore clients, TimeProvider clock)
{
    /// <summary>The deploy loop polls every five seconds; two minutes allows for a slow claim and a restart.</summary>
    public static readonly TimeSpan ReachableWithin = TimeSpan.FromMinutes(2);

    public IReadOnlyList<RunHostStatus> List()
    {
        var now = clock.GetUtcNow().UtcDateTime;
        return security.ListRunners().Where(IsRunHost).Select(runner => Status(runner, now)).ToList();
    }

    /// <summary>
    /// The status of each declared host id, in order, including ids no run host holds.
    /// A declared id names the run host by its runner id or its unique enrollment name (F6);
    /// the row keeps the declared id, which is what each environment refers to.
    /// </summary>
    public IReadOnlyList<RunHostStatus> ForHosts(IEnumerable<string> hostIds)
    {
        var hosts = List().ToDictionary(host => host.Id, StringComparer.Ordinal);
        return hostIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal)
            .Select(id => security.DeployHostRunnerId(id) is { } runnerId && hosts.TryGetValue(runnerId, out var host)
                ? host with { Id = id }
                : new RunHostStatus(id, id, RunHostStates.NotEnrolled, [], null, null, null))
            .ToList();
    }

    /// <summary>Names the run host of every environment and whether it is reachable.</summary>
    public ProjectEnvironmentsResponse Attach(ProjectEnvironmentsResponse environments)
        => environments with
        {
            RunHosts = ForHosts(environments.Environments.Select(item => item.Declaration.TargetHostId)),
        };

    /// <summary>Records the contact of an authenticated run host and returns whether it was one.</summary>
    public bool RecordContact(RunnerPrincipal? principal, DeployHostContact contact)
    {
        if (principal is null || !principal.Scopes.Contains(RunnerScopes.Deploy)) return false;
        if (security.RecordDeployHostContact(principal.RunnerId, contact) is null) return false;
        // The client identity enrollment created keeps the Hosts registry row fresh too.
        clients.RecordSeen(principal.RunnerId);
        return true;
    }

    private static bool IsRunHost(RunnerServiceIdentity runner)
        => runner.RevokedAt is null && runner.RetiredAt is null
            && runner.Credentials.Any(credential => credential.RevokedAt is null
                && credential.Scopes.Contains(RunnerScopes.Deploy, StringComparer.Ordinal));

    private static RunHostStatus Status(RunnerServiceIdentity runner, DateTime now)
    {
        var state = runner.LastSeenAt is not { } seen ? RunHostStates.NoContact
            : now - seen <= ReachableWithin ? RunHostStates.Reachable : RunHostStates.Unreachable;
        return new RunHostStatus(runner.Id, runner.Name, state,
            runner.Projects ?? [RunnerProjectBinding.AllProjects],
            runner.LastSeenAt, runner.LastClaimAt, runner.LastReportAt);
    }
}
