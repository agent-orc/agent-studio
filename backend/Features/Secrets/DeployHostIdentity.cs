using AgentStudio.Security;

namespace AgentStudio.Secrets;

/// <summary>
/// Whether a runner credential belongs to a deploy job's host (F6). The host is
/// the deploy target of project.yml, which also stays the SSH destination. It
/// names the enrolled runner by its id or by the owner-chosen enrollment name;
/// <c>hostRunnerId</c> is the runner that name resolves to, when unique.
/// </summary>
public static class DeployHostIdentity
{
    public const string DeniedCode = "workload-identity-denied";

    public static bool Belongs(RunnerPrincipal runner, string hostId, string? hostRunnerId)
        => string.Equals(runner.RunnerId, hostId, StringComparison.Ordinal)
           || hostRunnerId is not null && string.Equals(runner.RunnerId, hostRunnerId, StringComparison.Ordinal);

    /// <summary>Names the job's host and the credential's host, and what to put in project.yml.</summary>
    public static string Refusal(string hostId, RunnerPrincipal runner)
    {
        var name = runner.RunnerName?.Trim();
        var credential = string.IsNullOrEmpty(name) || name == runner.RunnerId
            ? $"'{runner.RunnerId}'"
            : $"'{runner.RunnerId}' (enrolled as '{name}')";
        return $"The deploy job belongs to host '{hostId}', but the credential belongs to host {credential}. "
               + "Set the deploy target in project.yml to the runner id or its unique enrollment name.";
    }
}
