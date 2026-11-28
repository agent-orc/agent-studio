using AgentStudio.Operator;

namespace AgentStudio.Deploy;

public static class DeployRecordEndpoints
{
    public const string Path = "/api/operations/deploy-records";

    public static void MapDeployRecordEndpoints(this WebApplication app)
    {
        app.MapPost(Path, (DeployRecordReport report, HttpContext context, DeployRecordStore store) =>
            Respond(store.Upsert(report, OperatorEndpoints.Actor(context))));
        app.MapPost(Path + "/import", (DeployRecordReport[] reports, HttpContext context, DeployRecordStore store) =>
            Respond(store.Import(reports, OperatorEndpoints.Actor(context))));
        app.MapGet(Path, (int? offset, int? limit, DeployRecordStore store) =>
            offset < 0 || limit is < 1 or > 100
                ? Results.BadRequest(new { error = "Use a nonnegative offset and a limit from 1 to 100." })
                : Results.Ok(store.Page(offset ?? 0, limit ?? 25)));
    }

    private static IResult Respond<T>(DeployOutcome<T> outcome) => outcome.Kind switch
    {
        DeployOutcomeKind.Ok => Results.Ok(outcome.Value),
        DeployOutcomeKind.Invalid => Results.BadRequest(new { error = outcome.Error }),
        _ => Results.Conflict(new { error = outcome.Error }),
    };
}
