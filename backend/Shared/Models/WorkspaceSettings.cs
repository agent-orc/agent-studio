namespace AgentStudio.Shared;

/// <summary>
/// Per-workspace default settings that sit beside a <see cref="WorkspaceRecord"/>
/// (keyed by <see cref="WorkspaceRecord.Id"/>), parallel to how
/// <see cref="ProjectSettings"/> sits beside <see cref="ProjectRecord"/>.
///
/// <para>This is the F45-era "workspace default" tier the orchestrator settings
/// migration introduces (AGT-1812): every field is a default that applies to
/// each project in the workspace unless that project sets its own override in
/// <see cref="ProjectSettings"/>. Resolution order is therefore
/// <c>project override → workspace default → platform constant default</c>,
/// implemented once in <see cref="AgentStudio.Registry.OrchestratorSettingsResolver"/>.</para>
///
/// <para>Scope note: only genuinely workspace-shaped knobs live here: the model
/// and thinking level the orchestrator decides with, the ADR-0026 autonomy
/// level, and the temporary local CLI execution-engine rollout. Process-wide
/// supervisor lifecycle flags stay a single platform-global value edited
/// through <see cref="AgentStudio.Configuration.OrchestratorConfigService"/>;
/// they gate whole hosted loops before any project or workspace scope exists.
/// See the AGT-1812 task notes for the original split.</para>
///
/// <para>Persisted as a single JSON map keyed by workspace id in
/// <c>&lt;TaskRepository&gt;/.metadata/workspace-settings.json</c> by
/// <see cref="AgentStudio.Registry.WorkspaceSettingsService"/>.</para>
/// </summary>
public record WorkspaceSettings
{
    /// <summary>
    /// Workspace-default orchestrator model. Null means "no workspace default";
    /// a project without its own <see cref="ProjectSettings.OrchestratorModel"/>
    /// then falls through to the platform default (Opus/Haiku per
    /// <c>OrchestratorRunner.DefaultModel</c>).
    /// </summary>
    public string? OrchestratorModel { get; init; }

    /// <summary>
    /// Workspace-default thinking / reasoning level for the orchestrator model.
    /// Null means "no workspace default"; falls through to the resolved model's
    /// own default capability level.
    /// </summary>
    public string? OrchestratorThinkingLevel { get; init; }

    /// <summary>
    /// Workspace-default local CLI execution engine. Null means projects
    /// without an override fall through to <see cref="CliExecutionEngines.Default"/>.
    /// The process-wide rollback selector takes precedence when present.
    /// </summary>
    public string? CliExecutionEngine { get; init; }

    /// <summary>
    /// Workspace-default ADR-0026 orchestrator-prep autonomy level (<c>0..4</c>).
    /// Null means "no workspace default"; a project without its own
    /// <see cref="ProjectSettings.AutonomyLevel"/> then falls through to the
    /// platform default (balanced, level 2).
    /// </summary>
    public int? AutonomyLevel { get; init; }

    /// <summary>
    /// Whether admission may apply Token Economy migrations marked
    /// <c>safeAuto</c> to non-explicit task models. Null keeps the platform
    /// default enabled. Explicit task, project, and configuration pins are
    /// always proposal-only regardless of this setting.
    /// </summary>
    public bool? AutoApplySafeModelMigrations { get; init; }
}

/// <summary>
/// On-disk shape of <c>&lt;TaskRepository&gt;/.metadata/workspace-settings.json</c>:
/// a version stamp plus a map of workspace id → <see cref="WorkspaceSettings"/>.
/// Stored as a wrapper (rather than a bare map) so a future schema bump has a
/// place to hang a version without a data migration.
/// </summary>
public record WorkspaceSettingsFile
{
    public int Version { get; init; } = 1;
    public Dictionary<string, WorkspaceSettings> Workspaces { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
