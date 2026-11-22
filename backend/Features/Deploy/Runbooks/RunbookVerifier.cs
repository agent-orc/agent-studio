using System.Globalization;
using AgentStudio.Projects;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Deploy;

/// <param name="Detail">One sentence that names the check, its target and what it observed.</param>
public sealed record RunbookVerificationOutcome(bool Passed, string Detail);

/// <summary>
/// Checks the <c>verify</c> target of a runbook after its scripts succeeded. A script that
/// exits 0 says nothing about the application; a fresh probe of a named health check, or a
/// telemetry alert rule that must not fire, does. Health checks get a few attempts, because
/// a restarted service needs a moment to answer; the run fails when none passes.
/// </summary>
public sealed class RunbookVerifier(ProjectEnvironmentService environments, IHealthCheckProbe probe,
    TimeProvider clock, IConfiguration? configuration = null, TelemetryAlertReader? telemetry = null)
{
    private readonly int _attempts = Math.Clamp(configuration?.GetValue("Runbooks:VerifyAttempts", 6) ?? 6, 1, 30);
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(
        Math.Clamp(configuration?.GetValue("Runbooks:VerifyIntervalSeconds", 10) ?? 10, 0, 120));

    /// <returns>Null when the environment declares the target; otherwise why the runbook cannot verify it.</returns>
    public string? Problem(string project, string environmentId, ProjectRunbookVerification verification)
    {
        var environment = Environment(project, environmentId);
        if (environment is null) return $"Environment {environmentId} has no readable declaration to verify against.";
        if (verification.Kind == ProjectRunbookVerification.HealthCheck)
            return Check(environment, verification.Target) is null
                ? $"Environment {environment.Name} declares no health check {verification.Target}." : null;
        if (Rule(environment, verification.Target) is null)
            return $"Environment {environment.Name} declares no alert rule {verification.Target}.";
        return telemetry is null ? "Telemetry is not available on this installation." : null;
    }

    public async Task<RunbookVerificationOutcome> VerifyAsync(string project, string environmentId,
        ProjectRunbookVerification verification, CancellationToken cancellationToken)
    {
        if (Problem(project, environmentId, verification) is { } problem) return new(false, problem);
        var environment = Environment(project, environmentId)!;
        return verification.Kind == ProjectRunbookVerification.HealthCheck
            ? await VerifyHealthAsync(project, environment, Check(environment, verification.Target)!, cancellationToken)
            : VerifyAlert(project, environment, Rule(environment, verification.Target)!);
    }

    private async Task<RunbookVerificationOutcome> VerifyHealthAsync(string project, EnvironmentDeclaration environment,
        HealthCheckDefinition check, CancellationToken cancellationToken)
    {
        var name = $"Health check {check.Id} ({HealthTargets.Display(check.Target)})";
        HealthSample? last = null;
        var started = clock.GetUtcNow();
        for (var attempt = 1; attempt <= _attempts; attempt++)
        {
            if (attempt > 1 && _interval > TimeSpan.Zero) await Task.Delay(_interval, clock, cancellationToken);
            last = check.Kind == "database"
                ? environments.DatabaseLivenessSample(project, environment.Id, check.Host ?? "", clock.GetUtcNow())
                : await probe.ProbeAsync(check, cancellationToken);
            if (last.Ok) return new(true, $"{name} passed: {Observed(last)}.");
        }
        var elapsed = (int)Math.Round((clock.GetUtcNow() - started).TotalSeconds);
        var attempts = _attempts == 1 ? "1 attempt" : $"{_attempts} attempts over {elapsed} s";
        return new(false, $"{name} did not pass in {attempts}: {Observed(last!)}.");
    }

    private RunbookVerificationOutcome VerifyAlert(string project, EnvironmentDeclaration environment,
        TelemetryAlertDefinition rule)
    {
        var observation = telemetry!.Read(project, environment.Id, rule, environment.TelemetryAlerts.StorageReservationBytes,
            clock.GetUtcNow());
        var name = $"Alert rule {rule.Id}";
        if (observation.Value is not { } value)
            return new(false, $"{name} could not be evaluated: no telemetry was observed in the last {rule.WindowMinutes} minutes.");
        return TelemetryAlertPolicy.Firing(rule, value)
            ? new(false, $"{name} is firing: {TelemetryAlertPolicy.Describe(rule, observation)}.")
            : new(true, $"{name} is not firing: {TelemetryAlertPolicy.Describe(rule, observation)}.");
    }

    private static string Observed(HealthSample sample)
    {
        var latency = sample.LatencyMs is { } ms ? $" after {ms.ToString(CultureInfo.InvariantCulture)} ms" : "";
        if (sample.Status is { } status) return $"answered {status.ToString(CultureInfo.InvariantCulture)}{latency}";
        return sample.Error is { } error ? $"{error}{latency}" : sample.Ok ? $"reachable{latency}" : $"no answer{latency}";
    }

    private EnvironmentDeclaration? Environment(string project, string environmentId)
        => environments.DeclarationFiles(project)?.Select(file => file.Declaration)
            .FirstOrDefault(item => item?.Id == environmentId);

    private static HealthCheckDefinition? Check(EnvironmentDeclaration environment, string id)
        => HealthCheckDeclarations.Effective(environment).FirstOrDefault(check => check.Id == id);

    private static TelemetryAlertDefinition? Rule(EnvironmentDeclaration environment, string id)
        => environment.TelemetryAlerts.Rules.FirstOrDefault(rule => rule.Id == id);
}
