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
    TimeProvider? clock = null) : ICliOneShot
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
                    result.Ok, request.Project, request.JobId);
            return result;
        }
        CliOneShotResult Failed(string error) =>
            Finish(CliOneShotResult.SpawnFailure(error, start, DateTime.UtcNow));
        if (!TaskServerPlaneProxy.IsConfigured(configuration))
            return Failed("Networked one-shot execution requires the standalone Task Server.");
        var timeout = (int)Math.Clamp((request.Timeout ?? TimeSpan.FromMinutes(2)).TotalSeconds, 1, 300);
        var jobRequest = new OneShotJobRequest(request.CliType, request.Model, request.Prompt,
            request.ThinkingLevel, timeout, 65_536,
            request.InlineImages?.Select(image => new OneShotJobImage(image.Base64, image.MediaType)).ToArray());
        try
        {
            var client = clients.CreateClient(TaskServerPlaneProxy.ClientName);
            using var submitted = await client.PostAsJsonAsync("/api/v1/one-shot-jobs", jobRequest, ct);
            submitted.EnsureSuccessStatusCode();
            var job = await submitted.Content.ReadFromJsonAsync<OneShotJobDto>(cancellationToken: ct);
            if (job is null) return Failed("Task Server returned no one-shot job.");
            if (job.Status == "waiting") return Failed(job.Error ?? $"waiting for a host with {job.Capability}");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var time = clock ?? TimeProvider.System;
            // The queue may use its whole admission window. A claim starts a new
            // provider budget on the server, so the caller must renew its budget
            // when it first observes that claim.
            using var deadlineTimer = time.CreateTimer(_ => deadline.Cancel(), null,
                TimeSpan.FromSeconds(timeout + 35), Timeout.InfiniteTimeSpan);
            var sawClaim = false;
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), time, deadline.Token);
                using var response = await client.GetAsync($"/api/v1/one-shot-jobs/{job.JobId}", deadline.Token);
                response.EnsureSuccessStatusCode();
                var current = await response.Content.ReadFromJsonAsync<OneShotJobDto>(cancellationToken: deadline.Token);
                if (current is null) return Failed("Task Server lost the one-shot job.");
                if (current.Status == "pending") continue;
                if (current.Status == "claimed")
                {
                    if (!sawClaim)
                    {
                        deadlineTimer.Change(TimeSpan.FromSeconds(timeout + 30), Timeout.InfiniteTimeSpan);
                        sawClaim = true;
                    }
                    continue;
                }
                if (current.Status != "completed") return Failed(current.Error ?? "Remote one-shot job failed.");
                var end = DateTime.UtcNow;
                var usage = current.Usage;
                return Finish(new CliOneShotResult(true, 0, current.Text ?? string.Empty, string.Empty,
                    end - start, current.Text ?? string.Empty,
                    usage is null ? null : new OrchestratorTokenUsage
                    {
                        Model = usage.Model ?? request.Model,
                        InputTokens = checked((int)usage.InputTokens),
                        OutputTokens = checked((int)usage.OutputTokens),
                        CacheReadTokens = checked((int)usage.CacheReadTokens),
                        CacheCreationTokens = checked((int)usage.CacheCreationTokens),
                        CliType = request.CliType,
                        ThinkingLevel = request.ThinkingLevel,
                    }, null,
                    new AgentMessageLatency(RequestedAt: start, CompletedAt: end,
                        TotalMs: (long)(end - start).TotalMilliseconds), null));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return Failed($"Remote one-shot job timed out after {timeout} seconds."); }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        { return Failed($"Remote one-shot transport failed: {ex.Message}"); }
    }
}
