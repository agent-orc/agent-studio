namespace AgentStudio.Tasks;

/// <summary>
/// Read surface for the parked-card Wiedervorlage.
///
/// <list type="bullet">
/// <item><c>GET /api/parked-cards</c> - every card sitting in a human-decision
/// lane, with its blocker, the latest recall verdict, and how long it has been
/// parked. Optional <c>?project=</c> filter, optional <c>?recallableOnly=true</c>.</item>
/// </list>
///
/// The route evaluates conditions live rather than replaying the last sweep, so
/// an operator who just cleared a precondition sees it immediately instead of
/// waiting for the next tick. Like the completed-lane integration-pending
/// listing, that live evaluation also self-heals the stored verdict; an
/// unchanged verdict writes nothing. There is deliberately no requeue mutation
/// here: re-queueing a parked card is the existing operator lane move, which
/// opens a fresh review-attempt epoch through <c>OperatorReviewRequeueService</c>.
/// </summary>
public static class ParkedCardEndpoints
{
    public static void MapParkedCardEndpoints(this WebApplication app)
    {
        app.MapGet("/api/parked-cards", (
            HttpContext context,
            ProjectRegistry registry,
            ParkedCardRecallSweep sweep,
            string? project,
            bool? recallableOnly,
            CancellationToken ct) =>
        {
            var items = sweep.Sweep(ct).AsEnumerable();

            if (context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is HumanPrincipal human)
                items = items.Where(item => ProjectAccessAuthorization.Allows(human.User, item.ProjectName, registry));

            if (!string.IsNullOrWhiteSpace(project))
                items = items.Where(item => string.Equals(item.ProjectName, project, StringComparison.OrdinalIgnoreCase));
            if (recallableOnly == true)
                items = items.Where(item => item.IsRecallable);

            // Oldest first: the whole point is that a card nobody looked at for
            // four days should be the first thing an operator sees.
            var ordered = items.OrderByDescending(item => item.ParkedForSeconds).ToList();
            return Results.Ok(new
            {
                total = ordered.Count,
                recallable = ordered.Count(item => item.IsRecallable),
                items = ordered,
            });
        });
    }
}
