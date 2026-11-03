using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>
/// The review daemon's one shadow package slot (AGT-3400 S4). It polls only
/// when the per-card claim came back empty, at most once a minute, and runs
/// one package at a time beside the per-card slots without taking one of
/// them, so per-card admission and claims are unchanged. A Task Server
/// without the package plane is asked again after half an hour.
/// </summary>
internal sealed class PackageReviewSlot(
    RunnerOptions options,
    IReviewPackagePlaneClient client,
    Func<string> instanceId,
    Action<string> log,
    Func<IReviewPackagePlaneClient, PackageReviewExecutor>? executor = null)
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan MissingPlaneBackoff = TimeSpan.FromMinutes(30);

    private Task? _running;
    private DateTime _nextPoll = DateTime.MinValue;

    public bool Busy => _running is { IsCompleted: false };

    public async Task PollAsync(DateTime nowUtc, CancellationToken shutdown)
    {
        await ReapAsync();
        if (!options.PackageReview || Busy || nowUtc < _nextPoll) return;
        _nextPoll = nowUtc + PollInterval;
        ReviewPackageClaimResponse claim;
        try
        {
            // A claim mints a fence; shutdown must not hide one that was minted.
            claim = await client.ClaimReviewPackageAsync(
                new ReviewPackageClaimRequest(options.RunnerId, instanceId()), CancellationToken.None);
        }
        catch (TaskServerException exception) when (exception.StatusCode is 404 or 405)
        {
            _nextPoll = nowUtc + MissingPlaneBackoff;
            log($"review package plane unavailable status={exception.StatusCode}; next poll in {MissingPlaneBackoff.TotalMinutes:0} min");
            return;
        }
        if (!string.Equals(claim.Status, "claimed", StringComparison.OrdinalIgnoreCase)) return;
        log($"claimed review package attempt={claim.Lease!.AttemptId} package={claim.Subject!.PackageId} "
            + $"members={claim.Subject.Members.Count}");
        var run = (executor ?? (plane => new PackageReviewExecutor(options, plane, instanceId, log)))(client);
        _running = run.RunClaimedAsync(claim, shutdown);
    }

    /// <summary>Waits for a running package at shutdown; its lease then expires or its report lands.</summary>
    public async Task DrainAsync() => await ReapAsync(wait: true);

    private async Task ReapAsync(bool wait = false)
    {
        if (_running is null || (!wait && !_running.IsCompleted)) return;
        try
        {
            await _running;
        }
        catch (OperationCanceledException)
        {
            log("review package slot stopped during shutdown");
        }
        catch (Exception exception)
        {
            log($"review package slot failed: {exception.Message}");
        }
        _running = null;
    }
}
