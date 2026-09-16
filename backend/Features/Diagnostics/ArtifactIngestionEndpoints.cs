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
        app.MapPost("/api/runner/artifacts", async (
            ArtifactIngestRequest req,
            HttpContext context,
            ITaskScanner scanner,
            RunLeaseService leases,
            AttemptAuthorityService authority,
            WorkspaceArtifactCommitService artifactCommits,
            RemoteResultFinalizationService finalization,
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

internal sealed class ArtifactIngestException : Exception
{
    public ArtifactIngestException(string message) : base(message) { }
}
