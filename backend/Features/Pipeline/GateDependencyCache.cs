using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Pipeline;

/// <summary>
/// Pipeline adapter over the dependency-cache protocol shared with Remote
/// Review. Planning remains gate-owned; lock hashing and cache transfer do not.
/// </summary>
internal sealed class GateDependencyCacheSession
{
    private readonly DependencyCacheSession _session;

    private GateDependencyCacheSession(DependencyCacheSession session)
    {
        _session = session;
    }

    public static GateDependencyCacheSession Create(
        string reviewWorkspaceRoot,
        string repositoryPath,
        string workspace,
        IReadOnlyList<GatePreparationCommand> preparation,
        IReadOnlyList<VerifyCommand> commands,
        ILogger logger)
    {
        var scopes = preparation
            .SelectMany(command => command.DependencyScopes)
            .Select(scope => new ReviewDependencyScopeDto(
                scope.WorkingSubdir,
                scope.Lockfiles))
            .Concat(commands
                .Where(command => command.Ecosystem == VerifyEcosystem.Node)
                .Select(command => new ReviewDependencyScopeDto(command.WorkingSubdir, [])))
            .GroupBy(scope => scope.WorkingSubdir, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ReviewDependencyScopeDto(
                group.Key,
                group.SelectMany(scope => scope.Lockfiles)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()))
            .ToArray();
        var preserveGlobs = preparation
            .SelectMany(command => command.PreserveGlobs ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var cacheParent = Path.Combine(
            reviewWorkspaceRoot,
            BuildTestGateRunner.DependencyCacheDirectoryName);
        return new GateDependencyCacheSession(DependencyCacheSession.Create(
            cacheParent,
            repositoryPath,
            workspace,
            scopes,
            preserveGlobs,
            log: message => logger.LogWarning("{DependencyCacheMessage}", message)));
    }

    public IReadOnlyList<string> Restore() => _session.RestoreVerified();

    public IReadOnlyList<string> Save() => _session.SaveVerified();

    public IReadOnlyList<string> Evict(string reason) => _session.DiscardIncludingWorkspace(reason);

    public bool Restored => _session.Restored;

    public BuildTestGateDependencyCacheDecision Decision(bool reranFromScratch)
        => new(
            _session.CacheKey,
            _session.Restored,
            _session.RestoredAge is null
                ? null
                : Math.Max(0, (long)_session.RestoredAge.Value.TotalSeconds),
            _session.RestoredSizeBytes,
            _session.Evicted,
            _session.EvictionReason,
            reranFromScratch,
            _session.SavedVerified);

    internal static string CachePath(string reviewWorkspaceRoot, string repositoryPath)
        => DependencyCacheSession.CachePath(
            Path.Combine(reviewWorkspaceRoot, BuildTestGateRunner.DependencyCacheDirectoryName),
            repositoryPath);
}
