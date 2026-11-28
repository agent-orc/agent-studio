using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

public sealed partial class TaskServerStore
{
    private static readonly IReadOnlyList<InvariantDefinitionDto> InvariantDefinitions =
    [
        new(
            "run-inventory",
            "run-inventory",
            "Runner process inventory and active leases",
            "A process has no lease, or a lease has no process after grace.",
            "Terminate the orphan process; record lease loss for the deployed backend requeue authority."),
        new(
            "worktree-hygiene",
            "worktree-hygiene",
            "Runner child cwd and host filesystem",
            "A tracked child cwd is marked deleted.",
            "Terminate the process tree."),
        new(
            "load-gate",
            "load-invariant",
            "One-minute host load and logical core count",
            "Normalized load remains above the configured threshold.",
            "Stop new claims while existing runs continue."),
        new(
            "lane-process-consistency",
            "lane-process-consistency",
            "Progress lane and active run authority",
            "A Progress task has no active run heartbeat after grace.",
            "Emit the Tranche 0 violation and retain the lane for the deployed backend requeue authority."),
    ];

    public async Task<InvariantRegistryDto> GetInvariantRegistryAsync(CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        var pending = Convert.ToInt32(await ScalarAsync(
            connection,
            "SELECT count(*) FROM runner_reconciliation_actions;",
            ct) ?? 0L,
            CultureInfo.InvariantCulture);
        await using var command = Command(connection, """
            SELECT sequence, occurred_at, actor_id, action, target_type, target_id, detail_json
              FROM audit
             WHERE action LIKE 'invariant.%'
             ORDER BY sequence DESC
             LIMIT 100;
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var recent = new List<AuditRecordDto>();
        while (await reader.ReadAsync(ct))
            recent.Add(new AuditRecordDto(
                reader.GetInt64(0),
                Parse(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6)));
        return new InvariantRegistryDto(InvariantDefinitions, recent, pending);
    }

/// <summary>
/// Tranche 0 reconciliation for the versioned Task Server. Runner-local orphan
/// termination is actionable here, while lease and lane mismatches are recorded
/// without requeueing. The deployed backend remains the sole requeue authority.
/// </summary>
    public async Task<int> ReconcileInvariantsAsync(CancellationToken ct)
    {
        if (!AuthorityReady || _mode is TaskServerMode.ReadOnly or TaskServerMode.Maintenance)
            return 0;
        var repairs = 0;
        await InWriteTransactionAsync(async (connection, transaction) =>
        {
            var now = UtcNow;
            var cutoff = InventoryCutoff(now);
            var inventories = await ReadFreshInventoriesAsync(connection, transaction, cutoff, ct);
            var leases = await ReadOwnedLeaseFactsAsync(connection, transaction, ct);
            var ownedRuns = leases.Select(lease => lease.RunId).ToHashSet(StringComparer.Ordinal);

            foreach (var inventory in inventories)
            {
                foreach (var process in inventory.Snapshot.Processes.Where(process =>
                             process.Pid > 0 && process.StartedAt <= cutoff))
                {
                    if (ownedRuns.Contains(process.RunId)) continue;
                    var actionId = OrphanActionId(inventory.RunnerId, inventory.InstanceId, process);
                    var inserted = await ExecuteAsync(connection, """
                        INSERT INTO runner_reconciliation_actions(
                            action_id, runner_id, instance_id, category, action,
                            detail, pid, run_id, task_key, created_at)
                        VALUES ($id, $runner, $instance, 'run-inventory',
                                'terminate-process', $detail, $pid, $run, $task, $at)
                        ON CONFLICT(action_id) DO NOTHING;
                        """, ct, transaction,
                        ("$id", actionId),
                        ("$runner", inventory.RunnerId),
                        ("$instance", inventory.InstanceId),
                        ("$detail", $"Process {process.Pid} for run '{process.RunId}' has no owning lease."),
                        ("$pid", process.Pid),
                        ("$run", process.RunId),
                        ("$task", process.TaskKey),
                        ("$at", Iso(now)));
                    if (inserted == 0) continue;
                    await AppendInvariantEventAsync(
                        connection, transaction, actionId, process.RunId,
                        process.TaskKey, "invariant.orphan-process",
                        "run-inventory", "terminate-process",
                        $"Process {process.Pid} had no owning lease and was scheduled for termination.",
                        ct);
                    await AuditAsync(
                        connection, transaction, "invariant-reconciler",
                        "invariant.orphan-process", "runner", inventory.RunnerId,
                        JsonSerializer.Serialize(new
                        {
                            category = "run-inventory",
                            process.RunId,
                            process.TaskKey,
                            process.Pid,
                            selfHealingAction = "terminate-process",
                            actionId,
                        }),
                        ct);
                    repairs++;
                }
            }

            foreach (var lease in leases.Where(lease => lease.IsActive))
            {
                var inventory = inventories.FirstOrDefault(item =>
                    string.Equals(item.RunnerId, lease.RunnerId, StringComparison.Ordinal)
                    && string.Equals(item.InstanceId, lease.InstanceId, StringComparison.Ordinal));
                if (inventory is null || lease.AcquiredAt > cutoff) continue;
                if (inventory.Snapshot.Processes.Any(process =>
                        string.Equals(process.RunId, lease.RunId, StringComparison.Ordinal)))
                    continue;

                var eventId = StableInvariantId("dead-run", lease.RunId, lease.TaskId);
                var alreadyReported = Convert.ToInt64(await ScalarAsync(
                    connection,
                    "SELECT count(*) FROM events WHERE idempotency_key = $key;",
                    ct,
                    transaction,
                    ("$key", $"invariant:{eventId}")) ?? 0L,
                    CultureInfo.InvariantCulture) > 0;
                if (alreadyReported) continue;
                await AppendInvariantEventAsync(
                    connection, transaction, eventId, lease.RunId,
                    lease.TaskKey, "invariant.lease-without-process",
                    "run-inventory", "backend-authority",
                    "The lease had no matching runner process after grace. Tranche 0 recorded the mismatch without requeueing because the deployed backend owns requeue authority.",
                    ct);
                await AuditAsync(
                    connection, transaction, "invariant-reconciler",
                    "invariant.lease-without-process", "run", lease.RunId,
                    JsonSerializer.Serialize(new
                        {
                            category = "run-inventory",
                            lease.TaskKey,
                            lease.RunnerId,
                            selfHealingAction = "backend-authority",
                            tranche = 0,
                        }),
                    ct);
                repairs++;
            }

            var stranded = new List<(string TaskId, string TaskKey, bool ProcessUnknown)>();
            await using (var command = Command(connection, """
                SELECT t.id, t.task_key,
                       EXISTS (
                           SELECT 1 FROM leases u
                            WHERE u.task_id = t.id AND u.status = 'process-unknown')
                  FROM tasks t
                 WHERE t.state = '3-progress'
                   AND t.updated_at <= $cutoff
                   AND NOT EXISTS (
                       SELECT 1 FROM leases l
                        WHERE l.task_id = t.id AND l.status = 'active');
                """, transaction, ("$cutoff", Iso(cutoff))))
            await using (var reader = await command.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct))
                    stranded.Add((
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetBoolean(2)));

            foreach (var task in stranded)
            {
                var eventId = StableInvariantId("lane-process", task.TaskId);
                var alreadyReported = Convert.ToInt64(await ScalarAsync(
                    connection,
                    "SELECT count(*) FROM events WHERE idempotency_key = $key;",
                    ct,
                    transaction,
                    ("$key", $"invariant:{eventId}")) ?? 0L,
                    CultureInfo.InvariantCulture) > 0;
                if (alreadyReported) continue;

                const string action = "containment-required";
                var detail = task.ProcessUnknown
                    ? "The Progress card had no active run heartbeat after grace; unresolved process authority was retained for explicit containment."
                    : "The Progress card had no active run heartbeat after grace. Tranche 0 retained the lane and delegated requeue to the deployed backend authority.";
                await AppendInvariantEventAsync(
                    connection, transaction, eventId, string.Empty,
                    task.TaskKey, "invariant.lane-process-consistency",
                    "lane-process-consistency", action, detail,
                    ct);
                await AuditAsync(
                    connection, transaction, "invariant-reconciler",
                    "invariant.lane-process-consistency", "task", task.TaskId,
                    JsonSerializer.Serialize(new
                        {
                            category = "lane-process-consistency",
                            task.TaskKey,
                            selfHealingAction = action,
                            tranche = 0,
                        }),
                        ct);
                repairs++;
            }
        }, ct);
        return repairs;
    }

    private async Task RecordRunnerInventoryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runnerId,
        string instanceId,
        RunnerProcessInventory? inventory,
        string actorId,
        CancellationToken ct)
    {
        if (inventory is null) return;
        var receivedAt = UtcNow;
        var observedAt = inventory.ObservedAt.ToUniversalTime();
        if (observedAt > receivedAt.AddMinutes(5))
            throw new ArgumentException("Runner inventory timestamp is in the future.");

        foreach (var actionId in inventory.AcknowledgedActionIds ?? [])
        {
            await ExecuteAsync(connection, """
                DELETE FROM runner_reconciliation_actions
                 WHERE action_id = $id AND runner_id = $runner AND instance_id = $instance;
                """, ct, transaction,
                ("$id", actionId),
                ("$runner", runnerId),
                ("$instance", instanceId));
        }

        await ExecuteAsync(connection, """
            INSERT INTO runner_inventories(runner_id, instance_id, observed_at, snapshot_json)
            VALUES ($runner, $instance, $observed, $snapshot)
            ON CONFLICT(runner_id, instance_id) DO UPDATE SET
                observed_at = excluded.observed_at,
                snapshot_json = excluded.snapshot_json
            WHERE excluded.observed_at >= runner_inventories.observed_at;
            """, ct, transaction,
            ("$runner", runnerId),
            ("$instance", instanceId),
            // Reconciliation freshness follows server receipt time. The runner's
            // observation time remains in snapshot_json for diagnosis.
            ("$observed", Iso(receivedAt)),
            ("$snapshot", JsonSerializer.Serialize(inventory)));

        foreach (var report in inventory.Reports ?? [])
        {
            if (string.IsNullOrWhiteSpace(report.ReportId)
                || string.IsNullOrWhiteSpace(report.Category))
                continue;
            var inserted = await ExecuteAsync(connection, """
                INSERT INTO invariant_reports(
                    report_id, runner_id, instance_id, category, detected_at, action, detail)
                VALUES ($id, $runner, $instance, $category, $detected, $action, $detail)
                ON CONFLICT(report_id) DO NOTHING;
                """, ct, transaction,
                ("$id", report.ReportId),
                ("$runner", runnerId),
                ("$instance", instanceId),
                ("$category", report.Category),
                ("$detected", Iso(report.DetectedAt)),
                ("$action", report.Action),
                ("$detail", report.Detail));
            if (inserted == 0) continue;
            await AppendInvariantEventAsync(
                connection, transaction, report.ReportId,
                report.RunId ?? string.Empty, report.TaskKey,
                $"invariant.{report.Category}", report.Category,
                report.Action, report.Detail, ct);
            await AuditAsync(
                connection, transaction, actorId,
                $"invariant.{report.Category}", "runner", runnerId,
                JsonSerializer.Serialize(new
                {
                    report.ReportId,
                    report.Category,
                    report.Action,
                    report.Detail,
                    report.RunId,
                    report.TaskKey,
                    report.Pid,
                }),
                ct);
        }
    }

    private async Task<IReadOnlyList<RunnerReconciliationAction>> ReadPendingReconciliationActionsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runnerId,
        string instanceId,
        CancellationToken ct)
    {
        var actions = new List<RunnerReconciliationAction>();
        var retired = new List<string>();
        var cutoff = InventoryCutoff(UtcNow);
        RunnerProcessInventory? inventory = null;
        await using (var command = Command(connection, """
            SELECT a.action_id, a.category, a.action, a.detail, a.pid, a.run_id, a.task_key,
                   CASE WHEN a.action = 'terminate-process' AND NOT EXISTS (
                       SELECT 1 FROM leases l
                        WHERE l.run_id = a.run_id AND l.status IN ('active', 'process-unknown')
                   ) THEN i.snapshot_json END AS orphan_snapshot
              FROM runner_reconciliation_actions a
              LEFT JOIN runner_inventories i
                ON i.runner_id = a.runner_id AND i.instance_id = a.instance_id AND i.observed_at >= $cutoff
             WHERE a.runner_id = $runner AND a.instance_id = $instance
             ORDER BY a.created_at, a.action_id;
            """, transaction,
            ("$runner", runnerId),
            ("$instance", instanceId),
            ("$cutoff", Iso(cutoff))))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var action = new RunnerReconciliationAction(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetInt32(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6));
                if (action.Action == "terminate-process")
                {
                    if (reader.IsDBNull(7))
                    {
                        retired.Add(action.ActionId);
                        continue;
                    }

                    inventory ??= JsonSerializer.Deserialize<RunnerProcessInventory>(reader.GetString(7));
                    if (inventory?.Processes.Any(process => process.Pid > 0 && process.Pid == action.Pid
                            && process.RunId == action.RunId && process.TaskKey == action.TaskKey
                            && process.StartedAt <= cutoff
                            && OrphanActionId(runnerId, instanceId, process) == action.ActionId) != true)
                    {
                        retired.Add(action.ActionId);
                        continue;
                    }
                }
                actions.Add(action);
            }
        }

        foreach (var actionId in retired)
            await ExecuteAsync(connection, "DELETE FROM runner_reconciliation_actions WHERE action_id = $id;",
                ct, transaction, ("$id", actionId));
        return actions;
    }

    private async Task AppendInvariantEventAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string eventIdentity,
        string runId,
        string? taskKey,
        string kind,
        string category,
        string action,
        string detail,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(taskKey)) return;
        var taskId = Convert.ToString(await ScalarAsync(
            connection,
            "SELECT id FROM tasks WHERE task_key = upper($key);",
            ct,
            transaction,
            ("$key", taskKey)), CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(taskId)) return;
        var fence = Convert.ToInt64(await ScalarAsync(
            connection,
            "SELECT COALESCE(fence, 0) FROM runs WHERE id = $run;",
            ct,
            transaction,
            ("$run", runId)) ?? 0L, CultureInfo.InvariantCulture);
        await ExecuteAsync(connection, """
            INSERT INTO events(
                event_id, run_id, task_id, kind, payload_json,
                idempotency_key, fence, occurred_at)
            VALUES ($event, $run, $task, $kind, $payload, $key, $fence, $at)
            ON CONFLICT(idempotency_key) DO NOTHING;
            """, ct, transaction,
            ("$event", $"evt_{eventIdentity}"),
            ("$run", runId),
            ("$task", taskId),
            ("$kind", kind),
            ("$payload", JsonSerializer.Serialize(new
            {
                category,
                selfHealingAction = action,
                detail,
            })),
            ("$key", $"invariant:{eventIdentity}"),
            ("$fence", fence),
            ("$at", Iso(UtcNow)));
    }

    private static async Task<IReadOnlyList<InventoryFact>> ReadFreshInventoriesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTime cutoff,
        CancellationToken ct)
    {
        var result = new List<InventoryFact>();
        await using var command = Command(connection, """
            SELECT runner_id, instance_id, observed_at, snapshot_json
              FROM runner_inventories
             WHERE observed_at >= $cutoff;
            """, transaction, ("$cutoff", Iso(cutoff)));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var snapshot = JsonSerializer.Deserialize<RunnerProcessInventory>(reader.GetString(3));
            if (snapshot is not null)
                result.Add(new InventoryFact(
                    reader.GetString(0), reader.GetString(1),
                    Parse(reader.GetString(2)), snapshot));
        }
        return result;
    }

    private static async Task<IReadOnlyList<LeaseFact>> ReadOwnedLeaseFactsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken ct)
    {
        var result = new List<LeaseFact>();
        await using var command = Command(connection, """
            SELECT l.run_id, l.task_id, t.task_key, l.runner_id, l.instance_id, l.acquired_at, l.status = 'active'
              FROM leases l
              JOIN tasks t ON t.id = l.task_id
             WHERE l.status IN ('active', 'process-unknown');
            """, transaction);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new LeaseFact(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), Parse(reader.GetString(5)), reader.GetBoolean(6)));
        return result;
    }

    private DateTime InventoryCutoff(DateTime now)
        => now.AddSeconds(-Math.Max(30, _options.InventoryGraceSeconds));

    // A reused PID needs a new directive and must not inherit an earlier acknowledgment.
    private static string OrphanActionId(string runnerId, string instanceId, RunnerProcessInfo process)
        => StableInvariantId("orphan", runnerId, instanceId, process.RunId,
            process.Pid.ToString(CultureInfo.InvariantCulture), Iso(process.StartedAt));

    private static string StableInvariantId(string category, params object[] identities)
    {
        var source = category + "|" + string.Join("|", identities);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))
            .ToLowerInvariant()[..24];
        return $"inv_{digest}";
    }

    private sealed record InventoryFact(
        string RunnerId,
        string InstanceId,
        DateTime ObservedAt,
        RunnerProcessInventory Snapshot);

    private sealed record LeaseFact(
        string RunId,
        string TaskId,
        string TaskKey,
        string RunnerId,
        string InstanceId,
        DateTime AcquiredAt,
        bool IsActive);
}

public sealed class TaskServerInvariantReconciliationService(
    TaskServerStore store,
    Microsoft.Extensions.Options.IOptions<TaskServerOptions> options,
    TaskServerStartupExecutionAdmission executionAdmission,
    ILogger<TaskServerInvariantReconciliationService> logger) : BackgroundService
{
    public bool ExecutionSuppressed => executionAdmission.IsPublicDemo;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (ExecutionSuppressed) return;

        var interval = TimeSpan.FromSeconds(
            Math.Clamp(options.Value.InvariantReconciliationSeconds, 30, 60));
        using var timer = new PeriodicTimer(interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await timer.WaitForNextTickAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }

            try
            {
                var repaired = await store.ReconcileInvariantsAsync(stoppingToken);
                if (repaired > 0)
                    logger.LogWarning(
                        "invariant-reconciliation reconciled={ReconciliationCount}", repaired);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError(exception, "invariant-reconciliation-failed");
            }
        }
    }
}
