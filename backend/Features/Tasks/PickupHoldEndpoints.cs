namespace AgentStudio.Tasks;

/// <summary>
/// AGT-2818 read surface for the queued-but-unpickable inventory.
///
/// <list type="bullet">
/// <item><c>GET /api/pickup-holds</c> - every card sitting in a pickup lane
/// that the pickup gate skips, with its mechanism, reason, age and ways out.
/// Optional <c>?project=</c> filter, optional <c>?unsatisfiableOnly=true</c> for
/// the subset that no run will ever clear.</item>
/// </list>
///
/// Evaluated live rather than replayed from the boot sweep, so an operator who
/// just released a target sees the hold disappear instead of waiting for a
/// restart. There is deliberately no mutation here: the ways out are offered on
/// the card and taken by the operator through the existing release and
/// references endpoints.
/// </summary>
public static class PickupHoldEndpoints
{
    public static void MapPickupHoldEndpoints(this WebApplication app)
    {
        app.MapGet("/api/pickup-holds", (
            PickupHoldSweep sweep,
            string? project,
            bool? unsatisfiableOnly,
            CancellationToken ct) =>
        {
            var items = sweep.Sweep(ct).AsEnumerable();

            if (!string.IsNullOrWhiteSpace(project))
                items = items.Where(item =>
                    string.Equals(item.ProjectName, project, StringComparison.OrdinalIgnoreCase));
            if (unsatisfiableOnly == true)
                items = items.Where(item => item.Hold.Unsatisfiable);

            var ordered = items.ToList();
            return Results.Ok(new
            {
                total = ordered.Count,
                unsatisfiable = ordered.Count(item => item.Hold.Unsatisfiable),
                byMechanism = ordered
                    .GroupBy(item => item.Hold.Mechanism, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
                items = ordered,
            });
        });
    }
}
