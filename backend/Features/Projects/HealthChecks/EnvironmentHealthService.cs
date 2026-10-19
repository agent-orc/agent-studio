using System.Text.Json;
using AgentStudio.Tasks;

namespace AgentStudio.Projects;

public sealed record EnvironmentHealthCheckView(string Id, string Kind, string Target, bool Derived,
    int ExpectedStatus, bool HasBodyMarker, int IntervalSeconds, int TimeoutSeconds, int FailureThreshold,
    int RecoveryThreshold, string Status, HealthSample? LastSample, int ConsecutiveFailures,
    double? Uptime24h, double? Uptime7d, double? Uptime30d, HealthLatency Latency24h,
    DateTimeOffset? CertificateNotAfter);

public sealed record EnvironmentHealthView(string Project, string EnvironmentId, string Name, string Kind,
    string Address, string Status, double? Uptime24h, double? Uptime7d, double? Uptime30d, HealthLatency Latency24h,
    HealthIncident? CurrentIncident, HealthIncident? LastIncident, IReadOnlyList<EnvironmentHealthCheckView> Checks);

public sealed record ProjectEnvironmentHealthResponse(string Project, DateTimeOffset CapturedAt, string ProbeOrigin,
    IReadOnlyList<EnvironmentHealthView> Environments);

public sealed record WorkspaceEnvironmentHealthResponse(DateTimeOffset CapturedAt, string ProbeOrigin,
    IReadOnlyList<EnvironmentHealthView> Environments);

public sealed record HealthRunSummary(int Probed, int Opened, int Closed, int CertificateWarnings);

/// <summary>
/// Runs the environment health checks from the control plane (IP-14) and reads uptime,
/// latency and incidents. A tick probes due checks through a bounded pool, then applies
/// each result in order: store the sample, decide with <see cref="HealthIncidentPolicy"/>,
/// persist the state, then report the incident change.
/// </summary>
public sealed class EnvironmentHealthService(TaskScannerService scanner, ProjectEnvironmentService environments,
    HealthCheckStore store, IHealthCheckProbe probe, HealthAlerts alerts, IConfiguration configuration,
    TimeProvider clock, ILogger<EnvironmentHealthService> logger)
{
    public const string ProbeOrigin = "control-plane";
    private readonly SemaphoreSlim _tick = new(1, 1);

    private sealed record DueCheck(string Project, EnvironmentDeclaration Environment, HealthCheckDefinition Check);

    public async Task<HealthRunSummary> RunDueAsync(CancellationToken cancellationToken = default)
    {
        await _tick.WaitAsync(cancellationToken);
        try
        {
            var now = clock.GetUtcNow();
            var due = new List<DueCheck>();
            var projects = Projects();
            foreach (var (project, declarations) in projects)
            {
                var state = store.Load(project);
                foreach (var environment in declarations)
                foreach (var check in HealthCheckDeclarations.Effective(environment))
                {
                    var last = state.Checks.GetValueOrDefault(HealthCheckStore.Key(environment.Id, check.Id))?.LastRunAt;
                    if (last is null || now - last >= TimeSpan.FromSeconds(check.IntervalSeconds))
                        due.Add(new(project, environment, check));
                }
            }
            var results = new HealthSample[due.Count];
            var parallelism = Math.Clamp(configuration.GetValue("HealthChecks:MaxConcurrency", 4), 1, 32);
            await Parallel.ForEachAsync(Enumerable.Range(0, due.Count),
                new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
                async (index, token) => results[index] = await probe.ProbeAsync(due[index].Check, token));

            var opened = 0;
            var closed = 0;
            var warnings = 0;
            foreach (var group in due.Select((item, index) => (item, sample: results[index])).GroupBy(row => row.item.Project))
            {
                var state = store.Load(group.Key);
                foreach (var (item, sample) in group)
                {
                    var change = Apply(item, sample, state);
                    if (change == HealthIncidentAction.Open) opened++;
                    if (change == HealthIncidentAction.Close) closed++;
                    if (WarnCertificate(item, sample, state)) warnings++;
                }
                store.Save(group.Key, state);
            }
            foreach (var (project, declarations) in projects) Reconcile(project, declarations);
            RunRetention(projects.Select(item => item.Project), now);
            return new(due.Count, opened, closed, warnings);
        }
        finally
        {
            _tick.Release();
        }
    }

    private HealthIncidentAction Apply(DueCheck item, HealthSample sample, ProjectHealthState projectState)
    {
        var key = HealthCheckStore.Key(item.Environment.Id, item.Check.Id);
        var state = projectState.Checks.TryGetValue(key, out var existing) ? existing : projectState.Checks[key] = new();
        store.Append(item.Project, item.Environment.Id, item.Check.Id, sample);
        var step = HealthIncidentPolicy.Next(
            new(state.ConsecutiveFailures, state.ConsecutiveSuccesses, state.OpenIncident is not null),
            sample.Ok, item.Check.FailureThreshold, item.Check.RecoveryThreshold);
        state.LastRunAt = sample.At;
        state.LastSample = sample;
        state.FailingSince = sample.Ok ? null : state.FailingSince ?? sample.At;
        state.ConsecutiveFailures = step.Streak.ConsecutiveFailures;
        state.ConsecutiveSuccesses = step.Streak.ConsecutiveSuccesses;
        switch (step.Action)
        {
            case HealthIncidentAction.Open:
                var started = state.FailingSince ?? sample.At;
                state.OpenIncident = new($"{item.Environment.Id}/{item.Check.Id}/{started.UtcDateTime:yyyyMMddTHHmmssZ}",
                    item.Environment.Id, item.Check.Id, started, sample.Error ?? "failed", step.Streak.ConsecutiveFailures);
                break;
            case HealthIncidentAction.Continue when state.OpenIncident is not null:
                state.OpenIncident = state.OpenIncident with { FailedProbes = state.OpenIncident.FailedProbes + 1 };
                break;
            case HealthIncidentAction.Close when state.OpenIncident is not null:
                state.LastIncident = state.OpenIncident with { EndedAt = sample.At };
                state.OpenIncident = null;
                break;
        }
        return step.Action;
    }

    private bool WarnCertificate(DueCheck item, HealthSample sample, ProjectHealthState projectState)
    {
        if (sample.CertificateNotAfter is not { } notAfter) return false;
        var state = projectState.Checks[HealthCheckStore.Key(item.Environment.Id, item.Check.Id)];
        if (state.CertificateNotAfter != notAfter)
        {
            state.CertificateNotAfter = notAfter;
            state.CertificateWarnedDays = [];
        }
        if (CertificateExpiryPolicy.Next(notAfter, clock.GetUtcNow(), state.CertificateWarnedDays) is not { } days)
            return false;
        if (!alerts.ReportCertificateExpiry(item.Project, item.Environment, item.Check, notAfter, days, state))
            return false;
        state.CertificateWarnedDays.Add(days);
        return true;
    }

    /// <summary>
    /// Completes the reports of incidents whose card or Activity entry is still missing,
    /// including those of an earlier tick that stopped between the save and the report.
    /// </summary>
    private void Reconcile(string project, IReadOnlyList<EnvironmentDeclaration> declarations)
    {
        var state = store.Load(project);
        var changed = false;
        foreach (var environment in declarations)
        foreach (var check in HealthCheckDeclarations.Effective(environment))
        {
            if (!state.Checks.TryGetValue(HealthCheckStore.Key(environment.Id, check.Id), out var checkState)) continue;
            if (checkState.OpenIncident is { } open && (open.CardId is null || !open.OpenedEventRaised))
            {
                var evidence = store.Samples(project, environment.Id, check.Id, open.StartedAt - TimeSpan.FromHours(1));
                checkState.OpenIncident = alerts.ReportOpened(project, environment, check, open, checkState, evidence);
                changed = true;
            }
            if (checkState.LastIncident is { RecoveryReported: false } last)
            {
                checkState.LastIncident = alerts.ReportRecovered(project, environment, check, last);
                changed = true;
            }
        }
        if (changed) store.Save(project, state);
    }

    private void RunRetention(IEnumerable<string> projects, DateTimeOffset now)
    {
        foreach (var project in projects)
        {
            var state = store.Load(project);
            if (state.LastRetentionAt is { } last && now - last < TimeSpan.FromHours(1)) continue;
            var folded = store.ApplyRetention(project, now);
            if (folded > 0)
                logger.LogInformation("environment-health retention project={Project} foldedDays={Folded}", project, folded);
            state.LastRetentionAt = now;
            store.Save(project, state);
        }
    }

    public ProjectEnvironmentHealthResponse? Read(string project)
    {
        var declarations = environments.Declarations(project);
        if (declarations is null) return null;
        var now = clock.GetUtcNow();
        return new(project, now, ProbeOrigin, View(project, declarations, store.Load(project), now));
    }

    public WorkspaceEnvironmentHealthResponse Overview()
    {
        var now = clock.GetUtcNow();
        return new(now, ProbeOrigin, Projects()
            .SelectMany(item => View(item.Project, item.Declarations, store.Load(item.Project), now))
            .OrderBy(item => item.Status switch { "down" => 0, "degraded" => 1, "unknown" => 2, _ => 3 })
            .ThenBy(item => item.Project, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.EnvironmentId, StringComparer.Ordinal).ToArray());
    }

    private IReadOnlyList<EnvironmentHealthView> View(string project, IReadOnlyList<EnvironmentDeclaration> declarations,
        ProjectHealthState state, DateTimeOffset now)
        => declarations.Select(environment =>
        {
            var checks = HealthCheckDeclarations.Effective(environment).Select(check =>
            {
                var checkState = state.Checks.GetValueOrDefault(HealthCheckStore.Key(environment.Id, check.Id));
                var samples = store.Samples(project, environment.Id, check.Id, now - HealthSeries.RawRetention);
                var status = checkState?.LastSample is null ? "unknown"
                    : checkState.OpenIncident is not null ? "down"
                    : checkState.LastSample.Ok ? "up" : "degraded";
                return new EnvironmentHealthCheckView(check.Id, check.Kind, check.Target, check.Derived,
                    check.ExpectedStatus, check.BodyContains is not null, check.IntervalSeconds, check.TimeoutSeconds,
                    check.FailureThreshold, check.RecoveryThreshold, status, checkState?.LastSample,
                    checkState?.ConsecutiveFailures ?? 0,
                    HealthSeries.Uptime(samples, now, TimeSpan.FromHours(24)),
                    HealthSeries.Uptime(samples, now, TimeSpan.FromDays(7)),
                    HealthSeries.Uptime(samples, now, HealthSeries.RawRetention),
                    HealthSeries.Latency(samples, now, TimeSpan.FromHours(24)), checkState?.CertificateNotAfter);
            }).ToArray();
            var states = checks.Select(check => state.Checks.GetValueOrDefault(HealthCheckStore.Key(environment.Id, check.Id)))
                .OfType<HealthCheckState>().ToArray();
            // The environment is as available as its worst check; latency is the first HTTP check's.
            var primary = checks.FirstOrDefault(check => check.Kind == "http") ?? checks.FirstOrDefault();
            var status = checks.Length == 0 || checks.All(check => check.Status == "unknown") ? "unknown"
                : checks.Any(check => check.Status == "down") ? "down"
                : checks.Any(check => check.Status is "degraded" or "unknown") ? "degraded" : "up";
            return new EnvironmentHealthView(project, environment.Id, environment.Name, environment.Kind,
                environment.Address, status, Worst(checks.Select(check => check.Uptime24h)),
                Worst(checks.Select(check => check.Uptime7d)), Worst(checks.Select(check => check.Uptime30d)),
                primary?.Latency24h ?? new(null, null, null),
                states.Select(item => item.OpenIncident).OfType<HealthIncident>().MinBy(item => item.StartedAt),
                states.Select(item => item.LastIncident).OfType<HealthIncident>().MaxBy(item => item.EndedAt),
                checks);
        }).ToArray();

    private static double? Worst(IEnumerable<double?> values)
    {
        var known = values.OfType<double>().ToArray();
        return known.Length == 0 ? null : known.Min();
    }

    private IReadOnlyList<(string Project, IReadOnlyList<EnvironmentDeclaration> Declarations)> Projects()
    {
        var result = new List<(string, IReadOnlyList<EnvironmentDeclaration>)>();
        foreach (var watch in scanner.GetWatchPaths())
        {
            try
            {
                if (environments.Declarations(watch.Name) is { Count: > 0 } declarations)
                    result.Add((watch.Name, declarations));
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "environment-health skipped project={Project}: declarations unreadable", watch.Name);
            }
        }
        return result;
    }
}
