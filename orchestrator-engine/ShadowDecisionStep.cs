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
    public async Task<OrchestratorDecisionReceiptV1?> RecordAsync(string occurrenceId, CancellationToken ct,
        string mode = DecisionReceiptPolicy.ShadowMode)
    {
        var input = await plane.GetGroundsInputAsync(occurrenceId, ct);
        if (input is null) return null;
        if (input.Mode != mode) throw new InvalidOperationException("decision-mode-mismatch");
        var (preparedInput, unavailable) = await PrepareArtifactSourcesAsync(input.Grounds, ct);
        var grounds = DecisionGroundsPolicy.Freeze(preparedInput);
        OrchestratorDecisionJobDto? job = null;
        var attempts = 0;
        var alternateUsed = false;
        if (!grounds.Pack.Incomplete && unavailable is null)
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
            input.DeterministicReason, job, attempts, alternateUsed, unavailable, mode));
        return await plane.PutDecisionReceiptAsync(receipt, ct);
    }

    private async Task<(DecisionGroundsInputV1 Input, string? Unavailable)> PrepareArtifactSourcesAsync(
        DecisionGroundsInputV1 input, CancellationToken ct)
    {
        var references = input.Facts.Select(fact => fact.Source)
            .Where(source => source.Kind == "artifact-window")
            .Distinct().ToArray();
        if (references.Length == 0) return (input, null);
        if (references.Length > 2) return (input, "decision artefact window limit exceeded");
        if (plane is not IEngineArtifactSource artifactSource)
            return (input, "decision artefact reader is unavailable");
        var reader = new EngineArtifactReader(artifactSource);
        var selected = new Dictionary<GroundsSourceRefV1, BoundedArtifactWindow>();
        foreach (var reference in references)
        {
            var value = reference.Ref;
            if (!value.StartsWith("artifact:", StringComparison.Ordinal))
                return (input, "decision artefact reference is invalid");
            var parts = value["artifact:".Length..].Split('/', 2);
            if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
                return (input, "decision artefact reference is invalid");
            try
            {
                var window = await reader.ReadWindowAsync(parts[0], parts[1], ct);
                if (window.OmissionReasons.Contains("oversize-source")
                    || window.OmissionReasons.Contains("non-text-source")
                    || !string.Equals(reference.Digest, window.Source.Digest, StringComparison.OrdinalIgnoreCase))
                    return (input, "decision artefact source is incomplete or changed");
                selected.Add(reference, window);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { return (input, "decision artefact source is unavailable"); }
        }
        var facts = new List<GroundsFactV1>(input.Facts.Count);
        var omissions = input.Omissions.ToList();
        foreach (var fact in input.Facts)
        {
            if (!selected.TryGetValue(fact.Source, out var window))
            {
                facts.Add(fact);
                continue;
            }
            var visible = window.Content.Length <= DecisionGroundsPolicy.MaxValueChars
                ? window.Content
                : window.Content[..DecisionGroundsPolicy.MaxValueChars] +
                  $" [omitted {window.Content.Length - DecisionGroundsPolicy.MaxValueChars} chars]";
            if (!string.IsNullOrEmpty(fact.Value) && fact.Value != visible
                && !(fact.Value == "[omitted: secret]" && window.OmissionReasons.Contains("secret")))
                return (input, "decision artefact window does not match its source");
            facts.Add(fact with { Value = visible, Source = window.Source });
            omissions.AddRange(window.OmissionReasons.Select(reason =>
                new GroundsOmissionV1($"facts.{fact.Id}.value", reason)));
        }
        return (input with { Facts = facts, Omissions = omissions }, null);
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
        catch (OperationCanceledException)
        {
            logger.LogDebug("shadow decision service stopped with {Count} occurrences cancelled", active.Count);
        }
    }

    private bool IsBackingOff(string occurrenceId)
    {
        lock (_retryAfter) return _retryAfter.ContainsKey(occurrenceId);
    }

    private async Task RecordAsync(string occurrenceId, CancellationToken ct)
    {
        try
        {
            var input = await plane.GetGroundsInputAsync(occurrenceId, ct);
            if (input is null) return;
            var receipt = await step.RecordAsync(occurrenceId, ct, input.Mode);
            if (receipt is not null)
                logger.LogInformation(
                    "shadow decision recorded; occurrence={Occurrence} receipt={Receipt} call={Call} matches={Matches}",
                    occurrenceId, receipt.ReceiptId, receipt.Call.Status, receipt.MatchesDeterministic);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown: the frozen grounds stay pending and the next engine start records the receipt.
            logger.LogDebug("shadow decision cancelled by shutdown; occurrence={Occurrence}", occurrenceId);
        }
        catch (Exception exception)
        {
            lock (_retryAfter) _retryAfter[occurrenceId] = DateTime.UtcNow + FailureBackoff;
            logger.LogWarning(exception, "shadow decision failed; occurrence={Occurrence}", occurrenceId);
        }
    }
}
