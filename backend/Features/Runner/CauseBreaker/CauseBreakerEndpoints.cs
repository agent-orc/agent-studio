namespace AgentStudio.Runner;

/// <summary>Operator surface of the fleet-wide cause breaker (AGT-W57).</summary>
public static class CauseBreakerEndpoints
{
    public static void MapCauseBreakerEndpoints(this WebApplication app)
    {
        app.MapGet("/api/cause-breakers", (bool? openOnly, CauseBreakerService breakers)
            => Results.Ok(breakers.List(openOnly == true)));

        // Releases one waiting card as a probe; a green review closes the breaker.
        app.MapPost("/api/cause-breakers/{fingerprint}/probe", (string fingerprint, CauseBreakerService breakers) =>
        {
            var record = breakers.RequestProbe(fingerprint);
            if (record is null)
                return Results.NotFound(new { error = $"No open cause breaker '{fingerprint}'" });
            return Results.Ok(record);
        });
    }
}
