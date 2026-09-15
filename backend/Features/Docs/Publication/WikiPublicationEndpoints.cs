namespace AgentStudio.Docs;

/// <summary>
/// Deployment evidence and supervised control for the hosted wiki revision.
///
/// <para>The GET is the diagnostics surface named in the hosting contract: it
/// reports the commit that is online right now, the commit it replaced, and the
/// typed outcome of the last attempt, so "which revision is published" is
/// answerable without shelling into the host.</para>
///
/// <para>The two POSTs are the supervised triggers. They run exactly the same
/// service the scheduled worker runs, so an operator-forced sync cannot take a
/// different path than the timer, and a rollback is a rehearsed call rather
/// than an improvised file operation. A rollback pins the project; the forced
/// sync is what releases that pin and resumes scheduled publication.</para>
/// </summary>
public static class WikiPublicationEndpoints
{
    public static void MapWikiPublicationEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects/{projectName}/wiki/publication",
            (string projectName, WikiPublicationService publication) =>
                publication.GetReport(projectName) is { } report
                    ? Results.Ok(report)
                    : Results.NotFound(new { error = $"Unknown project '{projectName}'" }));

        app.MapPost("/api/projects/{projectName}/wiki/publication/sync",
            (string projectName, WikiPublicationService publication, CancellationToken ct) =>
            {
                if (publication.GetReport(projectName) == null)
                    return Results.NotFound(new { error = $"Unknown project '{projectName}'" });
                // force: an operator sync is also how a rollback hold is
                // released. The scheduled trigger never forces.
                var outcome = publication.Synchronize(projectName, "operator", force: true, ct);
                return Respond(outcome, publication.GetReport(projectName)!);
            });

        app.MapPost("/api/projects/{projectName}/wiki/publication/rollback",
            (string projectName, WikiPublicationService publication) =>
            {
                if (publication.GetReport(projectName) == null)
                    return Results.NotFound(new { error = $"Unknown project '{projectName}'" });
                var outcome = publication.Rollback(projectName);
                return Respond(outcome, publication.GetReport(projectName)!);
            });
    }

    /// <summary>
    /// A failed attempt is a 409, not a 500: the request was well formed and the
    /// service is healthy, the deployment simply did not advance and the typed
    /// failure says why. The previous revision stays online either way, so the
    /// report is returned with both outcomes.
    /// </summary>
    private static IResult Respond(WikiPublicationOutcome outcome, WikiPublicationReport report) =>
        outcome.Status == WikiPublicationStatus.Failed
            ? Results.Conflict(new { outcome, report })
            : Results.Ok(new { outcome, report });
}
