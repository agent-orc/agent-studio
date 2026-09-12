namespace AgentStudio.Orchestrator;

/// <summary>
/// Read-only ORCH-1 context surface. GET builds from live cheap sources and
/// cached quota. POST /refresh expresses explicit operator intent and waits
/// for the existing quota probes before rebuilding the same response shape.
/// </summary>
public static class OrchestratorContextEndpoints
{
    public static void MapOrchestratorContextEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/orchestrator/context");

        group.MapGet("/global", (
            OrchestratorContextDigestService digests,
            CancellationToken ct) => Build("global", false, digests, ct));
        group.MapGet("/project:{projectId}", (
            string projectId,
            OrchestratorContextDigestService digests,
            CancellationToken ct) => Build($"project:{projectId}", false, digests, ct));
        group.MapGet("/task:{projectId}/{taskKey}", (
            string projectId,
            string taskKey,
            OrchestratorContextDigestService digests,
            CancellationToken ct) => Build($"task:{projectId}/{taskKey}", false, digests, ct));
        group.MapGet("/workbench:{projectId}/{workbenchKey}", (
            string projectId,
            string workbenchKey,
            OrchestratorWorkbenchPromptContextComposer composer) =>
            BuildWorkbench(projectId, workbenchKey, composer));

        group.MapPost("/global/refresh", (
            OrchestratorContextDigestService digests,
            CancellationToken ct) => Build("global", true, digests, ct));
        group.MapPost("/project:{projectId}/refresh", (
            string projectId,
            OrchestratorContextDigestService digests,
            CancellationToken ct) => Build($"project:{projectId}", true, digests, ct));
        group.MapPost("/task:{projectId}/{taskKey}/refresh", (
            string projectId,
            string taskKey,
            OrchestratorContextDigestService digests,
            CancellationToken ct) => Build($"task:{projectId}/{taskKey}", true, digests, ct));
        // A Dossier digest has no quota/board state to re-probe, so refresh
        // rebuilds from the same live descriptor read as the plain GET.
        group.MapPost("/workbench:{projectId}/{workbenchKey}/refresh", (
            string projectId,
            string workbenchKey,
            OrchestratorWorkbenchPromptContextComposer composer) =>
            BuildWorkbench(projectId, workbenchKey, composer));
    }

    /// <summary>
    /// A Dossier (workbench) context has no board/task digest (AGT-2725): the
    /// "Context" inspector for it is just the same descriptor + entrypoint
    /// excerpt bundle a Dossier-scoped chat turn implicitly carries.
    /// </summary>
    private static IResult BuildWorkbench(
        string projectId,
        string workbenchKey,
        OrchestratorWorkbenchPromptContextComposer composer)
    {
        var composed = composer.Compose(projectId, workbenchKey);
        if (composed is null)
            return Results.NotFound(new { error = $"Unknown Dossier '{workbenchKey}' in project '{projectId}'." });

        var capturedAt = DateTime.UtcNow;
        var response = new OrchestratorContextDigestResponse(
            $"workbench:{projectId}/{workbenchKey}",
            capturedAt,
            composed.PromptBlock,
            [new OrchestratorDigestSourceStatus(
                "dossier", "ok", capturedAt, string.Join(", ", composed.IncludedBlocks))]);
        return Results.Ok(response);
    }

    private static async Task<IResult> Build(
        string rawContextKey,
        bool forceQuotaRefresh,
        OrchestratorContextDigestService digests,
        CancellationToken ct)
    {
        if (!OrchestratorContextKey.TryParse(rawContextKey, out var context))
            return Results.BadRequest(new { error = "Invalid orchestrator context key." });

        try
        {
            var response = await digests.BuildAsync(context, forceQuotaRefresh, ct).ConfigureAwait(false);
            return Results.Ok(response);
        }
        catch (KeyNotFoundException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
    }
}
