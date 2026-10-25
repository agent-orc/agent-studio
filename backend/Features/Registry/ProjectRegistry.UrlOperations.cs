using AgentStudio.Security;

namespace AgentStudio.Registry;

public sealed partial class ProjectRegistry
{
    // Invalid project IDs must not allocate permanent coordination state.
    private readonly object[] _urlOperationGates =
        Enumerable.Range(0, 256).Select(static _ => new object()).ToArray();

    private object UrlOperationGate(string projectId)
        => _urlOperationGates[(int)((uint)StringComparer.OrdinalIgnoreCase.GetHashCode(projectId)
            % (uint)_urlOperationGates.Length)];

    /// <summary>Authorize removal before stopping a process, then commit only after stop succeeds.</summary>
    public ProjectRecord RemoveUrl(string id, string urlId, StudioUser? actor = null,
        Action? beforeRemove = null)
    {
        EnsureLoaded();
        lock (UrlOperationGate(id))
        {
            lock (_gate)
            {
                var project = _state.Projects.FirstOrDefault(p =>
                    string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
                    ?? throw new KeyNotFoundException($"Unknown projectId: {id}");
                var url = project.Urls[IndexOfUrl(project.Urls, urlId)];
                if (_networked && actor?.Role != StudioRoles.Owner
                    && (url.StartRule is not null || RepositoryCandidates(project).Any(candidate => candidate.Id == urlId)))
                    throw new UnauthorizedAccessException("owner-required");
            }

            beforeRemove?.Invoke();
            return MutateLocked(id, project =>
            {
                var index = IndexOfUrl(project.Urls, urlId);
                return project with { Urls = [.. project.Urls.Where((_, i) => i != index)] };
            }, "url-removed", actor);
        }
    }

    /// <summary>Keep process start ordered with URL changes without holding the registry lock.</summary>
    public T UseUrl<T>(string projectId, string urlId, Func<ProjectRecord, ProjectUrlRecord, T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        EnsureLoaded();
        lock (UrlOperationGate(projectId))
        {
            ProjectRecord project;
            ProjectUrlRecord url;
            lock (_gate)
            {
                project = _state.Projects.FirstOrDefault(p =>
                    string.Equals(p.Id, projectId, StringComparison.OrdinalIgnoreCase))
                    ?? throw new KeyNotFoundException($"Unknown projectId: {projectId}");
                url = project.Urls[IndexOfUrl(project.Urls, urlId)];
            }
            return action(project, url);
        }
    }
}
