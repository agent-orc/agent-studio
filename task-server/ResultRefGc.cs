using System.Diagnostics;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Options;

namespace AgentStudio.TaskServer;

public enum ResultRefGcAction
{
    Deleted,
    Spared,
    Failed,
}

public sealed record ResultRefGcDecision(
    string RunId,
    string TaskKey,
    string RepositoryId,
    string ImmutableRemoteRef,
    ResultRefGcAction Action,
    string Reason);

public sealed record ResultRefGcSweepResult(
    DateTime SweptAt,
    IReadOnlyList<ResultRefGcDecision> Decisions)
{
    public int Deleted => Decisions.Count(item => item.Action == ResultRefGcAction.Deleted);
    public int Spared => Decisions.Count(item => item.Action == ResultRefGcAction.Spared);
    public int Failed => Decisions.Count(item => item.Action == ResultRefGcAction.Failed);
}

public sealed record ResultRefDeleteResult(bool Success, string? Error = null);

public interface IResultRefDeleter
{
    Task<ResultRefDeleteResult> DeleteAsync(
        string repositoryUrl,
        string immutableRemoteRef,
        CancellationToken ct);
}

/// <summary>
/// Deletes one fully-qualified immutable result ref without invoking a shell.
/// The repository URL is deliberately never included in errors because it may
/// contain credentials.
/// </summary>
public sealed class GitResultRefDeleter(
    IOptions<TaskServerOptions> options) : IResultRefDeleter
{
    public async Task<ResultRefDeleteResult> DeleteAsync(
        string repositoryUrl,
        string immutableRemoteRef,
        CancellationToken ct)
    {
        var configured = options.Value;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(
            Math.Clamp(configured.ResultRefGcDeleteTimeoutSeconds, 5, 300)));

        var start = new ProcessStartInfo
        {
            FileName = string.IsNullOrWhiteSpace(configured.GitCommand)
                ? "git"
                : configured.GitCommand.Trim(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("push");
        start.ArgumentList.Add("--porcelain");
        start.ArgumentList.Add("--");
        start.ArgumentList.Add(repositoryUrl);
        start.ArgumentList.Add($":{immutableRemoteRef}");

        using var process = new Process { StartInfo = start };
        Task<string>? stdoutTask = null;
        Task<string>? stderrTask = null;
        try
        {
            if (!process.Start())
                return new ResultRefDeleteResult(false, "git process did not start");

            stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = OneLine(await stdoutTask);
            var error = OneLine(await stderrTask);
            if (process.ExitCode == 0)
                return new ResultRefDeleteResult(true);

            return new ResultRefDeleteResult(
                false,
                $"git exited {process.ExitCode}: {RedactRepositoryUrl(
                    FirstNonBlank(error, output, "no diagnostic output"),
                    repositoryUrl)}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await TerminateAndDrainAsync(process, stdoutTask, stderrTask);
            return new ResultRefDeleteResult(false, "git deletion timed out");
        }
        catch (OperationCanceledException)
        {
            await TerminateAndDrainAsync(process, stdoutTask, stderrTask);
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await TerminateAndDrainAsync(process, stdoutTask, stderrTask);
            return new ResultRefDeleteResult(
                false,
                RedactRepositoryUrl(OneLine(exception.Message), repositoryUrl));
        }
    }

    private static string FirstNonBlank(params string[] values)
        => values.First(value => !string.IsNullOrWhiteSpace(value));

    private static string OneLine(string value)
        => string.Join(
            " ",
            value.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Trim();

    private static string RedactRepositoryUrl(string value, string repositoryUrl)
        => value.Replace(
            repositoryUrl,
            "[repository]",
            StringComparison.Ordinal);

    private static void TryKill(Process process)
    {
        try
        {
            if (process.StartInfo is not null && !process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort only. The timeout is already the authoritative error.
        }
    }

    private static async Task TerminateAndDrainAsync(
        Process process,
        Task<string>? stdout,
        Task<string>? stderr)
    {
        TryKill(process);
        var drains = new List<Task>(3);
        try { drains.Add(process.WaitForExitAsync(CancellationToken.None)); }
        catch { /* A start failure has no process to wait for. */ }
        if (stdout is not null) drains.Add(stdout);
        if (stderr is not null) drains.Add(stderr);
        if (drains.Count == 0) return;
        try { await Task.WhenAll(drains).WaitAsync(TimeSpan.FromSeconds(3)); }
        catch { /* Bounded cleanup is best effort after the authoritative failure. */ }
    }
}

public sealed partial class TaskServerStore
{
    /// <summary>
    /// Applies the immutable result-ref retention policy once. Selection is
    /// fail-closed: a ref is eligible only after retention, accepted/archive,
    /// a terminal non-infrastructure review, and supersession by a newer
    /// result-bearing RunAttempt. The current review subject is therefore
    /// never deleted.
    /// </summary>
    public async Task<ResultRefGcSweepResult> SweepResultRefsAsync(
        IResultRefDeleter deleter,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(deleter);
        if (!AuthorityReady || _mode is TaskServerMode.ReadOnly or TaskServerMode.Maintenance)
            return new ResultRefGcSweepResult(UtcNow, []);

        var sweptAt = UtcNow;
        var inventory = await ReadResultRefGcInventoryAsync(ct);
        var decisions = new List<ResultRefGcDecision>(inventory.Count);
        var candidates = new List<ResultRefGcInventoryRow>();

        foreach (var item in inventory)
        {
            var sparedReason = SparedReason(item, sweptAt);
            if (sparedReason is null)
            {
                candidates.Add(item);
                continue;
            }

            decisions.Add(ToDecision(
                item,
                ResultRefGcAction.Spared,
                sparedReason));
        }

        foreach (var item in candidates.Take(
                     Math.Clamp(_options.ResultRefGcBatchSize, 1, 500)))
        {
            ct.ThrowIfCancellationRequested();
            var deleted = await deleter.DeleteAsync(
                item.RepositoryUrl!,
                item.ImmutableRemoteRef,
                ct);
            var action = deleted.Success
                ? ResultRefGcAction.Deleted
                : ResultRefGcAction.Failed;
            var reason = deleted.Success
                ? "retention-expired"
                : NormalizeGcError(deleted.Error);
            await RecordResultRefGcAttemptAsync(item, action, reason, sweptAt, ct);
            decisions.Add(ToDecision(item, action, reason));
        }

        foreach (var deferred in candidates.Skip(
                     Math.Clamp(_options.ResultRefGcBatchSize, 1, 500)))
        {
            decisions.Add(ToDecision(
                deferred,
                ResultRefGcAction.Spared,
                "batch-limit"));
        }

        return new ResultRefGcSweepResult(sweptAt, decisions);
    }

    private async Task<IReadOnlyList<ResultRefGcInventoryRow>> ReadResultRefGcInventoryAsync(
        CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT h.run_id,
                   t.task_key,
                   h.repository_id,
                   h.repository_url,
                   h.immutable_remote_ref,
                   h.result_sha,
                   h.fence,
                   h.retain_until,
                   t.state,
                   h.run_id = (
                       SELECT current_run.id
                         FROM runs current_run
                         JOIN result_handoffs current_handoff
                           ON current_handoff.run_id = current_run.id
                        WHERE current_run.task_id = h.task_id
                        ORDER BY current_run.created_at DESC, current_run.rowid DESC
                        LIMIT 1
                   ) AS is_current_attempt,
                   EXISTS (
                       SELECT 1
                         FROM review_subjects subject
                         JOIN review_attempts attempt
                           ON attempt.subject_id = subject.id
                        WHERE subject.source_run_id = h.run_id
                          AND attempt.reported_at IS NOT NULL
                          AND attempt.outcome IN ('Pass', 'PassWithConcerns', 'ProductFailure')
                   ) AS has_terminal_review,
                   EXISTS (
                       SELECT 1
                         FROM review_subjects subject
                         JOIN review_attempts attempt
                           ON attempt.subject_id = subject.id
                        WHERE subject.source_run_id = h.run_id
                          AND attempt.status IN ('queued', 'leased', 'process-unknown')
                   ) AS has_active_review
              FROM result_handoffs h
              JOIN tasks t ON t.id = h.task_id
              LEFT JOIN result_ref_gc gc ON gc.run_id = h.run_id
             WHERE h.immutable_remote_ref IS NOT NULL
               AND gc.deleted_at IS NULL
             ORDER BY h.retain_until, h.run_id;
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<ResultRefGcInventoryRow>();
        while (await reader.ReadAsync(ct))
        {
            result.Add(new ResultRefGcInventoryRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetInt64(6),
                Parse(reader.GetString(7)),
                reader.GetString(8),
                reader.GetBoolean(9),
                reader.GetBoolean(10),
                reader.GetBoolean(11)));
        }
        return result;
    }

    private async Task RecordResultRefGcAttemptAsync(
        ResultRefGcInventoryRow item,
        ResultRefGcAction action,
        string reason,
        DateTime attemptedAt,
        CancellationToken ct)
    {
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            await ExecuteAsync(connection, """
                INSERT INTO result_ref_gc(
                    run_id, immutable_remote_ref, status, attempted_at,
                    deleted_at, last_error)
                VALUES (
                    $run, $ref, $status, $attempted,
                    $deleted, $error)
                ON CONFLICT(run_id) DO UPDATE SET
                    immutable_remote_ref = excluded.immutable_remote_ref,
                    status = excluded.status,
                    attempted_at = excluded.attempted_at,
                    deleted_at = coalesce(result_ref_gc.deleted_at, excluded.deleted_at),
                    last_error = excluded.last_error;
                """, ct, transaction,
                ("$run", item.RunId),
                ("$ref", item.ImmutableRemoteRef),
                ("$status", action.ToString().ToLowerInvariant()),
                ("$attempted", Iso(attemptedAt)),
                ("$deleted", action == ResultRefGcAction.Deleted
                    ? Iso(attemptedAt)
                    : null),
                ("$error", action == ResultRefGcAction.Failed
                    ? reason
                    : null));
            await AuditAsync(
                connection,
                transaction,
                "result-ref-gc",
                $"result-ref-gc.{action.ToString().ToLowerInvariant()}",
                "run",
                item.RunId,
                JsonSerializer.Serialize(new
                {
                    item.TaskKey,
                    item.RepositoryId,
                    item.ImmutableRemoteRef,
                    action = action.ToString().ToLowerInvariant(),
                    reason,
                }),
                ct);
        }, ct);
    }

    private static string? SparedReason(
        ResultRefGcInventoryRow item,
        DateTime now)
    {
        if (!ValidImmutableResultRef(
                item.ImmutableRemoteRef,
                item.RunId,
                item.Fence,
                item.ResultSha))
            return "invalid-result-ref";
        if (item.IsCurrentAttempt)
            return "current-attempt";
        if (item.State is not ("6-completed" or "7-archive"))
            return "card-not-accepted";
        if (item.HasActiveReview)
            return "review-active";
        if (!item.HasTerminalReview)
            return "review-not-terminal";
        if (item.RetainUntil > now)
            return "retention-window";
        if (string.IsNullOrWhiteSpace(item.RepositoryUrl))
            return "repository-url-missing";
        return null;
    }

    private static bool ValidImmutableResultRef(
        string value,
        string runId,
        long fence,
        string resultSha)
        => string.Equals(
            value,
            FencedGitRefs.ImmutableResult(runId, fence, resultSha),
            StringComparison.Ordinal);

    private static ResultRefGcDecision ToDecision(
        ResultRefGcInventoryRow item,
        ResultRefGcAction action,
        string reason)
        => new(
            item.RunId,
            item.TaskKey,
            item.RepositoryId,
            item.ImmutableRemoteRef,
            action,
            reason);

    private static string NormalizeGcError(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? "git deletion failed"
            : string.Join(
                " ",
                value.Split(
                    ['\r', '\n'],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return normalized.Length <= 1000 ? normalized : normalized[..1000];
    }

    private sealed record ResultRefGcInventoryRow(
        string RunId,
        string TaskKey,
        string RepositoryId,
        string? RepositoryUrl,
        string ImmutableRemoteRef,
        string ResultSha,
        long Fence,
        DateTime RetainUntil,
        string State,
        bool IsCurrentAttempt,
        bool HasTerminalReview,
        bool HasActiveReview);
}

public sealed class ResultRefGcHostedService(
    TaskServerStore store,
    IResultRefDeleter deleter,
    IOptions<TaskServerOptions> options,
    TaskServerStartupExecutionAdmission executionAdmission,
    ILogger<ResultRefGcHostedService> logger) : BackgroundService
{
    public bool ExecutionSuppressed => executionAdmission.IsPublicDemo;

    public async Task<ResultRefGcSweepResult> RunOnceAsync(
        CancellationToken ct = default)
    {
        if (ExecutionSuppressed)
            return new ResultRefGcSweepResult(DateTime.UtcNow, []);

        var result = await store.SweepResultRefsAsync(deleter, ct);
        foreach (var item in result.Decisions)
        {
            logger.LogInformation(
                "result-ref-gc action={Action} reason={Reason} ref={ResultRef} run={RunId} task={TaskKey} repository={RepositoryId}",
                item.Action.ToString().ToLowerInvariant(),
                item.Reason,
                item.ImmutableRemoteRef,
                item.RunId,
                item.TaskKey,
                item.RepositoryId);
        }
        logger.LogInformation(
            "result-ref-gc sweep-completed deleted={Deleted} spared={Spared} failed={Failed}",
            result.Deleted,
            result.Spared,
            result.Failed);
        return result;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (ExecutionSuppressed) return;

        if (!options.Value.ResultRefGcEnabled)
        {
            logger.LogInformation("result-ref-gc disabled");
            return;
        }

        var interval = TimeSpan.FromMinutes(
            Math.Clamp(options.Value.ResultRefGcSweepMinutes, 5, 7 * 24 * 60));
        using var timer = new PeriodicTimer(interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "result-ref-gc sweep-failed");
            }

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
