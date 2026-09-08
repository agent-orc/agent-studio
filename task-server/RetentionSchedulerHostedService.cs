using System.Globalization;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Options;

namespace AgentStudio.TaskServer;

public sealed record RetentionRuntimeLoadDecision(bool Allowed, double? LoadPerCore, string Reason);

public interface IRetentionRuntimeLoadGate
{
    RetentionRuntimeLoadDecision Observe();
}

/// <summary>Reads the one-minute host load average on Linux. Unknown load is admitted and remains observable in logs.</summary>
public sealed class SystemRetentionRuntimeLoadGate(IOptions<TaskServerOptions> options) : IRetentionRuntimeLoadGate
{
    public RetentionRuntimeLoadDecision Observe()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/proc/loadavg"))
            return new RetentionRuntimeLoadDecision(true, null, "load-average-unavailable");
        try
        {
            var first = File.ReadAllText("/proc/loadavg").Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            var load = double.Parse(first, NumberStyles.Float, CultureInfo.InvariantCulture);
            var perCore = load / Math.Max(1, Environment.ProcessorCount);
            var maximum = Math.Max(0.1, options.Value.RetentionMaximumLoadPerCore);
            return new RetentionRuntimeLoadDecision(perCore <= maximum, perCore,
                perCore <= maximum ? "within-limit" : "runtime-load-high");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
        {
            return new RetentionRuntimeLoadDecision(true, null, "load-average-unreadable");
        }
    }
}

/// <summary>
/// Runs the retention policy once per server-local day. Mode and load checks happen before any write; archive,
/// feed publication, and full-backup failures are contained so no scheduler fault can terminate the server.
/// </summary>
public sealed class RetentionSchedulerHostedService(
    TaskServerStore store,
    RetentionManagementService retentionManagement,
    FullBackupManagementService fullBackupManagement,
    IRetentionRuntimeLoadGate loadGate,
    ITaskServerEventPublisher events,
    IOptions<TaskServerOptions> options,
    TaskServerStartupExecutionAdmission executionAdmission,
    TimeProvider clock,
    ILogger<RetentionSchedulerHostedService> logger) : BackgroundService
{
    private const string Actor = "retention-scheduler";

    public bool ExecutionSuppressed => executionAdmission.IsPublicDemo;

    public async Task<RetentionApplyResultDto?> RunOnceAsync(CancellationToken ct = default)
    {
        if (ExecutionSuppressed) return null;
        if (!store.AuthorityReady || store.Mode is TaskServerMode.ReadOnly or TaskServerMode.Maintenance or TaskServerMode.Draining)
        {
            logger.LogInformation("retention-scheduler skip-mode mode={Mode} authorityReady={AuthorityReady}", store.Mode, store.AuthorityReady);
            return null;
        }

        var load = loadGate.Observe();
        if (!load.Allowed)
        {
            logger.LogInformation("retention-scheduler skip-load reason={Reason} loadPerCore={LoadPerCore}", load.Reason, load.LoadPerCore);
            return null;
        }

        RetentionApplyResultDto result;
        try
        {
            result = await retentionManagement.ApplyScheduledRetentionRunAsync(Actor, ct);
            await retentionManagement.EmitRunCompletedAsync(result.RunId, result.AppliedActions, result.AppliedBytes, Actor, ct);
            logger.LogInformation(
                "retention-scheduler run-completed actions={Actions} bytes={Bytes} errors={Errors}",
                result.AppliedActions, result.AppliedBytes, result.Errors.Count);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "retention-scheduler run-failed");
            return null;
        }

        try
        {
            await events.PublishAsync(new TaskServerOperationalEvent(
                "retention.run.completed",
                clock.GetUtcNow().UtcDateTime,
                Actor,
                new
                {
                    result.RunId,
                    result.AppliedActions,
                    result.AppliedBytes,
                    errorCount = result.Errors.Count,
                }), ct);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "retention-scheduler event-publish-failed runId={RunId}", result.RunId);
        }

        try
        {
            await fullBackupManagement.CreateAsync(Actor, ct);
            var policy = await retentionManagement.GetActivePolicyAsync(ct);
            await fullBackupManagement.ThinAsync(policy.FullBackups, Actor, ct);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "retention-scheduler full-backup-failed runId={RunId}", result.RunId);
        }

        return result;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (ExecutionSuppressed) return;
        if (!options.Value.RetentionSchedulerEnabled)
        {
            logger.LogInformation("retention-scheduler disabled");
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Clamp(options.Value.RetentionSchedulerIntervalMinutes, 5, 24 * 60));
        using var timer = new PeriodicTimer(interval);
        DateOnly? lastRunDate = null;
        do
        {
            try
            {
                var now = clock.GetLocalNow();
                var scheduledHour = Math.Clamp(options.Value.RetentionScheduleHour, 0, 23);
                var today = DateOnly.FromDateTime(now.DateTime);
                if (now.Hour == scheduledHour && lastRunDate != today)
                {
                    if (await RunOnceAsync(stoppingToken) is not null) lastRunDate = today;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "retention-scheduler tick-failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
