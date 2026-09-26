namespace AgentStudio.Registry;

/// <summary>
/// AGT-1812 — the single place that resolves an orchestrator setting through the
/// settings hierarchy introduced by the workspace-defaults migration:
/// <c>process rollback → project override → workspace default → platform
/// constant default</c>.
///
/// <para>Mirrors the shape of <see cref="AgentStudio.Pipeline.PipelineStepConfigResolver"/>
/// and returns a resolution record carrying the winning value plus its source
/// and every candidate tier. The three-argument execution-engine overload is
/// the pure precedence function; its runtime overload captures the process
/// environment first.</para>
///
/// <para>Only genuinely workspace-shaped knobs resolve here: the orchestrator
/// model, its thinking level, the ADR-0026 autonomy level, and the temporary
/// local CLI execution-engine rollout. Process-wide supervisor lifecycle flags
/// are deliberately out of scope (see <see cref="WorkspaceSettings"/>).</para>
/// </summary>
public static class OrchestratorSettingsResolver
{
    /// <summary>Project visibility override, then workspace, then on by default.</summary>
    public static bool ResolveChatMetadata(ProjectSettings? project, WorkspaceSettings? workspace)
        => project?.ChatMetadataEnabled ?? workspace?.ChatMetadataEnabled ?? true;
    public const string SourceEnvironment = "environment";
    public const string SourceProject = "project";
    public const string SourceWorkspace = "workspace";
    public const string SourceDefault = "default";

    public sealed record ModelResolution(
        string Model,
        string Source,
        string? ProjectOverride,
        string? WorkspaceDefault,
        string PlatformDefault);

    public sealed record AutonomyResolution(
        int Level,
        string Source,
        int? ProjectOverride,
        int? WorkspaceDefault,
        int PlatformDefault);

    public sealed record CliExecutionEngineResolution(
        string ExecutionEngine,
        string Source,
        string? EnvironmentOverride,
        string? ProjectOverride,
        string? WorkspaceDefault,
        string PlatformDefault);

    /// <summary>
    /// Resolves the effective orchestrator model. First non-blank of
    /// project override, then workspace default, then the caller-supplied
    /// platform default (e.g. <c>OrchestratorRunner.DefaultModel</c>).
    /// </summary>
    public static ModelResolution ResolveModel(
        ProjectSettings? project,
        WorkspaceSettings? workspace,
        string platformDefault)
    {
        var p = Trim(project?.OrchestratorModel);
        var w = Trim(workspace?.OrchestratorModel);
        var platform = string.IsNullOrWhiteSpace(platformDefault) ? "" : platformDefault.Trim();

        if (p is not null) return new(p, SourceProject, p, w, platform);
        if (w is not null) return new(w, SourceWorkspace, null, w, platform);
        return new(platform, SourceDefault, null, null, platform);
    }

    /// <summary>
    /// Resolves the orchestrator model override only: the project override, else
    /// the workspace default, else <c>null</c> (meaning "no override; the caller
    /// keeps its own fallback chain such as a booted session model then the
    /// platform default"). Use this at call sites that already layer another
    /// fallback below the override.
    /// </summary>
    public static string? ResolveModelOverride(ProjectSettings? project, WorkspaceSettings? workspace)
        => Trim(project?.OrchestratorModel) ?? Trim(workspace?.OrchestratorModel);

    /// <summary>
    /// Resolves the orchestrator thinking-level override: project override, else
    /// workspace default, else <c>null</c> (fall through to the resolved model's
    /// own default level).
    /// </summary>
    public static string? ResolveThinkingLevelOverride(ProjectSettings? project, WorkspaceSettings? workspace)
        => Trim(project?.OrchestratorThinkingLevel) ?? Trim(workspace?.OrchestratorThinkingLevel);

    /// <summary>
    /// Resolves the local CLI execution-engine rollout. The process-wide
    /// rollback selector wins over the persisted project -> workspace ->
    /// platform-default chain so an operator can revert every local launch
    /// without first rewriting stored settings. Invalid values fail loud rather
    /// than silently selecting a different process path.
    /// </summary>
    public static CliExecutionEngineResolution ResolveCliExecutionEngine(
        ProjectSettings? project,
        WorkspaceSettings? workspace)
        => ResolveCliExecutionEngine(
            project,
            workspace,
            CliExecutionEngines.ReadEnvironmentOverride());

    /// <summary>
    /// Pure overload used by precedence tests and callers that already captured
    /// the process environment at their configuration boundary.
    /// </summary>
    public static CliExecutionEngineResolution ResolveCliExecutionEngine(
        ProjectSettings? project,
        WorkspaceSettings? workspace,
        string? environmentOverride)
    {
        var environment = CliExecutionEngines.NormalizeOverride(environmentOverride);
        var p = CliExecutionEngines.NormalizeOverride(project?.CliExecutionEngine);
        var w = CliExecutionEngines.NormalizeOverride(workspace?.CliExecutionEngine);

        if (environment is not null)
            return new(environment, SourceEnvironment, environment, p, w, CliExecutionEngines.Default);
        if (p is not null)
            return new(p, SourceProject, null, p, w, CliExecutionEngines.Default);
        if (w is not null)
            return new(w, SourceWorkspace, null, null, w, CliExecutionEngines.Default);
        return new(
            CliExecutionEngines.Default,
            SourceDefault,
            null,
            null,
            null,
            CliExecutionEngines.Default);
    }

    /// <summary>
    /// Resolves the effective autonomy level. First non-null of project override,
    /// then workspace default, then the platform default (balanced / 2).
    /// </summary>
    public static AutonomyResolution ResolveAutonomy(
        ProjectSettings? project,
        WorkspaceSettings? workspace,
        int platformDefault = 2)
    {
        var p = project?.AutonomyLevel;
        var w = workspace?.AutonomyLevel;

        if (p is not null) return new(p.Value, SourceProject, p, w, platformDefault);
        if (w is not null) return new(w.Value, SourceWorkspace, null, w, platformDefault);
        return new(platformDefault, SourceDefault, null, null, platformDefault);
    }

    private static string? Trim(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// AGT-1812 — injectable convenience over <see cref="OrchestratorSettingsResolver"/>
/// for runtime consumers that only have a project name in hand (hosted services,
/// the per-project runner). Resolves the project's owning workspace through the
/// registry, loads both settings tiers, and applies the pure resolver.
///
/// <para>The workspace lookup is deliberately tolerant: a project whose name does
/// not map to a registry record (or a record whose workspace has no settings)
/// resolves with a null workspace tier, so the result is byte-for-byte identical
/// to the pre-migration "project override → platform default" behaviour. This is
/// what keeps the change behaviour-preserving until an operator actually sets a
/// workspace default.</para>
/// </summary>
public sealed class OrchestratorDefaultsProvider
{
    private readonly AgentStudio.Projects.ProjectSettingsService _projectSettings;
    private readonly WorkspaceSettingsService _workspaceSettings;
    private readonly ProjectRegistry _projects;

    public OrchestratorDefaultsProvider(
        AgentStudio.Projects.ProjectSettingsService projectSettings,
        WorkspaceSettingsService workspaceSettings,
        ProjectRegistry projects)
    {
        _projectSettings = projectSettings;
        _workspaceSettings = workspaceSettings;
        _projects = projects;
    }

    /// <summary>The workspace defaults for the project's owning workspace, or an empty record.</summary>
    public WorkspaceSettings WorkspaceForProject(string projectName)
    {
        var workspaceId = _projects.FindByIdOrDisplayName(projectName)?.WorkspaceId;
        return _workspaceSettings.Get(workspaceId);
    }

    /// <summary>
    /// Effective orchestrator model override for a project (project → workspace),
    /// or null when neither tier sets one so the caller keeps its own fallback.
    /// </summary>
    public string? ResolveModelOverride(string projectName)
        => OrchestratorSettingsResolver.ResolveModelOverride(
            _projectSettings.Get(projectName), WorkspaceForProject(projectName));

    /// <summary>Effective thinking-level override for a project (project → workspace), or null.</summary>
    public string? ResolveThinkingLevelOverride(string projectName)
        => OrchestratorSettingsResolver.ResolveThinkingLevelOverride(
            _projectSettings.Get(projectName), WorkspaceForProject(projectName));

    /// <summary>
    /// Effective local CLI execution engine for a project, including the
    /// process-wide rollback selector and winning source for observability.
    /// </summary>
    public OrchestratorSettingsResolver.CliExecutionEngineResolution ResolveCliExecutionEngine(string projectName)
        => OrchestratorSettingsResolver.ResolveCliExecutionEngine(
            _projectSettings.Get(projectName), WorkspaceForProject(projectName));

    /// <summary>Effective autonomy level for a project (project → workspace → platform default).</summary>
    public int ResolveAutonomyLevel(string projectName, int platformDefault = 2)
        => OrchestratorSettingsResolver.ResolveAutonomy(
            _projectSettings.Get(projectName), WorkspaceForProject(projectName), platformDefault).Level;
}
