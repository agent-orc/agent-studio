using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using AgentStudio.Security;

namespace AgentStudio.Tasks;

/// <summary>The additive, bounded task-switch read contract.</summary>
public static class TaskCoreEndpoints
{
    public static void MapTaskCoreEndpoint(this RouteGroupBuilder group)
    {
        group.MapGet("/{jobId}/core", (string jobId, string? project, HttpContext context,
            AgentStudio.Registry.ProjectRegistry projects, TaskIndexCache index,
            ITaskCoreRuntime runtimeSource, IConfiguration configuration) =>
        {
            var started = Stopwatch.GetTimestamp();
            // Project identity is mandatory here, so authorization and lookup
            // never need the freshness-enforcing FindJob path.
            if (string.IsNullOrWhiteSpace(project))
                return Results.BadRequest(new { error = "project is required" });
            var projectRecord = projects.FindByIdOrDisplayName(project)
                ?? projects.FindByShortCode(project);
            if (projectRecord is null) return Results.NotFound(new { state = "missing" });
            if (context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is HumanPrincipal human
                && !ProjectAccessAuthorization.Allows(human.User, projectRecord.Id, projects))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            var lookup = index.GetCore(jobId, projectRecord.StorageLocation);
            if (lookup.Record is null)
            {
                context.Response.Headers.CacheControl = "no-store";
                return lookup.Warming
                    ? Results.Json(new { state = "warming", jobId = TaskCoreRecord.Limit(jobId, 128), projectId = projectRecord.Id }, statusCode: 202)
                    : Results.NotFound(new { state = "missing", jobId = TaskCoreRecord.Limit(jobId, 128), projectId = projectRecord.Id });
            }

            var core = lookup.Record;
            // These are resident runtime facts. No scanner, settings, Git,
            // review projection, token collector or sidecar read is involved.
            var runtime = runtimeSource.Read(core);
            var runtimeVersion = runtime.Version;
            var editable = !SecurityProfiles.IsPublicDemo(configuration)
                && (context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is not HumanPrincipal principal
                    || principal.User.Role != StudioRoles.Viewer);
            var etag = $"\"core-{core.Version:x16}-{runtimeVersion}-{(lookup.Warming ? "s" : "r")}-{(editable ? "e" : "v")}\"";
            context.Response.Headers.ETag = etag;
            context.Response.Headers.CacheControl = "private, no-cache";
            if (context.Request.Headers.IfNoneMatch.Any(value => value == etag))
                return Results.StatusCode(StatusCodes.Status304NotModified);

            var response = new TaskCoreResponse
            {
                State = lookup.Warming ? "stale" : "ready",
                ProjectId = projectRecord.Id, ProjectName = core.ProjectName,
                Id = core.Id, TaskKey = core.TaskKey, Key = core.Key,
                Title = core.Title, Kind = core.Kind, TaskType = core.TaskType,
                Lane = core.State, ArchiveState = core.ArchiveState,
                EnteredLaneAt = core.EnteredLaneAt, Phase = core.Phase,
                PhaseEnteredAt = core.PhaseEnteredAt, Order = core.Order,
                Mode = core.Mode, Released = core.Released, PendingIntent = core.PendingIntent,
                Pins = new TaskCorePins
                {
                    Model = core.Model, ModelExplicit = core.ModelExplicit,
                    ThinkingLevel = core.ThinkingLevel, ThinkingLevelExplicit = core.ThinkingLevelExplicit,
                    CliType = core.CliType, ContextMode = core.ContextMode,
                    UseOwnSession = core.UseOwnSession, AllowWebAccess = core.AllowWebAccess,
                    NoBranchExpected = core.NoBranchExpected,
                },
                Actions = new TaskCoreActions(editable, editable && core.State != TaskStates.Archive,
                    editable, editable && core.State != TaskStates.Archive),
                Blocking = new TaskCoreBlocking
                {
                    BlockerType = core.BlockerType, BlockerCondition = core.BlockerCondition,
                    BlockerStatus = core.BlockerStatus,
                    BlockerDescription = core.BlockerDescription,
                    OutcomeIssue = core.OutcomeIssue,
                    NeedsInput = core.NeedsInput, DependencyBlocked = core.DependencyBlocked,
                    DependencyState = core.DependencyState, Dependencies = core.Dependencies,
                    DependsOn = core.DependsOn, BlockedBy = core.BlockedBy,
                },
                Runtime = runtime, RuntimeVersion = runtimeVersion,
                StatusSummary = core.Status,
                Prompt = new TaskCorePromptResponse(core.Prompt.State, core.Prompt.Text,
                    core.Prompt.OriginalBytes, core.Prompt.Hash, core.Prompt.Cursor,
                    core.Prompt.Cursor is null ? null :
                        $"/api/tasks/{Uri.EscapeDataString(core.Id)}/files/prompt.md?project={Uri.EscapeDataString(projectRecord.Id)}"),
                Timeline = new TaskCoreTimelineResponse(core.Timeline.State, core.Timeline.Events,
                    core.Timeline.OriginalBytes, core.Timeline.Hash, core.Timeline.Cursor,
                    $"/api/tasks/{Uri.EscapeDataString(core.Id)}/timeline?project={Uri.EscapeDataString(projectRecord.Id)}"),
                CoreVersion = core.Version,
            };
            var body = JsonSerializer.SerializeToUtf8Bytes(response, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (body.Length > 16 * 1024)
                return Results.Problem("Task core projection exceeded its 16 KiB contract.", statusCode: 500);
            context.Response.Headers["Server-Timing"] = "task-core;dur=" +
                Stopwatch.GetElapsedTime(started).TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture);
            return Results.Bytes(body, "application/json; charset=utf-8");
        });
    }
}
