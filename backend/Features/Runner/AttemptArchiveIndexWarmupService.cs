namespace AgentStudio.Runner;

/// <summary>
/// Builds the attempt-authority archive index once after the host has started
/// (AGT-3109). The card detail reads archived attempts on every
/// <c>/runs</c>, <c>/timeline</c> and <c>/steering-feedback</c> poll; on Stable
/// the archives hold 62 files and 226 MiB, so the read that builds the index
/// takes seconds. Building it here keeps that cost off every request path. A
/// read that beats the warmup waits for the same single build and gets the
/// same answer.
/// </summary>
public sealed class AttemptArchiveIndexWarmupService : BackgroundService
{
    private readonly AttemptAuthorityService _authority;
    private readonly ILogger<AttemptArchiveIndexWarmupService> _logger;

    public AttemptArchiveIndexWarmupService(
        AttemptAuthorityService authority,
        ILogger<AttemptArchiveIndexWarmupService> logger)
    {
        _authority = authority;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Hand control back to the host before reading any archive, so
        // StartAsync and the health probe never wait for the build.
        await Task.Yield();
        if (stoppingToken.IsCancellationRequested) return;
        try
        {
            _authority.WarmArchiveIndex();
        }
        catch (InvalidDataException ex)
        {
            // An unreadable shard leaves the index unbuilt; history reads keep
            // failing closed and retry the build, as they did before.
            _logger.LogWarning(ex, "attempt-authority-archive-index-warmup-failed");
        }
    }
}
