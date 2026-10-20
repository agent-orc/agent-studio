using AgentStudio.Security;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Projects;

/// <summary>
/// The project.yml pipeline under <c>/api/projects/{projectName}/pipeline-definition</c>:
/// the parsed diagram with issues (line and path), validation of edited text,
/// the schema 2 migration dry run, and its confirmation. Only the confirmation
/// writes: one managed repository commit that is pushed like a pipeline post
/// step, so it is classified as <see cref="ExecutionAdmissionPath.PostStep"/> and
/// the public-demo profile refuses it by route identity. The reads and the
/// validation write nothing and stay unclassified.
/// </summary>
public static class ProjectPipelineDefinitionEndpoints
{
    public static void MapProjectPipelineDefinitionEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects/{projectName}/pipeline-definition", (
            string projectName,
            ProjectPipelineDefinitionService definitions) => ToResult(definitions.Read(projectName)));

        app.MapPost("/api/projects/{projectName}/pipeline-definition/validate", (
            string projectName,
            ValidatePipelineDefinitionRequest? body,
            ProjectPipelineDefinitionService definitions) => ToResult(definitions.Validate(projectName, body)));

        app.MapGet("/api/projects/{projectName}/pipeline-definition/migration", (
            string projectName,
            ProjectPipelineDefinitionService definitions) => ToResult(definitions.DryRun(projectName)));

        app.MapPost("/api/projects/{projectName}/pipeline-definition/migration", (
            string projectName,
            ConfirmPipelineMigrationRequest? body,
            ProjectPipelineDefinitionService definitions) => ToResult(definitions.Confirm(projectName, body)))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.PostStep);
    }

    private static IResult ToResult<T>(PipelineDefinitionResult<T> result) => result.ErrorCode switch
    {
        null => Results.Ok(result.Value),
        "not-found" => Results.NotFound(new { error = result.Error, code = result.ErrorCode }),
        "stale" => Results.Conflict(new { error = result.Error, code = result.ErrorCode }),
        "not-available" or "proposal-invalid" => Results.UnprocessableEntity(new { error = result.Error, code = result.ErrorCode }),
        "write-failed" => Results.Problem(result.Error, statusCode: StatusCodes.Status500InternalServerError),
        _ => Results.BadRequest(new { error = result.Error, code = result.ErrorCode }),
    };
}
