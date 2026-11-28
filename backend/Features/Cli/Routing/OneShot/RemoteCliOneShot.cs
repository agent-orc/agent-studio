using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.AdHoc;
using AgentStudio.Host;
using AgentStudio.Shared;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Cli;

/// <summary>Networked one-shot transport. Provider credentials remain on runner hosts.</summary>
public sealed class RemoteCliOneShot(string cliType, IHttpClientFactory clients,
    IConfiguration configuration, AdHocUsageRecorder? usageRecorder = null,
    TimeProvider? timeProvider = null, TimeSpan? pollInterval = null,
    CombinedOneShotJobs? combinedJobs = null) : ICliOneShot
{
    public string CliType => cliType;

    public async Task<CliOneShotResult> RunAsync(CliOneShotRequest request, CancellationToken ct = default)
    {
        var start = DateTime.UtcNow;
        CliOneShotResult Finish(CliOneShotResult result)
        {
            if (request.RecordUsage && usageRecorder is not null)
                AdHocClaudeInvoker.Record(usageRecorder,
                    request.Source ?? AdHocUsageSources.ReviewDecision,
                    request.Model, result.Usage, (long)result.Duration.TotalMilliseconds,
                    result.Ok, request.CliType, request.Project, request.JobId);
            return result;
        }
        CliOneShotResult Failed(string error) =>
            Finish(CliOneShotResult.SpawnFailure(error, start, DateTime.UtcNow));
        var standalone = TaskServerPlaneProxy.IsConfigured(configuration);
        if (!standalone && combinedJobs is null)
            return Failed("Combined one-shot job authority is unavailable.");
        var timeout = (int)Math.Clamp((request.Timeout ?? TimeSpan.FromMinutes(2)).TotalSeconds, 1, 300);
        var jobRequest = new OneShotJobRequest(request.CliType, request.Model, request.Prompt,
            request.ThinkingLevel, timeout, 65_536,
            request.InlineImages?.Select(image => new OneShotJobImage(image.Base64, image.MediaType)).ToArray(),
            request.CliType == "claude" ? request.ClaudeMaxOutputTokens : null);
        try
        {
            var client = standalone ? clients.CreateClient(TaskServerPlaneProxy.ClientName) : null;
            OneShotJobDto? job;
            if (standalone)
            {
                using var submitted = await client!.PostAsJsonAsync("/api/v1/one-shot-jobs", jobRequest, ct);
                submitted.EnsureSuccessStatusCode();
                job = await submitted.Content.ReadFromJsonAsync<OneShotJobDto>(cancellationToken: ct);
            }
            else job = await combinedJobs!.CreateAsync(jobRequest, ct);
            if (job is null) return Failed("Task Server returned no one-shot job.");
            if (job.Status == "waiting")
                return Failed(job.Error ?? $"waiting for a host with {job.Capability}") with { QuotaDeferred = true };
            // The Task Server owns queue expiry and extends job expiry at claim.
            // Start the caller's run budget only after a poll observes that claim.
            using var runBudget = new CancellationTokenSource(Timeout.InfiniteTimeSpan,
                timeProvider ?? TimeProvider.System);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, runBudget.Token);
            var running = false;
            while (true)
            {
                await Task.Delay(pollInterval ?? TimeSpan.FromSeconds(2),
                    timeProvider ?? TimeProvider.System, deadline.Token);
                OneShotJobDto? current;
                if (standalone)
                {
                    using var response = await client!.GetAsync($"/api/v1/one-shot-jobs/{job.JobId}", deadline.Token);
                    response.EnsureSuccessStatusCode();
                    current = await response.Content.ReadFromJsonAsync<OneShotJobDto>(cancellationToken: deadline.Token);
                }
                else current = await combinedJobs!.GetAsync(job.JobId, deadline.Token);
                if (current is null) return Failed("Task Server lost the one-shot job.");
                if (current.Status == "claimed" && !running)
                {
                    // Queue time belongs to admission. The provider gets its full
                    // requested run budget after the host claims the job.
                    runBudget.CancelAfter(TimeSpan.FromSeconds(timeout + 30));
                    running = true;
                }
                if (current.Status is "pending" or "claimed") continue;
                var ok = current.Status == "completed";
                var end = DateTime.UtcNow;
                var usage = current.Usage;
                var text = ok ? current.Text ?? string.Empty : string.Empty;
                return Finish(new CliOneShotResult(ok, ok ? 0 : -1, text, string.Empty,
                    end - start, text,
                    usage is null ? null : new OrchestratorTokenUsage
                    {
                        Model = usage.Model ?? request.Model,
                        InputTokens = checked((int)usage.InputTokens),
                        OutputTokens = checked((int)usage.OutputTokens),
                        CacheReadTokens = checked((int)usage.CacheReadTokens),
                        CacheCreationTokens = checked((int)usage.CacheCreationTokens),
                        CliType = request.CliType,
                        ThinkingLevel = request.ThinkingLevel,
                        ReasoningTokens = usage.ReasoningTokens,
                    }, null,
                    new AgentMessageLatency(RequestedAt: start, CompletedAt: end,
                        TotalMs: (long)(end - start).TotalMilliseconds),
                    ok ? null : current.Error ?? "Remote one-shot job failed."));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return Failed($"Remote one-shot job timed out after {timeout} seconds."); }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or ArgumentException)
        { return Failed($"Remote one-shot transport failed: {ex.Message}"); }
    }
}
