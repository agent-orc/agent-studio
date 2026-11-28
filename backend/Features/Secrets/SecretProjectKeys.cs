using AgentStudio.Diagnostics;
using AgentStudio.Registry;

namespace AgentStudio.Secrets;

/// <summary>
/// Names a project's secret scopes by its stable registry id (AGT-3987, F20 of
/// AGT-3885). A reference created now carries <c>secret://PROJ-002/stage/NAME</c>,
/// so renaming the project does not break it. References and stored secrets that
/// still carry the display name stay valid: a reference resolves its literal scope
/// first, and an id scope also reads retained display-name scopes.
/// A project the registry does not know keeps its route name as its key.
/// </summary>
public sealed class SecretProjectKeys(ProjectRegistry? registry = null)
{
    /// <summary>Resolves names without a registry: every project key is its route name.</summary>
    public static SecretProjectKeys Identity { get; } = new();

    /// <summary>The key new references and new secrets use: the registry id, else the given name.</summary>
    public string Canonical(string project)
        => Find(project)?.Id ?? project;

    /// <summary>The project's current display name, the name deploy routes and access checks use.</summary>
    public string DisplayName(string project)
        => Find(project)?.DisplayName ?? project;

    /// <summary>Every key a scope of this project may carry: the id first, then current and prior display names.</summary>
    public IReadOnlyList<string> Keys(string project)
    {
        if (Find(project) is not { } record) return [project];
        var projects = registry!.List();
        return new[] { record.Id, record.DisplayName }.Concat(record.SecretDisplayNames)
            .Where(key => key == record.Id || !projects.Any(other => other.Id != record.Id
                && (string.Equals(other.DisplayName, key, StringComparison.OrdinalIgnoreCase)
                    || other.SecretDisplayNames.Contains(key, StringComparer.OrdinalIgnoreCase))))
            .Distinct(StringComparer.Ordinal).ToArray();
    }

    public IReadOnlyList<AgentStudio.Shared.ProjectRecord> Projects() => registry?.List() ?? [];

    public bool Owns(string project, string? scopeProject)
        => scopeProject is not null && Keys(project).Contains(scopeProject, StringComparer.OrdinalIgnoreCase);

    /// <summary>The scope in canonical form: a display-name project segment becomes the id.</summary>
    public SecretScope Canonical(SecretScope scope) => scope.Kind switch
    {
        SecretScopeKind.Workspace => scope,
        SecretScopeKind.Project => SecretScope.ForProject(Canonical(scope.Project!)),
        _ => SecretScope.ForEnvironment(Canonical(scope.Project!), scope.Environment!)
    };

    /// <summary>The same scope under every key of its project, the literal scope first.</summary>
    public IReadOnlyList<SecretScope> Aliases(SecretScope scope)
    {
        if (scope.Kind == SecretScopeKind.Workspace) return [scope];
        var aliases = new List<SecretScope> { scope };
        foreach (var key in Keys(scope.Project!))
        {
            if (!SecretScope.TryParse(scope.Kind == SecretScopeKind.Project ? key : key + "/" + scope.Environment, out var alias))
                continue;
            if (!aliases.Contains(alias)) aliases.Add(alias);
        }
        return aliases;
    }

    /// <summary>
    /// Lookup order of a reference: each level of <see cref="SecretScope.InheritanceChain"/>
    /// under every key of the project, so an id reference also finds a secret
    /// that was stored under the display name before AGT-3987.
    /// </summary>
    public IReadOnlyList<SecretScope> LookupChain(SecretScope scope)
        => scope.InheritanceChain().SelectMany(Aliases).Distinct().ToArray();

    private AgentStudio.Shared.ProjectRecord? Find(string project)
    {
        if (registry is null || string.IsNullOrWhiteSpace(project)) return null;
        try
        {
            return registry.FindByIdOrDisplayName(project)
                ?? registry.List().SingleOrDefault(record => record.SecretDisplayNames.Contains(project, StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SilentCatch.Note(exception, "SecretProjectKeys: project registry unreadable");
            return null;
        }
    }
}
