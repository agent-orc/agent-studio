namespace AgentStudio.Readiness;

/// <summary>
/// Keeps the latest readiness report. It is the fact producer behind the
/// readiness reads: it collects shortly after start and then every
/// <see cref="Interval"/>, and an explicit recheck collects at once. Reads
/// return <see cref="Latest"/> and never start Git themselves.
/// </summary>
public sealed class ServerReadinessMonitor(
    Func<CancellationToken, Task<ServerReadinessFacts>> collect,
    TimeProvider clock,
    ILogger<ServerReadinessMonitor> logger) : BackgroundService
{
    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _single = new(1, 1);
    private ServerReadinessReport _latest = ServerReadinessReport.Pending;
    private string? _loggedProblems;

    public ServerReadinessReport Latest => Volatile.Read(ref _latest);

    public async Task<ServerReadinessReport> RefreshAsync(CancellationToken ct)
    {
        await _single.WaitAsync(ct);
        try
        {
            var report = ServerReadinessPolicy.Evaluate(await collect(ct));
            Volatile.Write(ref _latest, report);
            LogWhenChanged(report);
            return report;
        }
        finally
        {
            _single.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!await Wait(StartupDelay, stoppingToken)) return;
        do
        {
            try
            {
                await RefreshAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                logger.LogWarning(error, "server-readiness-collect-failed");
            }
        } while (await Wait(Interval, stoppingToken));
    }

    /// <summary>False once the host stops.</summary>
    private async Task<bool> Wait(TimeSpan delay, CancellationToken stoppingToken)
    {
        await Task.Delay(delay, clock, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        return !stoppingToken.IsCancellationRequested;
    }

    /// <summary>One log line per change of the problem set, so a stuck problem does not repeat every interval.</summary>
    private void LogWhenChanged(ServerReadinessReport report)
    {
        var problems = string.Join(" ", report.Checks
            .Where(check => check.State is ReadinessStates.Fail or ReadinessStates.Warn)
            .Select(check => check.Subject is null ? $"{check.Id}={check.State}" : $"{check.Id}[{check.Subject}]={check.State}"));
        if (problems == _loggedProblems) return;
        _loggedProblems = problems;
        if (report.Failing > 0)
            logger.LogWarning("server-readiness state={State} failing={Failing} attention={Attention} {Problems}",
                report.State, report.Failing, report.Attention, problems);
        else
            logger.LogInformation("server-readiness state={State} failing=0 attention={Attention} {Problems}",
                report.State, report.Attention, problems);
    }
}
