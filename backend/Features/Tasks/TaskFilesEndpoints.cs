

using System.Text;
using Microsoft.AspNetCore.Mvc;

using static AgentStudio.Tasks.TaskEndpointHelpers;

namespace AgentStudio.Tasks;

/// <summary>
/// Read/write of files inside a job folder: <c>prompt.md</c> /
/// <c>status.md</c> via <c>/files/{name}</c>, plus the
/// <c>attachments/</c> upload/download surface used by the prompt
/// editor and the read-only <c>results/</c> artifact route that backs
/// linked reports plus <c>status.md</c> image references. See
/// <c>docs/system/contracts/protocol-style.md</c> for the storage contract.
/// </summary>
public static class TaskFilesEndpoints
{
    public static void MapTaskFilesEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/{jobId}/result-history", (
            string jobId,
            [FromQuery] string? project,
            [FromQuery] string? watchPath,
            ResultHistoryQueryService history,
            AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var result = history.List(jobId, watchPath);
            return result.Success ? Results.Ok(result.Value ?? []) : ErrorResult(result);
        });

        group.MapGet("/{jobId}/result-history/{versionId}", (
            string jobId,
            string versionId,
            [FromQuery] string? project,
            [FromQuery] string? watchPath,
            ResultHistoryQueryService history,
            AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var result = history.Read(jobId, watchPath, versionId);
            return result.Success ? Results.Ok(result.Value) : ErrorResult(result);
        });

        group.MapGet("/{jobId}/files/{**path}", (
            string jobId,
            string path,
            [FromQuery] string? project,
            [FromQuery] string? watchPath,
            [FromQuery] string? at,
            [FromQuery] string? scope,
            TaskFileHistoryService files,
            AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            if (TryStripOperationSuffix(path, "history", out var historyPath))
            {
                var history = files.GetHistory(jobId, watchPath, historyPath, scope);
                return HistoryResult(history);
            }

            var content = files.ReadFile(jobId, watchPath, path, at, scope);
            return FileContentResult(content);
        });

        // Dev-seat twin of `/{jobId}/files/{**path}` for the `scope=code`
        // read: an arbitrary source file relative to the repo root on this
        // dev seat's checkout, used by the clickable protocol source
        // references to open a file in the source viewer. Split onto its own
        // path (studio-route-ownership dossier, "Split three mixed contracts
        // before cutover") so the Task Server-backed `/files/{**path}` route
        // never has to resolve or serve live repository content - only this
        // dev-seat route does, and the query-string `scope` is fixed here
        // rather than caller-selectable.
        group.MapGet("/{jobId}/checkout/{**path}", (
            string jobId,
            string path,
            [FromQuery] string? project,
            [FromQuery] string? watchPath,
            [FromQuery] string? at,
            TaskFileHistoryService files,
            AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            if (TryStripOperationSuffix(path, "history", out var historyPath))
            {
                var history = files.GetHistory(jobId, watchPath, historyPath, TaskFileSources.Code);
                return HistoryResult(history);
            }

            var content = files.ReadFile(jobId, watchPath, path, at, TaskFileSources.Code);
            return FileContentResult(content);
        });

        // Lists supported documents directly in the job root (status.md
        // excluded). Drives the detail view's Files tab (F48): prompt, aspect
        // verdicts, operator notes, and interactive isolated HTML surface as a
        // sortable, kind-classified manifest, with content fetched lazily
        // through `/files/{fileName}`.
        group.MapGet("/{jobId}/artifacts", (string jobId, string? project, string? watchPath, TaskScannerService scanner, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var response = scanner.ListArtifacts(jobId, watchPath);
            return response is null ? Results.NotFound() : Results.Ok(response);
        });

        group.MapPut("/{jobId}/files/{fileName}", (string jobId, string fileName, string? project, string? watchPath, UpdateJobFileRequest req, TaskMutationService mutations, TaskRunnerService runner, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            if (runner.IsJobLive(jobId, watchPath))
                return Results.Conflict("Cannot edit while the CLI is running for this task — stop it first.");

            try
            {
                var success = mutations.UpdateJobFile(jobId, fileName, req.Content, watchPath);
                return success ? Results.Ok() : Results.NotFound("Job not found or file is not editable.");
            }
            catch (IOException ex)
            {
                // File was locked by another process (editor, indexer, AV) for longer than
                // the retry window. Surface a tidy 503 instead of a stack-trace modal.
                return Results.Json(
                    new { error = "File is temporarily locked by another process — try saving again in a moment.", detail = ex.Message },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        // Prompt-editor screenshot uploads — written to <job>/attachments/<id>.<ext> and
        // referenced from prompt.md as a relative path so the CLI agent finds them on disk.
        group.MapPost("/{jobId}/attachments", async (string jobId, string? project, string? watchPath, HttpRequest request, TaskMutationService mutations, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            if (!request.HasFormContentType)
                return Results.BadRequest(new { error = "multipart/form-data expected" });

            var form = await request.ReadFormAsync();
            var file = form.Files["file"] ?? form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
                return Results.BadRequest(new { error = "No file uploaded" });

            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);

            var (fileName, error) = mutations.SaveAttachment(jobId, watchPath, ms.ToArray(), file.FileName, file.ContentType);
            if (fileName is null) return Results.BadRequest(new { error });

            // Relative URL so the editor renders it via the API; markdown stores `attachments/<file>`.
            var watchPathQuery = string.IsNullOrEmpty(watchPath) ? "" : $"?watchPath={Uri.EscapeDataString(watchPath)}";
            return Results.Ok(new
            {
                fileName,
                relativePath = $"attachments/{fileName}",
                url = $"/api/tasks/{Uri.EscapeDataString(jobId)}/attachments/{fileName}{watchPathQuery}"
            });
        }).DisableAntiforgery();

        group.MapGet("/{jobId}/attachments/{fileName}", (string jobId, string fileName, string? project, string? watchPath, TaskScannerService scanner, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var (path, contentType) = scanner.ResolveAttachment(jobId, fileName, watchPath);
            return path is null ? Results.NotFound() : Results.File(path, contentType);
        });

        // Read-only task-artifact route for the job's `results/` folder. It
        // remains the image source used by protocol markdown, and also serves
        // report HTML inline plus nested result artifacts linked from Activity,
        // Task, Result, and Docs. Unknown types retain octet-stream/download
        // behavior. ResolveResult confines every path to the current task.
        group.MapGet("/{jobId}/results/{**path}", (string jobId, string path, string? project, string? watchPath, HttpResponse response, TaskScannerService scanner, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var (resolved, contentType) = scanner.ResolveResult(jobId, path, watchPath);
            if (resolved is null) return Results.NotFound();
            if (contentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true)
            {
                response.Headers.ContentDisposition = "inline";
                response.Headers.ContentSecurityPolicy =
                    "sandbox allow-scripts; default-src 'none'; script-src 'unsafe-inline'; " +
                    "style-src 'unsafe-inline'; img-src data: blob:; font-src data:";
                response.Headers.XContentTypeOptions = "nosniff";
            }
            return Results.File(resolved, contentType, enableRangeProcessing: true);
        });

        // Ordered listing of every image under <job>/results/ (recursive),
        // captioned per spec/folder, with pass-fail status pulled from the
        // Playwright harvest index when available. Drives the protocol-pane
        // screenshot strip and the lightbox prev/next navigation.
        group.MapGet("/{jobId}/screenshots", (string jobId, string? project, string? watchPath, ScreenshotIndexService screenshots, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var entries = screenshots.ListJobScreenshots(jobId, watchPath);
            return Results.Ok(new TaskScreenshotsResponse
            {
                JobId = jobId,
                Screenshots = entries.ToList()
            });
        });

        // Compatibility image endpoint used by screenshot listings. General
        // result links now use the guarded, sub-path-aware /results route above.
        // Path traversal is rejected inside ResolveScreenshotFile.
        group.MapGet("/{jobId}/screenshot", (string jobId, string? path, string? project, string? watchPath, ScreenshotIndexService screenshots, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            if (string.IsNullOrWhiteSpace(path)) return Results.BadRequest(new { error = "path is required" });
            var (resolved, contentType) = screenshots.ResolveScreenshotFile(jobId, path, watchPath);
            return resolved is null ? Results.NotFound() : Results.File(resolved, contentType);
        });

        // The Activity artifact gallery must not download full-size evidence
        // into its thumbnail grid. Path resolution reuses the screenshot
        // boundary above; the derived WebP is cached and the existing result
        // route remains the full-size lightbox source.
        group.MapGet("/{jobId}/thumbnail", (
            string jobId,
            string? path,
            int? width,
            string? project,
            string? watchPath,
            HttpContext http,
            ScreenshotIndexService screenshots,
            ArtifactThumbnailService thumbnails,
            AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            if (string.IsNullOrWhiteSpace(path)) return Results.BadRequest(new { error = "path is required" });
            var (resolved, _) = screenshots.ResolveScreenshotFile(jobId, path, watchPath);
            if (resolved is null) return Results.NotFound();

            var thumbnail = thumbnails.Create(resolved, width);
            if (thumbnail is null) return Results.NotFound();
            http.Response.Headers.CacheControl = "private, max-age=3600";
            http.Response.Headers.LastModified = thumbnail.LastModified.ToString("R");
            return Results.Bytes(thumbnail.Bytes, thumbnail.ContentType);
        });
    }

    private static bool TryStripOperationSuffix(string path, string suffix, out string filePath)
    {
        filePath = "";
        if (string.IsNullOrWhiteSpace(path)) return false;

        var normalized = path.Replace('\\', '/').TrimEnd('/');
        var marker = "/" + suffix;
        if (!normalized.EndsWith(marker, StringComparison.OrdinalIgnoreCase))
            return false;

        filePath = normalized[..^marker.Length];
        return !string.IsNullOrWhiteSpace(filePath);
    }

    private static IResult HistoryResult(TaskFileLookupResult<IReadOnlyList<TaskFileHistoryEntry>> result)
    {
        if (!result.Success) return ErrorResult(result);
        return Results.Ok(result.Value ?? []);
    }

    private static IResult FileContentResult(TaskFileLookupResult<TaskFileContent> result)
    {
        if (!result.Success) return ErrorResult(result);
        var content = result.Value!;
        return Results.Text(content.Content, content.ContentType, Encoding.UTF8);
    }

    private static IResult ErrorResult<T>(TaskFileLookupResult<T> result)
    {
        var body = new { error = result.Error ?? "File lookup failed." };
        return result.StatusCode switch
        {
            StatusCodes.Status400BadRequest => Results.BadRequest(body),
            StatusCodes.Status404NotFound => Results.NotFound(body),
            StatusCodes.Status503ServiceUnavailable => Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable),
            _ => Results.Json(body, statusCode: result.StatusCode)
        };
    }

}
