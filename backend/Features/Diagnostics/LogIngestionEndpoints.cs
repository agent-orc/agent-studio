namespace AgentStudio.Diagnostics;

/// <summary>
/// Runner → Server log-ingestion API under <c>/api/runner/logs</c> (Step 6).
/// A Runner — local today, remote tomorrow — ships the output lines it produced
/// for a task and the server appends them to that task's durable
/// <c>logs/cli-output.log</c>, in the same <c>[HH:mm:ss.fff] [stream] text</c>
/// format the in-process consolidation writes, so the existing projection and
/// the ~7 durable-log readers consume it unchanged.
///
/// <para>
/// File-backed by design: the durable log is the source of truth for history
/// and cross-machine reads, while a run in progress is still read locally and
/// directly (see RunLogStore / per-stream files). The append is FileShare-safe
/// so a concurrent reader never trips the Windows "file in use" failure.
/// </para>
/// </summary>
public static class LogIngestionEndpoints
{
    internal const int CliOutputLogCapBytes = CliOutputLogFile.MaxBytes;

    public static void MapLogIngestionEndpoints(this WebApplication app)
    {
        app.MapPost("/api/runner/logs", (
            LogIngestRequest req,
            HttpContext context,
            ITaskScanner scanner,
            RunLeaseService leases,
            AttemptAuthorityService authority,
            AgentStudio.Docs.WikiAgentReadService wikiReads,
            AgentStudio.Secrets.SecretValueScrubber secretValues) =>
        {
            if (!RunnerLeaseAuthorization.IsCurrent(context, leases, req.TaskKey, req.RunnerId, req.LeaseId, req.FencingToken))
                return Results.Conflict(new LogIngestResponse(req.TaskKey, 0, "The authenticated Runner does not hold the current fenced lease."));
            if (req.Lines is null || req.Lines.Count == 0)
                return Results.Ok(new LogIngestResponse(req.TaskKey, 0, "no lines"));

            var folder = ResolveFolder(scanner, req.TaskKey);
            if (folder is null)
                return Results.NotFound(new LogIngestResponse(req.TaskKey, 0, $"No task '{req.TaskKey}'."));

            var logsDir = Path.Combine(folder, "logs");
            var logPath = Path.Combine(logsDir, "cli-output.log");

            var rendered = string.Join(Environment.NewLine,
                req.Lines.Select(l => $"[{l.Timestamp:HH:mm:ss.fff}] [{l.Stream}] {secretValues.Scrub(CredentialRedactor.Redact(AnsiText.Strip(l.Text)))}"));
            var deliveryReceipt = DeliveryReceipt(req);

            try
            {
                void Append()
                {
                    Directory.CreateDirectory(logsDir);
                    var receiptLine = deliveryReceipt is null
                        ? string.Empty
                        : Environment.NewLine
                          + $"[{req.Lines[^1].Timestamp:HH:mm:ss.fff}] [system] {deliveryReceipt}";
                    AppendBounded(
                        logPath,
                        rendered + receiptLine,
                        deliveryReceipt,
                        req.Lines[^1].Timestamp);
                }

                var authorityResult = Authorize(req, authority, Append);
                if (authorityResult is not null)
                {
                    if (authorityResult.Status == AttemptWriteStatus.Duplicate)
                        return Results.Ok(new LogIngestResponse(req.TaskKey, 0, "duplicate delivery"));
                    if (authorityResult.Status != AttemptWriteStatus.Accepted)
                        return Results.Conflict(authorityResult);
                }
                else
                {
                    Append();
                }
            }
            catch (Exception ex)
            {
                return Results.Problem(CredentialRedactor.Redact($"Failed to ingest logs for '{req.TaskKey}': {ex.Message}"));
            }

            // Remote runs do not flow through the in-process CliRouter. Attribute
            // their tool-use lines only after the durable, fenced append succeeds.
            try
            {
                wikiReads.ProcessOutput(req.TaskKey, req.Lines.Select(line => new CliOutputLine
                {
                    Timestamp = line.Timestamp,
                    Stream = line.Stream,
                    Text = line.Text,
                }));
            }
            catch (Exception ex)
            {
                SilentCatch.Note(ex, "WikiAgentReadService: remote log attribution failed.");
            }

            return Results.Ok(new LogIngestResponse(req.TaskKey, req.Lines.Count));
        });
    }

    private static AttemptWriteResult? Authorize(
        LogIngestRequest req,
        AttemptAuthorityService authority,
        Action append)
    {
        if (string.IsNullOrWhiteSpace(req.AttemptId) || !req.Fence.HasValue
            || !req.AuthorityEpoch.HasValue || string.IsNullOrWhiteSpace(req.IdempotencyKey))
        {
            // Log ingest is best-effort diagnostic, not an authoritative write: append even
            // without canonical write authority so a runner on an older agent-host protocol
            // (no AttemptId/Fence/AuthorityEpoch/IdempotencyKey) does not 409-storm and stall
            // its runs. Durable authoritative state lives elsewhere; a log line needs no fencing.
            return null;
        }
        return authority.ExecuteRunWrite(
            new AttemptWriteReference(
                req.AttemptId, req.Fence.Value, req.AuthorityEpoch.Value, req.IdempotencyKey),
            "log",
            req.TaskKey,
            append);
    }

    private static string? DeliveryReceipt(LogIngestRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.AttemptId)
            || string.IsNullOrWhiteSpace(req.IdempotencyKey))
        {
            return null;
        }

        var digest = AttemptAuthorityService.Hash(
            $"log\n{req.AttemptId.Trim()}\n{req.IdempotencyKey.Trim()}");
        return $"[runner-log-delivery:{digest}]";
    }

    /// <summary>
    /// Compatibility seam for the remote-ingestion tests. All durable CLI-log
    /// writers now share <see cref="CliOutputLogFile"/> so local and remote
    /// output obey the same cap and rotation contract.
    /// </summary>
    internal static bool AppendBounded(
        string logPath,
        string renderedPayload,
        string? deliveryReceipt,
        DateTime markerTimestamp)
        => CliOutputLogFile.Append(
            logPath,
            renderedPayload,
            markerTimestamp,
            deliveryReceipt);

    private static string? ResolveFolder(ITaskScanner scanner, string taskKey)
    {
        if (string.IsNullOrWhiteSpace(taskKey)) return null;
        var task = scanner.ScanAllJobs().FirstOrDefault(t =>
            string.Equals(t.TaskKey, taskKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(t.Id, taskKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(t.Key, taskKey, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(task?.FolderPath) ? null : task!.FolderPath;
    }
}
