using System.Text.Json;

namespace AgentStudio.Admin;

/// <summary>
/// Surface for the orchestrator + supervisor flag toggles. The local profile
/// uses the attribution registration boundary in
/// <see cref="AgentStudio.Clients.ClientIdentityMiddleware"/>. The networked
/// profile requires an authenticated human session and applies role checks.
/// All flags require a backend restart to take effect; the response
/// surfaces that explicitly so the frontend can render the
/// "saved and active" state.
/// </summary>
public static class AdminConfigEndpoints
{
    public static void MapAdminConfigEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/admin/config");

        group.MapGet("/orchestrator", (OrchestratorConfigService svc) =>
        {
            return Results.Ok(svc.GetSnapshot());
        });

        group.MapPut("/orchestrator", (
            UpdateOrchestratorConfigRequest req,
            OrchestratorConfigService svc) =>
        {
            if (req?.Values == null || req.Values.Count == 0)
            {
                return Results.BadRequest(new { error = "values is required" });
            }
            try
            {
                var snapshot = svc.ApplyOverrides(req.Values);
                return Results.Ok(snapshot);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // --- Application-wide system-prompt templates (view + override) ---
        var prompts = app.MapGroup("/api/admin/prompts");

        prompts.MapGet("", (PromptAdminService svc) => Results.Ok(svc.GetCatalog()));

        // Coverage roll-up: which prompt-source sites are template-backed vs
        // still inline. Static path registered before "/{name}" so it is not
        // shadowed by the detail route.
        prompts.MapGet("/coverage", (PromptAdminService svc) => Results.Ok(svc.GetCoverage()));

        prompts.MapPost("/review-all", (ReviewPromptsRequest? req, PromptAdminService svc) =>
            Results.Ok(svc.ReviewAll(req?.ReviewedBy)));

        prompts.MapPost("/{name}/review", (string name, ReviewPromptsRequest? req, PromptAdminService svc) =>
        {
            var result = svc.Review(name, req?.ReviewedBy);
            return result == null
                ? Results.NotFound(new { error = $"Unknown prompt '{name}'." })
                : Results.Ok(result);
        });

        prompts.MapGet("/{name}", (string name, PromptAdminService svc) =>
        {
            var detail = svc.GetDetail(name);
            return detail == null ? Results.NotFound(new { error = $"Unknown prompt '{name}'." }) : Results.Ok(detail);
        });

        // Non-persisting "Probelauf": renders the effective template (or an
        // explicit draft) against supplied slot values and reports filled vs
        // missing slots.
        prompts.MapPost("/{name}/preview", (string name, PreviewPromptRequest? req, PromptAdminService svc) =>
        {
            var result = svc.Preview(name, req?.Values, req?.Content);
            return result == null ? Results.NotFound(new { error = $"Unknown prompt '{name}'." }) : Results.Ok(result);
        });

        prompts.MapPut("/{name}", (string name, SavePromptOverrideRequest req, PromptAdminService svc) =>
        {
            if (req?.Content == null)
                return Results.BadRequest(new { error = "content is required" });
            var detail = svc.SaveOverride(name, req.Content);
            return detail == null ? Results.NotFound(new { error = $"Unknown prompt '{name}'." }) : Results.Ok(detail);
        });

        prompts.MapDelete("/{name}", (string name, PromptAdminService svc) =>
        {
            var detail = svc.ResetToDefault(name);
            return detail == null ? Results.NotFound(new { error = $"Unknown prompt '{name}'." }) : Results.Ok(detail);
        });

        // "Keep mine" after a default update: re-points the override's recorded
        // base SHA at the current default, clearing the drift banner.
        prompts.MapPost("/{name}/rebaseline", (string name, PromptAdminService svc) =>
        {
            var detail = svc.RebaselineOverride(name);
            return detail == null ? Results.NotFound(new { error = $"Unknown prompt '{name}'." }) : Results.Ok(detail);
        });

        var maintenance = app.MapGroup("/api/maintenance");

        maintenance.MapPost("/backfill-keys", (TaskMutationService mutations) =>
        {
            var count = mutations.BackfillTaskKeys();
            return Results.Ok(new { stamped = count });
        });

        // One-shot sweep for the duplicate display-key root cause: two tasks
        // sharing one key (e.g. ASS-594 minted onto two different tasks after a
        // rewound counter). Keeps the oldest task on the contested key and
        // re-keys the namesakes. Idempotent — also runs at boot. See
        // TaskMutationService.DeduplicateTaskKeys.
        maintenance.MapPost("/dedupe-task-keys", (TaskMutationService mutations) =>
        {
            var count = mutations.DeduplicateTaskKeys();
            return Results.Ok(new { rekeyed = count });
        });

        // One-shot sweep for the duplicate-slug root cause: neutralises stale
        // namesake folders (the shells behind the recurring 409-on-archive) by
        // renaming them with a leading underscore so the scanner ignores them.
        // Idempotent — safe to re-run. See TaskStateMachine.DedupeSlugFolders.
        maintenance.MapPost("/dedupe-slug-folders", (TaskStateMachine states) =>
        {
            var report = states.DedupeSlugFolders();
            return Results.Ok(report);
        });

        // Backfill AGT-2808: cards stamped with a model but no thinking
        // level (pre-policy-fix creations). Report-only by default;
        // apply=true persists the resolved level (thinkingLevelExplicit
        // stays false - policy-derived, not an operator pin).
        maintenance.MapPost("/backfill-thinking-levels", (bool apply, TaskMutationService mutations) =>
        {
            var entries = mutations.BackfillThinkingLevels(apply);
            return Results.Ok(new { apply, count = entries.Count, entries });
        });
    }
}

public sealed class UpdateOrchestratorConfigRequest
{
    public Dictionary<string, JsonElement>? Values { get; set; }
}

public sealed class SavePromptOverrideRequest
{
    public string? Content { get; set; }
}

public sealed class PreviewPromptRequest
{
    /// <summary>Slot values to render with; missing/empty slots are reported back.</summary>
    public Dictionary<string, string?>? Values { get; set; }

    /// <summary>Optional draft template to preview instead of the saved effective content.</summary>
    public string? Content { get; set; }
}

public sealed class ReviewPromptsRequest
{
    public string? ReviewedBy { get; set; }
}
