using AgentStudio.Git;
using AgentStudio.Security;

namespace AgentStudio.Tasks;

/// <summary>
/// Metrics card surface of the board source: configured source, mirror state,
/// recorded differences by card and field, and the cutover readiness. An
/// operator explains a difference by its fingerprint; the explanation is kept
/// with the record until the same field disagrees with other values.
/// </summary>
public static class BoardSourceEndpoints
{
    public sealed record ExplainBoardDifferenceBody(string? Fingerprint, string? Explanation);

    public static void MapBoardSourceEndpoints(this WebApplication app)
    {
        app.MapGet("/api/admin/board-source", (BoardSourceSettings settings, BoardShadowLedger ledger, TimeProvider clock)
                => Results.Ok(Status(settings, ledger, clock)))
            .RequirePermission(IamPermissions.WorkspaceOperate);

        app.MapPost("/api/admin/board-source/differences/explain", (
                ExplainBoardDifferenceBody body, HttpContext context, BoardSourceSettings settings, BoardShadowLedger ledger,
                TimeProvider clock) =>
            {
                if (string.IsNullOrWhiteSpace(body.Fingerprint))
                    return Results.BadRequest(new { error = "fingerprint is required." });
                if (string.IsNullOrWhiteSpace(body.Explanation))
                    return Results.BadRequest(new { error = "explanation is required." });
                var actor = context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is HumanPrincipal human
                    ? human.User.Id
                    : DefaultClientIdentity.Id;
                return ledger.Explain(body.Fingerprint, body.Explanation, actor)
                    ? Results.Ok(Status(settings, ledger, clock))
                    : Results.NotFound(new { error = "No recorded difference has this fingerprint." });
            })
            .RequirePermission(IamPermissions.WorkspaceOperate);
    }

    private static BoardSourceStatusResponse Status(BoardSourceSettings settings, BoardShadowLedger ledger, TimeProvider clock)
        => ledger.Snapshot(settings.Current) with
        {
            BoardRead = BoardReadAcceptance.From(GitProcessTelemetry
                .GetStatsSnapshot(GitTelemetryAdminEndpoints.StatsWindow, clock)
                .FirstOrDefault(stats => stats.Label == "tasks/grouped")),
        };
}
