using System.Text.RegularExpressions;

namespace AgentStudio.Secrets;

public sealed record SecretAccessDecision(bool Allowed, string Code, string Reason)
{
    public static SecretAccessDecision Allow { get; } = new(true, "allowed", "allowed");
    public static SecretAccessDecision Deny(string code, string reason) => new(false, code, reason);
}

/// <summary>
/// Pure scope check for issuing a value to a workload. A workload reads the
/// workspace scope, its own project scope and, when it runs for an
/// environment, that environment's scope. A deploy job reads only names its
/// environment declaration lists.
/// </summary>
public static partial class SecretAccessPolicy
{
    [GeneratedRegex("^[a-z][a-z0-9:._-]{0,63}$")]
    private static partial Regex PurposePattern();

    public static bool IsValidPurpose(string? purpose) => purpose is not null && PurposePattern().IsMatch(purpose);

    public static SecretAccessDecision ForWorkload(SecretReference reference, SecretWorkload workload, string? purpose)
    {
        if (!IsValidPurpose(purpose))
            return SecretAccessDecision.Deny("invalid-purpose", "A purpose is required: lowercase letters, digits, ':', '.', '_' or '-'.");
        if (string.IsNullOrWhiteSpace(workload.Project) || string.IsNullOrWhiteSpace(workload.Identity))
            return SecretAccessDecision.Deny("workload-unbound", "The workload is not bound to a project.");
        if (workload.DeclaredNames is { } declared && !declared.Contains(reference.Name, StringComparer.Ordinal))
            return SecretAccessDecision.Deny("reference-not-declared", $"The workload does not declare '{reference.Name}'.");

        var scope = reference.Scope;
        if (scope.Kind == SecretScopeKind.Workspace) return SecretAccessDecision.Allow;
        if (!string.Equals(scope.Project, workload.Project, StringComparison.OrdinalIgnoreCase))
            return SecretAccessDecision.Deny("project-scope-denied", "The reference belongs to another project.");
        if (scope.Kind == SecretScopeKind.Project) return SecretAccessDecision.Allow;
        if (workload.Environment is null)
            return SecretAccessDecision.Deny("environment-scope-denied", "The workload does not run for an environment.");
        return string.Equals(scope.Environment, workload.Environment, StringComparison.Ordinal)
            ? SecretAccessDecision.Allow
            : SecretAccessDecision.Deny("environment-scope-denied", "The reference belongs to another environment.");
    }
}

/// <summary>
/// Pure management rights of a human. Owners and operators write; the
/// workspace scope needs access to every project. Viewers and the Product
/// role read metadata only. Nobody reads a value back.
/// </summary>
public static class SecretManagementPolicy
{
    public static SecretAccessDecision CanWrite(string? role, bool unrestrictedProjects, SecretScope scope, bool projectMember)
    {
        if (role is not ("owner" or "operator"))
            return SecretAccessDecision.Deny("role-denied", "Owner or operator role is required to change secrets.");
        if (scope.Kind == SecretScopeKind.Workspace)
            return unrestrictedProjects
                ? SecretAccessDecision.Allow
                : SecretAccessDecision.Deny("project-scope-denied", "Workspace secrets need access to every project.");
        return projectMember
            ? SecretAccessDecision.Allow
            : SecretAccessDecision.Deny("project-scope-denied", "This account is not a member of the project.");
    }

    public static bool CanSeeMetadata(SecretScope scope, bool unrestrictedProjects, Func<string, bool> projectMember)
        => unrestrictedProjects || scope.Kind == SecretScopeKind.Workspace || projectMember(scope.Project!);
}
