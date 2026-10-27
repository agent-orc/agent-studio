using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;
using AgentStudio.Cli;
using AgentStudio.Registry;
using AgentStudio.Review;
using AgentStudio.Runner;
using AgentStudio.Security;
using AgentStudio.Shared;
using AgentStudio.TestRuns;
using AgentStudio.Tokens;

namespace AgentStudio.Tasks;

/// <summary>Independent task detail reads. None invokes the legacy full-detail handler.</summary>
public static class TaskDetailResources
{
    public static void MapTaskDetailResources(this RouteGroupBuilder group)
    {
        group.MapGet("/{jobId}/details/overview", (string jobId, string? project, long? generation,
            HttpContext context, ProjectRegistry projects, TaskIndexCache index,
            ITaskCoreRuntime runtime, TaskScannerService scanner,
            AgentStudio.Runner.AttemptAuthorityService attemptAuthority, CliRouter router, TaskRunnerService runners) =>
        {
            var lookup = Resolve(jobId, project, generation, context, projects, index);
            if (lookup.Error is not null) return lookup.Error;
            var core = lookup.Core!;
            var facts = index.GetDetailFacts(core.Id, core.WatchPath);
            var task = facts.Info;
            if (task is null || task.TaskKey != core.TaskKey) return Results.NotFound();
            var data = new TaskOverviewResource(task.Decision,
                TaskEndpointHelpers.BuildPlanningSpawnSummary(task),
                TaskEndpointHelpers.BuildConceptDossierSummary(task),
                task.OlderBriefDelivery, task.RelatedWikiPages, task.CreationSource,
                task.CompletionClaim, task.ParkedBlocker, task.NeedsInput,
                task.References, task.References.DependsOn.Count > 0
                    ? facts.References.EvaluateWaitsOn(task) : null,
                // AGT-3096: the core-first detail needs the same acceptance
                // lineage gap the legacy full-detail read projects.
                AcceptanceAttemptLineagePolicy.ReadGap(task, attemptAuthority),
                ExecutionLocation(task, router, runners), task.PendingIntent);
            return Reply(context, core, lookup.ProjectId!, runtime, "overview", "ready", data,
                null, Modified(Path.Combine(core.FolderPath, "task.json")));
        });

        group.MapGet("/{jobId}/details/git", (string jobId, string? project, long? generation,
            HttpContext context, ProjectRegistry projects, TaskIndexCache index,
            ITaskCoreRuntime runtime, TaskListGitProjectionCache snapshots,
            TaskScannerService scanner, ILoggerFactory loggerFactory) =>
        {
            using var gitTelemetry = AgentStudio.Git.GitProcessTelemetry.BeginRequest("tasks/detail/git",
                loggerFactory.CreateLogger("TaskGitResource"), includeNested: true);
            var lookup = Resolve(jobId, project, generation, context, projects, index);
            if (lookup.Error is not null) return lookup.Error;
            var core = lookup.Core!;
            var key = core.TaskKey;
            var task = new TaskInfo { TaskKey = key, WatchPath = core.WatchPath };
            var snapshot = snapshots.ReadCacheOnly([task]);
            var freshness = snapshots.ReadFreshness([task]);
            var data = new TaskGitResource(
                snapshot.Merge.GetValueOrDefault(key), snapshot.Integration.GetValueOrDefault(key),
                snapshot.Publish.GetValueOrDefault(key), snapshot.TestRuns.GetValueOrDefault(key),
                core.Commit, core.Commits, index.GetDetailFacts(core.Id, core.WatchPath).Info?.Provenance);
            var state = freshness.GitStateAt is null ? "unavailable" : freshness.Stale ? "stale" : "ready";
            var reason = state == "unavailable" ? "git-snapshot-pending" :
                state == "stale" ? freshness.Restored ? "git-snapshot-restored" : "git-snapshot-refreshing" : null;
            return Reply(context, core, lookup.ProjectId!, runtime, "git", state, data, reason, freshness.GitStateAt?.UtcDateTime);
        });

        group.MapGet("/{jobId}/details/usage", async (string jobId, string? project, long? generation,
            HttpContext context, ProjectRegistry projects, TaskIndexCache index,
            ITaskCoreRuntime runtime, TaskScannerService scanner, TaskTokenUsageCache tokens) =>
        {
            var lookup = Resolve(jobId, project, generation, context, projects, index);
            if (lookup.Error is not null) return lookup.Error;
            var core = lookup.Core!;
            var summary = await tokens.GetAsync(core.ProjectName, core.WatchPath, core.Id, context.RequestAborted);
            var data = new TaskUsageResource(summary, core.LastUsage,
                scanner.ReadContextUsage(core.FolderPath));
            return Reply(context, core, lookup.ProjectId!, runtime, "usage", "ready", data, null,
                Modified(Path.Combine(core.FolderPath, "task.json")));
        });

        group.MapGet("/{jobId}/details/review", (string jobId, string? project, long? generation,
            bool? evidence,
            HttpContext context, ProjectRegistry projects, TaskIndexCache index,
            ITaskCoreRuntime runtime, TaskListGitProjectionCache snapshots) =>
        {
            var lookup = Resolve(jobId, project, generation, context, projects, index);
            if (lookup.Error is not null) return lookup.Error;
            var core = lookup.Core!;
            var task = new TaskInfo { TaskKey = core.TaskKey, WatchPath = core.WatchPath };
            var snapshot = snapshots.ReadCacheOnly([task]);
            var data = new TaskReviewResource(snapshot.ReviewProjection.GetValueOrDefault(core.TaskKey),
                evidence == true ? ReviewEvidenceLog.ReadLatestPerId(core.FolderPath) : null);
            var freshness = snapshots.ReadFreshness([task]);
            var state = freshness.GitStateAt is null ? "warming" : freshness.Stale ? "stale" : "ready";
            return Reply(context, core, lookup.ProjectId!, runtime, "review", state, data,
                state == "ready" ? null : "review-projection-pending", freshness.GitStateAt?.UtcDateTime);
        });

        group.MapGet("/{jobId}/details/documents", (string jobId, string? project, long? generation,
            string? name, HttpContext context, ProjectRegistry projects, TaskIndexCache index,
            ITaskCoreRuntime runtime, TaskScannerService scanner) =>
        {
            if (name is not ("prompt" or "status")) return Results.BadRequest(new { error = "name must be prompt or status" });
            var lookup = Resolve(jobId, project, generation, context, projects, index);
            if (lookup.Error is not null) return lookup.Error;
            var core = lookup.Core!;
            var path = Path.Combine(core.FolderPath, name + ".md");
            string? markdown;
            try { markdown = File.Exists(path) ? File.ReadAllText(path) : null; }
            catch (IOException) { return Reply(context, core, lookup.ProjectId!, runtime, "documents", "unavailable",
                new TaskDocumentResource(name, null, null), "document-read-failed", null); }
            catch (UnauthorizedAccessException) { return Reply(context, core, lookup.ProjectId!, runtime, "documents", "unavailable",
                new TaskDocumentResource(name, null, null), "document-access-denied", null); }
            var summary = name == "status" ? scanner.ResolveSummaryState(core.TaskKey, markdown) : null;
            var assessment = name == "status" ? SummaryAssessmentStore.ReadFor(core.FolderPath, markdown) : null;
            return Reply(context, core, lookup.ProjectId!, runtime, "documents", "ready",
                new TaskDocumentResource(name, markdown, summary) { Assessment = assessment }, null, Modified(path));
        });

        group.MapGet("/{jobId}/details/history", (string jobId, string? project, long? generation,
            HttpContext context, ProjectRegistry projects, TaskIndexCache index, ITaskCoreRuntime runtime) =>
        {
            var lookup = Resolve(jobId, project, generation, context, projects, index);
            if (lookup.Error is not null) return lookup.Error;
            var core = lookup.Core!;
            var data = new TaskHistoryResource(
                TaskScannerService.ReadPromptHistory(core.FolderPath),
                TitleHistoryLog.Read(core.FolderPath),
                TaskScannerService.BuildLog(core.FolderPath));
            return Reply(context, core, lookup.ProjectId!, runtime, "history", "ready", data, null,
                Directory.Exists(core.FolderPath) ? Directory.GetLastWriteTimeUtc(core.FolderPath) : null);
        });
    }

    private static (TaskCoreRecord? Core, string? ProjectId, IResult? Error) Resolve(string jobId, string? project,
        long? generation, HttpContext context, ProjectRegistry projects, TaskIndexCache index)
    {
        if (string.IsNullOrWhiteSpace(project)) return (null, null, Results.BadRequest(new { error = "project is required" }));
        var record = projects.FindByIdOrDisplayName(project) ?? projects.FindByShortCode(project);
        if (record is null) return (null, null, Results.NotFound());
        if (context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is HumanPrincipal human
            && !ProjectAccessAuthorization.Allows(human.User, record.Id, projects))
            return (null, null, Results.StatusCode(StatusCodes.Status403Forbidden));
        var lookup = index.GetCore(jobId, record.StorageLocation);
        var core = lookup.Record;
        // A re-hydrating index cannot tell a missing task from one it has not
        // placed yet; answer like the core route so clients retry, not revoke.
        if (core is null)
            return (null, null, lookup.Warming
                ? Results.Json(new { state = "warming", reason = "task-index-warming" }, statusCode: StatusCodes.Status202Accepted)
                : Results.NotFound());
        if (generation is not null && generation != core.Version)
            return (null, null, Results.Conflict(new { state = "stale", reason = "core-generation-changed" }));
        return (core, record.Id, null);
    }

    private static IResult Reply<T>(HttpContext context, TaskCoreRecord core, string projectId,
        ITaskCoreRuntime runtime, string resource, string state, T data, string? reason, DateTime? computedAt)
    {
        var attemptId = runtime.Read(core).AttemptId;
        // Serialized once, with the host's shared HTTP options: the same bytes
        // are hashed into `version` and embedded as the wire `data`.
        var json = context.RequestServices.GetRequiredService<IOptions<HttpJsonOptions>>().Value.SerializerOptions;
        var content = JsonSerializer.SerializeToUtf8Bytes(data, json);
        var version = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var etag = $"\"{resource}-{state}-{reason}-{version}-{core.Version:x}-{attemptId}\"";
        context.Response.Headers.ETag = etag;
        context.Response.Headers.CacheControl = "private, no-cache";
        if (context.Request.Headers.IfNoneMatch.Any(value => value == etag))
            return Results.StatusCode(StatusCodes.Status304NotModified);
        return Results.Ok(new TaskDetailResource(core.Id, core.TaskKey,
            projectId, attemptId, core.Version,
            resource, version, computedAt, state, new PreSerializedJson(content), reason));
    }

    /// <summary>
    /// The full execution location (branch, worktree, trust reason, activity
    /// stamp) that the board card leaves out. Same resident reads as the
    /// board overlay; no scan, Git or disk access.
    /// </summary>
    private static TaskExecutionLocation ExecutionLocation(TaskInfo task, CliRouter router, TaskRunnerService runners)
    {
        var progress = task.State == TaskStates.Progress;
        var execution = progress ? router.Get(task.CliType).GetExecution(task.TaskKey) : null;
        var activity = progress
            ? TaskRunActivityClassifier.Classify(runners.GetRunActivityForJob(task.Id, task.ProjectName), execution, outcomeIssue: null)
            : null;
        return runners.ResolveExecutionLocation(task, execution, activity);
    }

    private static DateTime? Modified(string path) => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
}

/// <summary>
/// Reply envelope. `CoreVersion` is a decimal string on the wire, as on the
/// core route. `Data` is one of the resource records below, already serialized.
/// </summary>
public sealed record TaskDetailResource(string Id, string TaskKey, string ProjectId,
    string? AttemptId,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)]
    long CoreVersion, string Resource, string Version,
    DateTime? ComputedAt, string State, PreSerializedJson Data, string? Reason);

/// <summary>UTF-8 JSON written verbatim, so a hashed payload is not serialized twice.</summary>
[JsonConverter(typeof(PreSerializedJsonConverter))]
public readonly record struct PreSerializedJson(byte[] Utf8);

internal sealed class PreSerializedJsonConverter : JsonConverter<PreSerializedJson>
{
    public override PreSerializedJson Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return new PreSerializedJson(JsonSerializer.SerializeToUtf8Bytes(document.RootElement));
    }

    public override void Write(Utf8JsonWriter writer, PreSerializedJson value, JsonSerializerOptions options) =>
        writer.WriteRawValue(value.Utf8, skipInputValidation: true);
}
public sealed record TaskGitResource(TaskMergeSignal? MergeSignal,
    TaskIntegrationStatus? Integration, TaskPublishSignal? PublishSignal,
    TaskTestRunEvidence? TestEvidence, TaskCommitInfo? Commit,
    IReadOnlyList<TaskCommitInfo> Commits, TaskProvenance? Provenance);
public sealed record TaskUsageResource(TaskTokenSummary? TokenSummary,
    SessionUsage? LastUsage, ContextUsageSnapshot? ContextUsage);
public sealed record TaskReviewResource(ReviewProjectionView? ReviewProjection,
    IReadOnlyList<ReviewEvidenceEntry>? Evidence);
public sealed record TaskDocumentResource(string Name, string? Markdown, TaskSummaryState? SummaryState)
{
    /// <summary>Closing Result assessment for the status document (AGT-3046); null for prompt or before one ran.</summary>
    public SummaryAssessment? Assessment { get; init; }
}
public sealed record TaskHistoryResource(IReadOnlyList<TaskPromptHistoryEntry> PromptHistory,
    IReadOnlyList<TaskTitleHistoryEntry> TitleHistory, IReadOnlyList<TaskLogEntry> Log);
public sealed record TaskOverviewResource(DecisionContent? Decision,
    PlanningSpawnSummary? PlanningSpawn, ConceptDossierSummary? ConceptDossier,
    OlderBriefDeliveryOffer? OlderBriefDelivery, IReadOnlyList<RelatedWikiPage> RelatedWikiPages,
    string CreationSource, TaskCompletionClaim? CompletionClaim, ParkedBlockerStatus? ParkedBlocker,
    NeedsInputStatus? NeedsInput, TaskReferences References, WaitsOnStatus? WaitsOn,
    AcceptanceAttemptGap? AcceptanceAttemptGap,
    TaskExecutionLocation? ExecutionLocation, PendingIntent? PendingIntent);
