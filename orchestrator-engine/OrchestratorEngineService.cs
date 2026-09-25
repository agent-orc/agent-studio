using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentStudio.OrchestratorEngine;

public sealed class OrchestratorEngineService : BackgroundService
{
    private readonly EngineOptions _options;
    private readonly EngineTaskServerClient _client;
    private readonly IReadOnlyDictionary<OrchestrationStage, IOrchestrationStageHandler> _handlers;
    private readonly ILogger<OrchestratorEngineService> _logger;
    private readonly string _instanceId =
        $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    public OrchestratorEngineService(
        EngineOptions options,
        EngineTaskServerClient client,
        IEnumerable<IOrchestrationStageHandler> handlers,
        ILogger<OrchestratorEngineService> logger)
    {
        _options = options;
        _client = client;
        _handlers = handlers.ToDictionary(handler => handler.Stage);
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _client.EnsureCompatibleAsync(stoppingToken);
        _logger.LogInformation(
            "orchestrator-engine compatible with Task Server; client={ClientId} instance={InstanceId}",
            _options.ClientId,
            _instanceId);

        var loops = new List<Task>();
        Start(OrchestrationStage.ReviewDecision, _options.ReviewConcurrency);
        Start(OrchestrationStage.Council, _options.CouncilConcurrency);
        Start(OrchestrationStage.PostProcessing, _options.PostProcessingConcurrency);
        Start(OrchestrationStage.GateDispatch, _options.GateDispatchConcurrency);
        Start(OrchestrationStage.CompletionJudge, _options.CompletionJudgeConcurrency);
        await Task.WhenAll(loops);
        return;

        void Start(OrchestrationStage stage, int concurrency)
        {
            for (var slot = 0; slot < concurrency; slot++)
                loops.Add(RunStageLoopAsync(stage, slot, stoppingToken));
        }
    }

    private async Task RunStageLoopAsync(
        OrchestrationStage stage,
        int slot,
        CancellationToken ct)
    {
        var handler = _handlers[stage];
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var claim = await _client.ClaimAsync(
                    new OrchestrationClaimRequest(
                        _options.ClientId,
                        _instanceId,
                        [stage],
                        _options.LeaseSeconds),
                    ct);
                if (claim.Status != "claimed" || claim.Run is null || claim.Lease is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(_options.PollSeconds), ct);
                    continue;
                }
                await ProcessClaimAsync(handler, claim.Run, claim.Lease, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (EngineProtocolException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "orchestration loop failed; stage={Stage} slot={Slot}",
                    stage,
                    slot);
                await Task.Delay(TimeSpan.FromSeconds(_options.PollSeconds), ct);
            }
        }
    }

    private async Task ProcessClaimAsync(
        IOrchestrationStageHandler handler,
        OrchestrationRunDto run,
        OrchestrationLeaseDto lease,
        CancellationToken ct)
    {
        try
        {
            var decision = await handler.ExecuteAsync(run, ct);
            var completed = await _client.CompleteStageAsync(
                run.RunId,
                new CompleteOrchestrationStageRequest(
                    _options.ClientId,
                    _instanceId,
                    lease.LeaseId,
                    lease.Fence,
                    handler.Stage,
                    decision.Action,
                    decision.OutputJson,
                    $"engine:{run.RunId}:{handler.Stage}:{lease.Fence}"),
                ct);
            _logger.LogInformation(
                "orchestration stage settled; run={RunId} stage={Stage} action={Action} status={Status}",
                run.RunId,
                handler.Stage,
                decision.Action,
                completed.Status);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (GatePendingException)
        {
            await _client.ReleaseAsync(
                run.RunId,
                new ReleaseOrchestrationLeaseRequest(
                    _options.ClientId, _instanceId, lease.LeaseId, lease.Fence,
                    "gate-pending"), ct);
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _options.PollSeconds)), ct);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "orchestration stage execution failed; run={RunId} stage={Stage}; releasing lease",
                run.RunId,
                handler.Stage);
            try
            {
                await _client.ReleaseAsync(
                    run.RunId,
                    new ReleaseOrchestrationLeaseRequest(
                        _options.ClientId,
                        _instanceId,
                        lease.LeaseId,
                        lease.Fence,
                        exception.GetType().Name),
                    CancellationToken.None);
            }
            catch (Exception releaseException)
            {
                _logger.LogWarning(
                    releaseException,
                    "orchestration lease release failed; server expiry will recover run={RunId}",
                    run.RunId);
            }
        }
    }
}
