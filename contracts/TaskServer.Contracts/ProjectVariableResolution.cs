namespace AgentStudio.TaskServer.Contracts;

/// <summary>Scopes of the same kind are OR; different kinds are AND. More constrained scopes win.</summary>
public static class ProjectVariableResolution
{
    public static ProjectVariableDefinition? Resolve(IEnumerable<ProjectVariableDefinition> variables,
        string name, string environment, ProjectTenantDefinition? tenant)
        => variables.Where(item => item.Name == name && Matches(item, environment, tenant))
            .OrderByDescending(Specificity).FirstOrDefault();

    public static bool Matches(ProjectVariableDefinition variable, string environment, ProjectTenantDefinition? tenant)
        => (variable.Environments.Count == 0 || variable.Environments.Contains(environment, StringComparer.Ordinal))
           && (variable.Tenants.Count == 0 || tenant is not null && variable.Tenants.Contains(tenant.Name, StringComparer.Ordinal))
           && (variable.TenantTags.Count == 0 || tenant is not null
               && variable.TenantTags.Intersect(tenant.Tags, StringComparer.Ordinal).Any());

    // A dimension is more specific than a broader list within that dimension.
    // At equal dimension count, an explicit tenant outranks a tag, then environment.
    public static int Specificity(ProjectVariableDefinition variable)
    {
        var environment = variable.Environments.Count > 0 ? 1 : 0;
        var tags = variable.TenantTags.Count > 0 ? 1 : 0;
        var tenant = variable.Tenants.Count > 0 ? 1 : 0;
        return (environment + tags + tenant) * 1_000_000 + tenant * 100_000 + tags * 10_000
            + environment * 1_000 + (tenant == 0 ? 0 : 99 - Math.Min(99, variable.Tenants.Count)) * 100
            + (tags == 0 ? 0 : 99 - Math.Min(99, variable.TenantTags.Count)) * 10
            + (environment == 0 ? 0 : 9 - Math.Min(9, variable.Environments.Count));
    }

    public static bool Ambiguous(ProjectVariableDefinition left, ProjectVariableDefinition right,
        IReadOnlyList<ProjectTenantDefinition> tenants, IEnumerable<string> environments)
        => Specificity(left) == Specificity(right)
           && environments.Any(environment => Matches(left, environment, null) && Matches(right, environment, null)
               || tenants.Any(tenant => Matches(left, environment, tenant) && Matches(right, environment, tenant)));
}
