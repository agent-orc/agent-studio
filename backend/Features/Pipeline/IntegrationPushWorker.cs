using System.Diagnostics;

namespace AgentStudio.Pipeline;

/// <summary>
/// Drains <see cref="IntegrationPushQueue"/> and performs the integration-branch
/// push off the accept-transition request path (AGT-1999). One reader, processed
/// strictly in order so two pushes of the same branch never race. It delegates to
/// <see cref="MergeIntoDevelopRunner.PushIntegrationBranchAsync"/>, which owns the
/// git push, the AGT-1944 environmental retry, and recording the visible
/// <c>post-merge-into-develop-push</c> step outcome. Failures are already
/// recorded and swallowed inside the runner; this loop only guards against a
/// throw so one bad item can never tear the worker down.
/// </summary>
public sealed class IntegrationPushWorker : BackgroundService
{
    private readonly IntegrationPushQueue _queue;
    private readonly MergeIntoDevelopRunner _runner;
    private readonly ILogger<IntegrationPushWorker> _logger;
    private readonly AgentStudio.Bus.AgentMessageBusBridge? _bus;

    public IntegrationPushWorker(
        IntegrationPushQueue queue,
        MergeIntoDevelopRunner runner,
        ILogger<IntegrationPushWorker> logger,
        AgentStudio.Bus.AgentMessageBusBridge? bus = null)
    {
        _queue = queue;
        _runner = runner;
        _logger = logger;
        _bus = bus;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var request in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                await ProcessAsync(request, stoppingToken);
            }
        }
        catch (OperationCanceledException __ex) when (stoppingToken.IsCancellationRequested)
        {
            SilentCatch.Note(__ex, "IntegrationPushWorker: graceful shutdown; unpushed items are recovered by the durable backstop.");
        }
    }

    /// <summary>
    /// Pushes one queued integration-branch request. Exposed so tests can drain
    /// the queue deterministically without standing up the background loop;
    /// production reaches it only through <see cref="ExecuteAsync"/>.
    /// </summary>
    internal async Task ProcessAsync(IntegrationPushRequest request, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await _runner.PushIntegrationBranchAsync(
                request.Project, request.JobId, request.JobFolderPath, request.WatchPath, request.IntegrationBranch, ct,
                request.ApprovedSha, request.DeliverySequence);
            sw.Stop();
            if (result.Success)
            {
                _logger.LogInformation(
                    "Integration-push worker pushed {Branch} for {JobId} ({Status}) in {ElapsedMs}ms",
                    request.IntegrationBranch, request.JobId, result.Status, sw.ElapsedMilliseconds);
            }
            else if (_bus != null)
            {
                await _bus.EmitManagedRepoPushFailureAsync(
                    request.Project,
                    request.JobId,
                    request.WatchPath ?? "(unresolved)",
                    request.IntegrationBranch,
                    result.Status,
                    result.Error,
                    attempts: 1,
                    ct: ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Integration-push worker failed for {JobId}", request.JobId);
            if (_bus != null)
                await _bus.EmitManagedRepoPushFailureAsync(
                    request.Project,
                    request.JobId,
                    request.WatchPath ?? "(unresolved)",
                    request.IntegrationBranch,
                    "error",
                    ex.Message,
                    attempts: 1,
                    ct: ct);
        }
    }
}
