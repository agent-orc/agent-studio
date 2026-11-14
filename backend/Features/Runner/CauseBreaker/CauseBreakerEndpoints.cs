namespace AgentStudio.Runner;

/// <summary>Operator surface of the fleet-wide cause breaker (AGT-W57).</summary>
public static class CauseBreakerEndpoints
{
    public static void MapCauseBreakerEndpoints(this WebApplication app)
    {
        app.MapGet("/api/cause-breakers", (bool? openOnly, CauseBreakerService breakers)
            => Results.Ok(breakers.List(openOnly == true)));

        // AGT-3617: the cause card's detail view asks whether it holds cards.
        // A card that holds none answers with a null breaker, not an error.
        app.MapGet("/api/cause-breakers/by-cause/{causeKey}", (string causeKey, CauseBreakerService breakers)
            => Results.Ok(new CauseBreakerForCauseResponse(breakers.ForCause(causeKey))));

        // Releases one waiting card as a probe; a green review closes the breaker.
        // The newest waiting delivery is the default; the body may name the card.
        app.MapPost("/api/cause-breakers/{fingerprint}/probe", (string fingerprint, CauseBreakerProbeRequest? request,
            CauseBreakerService breakers) =>
        {
            var record = breakers.RequestProbe(fingerprint, request?.TaskKey);
            if (record is null)
                return Results.NotFound(new { error = $"No open cause breaker '{fingerprint}'" });
            if (record.IsOpen && record.CloseReason is null && record.ProbeTaskKey is null)
                return Results.Conflict(new
                {
                    error = "probe-not-released",
                    message = string.IsNullOrWhiteSpace(request?.TaskKey)
                        ? "No waiting card could be released for a re-check. The next sweep repairs the hold; try again in a minute."
                        : $"{request.TaskKey} is not a waiting card of this cause, or it could not be released for a re-check.",
                });
            return Results.Ok(record);
        });
    }
}

/// <summary>Optional probe choice: the waiting card to re-check instead of the newest delivery.</summary>
public sealed record CauseBreakerProbeRequest(string? TaskKey);

public sealed record CauseBreakerForCauseResponse(CauseBreakerCauseView? Breaker);
