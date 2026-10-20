namespace AgentStudio.Diagnostics;

public sealed record RunnerEventIngestRequest(
    string TaskKey,
    string Kind,
    DateTime Timestamp,
    string? Message,
    string? RunnerId = null,
    string? LeaseId = null,
    long FencingToken = 0,
    string? EventId = null,
    string? SessionId = null,
    string? TurnId = null,
    int? RunIndex = null,
    string? Cli = null,
    string? Model = null,
    string? ThinkingLevel = null,
    long? DurationMs = null,
    long? InputTokens = null,
    long? OutputTokens = null,
    long? ReasoningTokens = null,
    string? Severity = null,
    string? Code = null,
    string? ImplementationStatus = null,
    string? PipelineStatus = null);

public static class RunnerEventIngestionEndpoints
{
    public static void MapRunnerEventIngestionEndpoints(this WebApplication app)
    {
        app.MapPost("/api/runner/events", async (
            RunnerEventIngestRequest request,
            HttpContext context,
            AccessSecurityStore security,
            RunLeaseService leases,
            ITaskScanner scanner,
            RunnerEventJournal journal,
            AgentStudio.Secrets.SecretValueScrubber secretValues,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.TaskKey) || string.IsNullOrWhiteSpace(request.Kind))
                return Results.BadRequest(new { error = "taskKey-and-kind-required" });
            var kind = AgentStudio.Projection.RunnerEventSource.NormalizeKind(request.Kind);
            if (kind is null)
                return Results.BadRequest(new { error = "unsupported-runner-event-kind" });

            var runner = context.Items[AccessSecurityMiddleware.RunnerPrincipalItem] as RunnerPrincipal;
            if (runner is not null && !RunnerLeaseAuthorization.IsCurrent(
                    context, leases, request.TaskKey, request.RunnerId, request.LeaseId, request.FencingToken))
                return Results.Conflict(new { error = "stale-runner-lease" });

            var task = scanner.ScanAllJobs().FirstOrDefault(candidate =>
                string.Equals(candidate.TaskKey, request.TaskKey, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.Key, request.TaskKey, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.Id, request.TaskKey, StringComparison.OrdinalIgnoreCase));
            if (task is null) return Results.NotFound(new { error = "task-not-found" });

            var timestamp = request.Timestamp == default ? DateTime.UtcNow : request.Timestamp.ToUniversalTime();
            var recorded = new RunnerRecordedEvent
            {
                Id = string.IsNullOrWhiteSpace(request.EventId)
                    ? $"runner:{timestamp:O}:{Guid.NewGuid():N}"
                    : request.EventId.Trim(),
                Kind = kind,
                Timestamp = timestamp,
                SessionId = request.SessionId,
                TurnId = request.TurnId,
                RunIndex = request.RunIndex,
                Cli = request.Cli,
                Model = request.Model,
                ThinkingLevel = request.ThinkingLevel,
                DurationMs = request.DurationMs,
                InputTokens = request.InputTokens,
                OutputTokens = request.OutputTokens,
                ReasoningTokens = request.ReasoningTokens,
                Severity = request.Severity,
                Code = request.Code,
                Message = request.Message is null ? null : secretValues.Scrub(request.Message),
                ImplementationStatus = request.ImplementationStatus,
                PipelineStatus = request.PipelineStatus,
            };
            await journal.AppendAsync(task, recorded, ct);

            if (runner is not null)
            {
                security.AppendRunAudit(new RunSecurityAuditEvent(
                    timestamp,
                    "event:" + CredentialRedactor.Redact(kind).Trim(), request.TaskKey, task.ProjectName,
                    string.IsNullOrWhiteSpace(task.OwnerClientId) ? "automation:unknown" : task.OwnerClientId,
                    runner.RunnerId, runner.CredentialId,
                    Outcome: secretValues.Scrub(CredentialRedactor.Redact(request.Message))));
            }
            return Results.Accepted();
        });
    }
}
