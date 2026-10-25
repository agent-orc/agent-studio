using System.Net;
using System.Text.RegularExpressions;

namespace AgentRunner.RunTargets;

/// <summary>Run-target settings read from the host environment (RUN_TARGET_*).</summary>
public sealed partial record RunTargetOptions(
    string HostId,
    string StateDirectory,
    string SecretDirectory,
    string SecretRuntimeDirectory,
    TimeSpan PollInterval,
    RunTargetReconcilerSettings Reconciler)
{
    /// <summary>Caddy 2.11 on Alpine (the caddy:2-alpine index on 2026-10-20), pinned like an app image.</summary>
    public const string DefaultEdgeImage =
        "caddy:2.11-alpine@sha256:d8542f48d34a9cf4e4c11a478865229840e87e4c96ea3f439101f31a5d35f75f";

    [GeneratedRegex(@"\A[^\s@{}""]+@[^\s@{}""]+\z")]
    private static partial Regex Email();

    public static RunTargetOptions From(RunnerOptions runner, Func<string, string?> environment)
    {
        string? Value(string name) => environment(name)?.Trim() is { Length: > 0 } value ? value : null;
        int Port(string name, int fallback)
            => Value(name) is { } text ? int.TryParse(text, out var port) && port is >= 1 and <= 65535
                ? port : throw new ArgumentException($"{name} must be a TCP port.") : fallback;

        var email = Value("RUN_TARGET_ACME_EMAIL");
        if (email is not null && !Email().IsMatch(email))
            throw new ArgumentException("RUN_TARGET_ACME_EMAIL is not an e-mail address.");
        var proxies = (Value("RUN_TARGET_CLOUDFLARE_TRUSTED_PROXIES") ?? "")
            .Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
        if (proxies.Any(item => !IPNetwork.TryParse(item, out _)))
            throw new ArgumentException("RUN_TARGET_CLOUDFLARE_TRUSTED_PROXIES must list CIDR ranges.");
        var certificates = Value("RUN_TARGET_CLOUDFLARE_CERT_DIR");
        if (certificates is not null && !Path.IsPathFullyQualified(certificates))
            throw new ArgumentException("RUN_TARGET_CLOUDFLARE_CERT_DIR must be an absolute path.");
        var poll = Value("RUN_TARGET_POLL_SECONDS") is { } pollText
            ? int.TryParse(pollText, out var seconds) && seconds is >= 2 and <= 3600
                ? seconds : throw new ArgumentException("RUN_TARGET_POLL_SECONDS must be between 2 and 3600.")
            : 15;
        var stateDirectory = Path.GetFullPath(Value("RUN_TARGET_STATE_DIR") ?? Path.Combine(runner.StateDir, "run-target"));
        var runtimeDirectory = Value("RUN_TARGET_SECRET_RUNTIME_DIR") ?? "/run/agent-host/run-target-secrets";
        if (!Path.IsPathFullyQualified(runtimeDirectory))
            throw new ArgumentException("RUN_TARGET_SECRET_RUNTIME_DIR must be an absolute path.");
        var edgeImage = Value("RUN_TARGET_EDGE_IMAGE") ?? DefaultEdgeImage;
        if (!RunTargetNames.IsDigest(edgeImage))
            throw new ArgumentException("RUN_TARGET_EDGE_IMAGE must be pinned by sha256 digest.");
        return new(
            runner.RunnerId,
            stateDirectory,
            Value("RUN_TARGET_SECRET_DIR") ?? "/etc/agent-host/secrets",
            runtimeDirectory,
            TimeSpan.FromSeconds(poll),
            new RunTargetReconcilerSettings(
                edgeImage,
                new RunTargetEdgeSettings(email, certificates is not null, proxies),
                certificates,
                Port("RUN_TARGET_HTTP_PORT", 80),
                Port("RUN_TARGET_HTTPS_PORT", 443)));
    }
}

/// <summary>
/// <c>RUNNER_ROLE=run-target</c>: polls Studio for the desired state of the
/// environments placed on this host, converges them, and reports observed state
/// and deploy-job results. It never listens on a port. A Studio outage changes
/// nothing Studio decides: polling backs off until Studio answers again, and
/// each failed poll runs a supervise-only pass from the host's own state, so
/// crash restarts, crash rollbacks, and secret files after a reboot do not wait
/// for Studio. No failure ends the daemon; only cancellation does.
/// </summary>
public sealed class RunTargetDaemon(
    IRunTargetControlPlane controlPlane,
    RunTargetReconciler reconciler,
    TimeSpan pollInterval,
    Func<TimeSpan, CancellationToken, Task> delay,
    Action<string> log)
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    public async Task RunAsync(CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await CycleAsync(ct);
                failures = 0;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                failures++;
                log($"run-target: cycle failed ({ex.GetType().Name}: {ex.Message}); attempt {failures}, running apps are unchanged");
            }
            var wait = failures == 0 ? pollInterval
                : TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, pollInterval.Ticks * (1L << Math.Min(failures, 10))));
            try { await delay(wait, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }

    public async Task CycleAsync(CancellationToken ct)
    {
        RunTargetDesiredState desired;
        try
        {
            desired = await controlPlane.GetDesiredStateAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            await SuperviseWithoutStudioAsync($"{ex.GetType().Name}: {ex.Message}", ct);
            throw;
        }
        var result = await reconciler.ReconcileAsync(desired, ct);
        foreach (var report in result.JobReports)
        {
            if (!await controlPlane.ReportJobAsync(report, ct))
                log($"run-target: Studio no longer holds deploy job {report.JobId}; the next poll reconciles it");
        }
        await controlPlane.PostObservedStateAsync(new(result.Observations), ct);
    }

    private async Task SuperviseWithoutStudioAsync(string reason, CancellationToken ct)
    {
        try
        {
            await reconciler.SuperviseOnlyAsync(reason, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log($"run-target: supervise-only pass failed ({ex.GetType().Name}: {ex.Message})");
        }
    }

    /// <summary>Production composition for <c>RUNNER_ROLE=run-target</c>.</summary>
    public static RunTargetDaemon Create(RunTargetOptions options, HttpClient http, Action<string> log)
    {
        static Task Delay(TimeSpan wait, CancellationToken ct) => Task.Delay(wait, TimeProvider.System, ct);
        var reconciler = new RunTargetReconciler(
            new DockerCliRunTarget(),
            new FileRunTargetSecretSource(options.SecretDirectory),
            new RunTargetSecretFiles(options.SecretRuntimeDirectory, RunTargetSecretFiles.ProcMountsIsMemoryBacked),
            new RunTargetStateStore(options.StateDirectory, log),
            options.Reconciler,
            TimeProvider.System,
            Delay,
            log);
        return new(new RunTargetClient(http, options.HostId), reconciler, options.PollInterval, Delay, log);
    }
}
