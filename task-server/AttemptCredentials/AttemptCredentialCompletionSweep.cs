namespace AgentStudio.TaskServer.AttemptCredentials;

/// <summary>
/// SM-11: closes attempt credentials of finished attempts within one sweep
/// interval, so a token taken from a finished attempt is refused at the issuer
/// even when its host crashed before teardown.
/// </summary>
public sealed class AttemptCredentialCompletionSweep(
    TaskServerStore store, IServiceScopeFactory scopes, IAttemptRootSecretStore roots,
    TimeProvider clock, ILogger<AttemptCredentialCompletionSweep> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                // The issuer is a typed HTTP client; resolve a fresh one per sweep.
                await using var scope = scopes.CreateAsyncScope();
                var issuer = scope.ServiceProvider.GetRequiredService<IRepositoryTokenIssuer>();
                var completed = await store.RevokeFinishedAttemptCredentialsAsync(issuer, roots, stoppingToken);
                if (completed > 0) logger.LogInformation("Completed cleanup for {Count} attempt credentials.", completed);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Attempt credential completion sweep failed; retrying next sweep.");
            }
        }
    }
}
