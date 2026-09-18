namespace AgentStudio.Tags;

public sealed record TagMaintenanceChoice(string Option);
public static class TagMaintenanceEndpoints
{
    public static void MapTagMaintenanceEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{project}/tag-maintenance");
        group.MapGet("/", (string project, TagMaintenanceService service) =>
            Guard(() => Task.FromResult<IResult>(Results.Ok(service.Read(project)))));
        group.MapPost("/run", (string project, TagMaintenanceService service, CancellationToken ct) =>
            Guard(async () => Results.Ok(await service.RunAsync(project, force: true, ct))));
        group.MapPost("/decisions/{id}", (string project, string id, TagMaintenanceChoice choice,
            HttpContext context, TagMaintenanceService service, CancellationToken ct) => Guard(async () =>
        {
            var result = await service.DecideAsync(project, id, choice.Option,
                context.Request.Headers["X-Client-Id"].ToString(), ct);
            return result.Status == "partial" ? Results.Conflict(result) : Results.Ok(result);
        }));
    }
    private static async Task<IResult> Guard(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
    }
}

public sealed class TagMaintenanceWorker(TagMaintenanceService maintenance, IConfiguration config,
    ILogger<TagMaintenanceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (config.GetValue<bool?>("TagMaintenance:Enabled") == false) continue;
            foreach (var project in maintenance.Projects())
            {
                if (config.GetValue<bool?>($"TagMaintenance:Projects:{project}:Enabled") == false) continue;
                try
                {
                    var run = await maintenance.RunAsync(project, ct: stoppingToken);
                    if (run?.Status == "failed")
                        logger.LogWarning("tag-maintenance-failed project={Project} run={Run} error={Error}", project, run.Id, run.Error);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception ex) { logger.LogError(ex, "tag-maintenance-failed project={Project}", project); }
            }
        }
    }
}
