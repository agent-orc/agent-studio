using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Supervisor;

/// <summary>
/// CLI-driven soft reasoning. Per project, every
/// <c>Supervisor:SoftReasoningIntervalSeconds</c> seconds (default 600), a
/// short Claude (or other CLI) session is spawned with the project state and
/// recent activity, asked to emit one or more
/// <c>[[SUPERVISOR_OBSERVATION: ...]]</c> sentinels, and any returned advisories
/// are appended to <c>observations.jsonl</c> with source
/// <see cref="SupervisorSource.SoftReasoning"/>.
/// </summary>
/// <remarks>
/// Off by default to avoid surprise token spend. Enable via
/// <c>Supervisor:SoftReasoningEnabled = true</c>. Hard rate-limit via
/// <c>Supervisor:SoftReasoningCallsPerHour</c> (default 60).
/// </remarks>
public sealed class SoftReasoningHostedService : BackgroundService
{
    private readonly TaskRunnerService _taskRunner;
    private readonly ProjectObservationService _observe;
    private readonly RuntimePromptService _prompts;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SoftReasoningHostedService> _logger;
    private readonly AgentMessageBusBridge? _bus;
    private readonly AdHocUsageRecorder? _usage;
    private readonly AgentStudio.Cli.CliOneShotRegistry? _oneShotRegistry;
    private readonly ReviewInputDecisionGateway? _reviewInputDecisions;
    private readonly TimeProvider _time;

    private readonly Queue<DateTime> _callTimestamps = new();

    public SoftReasoningHostedService(
        TaskRunnerService taskRunner,
        ProjectObservationService observe,
        RuntimePromptService prompts,
        IConfiguration configuration,
        ILogger<SoftReasoningHostedService> logger,
        AgentMessageBusBridge? bus = null,
        AdHocUsageRecorder? usage = null,
        AgentStudio.Cli.CliOneShotRegistry? oneShotRegistry = null,
        ReviewInputDecisionGateway? reviewInputDecisions = null,
        TimeProvider? time = null)
    {
        _taskRunner = taskRunner;
        _observe = observe;
        _prompts = prompts;
        _configuration = configuration;
        _logger = logger;
        _bus = bus;
        _usage = usage;
        _oneShotRegistry = oneShotRegistry;
        _reviewInputDecisions = reviewInputDecisions;
        _time = time ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workspace = _configuration["TaskRepository"];
        if (string.IsNullOrWhiteSpace(workspace))
        {
            _logger.LogWarning("TaskRepository not configured; SoftReasoningHostedService idle.");
            return;
        }

        var intervalSeconds = _configuration.GetValue("Supervisor:SoftReasoningIntervalSeconds", 600);

        try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); } catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_configuration.GetValue("Supervisor:SoftReasoningEnabled", false))
                    await TickOnceAsync(workspace!, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogWarning(ex, "SoftReasoning tick failed"); }

            intervalSeconds = _configuration.GetValue("Supervisor:SoftReasoningIntervalSeconds", 600);
            try { await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal async Task TickOnceAsync(string workspace, CancellationToken ct)
    {
        var status = _taskRunner.GetStatus();
        if (status?.Projects == null) return;

        var maxPerHour = _configuration.GetValue("Supervisor:SoftReasoningCallsPerHour", 60);
        var cliBinary = _configuration.GetValue("Supervisor:SoftReasoningCli", "claude");
        var model = _configuration.GetValue(
            "Supervisor:SoftReasoningModel", ModelFamilyResolver.Resolve(ModelFamilies.ClaudeHaiku));

        foreach (var (project, projectStatus) in status.Projects)
        {
            if (!RateLimitOk(maxPerHour))
            {
                _logger.LogInformation("SoftReasoning rate limit reached ({MaxPerHour}/h); skipping {Project}", maxPerHour, project);
                continue;
            }

            try
            {
                var observation = await _observe.ObserveAsync(project, ct);
                if (string.IsNullOrEmpty(observation.CurrentJobId)) continue; // nothing to reason about while idle
                if (_reviewInputDecisions?.EngineDecides == true)
                {
                    var summary = JsonSerializer.Serialize(new
                    {
                        observation.RunnerStatus,
                        observation.CurrentRunState,
                        observation.LastProgressAt,
                        observation.ErrorCounts,
                        decisions = observation.RecentDecisions.TakeLast(8),
                        observation.Omissions,
                    });
                    var digest = "sha256:" + Convert.ToHexStringLower(
                        System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(summary)));
                    _reviewInputDecisions.Submit(new ReviewInputDecisionRequest(
                        "supervision", project, observation.CurrentJobId, TaskStates.Progress,
                        $"{observation.CurrentJobId}:{_time.GetUtcNow():yyyyMMddHHmm}", summary, digest,
                        observation.Omissions.Select(reason => new GroundsOmissionV1("supervision", reason)).ToArray(),
                        observation.CurrentJobId));
                    continue;
                }
                var prompt = BuildPrompt(project, observation);
                var sw = AdHocClaudeInvoker.StartTiming();
                var rawOutput = await InvokeCliAsync(cliBinary, model, prompt, TimeSpan.FromSeconds(120), ct);
                sw.Stop();
                var (output, callUsage) = AdHocClaudeInvoker.ParseOrFallback(rawOutput, model);
                AdHocClaudeInvoker.Record(_usage, AdHocUsageSources.SoftReasoning, model, callUsage, sw.ElapsedMilliseconds, ok: true, project: project, jobId: observation.CurrentJobId);
                _callTimestamps.Enqueue(DateTime.UtcNow);
                var advisories = SoftReasoningParsing.Parse(output, project, DateTime.UtcNow, observation.CurrentJobId);
                foreach (var advisory in advisories)
                {
                    HardHealthCheckHostedService.AppendObservationRecord(workspace, advisory);
                    try { _ = _bus?.EmitAdvisoryAsync(advisory); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Bus mirror of soft-reasoning advisory failed for {Project}", project); }
                }
                _logger.LogInformation("SoftReasoning {Project}: {Count} observation(s)", project, advisories.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SoftReasoning failed for {Project}", project);
            }
        }
    }

    private bool RateLimitOk(int maxPerHour)
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromHours(1);
        while (_callTimestamps.Count > 0 && _callTimestamps.Peek() < cutoff) _callTimestamps.Dequeue();
        return _callTimestamps.Count < maxPerHour;
    }

    private string BuildPrompt(string project, SupervisorObservation o)
    {
        var samples = string.Join("\n", o.RecentAgentSamples.TakeLast(20));
        var decisions = string.Join("\n", o.RecentDecisions.Select(d =>
            $"{d.At:HH:mm:ss} [{d.Kind}] {d.Summary}" +
            (d.EvidenceRef is null ? "" : $" source={d.EvidenceRef}")));
        var values = new Dictionary<string, string?>
        {
            ["project"] = project,
            ["runner_status"] = o.RunnerStatus,
            ["current_job_id"] = o.CurrentJobId ?? string.Empty,
            ["current_run_state"] = o.CurrentRunState ?? string.Empty,
            ["last_progress_at"] = o.LastProgressAt?.ToString("u") ?? "-",
            ["error_cli"] = o.Omissions.Contains("errorCounts:raw-log")
                ? "unknown (raw log omitted)" : o.ErrorCounts.CliErrorsLastHour.ToString(),
            ["error_orch"] = o.Omissions.Contains("errorCounts:raw-log")
                ? "unknown (raw log omitted)" : o.ErrorCounts.OrchestratorErrorsLastHour.ToString(),
            ["error_failures"] = o.Omissions.Contains("errorCounts:raw-log")
                ? "unknown (raw log omitted)" : o.ErrorCounts.RunFailuresLastHour.ToString(),
            ["recent_samples"] = samples.Length == 0 ? "(omitted: raw agent stream)" : samples,
            ["recent_decisions"] = decisions,
            ["evidence_omissions"] = string.Join(", ", o.Omissions),
        };
        try
        {
            return _prompts.Render("supervisor-soft-reasoning.md", values) +
                "\nEvidence omissions: " + values["evidence_omissions"];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falling back to inline supervisor-soft-reasoning prompt template");
            return InlineFallback(values);
        }
    }

    private static string InlineFallback(Dictionary<string, string?> v) =>
        $"Project: {v["project"]}\nState: {v["runner_status"]} active={v["current_job_id"]}\nLast progress: {v["last_progress_at"]}\n\nRecent samples:\n{v["recent_samples"]}\n\nEmit observations as [[SUPERVISOR_OBSERVATION: severity=<info|warn|high>; topic=<tag>; message=<one-line>]] then [[TASK_DONE]].";

    /// <summary>
    /// Route to <see cref="AgentStudio.Cli.ICliOneShot"/>
    /// when the registry is wired; fall back to a local stdin-piped variant
    /// for test setups that build the service without DI. We removed the
    /// previous <c>-p &lt;prompt&gt;</c> argv path that caused the
    /// 2026-05-11 empty-reply incident.
    /// </summary>
    private async Task<string> InvokeCliAsync(string cli, string model, string prompt, TimeSpan timeout, CancellationToken ct)
    {
        var oneShot = _oneShotRegistry?.Get("claude");
        if (oneShot != null)
        {
            var r = await oneShot.RunAsync(new AgentStudio.Cli.CliOneShotRequest(
                CliType: "claude", Model: model, Prompt: prompt)
            {
                Timeout = timeout,
                Source = AdHocUsageSources.SoftReasoning,
                RecordUsage = false,
            }, ct);
            if (!r.Ok)
            {
                _logger.LogWarning(
                    "Soft-reasoning CLI call failed: exit={ExitCode} duration={Duration}ms error={Error}",
                    r.ExitCode, r.Duration.TotalMilliseconds, r.Error);
            }
            return CliOneShotCompatibility.ToClaudeResultEnvelope(r, model);
        }

        var psi = new ProcessStartInfo
        {
            FileName = cli,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("json");
        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add(model);
        psi.ArgumentList.Add("--dangerously-skip-permissions");

        using var p = Process.Start(psi);
        if (p == null) return string.Empty;
        try
        {
            await p.StandardInput.WriteAsync(prompt.AsMemory(), ct);
            p.StandardInput.Close();
        }
        catch (Exception __ex) { SilentCatch.Note(__ex, "SoftReasoningHostedService: stdin may already be closed"); /* stdin may already be closed */ }

        var stderrTask = p.StandardError.ReadToEndAsync(ct);
        var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await p.WaitForExitAsync(cts.Token);
            _ = await stderrTask;
            return await stdoutTask;
        }
        catch (OperationCanceledException)
        {
            AgentStudio.Diagnostics.CliKillAudit.Trace(p, "SoftReasoningHostedService:226 (entireProcessTree)");
            try { p.Kill(true); } catch (Exception __ex) { SilentCatch.Note(__ex, "SoftReasoningHostedService:229"); }
            try { await p.WaitForExitAsync(CancellationToken.None); } catch (Exception __ex) { SilentCatch.Note(__ex, "SoftReasoningHostedService: wait after kill"); }
            try { _ = await stderrTask; } catch (Exception __ex) { SilentCatch.Note(__ex, "SoftReasoningHostedService: drain stderr after kill"); }
            try { _ = await stdoutTask; } catch (Exception __ex) { SilentCatch.Note(__ex, "SoftReasoningHostedService: drain stdout after kill"); }
            return string.Empty;
        }
    }
}
