using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Options;

namespace AgentStudio.TaskServer;

/// <summary>
/// Daily archive run, patterned on <see cref="ResultRefGcHostedService"/>: a <see cref="PeriodicTimer"/> tick
/// checks the configured hour once per day, skips while the server cannot safely mutate state, and never lets
/// a failed sweep stop the process.
/// </summary>
public sealed class RetentionSchedulerHostedService(
    TaskServerStore store,
    RetentionManagementService retentionManagement,
    IOptions<TaskServerOptions> options,
    TaskServerStartupExecutionAdmission executionAdmission,
    TimeProvider clock,
    ILogger<RetentionSchedulerHostedService> logger) : BackgroundService
{
    public bool ExecutionSuppressed => executionAdmission.IsPublicDemo;

    public async Task<RetentionApplyResultDto?> RunOnceAsync(CancellationToken ct = default)
    {
        if (ExecutionSuppressed) return null;
        if (!store.AuthorityReady || store.Mode is TaskServerMode.ReadOnly or TaskServerMode.Maintenance or TaskServerMode.Draining)
        {
            logger.LogInformation("retention-scheduler skip-mode mode={Mode} authorityReady={AuthorityReady}", store.Mode, store.AuthorityReady);
            return null;
        }

        try
        {
            var result = await retentionManagement.ApplyAsync(new RunRetentionRequest(), "retention-scheduler", ct);
            await retentionManagement.EmitRunCompletedAsync(result.RunId, result.AppliedActions, result.AppliedBytes, "retention-scheduler", ct);
            logger.LogInformation(
                "retention-scheduler run-completed actions={Actions} bytes={Bytes} errors={Errors}",
                result.AppliedActions, result.AppliedBytes, result.Errors.Count);
            return result;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "retention-scheduler run-failed");
            return null;
        }
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
                var now = clock.GetUtcNow();
                var scheduledHour = Math.Clamp(options.Value.RetentionScheduleHourUtc, 0, 23);
                var today = DateOnly.FromDateTime(now.UtcDateTime);
                if (now.UtcDateTime.Hour == scheduledHour && lastRunDate != today)
                {
                    await RunOnceAsync(stoppingToken);
                    lastRunDate = today;
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
