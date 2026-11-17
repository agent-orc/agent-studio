using AgentStudio.Deploy;
using AgentStudio.Telemetry;

namespace AgentStudio.Projects;

/// <summary>Release and masked telemetry captured when an environment incident opens.</summary>
public sealed record HealthIncidentContext(string ProjectId, string? ReleaseVersion, DateTime? DeployedAtUtc,
    IReadOnlyList<string> ReleaseCards, long? Restarts, IReadOnlyList<OperationsLogLine> ErrorLines);

public interface IHealthIncidentContext
{
    HealthIncidentContext Read(string project, string environmentId, DateTimeOffset startedAt);
}

public sealed class HealthIncidentContextReader(DeployService deploy, ProjectEnvironmentService environments,
    IOperationsTelemetryReader telemetry, TelemetryStore intake, TimeProvider clock) : IHealthIncidentContext
{
    public HealthIncidentContext Read(string project, string environmentId, DateTimeOffset startedAt)
    {
        var status = deploy.Status(project)?.FirstOrDefault(item => item.Environment.Id == environmentId);
        var until = clock.GetUtcNow();
        var since = startedAt < until.AddDays(-30) ? until.AddDays(-30) : startedAt;
        var metrics = telemetry.ReadMetrics(project, environmentId, since, until);
        var lines = telemetry.SearchLogs(new OperationsLogQuery(project, environmentId, since, until,
            null, status?.CurrentRelease?.Id, null, 100));
        // Operations telemetry is optional today. The built-in OTLP store and run-target
        // observation still provide real incident evidence when that adapter is absent.
        if (lines is null)
            lines = intake.QueryLogs(new TelemetryEnvironmentKey(project, environmentId),
                new LogQuery(TelemetryAdmissionPolicy.UnixNanos(since), TelemetryAdmissionPolicy.UnixNanos(until),
                    null, null, new Dictionary<string, string>(), 100, null))
                .Records.Reverse().Select(item => new OperationsLogLine(item.Time, item.Severity, item.Body,
                    item.Attributes.GetValueOrDefault("release.id"))).ToArray();
        // The telemetry reader owns masking. Keep only error or crash evidence in the card.
        var errors = lines?.Where(item => item.Level.Equals("error", StringComparison.OrdinalIgnoreCase)
                || item.Level.Equals("fatal", StringComparison.OrdinalIgnoreCase)
                || item.Message.Contains("crash", StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.At).TakeLast(5).ToArray() ?? [];
        return new(status?.Environment.ProjectId ?? project, status?.CurrentRelease?.Version,
            status?.CurrentDeployment?.FinishedAtUtc, status?.CurrentRelease?.CardKeys ?? [],
            metrics?.Restarts ?? environments.Read(project)?.Environments
                .FirstOrDefault(item => item.Declaration.Id == environmentId)?.Observed?.Restarts, errors);
    }
}
