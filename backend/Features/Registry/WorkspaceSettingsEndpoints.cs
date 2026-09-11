namespace AgentStudio.Registry;

/// <summary>
/// AGT-1812 — read/write surface for per-workspace default orchestrator settings
/// (the workspace tier of the two-tier orchestrator config). The workspace
/// analog of the per-project routes in
/// <c>AgentStudio.Projects.ProjectSettingsEndpoints</c>: each mutation validates
/// the workspace id against <see cref="WorkspaceRegistry"/> and 404s on an
/// unknown id so a typo fails loud instead of writing orphan settings.
/// </summary>
public static class WorkspaceSettingsEndpoints
{
    public static void MapWorkspaceSettingsEndpoints(this WebApplication app)
    {
        // Current workspace defaults + the platform fallbacks the UI renders as
        // the "inherited" state. Null model / thinkingLevel / autonomyLevel mean
        // "no workspace default set"; the UI shows the platform default greyed.
        app.MapGet("/api/workspaces/{id}/settings", (
            string id, WorkspaceRegistry workspaces, WorkspaceSettingsService settings) =>
        {
            if (workspaces.Find(id) is null)
                return Results.NotFound(new { error = $"Unknown workspaceId '{id}'" });

            var s = settings.Get(id);
            return Results.Ok(new
            {
                orchestratorModel = s.OrchestratorModel,
                orchestratorThinkingLevel = s.OrchestratorThinkingLevel,
                cliExecutionEngine = s.CliExecutionEngine,
                effectiveCliExecutionEngine = OrchestratorSettingsResolver
                    .ResolveCliExecutionEngine(null, s).ExecutionEngine,
                cliExecutionEngineSource = OrchestratorSettingsResolver
                    .ResolveCliExecutionEngine(null, s).Source,
                autonomyLevel = s.AutonomyLevel,
                autoApplyModelMigrations = s.AutoApplyModelMigrations ?? true,
                // Platform fallbacks so the UI can render the effective "inherited"
                // value without hardcoding it or a second round-trip.
                defaultOrchestratorModel = OrchestratorRunner.DefaultModel,
                defaultAutonomyLevel = 2,
                defaultCliExecutionEngine = CliExecutionEngines.Default,
            });
        });

        // AGT-2716: workspace-wide switch for automatic model-migration
        // application at run admission. Null request body value restores the
        // platform default (on).
        app.MapPut("/api/workspaces/{id}/auto-apply-model-migrations", (
            string id, SetWorkspaceAutoApplyModelMigrationsRequest req,
            WorkspaceRegistry workspaces, WorkspaceSettingsService settings) =>
        {
            if (workspaces.Find(id) is null)
                return Results.NotFound(new { error = $"Unknown workspaceId '{id}'" });

            settings.SetAutoApplyModelMigrations(id, req.Enabled);
            return Results.Ok(new { autoApplyModelMigrations = settings.Get(id).AutoApplyModelMigrations ?? true });
        });

        // Workspace-default orchestrator model (+ optional thinking level). A
        // blank model clears the default so projects fall through to the platform
        // default. Takes effect on the next orchestrator decision without a
        // backend restart (resolved live at the read site).
        app.MapPut("/api/workspaces/{id}/orchestrator-model", (
            string id, SetWorkspaceOrchestratorModelRequest req,
            WorkspaceRegistry workspaces, WorkspaceSettingsService settings) =>
        {
            if (workspaces.Find(id) is null)
                return Results.NotFound(new { error = $"Unknown workspaceId '{id}'" });

            settings.SetOrchestratorModel(id, req.Model, req.ThinkingLevel);
            var s = settings.Get(id);
            return Results.Ok(new
            {
                orchestratorModel = s.OrchestratorModel,
                orchestratorThinkingLevel = s.OrchestratorThinkingLevel,
            });
        });

        app.MapGet("/api/workspaces/{id}/cli-execution-engine", (
            string id,
            WorkspaceRegistry workspaces,
            WorkspaceSettingsService settings) =>
        {
            if (workspaces.Find(id) is null)
                return Results.NotFound(new { error = $"Unknown workspaceId '{id}'" });

            var r = OrchestratorSettingsResolver.ResolveCliExecutionEngine(null, settings.Get(id));
            return Results.Ok(new
            {
                executionEngine = r.ExecutionEngine,
                source = r.Source,
                workspaceDefault = r.WorkspaceDefault,
                platformDefault = r.PlatformDefault,
                available = CliExecutionEngines.All,
            });
        });

        app.MapPut("/api/workspaces/{id}/cli-execution-engine", (
            string id,
            SetCliExecutionEngineRequest req,
            WorkspaceRegistry workspaces,
            WorkspaceSettingsService settings) =>
        {
            if (workspaces.Find(id) is null)
                return Results.NotFound(new { error = $"Unknown workspaceId '{id}'" });
            if (!string.IsNullOrWhiteSpace(req.ExecutionEngine)
                && !CliExecutionEngines.IsValid(req.ExecutionEngine))
            {
                return Results.BadRequest(new
                {
                    error = $"Unsupported CLI execution engine '{req.ExecutionEngine}'",
                });
            }

            settings.SetCliExecutionEngine(id, req.ExecutionEngine);
            var r = OrchestratorSettingsResolver.ResolveCliExecutionEngine(null, settings.Get(id));
            return Results.Ok(new
            {
                executionEngine = r.ExecutionEngine,
                source = r.Source,
                workspaceDefault = r.WorkspaceDefault,
                platformDefault = r.PlatformDefault,
                available = CliExecutionEngines.All,
            });
        });

        // Workspace-default autonomy level (0..4; clamped server-side). Null
        // clears the default so projects fall through to balanced/2.
        app.MapPut("/api/workspaces/{id}/autonomy", (
            string id, SetWorkspaceAutonomyLevelRequest req,
            WorkspaceRegistry workspaces, WorkspaceSettingsService settings) =>
        {
            if (workspaces.Find(id) is null)
                return Results.NotFound(new { error = $"Unknown workspaceId '{id}'" });

            settings.SetAutonomyLevel(id, req.Level);
            return Results.Ok(new { autonomyLevel = settings.Get(id).AutonomyLevel });
        });
    }
}

/// <summary>Body for <c>PUT /api/workspaces/{id}/orchestrator-model</c>.</summary>
public sealed record SetWorkspaceOrchestratorModelRequest
{
    /// <summary>Model id; blank clears the workspace default.</summary>
    public string? Model { get; init; }
    /// <summary>Optional thinking level; null leaves it unchanged, blank clears it.</summary>
    public string? ThinkingLevel { get; init; }
}

/// <summary>Body for <c>PUT /api/workspaces/{id}/autonomy</c>.</summary>
public sealed record SetWorkspaceAutonomyLevelRequest
{
    /// <summary>Autonomy level 0..4; null clears the workspace default.</summary>
    public int? Level { get; init; }
}

/// <summary>Body for <c>PUT /api/workspaces/{id}/auto-apply-model-migrations</c>.</summary>
public sealed record SetWorkspaceAutoApplyModelMigrationsRequest
{
    /// <summary>Null restores the platform default (on).</summary>
    public bool? Enabled { get; init; }
}
