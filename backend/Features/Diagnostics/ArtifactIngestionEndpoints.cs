using System.Text.RegularExpressions;

namespace AgentStudio.Diagnostics;

/// <summary>
/// Runner -> Server artifact-ingestion API under <c>/api/runner/artifacts</c>.
/// Remote runners send screenshots and result files to the server, which owns
/// the durable task folder and workspace evidence commit.
/// </summary>
public static class ArtifactIngestionEndpoints
{
    private static readonly Regex UnsafeSegment = new(@"(^|[\\/])\.\.([\\/]|$)", RegexOptions.Compiled);
    private static readonly Regex WindowsRootedPath = new(@"^[A-Za-z]:[\\/]", RegexOptions.Compiled);
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".md", ".json", ".jsonl", ".yaml", ".yml", ".xml", ".csv"
    };

    public static void MapArtifactIngestionEndpoints(this WebApplication app)
    {
        app.MapGet("/api/runner/artifacts/limits", (
            string taskKey,
            ITaskScanner scanner,
            AgentStudio.Projects.ProjectSettingsService settings,
            ArtifactRequestLimits requestLimits) =>
        {
            var task = ResolveTask(scanner, taskKey);
            if (task is null) return Results.NotFound();
            return Results.Ok(ArtifactTransferPolicy.Resolve(
                requestLimits.MaxRequestBodyBytes,
                settings.Get(task.ProjectName).ResultArtifactMaxFileBytes,
                settings.Get(task.ProjectName).ResultArtifactMaxTotalBytes));
        });

        app.MapPost("/api/runner/artifacts/outcome", (
            ArtifactTransferReportRequest req,
            HttpContext context,
            ITaskScanner scanner,
            RunLeaseService leases,
            AgentStudio.Tasks.TimelineLog timeline) =>
        {
            if (!RunnerLeaseAuthorization.IsCurrent(
                    context, leases, req.TaskKey, req.RunnerId, req.LeaseId, req.FencingToken))
                return Results.Conflict(new { error = "stale-runner-lease" });
            var task = ResolveTask(scanner, req.TaskKey);
            if (task is null) return Results.NotFound(new { error = "task-not-found" });
            if (!string.Equals(req.Status, "partial", StringComparison.OrdinalIgnoreCase)
                || req.Issues is null || req.Issues.Count == 0)
                return Results.BadRequest(new { error = "partial-artifact-issues-required" });

            var issue = req.Issues[0];
            var summary = ArtifactTransferPolicy.BoardFact(issue);
            timeline.Append(
                task.FolderPath,
                TimelineEventKinds.ResultArtifactsPartial,
                TimelineActors.System,
                summary,
                req.AttemptId,
                details: new Dictionary<string, string>
                {
                    ["artifactStatus"] = "partial",
                    ["typedOutcome"] = issue.Outcome,
                    ["path"] = issue.Path,
                    ["sizeBytes"] = issue.SizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["reason"] = issue.Reason,
                    ["notTransferredCount"] = req.Issues.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
            return Results.Accepted(value: new { status = "partial", fact = summary });
        });

        app.MapPost("/api/runner/artifacts", async (
            ArtifactIngestRequest req,
            HttpContext context,
            ITaskScanner scanner,
            RunLeaseService leases,
            AttemptAuthorityService authority,
            WorkspaceArtifactCommitService artifactCommits,
            RemoteResultFinalizationService finalization,
            AgentStudio.Projects.ProjectSettingsService settings,
            ArtifactRequestLimits requestLimits,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("AgentStudio.Diagnostics.ArtifactIngestionEndpoints");
            if (!RunnerLeaseAuthorization.IsCurrent(context, leases, req.TaskKey, req.RunnerId, req.LeaseId, req.FencingToken))
                return Results.Conflict(new ArtifactIngestResponse(req.TaskKey, 0, [], "The authenticated Runner does not hold the current fenced lease."));
            if ((req.Artifacts is null || req.Artifacts.Count == 0) && !req.FinalizeResult)
                return Results.Ok(new ArtifactIngestResponse(req.TaskKey, 0, [], "no artifacts"));

            var task = ResolveTask(scanner, req.TaskKey);
            if (task is null)
                return Results.NotFound(new ArtifactIngestResponse(req.TaskKey, 0, [], $"No task '{req.TaskKey}'."));

            req = req with { Artifacts = req.Artifacts ?? [] };
            var limits = ArtifactTransferPolicy.Resolve(
                requestLimits.MaxRequestBodyBytes,
                settings.Get(task.ProjectName).ResultArtifactMaxFileBytes,
                settings.Get(task.ProjectName).ResultArtifactMaxTotalBytes);
            var oversized = req.Artifacts
                .Select(artifact => ArtifactTransferPolicy.DecodedLength(artifact.ContentBase64))
                .FirstOrDefault(size => size > limits.MaxFileBytes);
            if (oversized > limits.MaxFileBytes)
                return ArtifactRequestLimitMiddleware.Problem(limits.MaxFileBytes, oversized);

            var projection = authority.GetTaskProjection(req.TaskKey);
            AttemptWriteReference? write = null;
            string? evidenceDigest = null;
            if (string.IsNullOrWhiteSpace(req.AttemptId) || !req.Fence.HasValue
                || !req.AuthorityEpoch.HasValue || string.IsNullOrWhiteSpace(req.IdempotencyKey))
            {
                if (!projection.LegacyTask)
                    return Results.Conflict(new AttemptWriteResult(
                        AttemptWriteStatus.Invalid, req.AttemptId ?? string.Empty,
                        "Canonical runner writes require AttemptId, Fence, AuthorityEpoch, and IdempotencyKey."));
            }
            else
            {
                var digestInput = string.Join("\n", req.Artifacts
                    .OrderBy(x => x.Path, StringComparer.Ordinal)
                    .Select(x => $"{x.Path}:{AttemptAuthorityService.Hash(x.ContentBase64 ?? string.Empty)}"));
                write = new AttemptWriteReference(
                    req.AttemptId, req.Fence.Value, req.AuthorityEpoch.Value, req.IdempotencyKey);
                evidenceDigest = "artifact-set:" + AttemptAuthorityService.Hash(digestInput);
            }

            ArtifactIngestResponse written;
            try
            {
                ArtifactIngestResponse? sideEffectResult = null;
                if (write is null)
                {
                    written = WriteArtifacts(task, req);
                }
                else
                {
                    var accepted = authority.ExecuteRunWrite(
                        write,
                        "artifact",
                        req.TaskKey,
                        () => sideEffectResult = WriteArtifacts(task, req),
                        evidenceDigest);
                    if (accepted.Status == AttemptWriteStatus.Duplicate)
                    {
                        written = DescribeArtifacts(req) with { Message = "duplicate delivery" };
                    }
                    else
                    {
                        if (accepted.Status != AttemptWriteStatus.Accepted)
                            return Results.Conflict(accepted);
                        written = sideEffectResult!;
                    }
                }
            }
            catch (ArtifactIngestException ex)
            {
                // The result genuinely did not arrive: nothing durable was
                // persisted, so this - and not a degraded summary - is what
                // "finalize missing" means (AGT-2850).
                logger.LogWarning(
                    "remote-result-finalize-missing taskKey={TaskKey} jobId={JobId} status=rejected:{Reason}",
                    req.TaskKey,
                    task.Id,
                    CredentialRedactor.Redact(ex.Message));
                return Results.BadRequest(new ArtifactIngestResponse(req.TaskKey, 0, [], CredentialRedactor.Redact(ex.Message)));
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    "remote-result-finalize-missing taskKey={TaskKey} jobId={JobId} status=failed:{Reason}",
                    req.TaskKey,
                    task.Id,
                    CredentialRedactor.Redact(ex.Message));
                return Results.Problem(CredentialRedactor.Redact($"Failed to ingest artifacts for '{req.TaskKey}': {ex.Message}"));
            }

            // The artefacts are durable from here on, so the result HAS arrived.
            // Summary generation is a retryable step that must not hold the
            // acknowledgement: it runs behind a bounded budget and, when it is
            // refused or still running, leaves a queued retry instead of a lost
            // run (AGT-2850).
            var resultDocument = req.FinalizeResult
                ? await finalization.FinalizeForAcknowledgementAsync(task)
                : ResultAcknowledgementPlan.NotRequested;

            var committedFiles = written.Files.ToList();
            if (resultDocument.CommitStatusDocument)
                committedFiles.Add("status.md");
            var commit = artifactCommits.TryCommitArtifactUpload(
                null,
                task.Id,
                task.FolderPath,
                committedFiles);

            var status = commit.Success
                ? commit.DidCommit ? "committed" : $"skipped:{commit.Error}"
                : $"failed:{commit.Error}";
            logger.LogInformation(
                "runner-artifact-ingest taskKey={TaskKey} jobId={JobId} uploaded={Uploaded} commitStatus={CommitStatus} "
                + "sha={Sha} resultDocument={ResultDocument}",
                req.TaskKey, task.Id, written.Uploaded, status, commit.Sha ?? "",
                resultDocument.ResultDocumentStatus ?? "not-requested");

            return Results.Ok(written with
            {
                CommitSha = commit.Sha,
                CommitStatus = status,
                ResultDocumentGenerated = resultDocument.Generated,
                ResultDocumentStatus = resultDocument.ResultDocumentStatus
            });
        });
    }

    private static ArtifactIngestResponse DescribeArtifacts(ArtifactIngestRequest req)
    {
        var files = req.Artifacts
            .Select(artifact => NormalizeResultsPath(artifact.Path).Replace('\\', '/'))
            .ToList();
        return new ArtifactIngestResponse(req.TaskKey, files.Count, files);
    }

    internal static ArtifactIngestResponse WriteArtifacts(TaskInfo task, ArtifactIngestRequest req)
    {
        if (string.IsNullOrWhiteSpace(task.FolderPath))
            throw new ArtifactIngestException("Task folder is missing.");

        var resultsDir = Path.Combine(task.FolderPath, TaskPaths.ResultsDirName);
        Directory.CreateDirectory(resultsDir);

        var files = new List<string>();
        foreach (var artifact in req.Artifacts)
        {
            var rel = NormalizeResultsPath(artifact.Path);
            var destination = Path.GetFullPath(Path.Combine(task.FolderPath, rel));
            var resultsRoot = Path.GetFullPath(resultsDir) + Path.DirectorySeparatorChar;
            if (!destination.StartsWith(resultsRoot, StringComparison.OrdinalIgnoreCase))
                throw new ArtifactIngestException($"Artifact path escapes results/: {artifact.Path}");

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(artifact.ContentBase64 ?? string.Empty);
            }
            catch (FormatException)
            {
                throw new ArtifactIngestException($"Artifact content is not valid base64: {artifact.Path}");
            }

            bytes = RedactTextArtifact(rel, bytes);

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllBytes(destination, bytes);
            files.Add(rel.Replace('\\', '/'));
        }

        return new ArtifactIngestResponse(req.TaskKey, files.Count, files);
    }

    private static byte[] RedactTextArtifact(string path, byte[] bytes)
    {
        if (!TextExtensions.Contains(Path.GetExtension(path))) return bytes;
        try
        {
            var utf8 = new System.Text.UTF8Encoding(false, true);
            return utf8.GetBytes(CredentialRedactor.Redact(utf8.GetString(bytes)));
        }
        catch (System.Text.DecoderFallbackException)
        {
            throw new ArtifactIngestException($"Text artifact is not valid UTF-8: {path}");
        }
    }

    internal static string NormalizeResultsPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArtifactIngestException("Artifact path is required.");

        var normalized = path.Trim().Replace('\\', '/');
        if (Path.IsPathRooted(normalized) || WindowsRootedPath.IsMatch(normalized) || UnsafeSegment.IsMatch(normalized))
            throw new ArtifactIngestException($"Artifact path must stay under results/: {path}");

        normalized = normalized.StartsWith("results/", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : "results/" + normalized.TrimStart('/');

        if (string.Equals(normalized, "results/", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("/", StringComparison.Ordinal))
        {
            throw new ArtifactIngestException($"Artifact path must name a file: {path}");
        }

        return normalized;
    }

    private static TaskInfo? ResolveTask(ITaskScanner scanner, string taskKey)
    {
        if (string.IsNullOrWhiteSpace(taskKey)) return null;
        return scanner.ScanAllJobs().FirstOrDefault(t =>
            string.Equals(t.TaskKey, taskKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(t.Id, taskKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(t.Key, taskKey, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed record ArtifactRequestLimits(long MaxRequestBodyBytes);

public static class ArtifactTransferPolicy
{
    public const long DefaultMaxFileBytes = 8L * 1024 * 1024;
    public const long DefaultMaxTotalBytes = 100L * 1024 * 1024;
    private const long JsonEnvelopeReserveBytes = 64L * 1024;

    public static long DecodedLength(string? contentBase64)
    {
        if (string.IsNullOrEmpty(contentBase64)) return 0;
        var padding = contentBase64.EndsWith("==", StringComparison.Ordinal) ? 2
            : contentBase64.EndsWith('=') ? 1 : 0;
        return Math.Max(0, contentBase64.Length / 4L * 3 - padding);
    }

    public static ArtifactTransferLimitsResponse Resolve(
        long maxRequestBodyBytes,
        long? projectMaxFileBytes,
        long? projectMaxTotalBytes)
    {
        var requestBudget = Math.Max(1, maxRequestBodyBytes);
        // Base64 expands by 4/3. Reserve bounded JSON and path metadata before
        // advertising a raw-byte ceiling to the runner.
        var requestSafeRawBytes = Math.Max(
            1,
            (requestBudget - Math.Min(JsonEnvelopeReserveBytes, requestBudget / 4)) / 4 * 3);
        var configuredFileBytes = projectMaxFileBytes is > 0
            ? projectMaxFileBytes.Value
            : DefaultMaxFileBytes;
        var maxTotalBytes = projectMaxTotalBytes is > 0
            ? projectMaxTotalBytes.Value
            : DefaultMaxTotalBytes;
        var maxFileBytes = Math.Min(Math.Min(configuredFileBytes, requestSafeRawBytes), maxTotalBytes);
        return new ArtifactTransferLimitsResponse(requestBudget, maxFileBytes, maxTotalBytes);
    }

    public static string BoardFact(ArtifactTransferIssue issue)
        => $"result artifact {Path.GetFileName(issue.Path)} {Megabytes(issue.SizeBytes)} MB "
           + $"{issue.Reason}; not transferred";

    private static string Megabytes(long bytes)
        => Math.Round(bytes / 1024d / 1024d, 1, MidpointRounding.AwayFromZero)
            .ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
}

internal sealed class ArtifactIngestException : Exception
{
    public ArtifactIngestException(string message) : base(message) { }
}
