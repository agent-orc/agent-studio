using System.Diagnostics;

namespace AgentStudio.Search;

/// <summary>
/// Keeps the task blob of <see cref="GlobalSearchIndex"/> warm so the palette's
/// first keystroke does not pay for the build. The index rebuilds only when
/// <see cref="TaskIndexCache"/> publishes a new snapshot and then re-reads only
/// the cards whose prompt.md/status.md actually changed, so this loop is idle
/// work in the steady state.
/// </summary>
public sealed class GlobalSearchIndexWarmer(
    GlobalSearchIndex index,
    ILogger<GlobalSearchIndexWarmer> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var ticker = new PeriodicTimer(Interval);
        try
        {
            do { Warm(); }
            while (await ticker.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException ex) when (stoppingToken.IsCancellationRequested)
        {
            SilentCatch.Note(ex, "GlobalSearchIndexWarmer: stopped by host shutdown.");
        }
    }

    private void Warm()
    {
        try
        {
            var timer = Stopwatch.StartNew();
            var cards = index.Warm();
            if (timer.ElapsedMilliseconds > 250)
                logger.LogInformation(
                    "global-search-index-warmed cards={Cards} durationMs={DurationMs}",
                    cards, timer.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            // A failed warm only costs the next query an on-demand rebuild.
            logger.LogWarning(ex, "global-search-index-warm-failed");
        }
    }
}
