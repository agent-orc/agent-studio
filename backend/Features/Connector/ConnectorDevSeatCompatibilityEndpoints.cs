namespace AgentStudio.Connector;

public static class ConnectorDevSeatCompatibilityEndpoints
{
    private const string CompatibilityCli = "codex";

    public static void MapConnectorDevSeatCompatibilityEndpoints(this WebApplication app)
    {
        app.MapGet("/api/settings/cli", (CliRouter router) =>
            Results.Ok(Describe(router.Get(CompatibilityCli))));
        app.MapPut("/api/settings/cli", (SetCliPathRequest request, CliRouter router) =>
        {
            var cli = (GenericCliExecutionService)router.Get(CompatibilityCli);
            cli.SetCliPath(request.Path);
            return Results.Ok(Describe(cli));
        });
        app.MapGet("/api/settings/cli/models", async (CliRouter router, CancellationToken cancellationToken) =>
            Results.Ok(await router.Get(CompatibilityCli).GetModelCatalogAsync(false, cancellationToken)));
        app.MapPost("/api/settings/cli/test", (SetCliPathRequest request, CliRouter router) =>
        {
            var (available, version, path) = router.Get(CompatibilityCli).TestCliPath(request.Path);
            return Results.Ok(new { path, available, version, hasToken = false });
        });
        app.MapPut("/api/settings/cli/token", () => Results.Json(new
        {
            code = "copilot-cli-retired",
            message = "The retired Copilot token surface does not persist browser credentials.",
        }, statusCode: StatusCodes.Status410Gone));
    }

    private static object Describe(ICliExecutionService cli)
    {
        var (available, version, path) = cli.TestCliPath();
        return new { path, available, version, hasToken = false };
    }
}
