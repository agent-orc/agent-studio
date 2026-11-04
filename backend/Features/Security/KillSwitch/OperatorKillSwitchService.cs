namespace AgentStudio.Security;

public sealed record OperatorKillSwitchOptions(TimeSpan PollInterval, TimeSpan DrillInterval)
{
    public const string Section = "Security:OperatorKillSwitch";
    public const int DefaultDrillIntervalHours = 720;

    public int DrillIntervalHours => (int)DrillInterval.TotalHours;

    /// <summary>A monthly drill by default (30 days); <c>DrillIntervalHours = 0</c> turns the schedule off.</summary>
    public static OperatorKillSwitchOptions From(IConfiguration configuration) => new(
        TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue(Section + ":PollSeconds", 2), 1, 10)),
        TimeSpan.FromHours(Math.Clamp(configuration.GetValue(Section + ":DrillIntervalHours", DefaultDrillIntervalHours), 0, 24 * 366)));
}

/// <summary>
/// Runs in the API (security model SM-2). Holds the host lock that tells the
/// out-of-band command the API is running, applies the command's latch within
/// one poll interval, retries pending Operations Server revocations, delivers
/// notices the command could not send, and runs the scheduled drill.
/// </summary>
public sealed class OperatorKillSwitchService(
    OperatorKillSwitch killSwitch,
    AccessSecurityStore store,
    OperatorKillSwitchOptions options,
    TimeProvider clock,
    ILogger<OperatorKillSwitchService> logger) : BackgroundService
{
    private static readonly TimeSpan CommandWait = TimeSpan.FromSeconds(15);
    private FileStream? _hostLock;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _hostLock = OperatorKillSwitchHostLock.TryAcquire(store.SecurityFolder);
        // The command holds the lock only while it applies a kill; let it finish before this API serves requests.
        var waited = TimeSpan.Zero;
        while (_hostLock is null && store.OperatorKillRequested && waited < CommandWait)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            waited += TimeSpan.FromMilliseconds(250);
            _hostLock = OperatorKillSwitchHostLock.TryAcquire(store.SecurityFolder);
        }
        if (_hostLock is null)
            logger.LogWarning("Kill switch: the control plane lock in {Folder} is held by another process; retrying", store.SecurityFolder);
        else
        {
            try { await TickAsync(cancellationToken); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Kill switch start-up pass failed; the poll retries it");
            }
        }
        await base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        _hostLock?.Dispose();
        _hostLock = null;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(options.PollInterval, clock, stoppingToken); }
            catch (OperationCanceledException) { return; }
            _hostLock ??= OperatorKillSwitchHostLock.TryAcquire(store.SecurityFolder);
            if (_hostLock is null) continue;
            try { await TickAsync(stoppingToken); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Kill switch tick failed; retrying");
            }
        }
    }

    /// <summary>One pass; public so a test drives it without waiting for the poll interval.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        await killSwitch.ApplyOutOfBandRequestAsync(removeLatch: true, ct);
        await killSwitch.RetryPendingAsync(ct);
        await killSwitch.NotifyPendingAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var state = killSwitch.Log.Read();
        var next = OperatorKillSwitchPolicy.NextDrillAt(state.NextDrillAt, now, options.DrillInterval);
        // A drill needs an Owner to delegate its test session; before bootstrap there is none.
        if (OperatorKillSwitchPolicy.DrillDue(next, now, options.DrillInterval) && !store.BootstrapRequired)
        {
            var result = await killSwitch.RunDrillAsync(OperatorKillTriggers.ScheduledDrill, ownerId: null, ct);
            if (!result.Passed) logger.LogError("Kill switch drill {DrillId} failed: {Failure}", result.Id, result.Failure);
            next = now + options.DrillInterval;
        }
        if (next != state.NextDrillAt) killSwitch.Log.Update(current => current with { NextDrillAt = next });
    }
}

public static class OperatorKillSwitchServices
{
    public static IServiceCollection AddOperatorKillSwitch(this IServiceCollection services)
    {
        services.AddSingleton(sp => OperatorKillSwitchOptions.From(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton(sp => OperationsServerRevocationOptions.From(sp.GetRequiredService<IConfiguration>()));
        services.AddHttpClient(OperationsServerOperatorRevocation.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        services.AddSingleton<IOperatorPrincipalRevocation>(sp => new OperationsServerOperatorRevocation(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(OperationsServerOperatorRevocation.HttpClientName),
            sp.GetRequiredService<OperationsServerRevocationOptions>()));
        services.AddSingleton<IOperatorKillNotifier>(sp => new OperatorFeedKillNotifier(sp.GetRequiredService<AgentMessageBusBridge>()));
        services.AddSingleton(sp =>
        {
            var store = sp.GetRequiredService<AccessSecurityStore>();
            return new OperatorKillSwitch(store, new OperatorKillSwitchLog(store.SecurityFolder),
                sp.GetRequiredService<IOperatorPrincipalRevocation>(), sp.GetRequiredService<IOperatorKillNotifier>(),
                sp.GetRequiredService<TimeProvider>(), sp.GetService<ILogger<OperatorKillSwitch>>());
        });
        services.AddSingleton<OperatorKillSwitchService>();
        services.AddHostedService(sp => sp.GetRequiredService<OperatorKillSwitchService>());
        return services;
    }
}
