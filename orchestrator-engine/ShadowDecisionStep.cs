using AgentStudio.OrchestrationDecisions;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentStudio.OrchestratorEngine;

/// <summary>Decision-plane routes the shadow model step needs from the authority host.</summary>
public interface IDecisionReceiptPlane
{
    Task<IReadOnlyList<string>> GetPendingDecisionGroundsAsync(int limit, CancellationToken ct);
    Task<DecisionShadowInputV1?> GetGroundsInputAsync(string runId, CancellationToken ct);
    Task<OrchestratorDecisionJobDto> CreateDecisionJobAsync(OrchestratorDecisionJobRequest request, CancellationToken ct);
    Task<OrchestratorDecisionJobDto> GetDecisionJobAsync(string jobId, CancellationToken ct);
    Task<OrchestratorDecisionReceiptV1> PutDecisionReceiptAsync(OrchestratorDecisionReceiptV1 receipt, CancellationToken ct);
}

/// <summary>
/// AGT-W73 OD-S1 shadow model step. It freezes the host's grounds input,
/// asks a runner host to run the deciding model as a claimable job (provider
/// credentials stay in runner-host custody), and stores one receipt per
/// occurrence. It runs beside the stage loops and never feeds a dispatch.
/// A transport failure gets one attempt on the configured alternate route;
/// any other failure or an expired wait records ask(reason=decision-unavailable).
/// </summary>
public sealed class ShadowDecisionStep(IDecisionReceiptPlane plane, EngineOptions options, TimeProvider time)
{
    public async Task<OrchestratorDecisionReceiptV1?> RecordAsync(string occurrenceId, CancellationToken ct)
    {
        var input = await plane.GetGroundsInputAsync(occurrenceId, ct);
        if (input is null) return null;
        var grounds = DecisionGroundsPolicy.Freeze(input.Grounds);
        OrchestratorDecisionJobDto? job = null;
        var attempts = 0;
        var alternateUsed = false;
        string? unavailable = null;
        if (!grounds.Pack.Incomplete)
        {
            var route = grounds.ModelRoute;
            var prompt = DecisionReceiptPolicy.RenderPrompt(grounds);
            attempts = 1;
            job = await RunAsync(grounds, prompt, 1, route.Cli, route.Model, route.ThinkingLevel, ct);
            if (job.Status == DecisionJobStatuses.Failed && job.Result?.TransportFailure == true
                && route.AlternateModel is { } alternate)
            {
                attempts = 2;
                alternateUsed = true;
                job = await RunAsync(grounds, prompt, 2, route.AlternateCli ?? route.Cli, alternate,
                    route.AlternateThinkingLevel, ct);
            }
            if (!DecisionJobStatuses.IsTerminal(job.Status))
                unavailable = $"decision job {job.JobId} was not completed within {options.ShadowDecisionTimeoutSeconds} s";
        }
        var receipt = DecisionReceiptPolicy.Build(new DecisionReceiptFacts(grounds,
            new DecisionChoiceV1(input.DeterministicAction, input.DeterministicTarget, "applied:deterministic"),
            input.DeterministicReason, job, attempts, alternateUsed, unavailable));
        return await plane.PutDecisionReceiptAsync(receipt, ct);
    }

    private async Task<OrchestratorDecisionJobDto> RunAsync(DecisionGroundsV1 grounds, string prompt,
        int attempt, string cli, string model, string? thinkingLevel, CancellationToken ct)
    {
        var job = await plane.CreateDecisionJobAsync(new OrchestratorDecisionJobRequest(
            grounds.OccurrenceId, grounds.Scope.ProjectId ?? string.Empty, grounds.Scope.TaskId ?? string.Empty,
            attempt, grounds.ContentDigest, prompt, cli, model, thinkingLevel,
            DecisionGroundsPolicy.MaxCallInputTokens, DecisionGroundsPolicy.MaxCallOutputTokens), ct);
        // The deadline follows the durable job, so an engine restart resumes the same wait.
        var deadline = job.CreatedAt.AddSeconds(options.ShadowDecisionTimeoutSeconds);
        while (!DecisionJobStatuses.IsTerminal(job.Status) && time.GetUtcNow().UtcDateTime < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.PollSeconds)), time, ct);
            job = await plane.GetDecisionJobAsync(job.JobId, ct);
        }
        return job;
    }
}

/// <summary>Drains occurrences whose frozen grounds have no receipt yet.</summary>
public sealed class ShadowDecisionService(
    IDecisionReceiptPlane plane,
    ShadowDecisionStep step,
    EngineOptions options,
    ILogger<ShadowDecisionService> logger) : BackgroundService
{
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(5);
    private readonly Dictionary<string, DateTime> _retryAfter = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var active = new Dictionary<string, Task>(StringComparer.Ordinal);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var done in active.Where(item => item.Value.IsCompleted).Select(item => item.Key).ToArray())
                    active.Remove(done);
                if (active.Count < options.ShadowDecisionConcurrency)
                {
                    var pending = await plane.GetPendingDecisionGroundsAsync(50, stoppingToken);
                    var now = DateTime.UtcNow;
                    lock (_retryAfter)
                        foreach (var expired in _retryAfter.Where(item => item.Value <= now).Select(item => item.Key).ToArray())
                            _retryAfter.Remove(expired);
                    foreach (var occurrence in pending.Where(id => !active.ContainsKey(id)
                                     && !IsBackingOff(id))
                                 .Take(options.ShadowDecisionConcurrency - active.Count))
                        active[occurrence] = RecordAsync(occurrence, stoppingToken);
                }
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.PollSeconds)), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "shadow decision poll failed");
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.PollSeconds)), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
        try { await Task.WhenAll(active.Values); }
        catch (OperationCanceledException) { }
    }

    private bool IsBackingOff(string occurrenceId)
    {
        lock (_retryAfter) return _retryAfter.ContainsKey(occurrenceId);
    }

    private async Task RecordAsync(string occurrenceId, CancellationToken ct)
    {
        try
        {
            var receipt = await step.RecordAsync(occurrenceId, ct);
            if (receipt is not null)
                logger.LogInformation(
                    "shadow decision recorded; occurrence={Occurrence} receipt={Receipt} call={Call} matches={Matches}",
                    occurrenceId, receipt.ReceiptId, receipt.Call.Status, receipt.MatchesDeterministic);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception exception)
        {
            lock (_retryAfter) _retryAfter[occurrenceId] = DateTime.UtcNow + FailureBackoff;
            logger.LogWarning(exception, "shadow decision failed; occurrence={Occurrence}", occurrenceId);
        }
    }
}
