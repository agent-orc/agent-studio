using AgentStudio.Tasks;

namespace AgentStudio.Projects;

public sealed record EnvironmentHealthCheckView(string Id, string Kind, string Target, bool Derived,
    int ExpectedStatus, bool HasBodyMarker, int IntervalSeconds, int TimeoutSeconds, int FailureThreshold,
    int RecoveryThreshold, string Status, HealthSample? LastSample, int ConsecutiveFailures,
    double? Uptime24h, double? Uptime7d, double? Uptime30d, HealthLatency Latency24h,
    DateTimeOffset? CertificateNotAfter);

public sealed record EnvironmentHealthView(string Project, string EnvironmentId, string Name, string Kind,
    string Address, string Status, double? Uptime24h, double? Uptime7d, double? Uptime30d, HealthLatency Latency24h,
    HealthIncident? CurrentIncident, HealthIncident? LastIncident, IReadOnlyList<EnvironmentHealthCheckView> Checks,
    string? Error = null, string? IncidentCardKey = null);

public sealed record ProjectEnvironmentHealthResponse(string Project, DateTimeOffset CapturedAt, string ProbeOrigin,
    IReadOnlyList<EnvironmentHealthView> Environments);

public sealed record WorkspaceEnvironmentHealthResponse(DateTimeOffset CapturedAt, string ProbeOrigin,
    IReadOnlyList<EnvironmentHealthView> Environments);

public sealed record HealthRunSummary(int Probed, int Opened, int Closed, int CertificateWarnings);

/// <summary>
/// Runs the environment health checks from the control plane (IP-14) and reads uptime,
/// latency and incidents. A tick probes due checks through a bounded pool, then applies
/// each result in order: store the sample, decide with <see cref="HealthIncidentPolicy"/>,
/// persist the state, then group the check incidents into environment incidents
/// (<see cref="EnvironmentIncidentPolicy"/>) and report their changes. Every project, and
/// every environment within the reports, fails on its own: a broken declaration or a failing
/// card write is logged and shown on its environment while the others keep being watched.
/// Only a deployed environment is probed: before its first healthy deploy report and after a
/// confirmed stop it reports <see cref="NotDeployed"/>, adds no samples and opens no incident.
/// </summary>
public sealed class EnvironmentHealthService(TaskScannerService scanner, ProjectEnvironmentService environments,
    HealthCheckStore store, IHealthCheckProbe probe, HealthAlerts alerts, IncidentWebhookService webhooks, IConfiguration configuration,
    TimeProvider clock, ILogger<EnvironmentHealthService> logger, TelemetryAlertReader? telemetry = null)
{
    public const string ProbeOrigin = "control-plane";
    /// <summary>The environment id under which a project whose declarations cannot be listed reports.</summary>
    public const string DeclarationsId = "declarations";
    /// <summary>The status of an environment and its checks while no deployment of it runs.</summary>
    public const string NotDeployed = "not-deployed";
    private const string Removed = "check-removed";
    private readonly SemaphoreSlim _tick = new(1, 1);

    private sealed record DueCheck(string Project, EnvironmentDeclaration Environment, HealthCheckDefinition Check);

    /// <param name="Readable">False when the declaration directory itself failed; its incidents stay as they are.</param>
    /// <param name="Running">The environments a host runs; null when the deploy state is unreadable, which keeps every declaration watched.</param>
    private sealed record ProjectEnvironments(string Project, IReadOnlyList<EnvironmentDeclaration> Valid,
        IReadOnlyList<EnvironmentDeclarationFile> Broken, bool Readable, IReadOnlySet<string>? Running = null)
    {
        public bool Deployed(EnvironmentDeclaration environment) => Running?.Contains(environment.Id) != false;

        /// <summary>The declared environments whose checks run.</summary>
        public IEnumerable<EnvironmentDeclaration> Watched => Valid.Where(Deployed);
    }

    public async Task<HealthRunSummary> RunDueAsync(CancellationToken cancellationToken = default)
    {
        await _tick.WaitAsync(cancellationToken);
        try
        {
            var now = clock.GetUtcNow();
            var projects = Projects();
            var due = new List<DueCheck>();
            foreach (var project in projects)
                Isolated(project.Project, "schedule", () => due.AddRange(DueChecks(project, now)));
            var results = new HealthSample[due.Count];
            var parallelism = Math.Clamp(configuration.GetValue("HealthChecks:MaxConcurrency", 4), 1, 32);
            await Parallel.ForEachAsync(Enumerable.Range(0, due.Count),
                new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
                async (index, token) => results[index] = await ProbeSafelyAsync(due[index], token));

            var opened = 0;
            var closed = 0;
            var warnings = 0;
            foreach (var group in due.Select((item, index) => (item, sample: results[index])).GroupBy(row => row.item.Project))
                Isolated(group.Key, "apply", () =>
                {
                    var state = store.Load(group.Key);
                    foreach (var (item, sample) in group)
                    {
                        var change = Apply(item, sample, state);
                        if (change == HealthIncidentAction.Open) opened++;
                        if (change == HealthIncidentAction.Close) closed++;
                        if (Isolated(group.Key, "certificate", () => WarnCertificate(item, sample, state)) == true) warnings++;
                    }
                    store.Save(group.Key, state);
                });
            if (telemetry is not null)
                foreach (var project in projects)
                foreach (var environment in project.Watched.Where(item => item.Kind == "production"))
                        foreach (var rule in environment.TelemetryAlerts.Rules)
                        {
                            Isolated(project.Project, "telemetry " + environment.Id + "/" + rule.Id, () =>
                            {
                                var state = store.Load(project.Project);
                                var check = TelemetryCheck(rule);
                                var observation = telemetry.Read(project.Project, environment.Id, rule,
                                    environment.TelemetryAlerts.StorageReservationBytes, now);
                                var sample = new HealthSample(now, observation.Value is not { } value
                                    || !TelemetryAlertPolicy.Firing(rule, value), null, null,
                                    observation.Value is null ? HealthSample.NotObserved
                                        : TelemetryAlertPolicy.Describe(rule, observation));
                                var action = Apply(new DueCheck(project.Project, environment, check), sample, state);
                                if (action == HealthIncidentAction.Open) opened++;
                                if (action == HealthIncidentAction.Close) closed++;
                                store.Save(project.Project, state);
                            });
                        }
            foreach (var project in projects) Isolated(project.Project, "report", () => Reconcile(project, now));
            await Task.WhenAll(projects.Select(project => NotifyDueAsync(project, cancellationToken)));
            foreach (var project in projects) Isolated(project.Project, "retention", () => RunRetention(project.Project, now));
            return new(due.Count, opened, closed, warnings);
        }
        finally
        {
            _tick.Release();
        }
    }

    private List<DueCheck> DueChecks(ProjectEnvironments project, DateTimeOffset now)
    {
        var state = store.Load(project.Project);
        var due = new List<DueCheck>();
        foreach (var environment in project.Watched)
        foreach (var check in HealthCheckDeclarations.Effective(environment))
        {
            var last = state.Checks.GetValueOrDefault(HealthCheckStore.Key(environment.Id, check.Id))?.LastRunAt;
            if (last is null || now - last >= TimeSpan.FromSeconds(check.IntervalSeconds))
                due.Add(new(project.Project, environment, check));
        }
        return due;
    }

    private async Task<HealthSample> ProbeSafelyAsync(DueCheck item, CancellationToken cancellationToken)
    {
        try
        {
            // A managed database publishes no port; its check reads the run host's reports (AGT-3878).
            if (item.Check.Kind == "database")
                return environments.DatabaseLivenessSample(item.Project, item.Environment.Id, item.Check.Host ?? "", clock.GetUtcNow());
            return await probe.ProbeAsync(item.Check, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "environment-health probe failed project={Project} environment={Environment} check={Check}",
                item.Project, item.Environment.Id, item.Check.Id);
            return new(clock.GetUtcNow(), false, null, null, HealthSample.ProbeFailed);
        }
    }

    private HealthIncidentAction Apply(DueCheck item, HealthSample sample, ProjectHealthState projectState)
    {
        var key = HealthCheckStore.Key(item.Environment.Id, item.Check.Id);
        var state = projectState.Checks.TryGetValue(key, out var existing) ? existing : projectState.Checks[key] = new();
        state.LastRunAt = sample.At;
        state.LastSample = sample;
        // A refused address or a probe fault says nothing about the target: shown on the check, not counted.
        if (!sample.MeasuresAvailability) return HealthIncidentAction.None;
        store.Append(item.Project, item.Environment.Id, item.Check.Id, sample);
        var streak = new HealthCheckStreak(state.ConsecutiveFailures, state.ConsecutiveSuccesses,
            state.OpenIncident is not null);
        var step = item.Check.Kind == "telemetry"
            ? TelemetryAlertPolicy.Next(streak, !sample.Ok, item.Check.RecoveryThreshold)
            : HealthIncidentPolicy.Next(streak, sample.Ok, item.Check.FailureThreshold,
                item.Check.RecoveryThreshold);
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
                state.LastIncident = state.OpenIncident with { EndedAt = sample.At, EndReason = "recovered" };
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
    /// Closes the incidents of checks that no longer run, groups the check incidents of each
    /// environment into one environment incident and completes every report that is still
    /// missing, including those of an earlier tick that stopped between the save and the report.
    /// </summary>
    private void Reconcile(ProjectEnvironments project, DateTimeOffset now)
    {
        // A project whose declarations cannot be listed keeps its incidents until it reads again.
        if (!project.Readable) return;
        var state = store.Load(project.Project);
        var declared = project.Valid.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var broken = project.Broken.Select(item => item.EnvironmentId).ToHashSet(StringComparer.Ordinal);
        var running = project.Watched.SelectMany(environment => EffectiveChecks(environment)
            .Select(check => HealthCheckStore.Key(environment.Id, check.Id))).ToHashSet(StringComparer.Ordinal);
        var changed = false;
        foreach (var (key, checkState) in state.Checks)
        {
            if (running.Contains(key) || broken.Contains(EnvironmentOf(key)) || checkState.OpenIncident is not { } open)
                continue;
            // A stopped environment closes its incidents; a check gone from the declaration closes its own.
            var reason = declared.TryGetValue(EnvironmentOf(key), out var environment) && !project.Deployed(environment)
                ? NotDeployed : Removed;
            checkState.LastIncident = open with { EndedAt = now, EndReason = reason };
            checkState.OpenIncident = null;
            checkState.ConsecutiveFailures = 0;
            changed = true;
        }
        var environmentIds = declared.Keys.Concat(state.Environments.Keys).Concat(state.Checks.Keys.Select(EnvironmentOf))
            .Where(id => !broken.Contains(id)).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var environmentId in environmentIds)
        {
            try
            {
                changed |= ReconcileEnvironment(project.Project, environmentId, declared.GetValueOrDefault(environmentId),
                    state, now);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "environment-health report failed project={Project} environment={Environment}; "
                    + "the next tick retries", project.Project, environmentId);
                changed = true;
            }
        }
        foreach (var key in state.Checks.Where(pair => !running.Contains(pair.Key) && !broken.Contains(EnvironmentOf(pair.Key))
                     && pair.Value.OpenIncident is null && pair.Value.LastIncident is null or { RecoveryReported: true })
                     .Select(pair => pair.Key).ToArray())
            changed |= state.Checks.Remove(key);
        foreach (var id in state.Environments.Where(pair => !declared.ContainsKey(pair.Key) && !broken.Contains(pair.Key)
                     && pair.Value.OpenIncident is null && pair.Value.LastIncident is null or { RecoveryReported: true, RecoveryCommentReported: not false })
                     .Select(pair => pair.Key).ToArray())
            changed |= state.Environments.Remove(id);
        if (changed) store.Save(project.Project, state);
    }

    private bool ReconcileEnvironment(string project, string environmentId, EnvironmentDeclaration? declaration,
        ProjectHealthState state, DateTimeOffset now)
    {
        var checks = state.Checks.Where(pair => EnvironmentOf(pair.Key) == environmentId)
            .Select(pair => (CheckId: pair.Key[(environmentId.Length + 1)..], State: pair.Value)).ToArray();
        var open = checks.Where(item => item.State.OpenIncident is not null)
            .OrderBy(item => item.State.OpenIncident!.StartedAt).ToArray();
        if (!state.Environments.TryGetValue(environmentId, out var environmentState))
        {
            if (open.Length == 0) return false;
            environmentState = state.Environments[environmentId] = new();
        }
        var changed = false;
        var action = EnvironmentIncidentPolicy.Next(environmentState.OpenIncident is not null, open.Length);
        if (action == EnvironmentIncidentAction.Open)
        {
            var started = open[0].State.OpenIncident!.StartedAt;
            environmentState.OpenIncident = new($"{environmentId}/{started.UtcDateTime:yyyyMMddTHHmmssZ}", environmentId,
                declaration?.Name ?? environmentId, started, EnvironmentKind: declaration?.Kind);
            changed = true;
        }
        if (action != EnvironmentIncidentAction.Close && environmentState.OpenIncident is { } incident)
        {
            var definitions = declaration is null ? new Dictionary<string, HealthCheckDefinition>()
                : EffectiveChecks(declaration).ToDictionary(check => check.Id, StringComparer.Ordinal);
            FailingCheck? Failing((string CheckId, HealthCheckState State) item)
                => definitions.GetValueOrDefault(item.CheckId) is { } check && item.State.OpenIncident is { } checkIncident
                    ? new(check, checkIncident, store.Samples(project, environmentId, item.CheckId,
                        checkIncident.StartedAt - TimeSpan.FromHours(1)))
                    : null;
            if (declaration is not null && (incident.CardId is null || !incident.OpenedEventRaised))
            {
                var hadCard = incident.CardId is not null;
                incident = environmentState.OpenIncident = alerts.ReportOpened(project, declaration, incident,
                    environmentState, open.Select(Failing).OfType<FailingCheck>().ToArray());
                // The new card (or the reopened one) shows every check that fails now.
                if (!hadCard && incident.CardId is { } cardId)
                    foreach (var item in open)
                        item.State.OpenIncident = item.State.OpenIncident! with { CardId = cardId, Reported = true };
                changed = true;
            }
            if (incident.CardId is not null)
                foreach (var item in open.Where(item => !item.State.OpenIncident!.Reported))
                {
                    if (Failing(item) is not { } failing || !alerts.ReportCheckFailing(project, incident, failing)) continue;
                    item.State.OpenIncident = item.State.OpenIncident! with { CardId = incident.CardId, Reported = true };
                    changed = true;
                }
            foreach (var item in checks.Where(item => item.State.LastIncident is { RecoveryReported: false }))
            {
                if (incident.CardId is not null && !alerts.ReportCheckRecovered(project, incident, item.State.LastIncident!))
                    continue;
                item.State.LastIncident = item.State.LastIncident! with { RecoveryReported = true };
                changed = true;
            }
        }
        if (action == EnvironmentIncidentAction.Close && environmentState.OpenIncident is { } closing)
        {
            var ended = Ended(checks, closing).Select(item => item.EndedAt!.Value).DefaultIfEmpty(now).Max();
            environmentState.LastIncident = closing with { EndedAt = ended };
            environmentState.OpenIncident = null;
            // The environment's recovery report covers the checks that recovered with it.
            foreach (var item in checks.Where(item => item.State.LastIncident is { RecoveryReported: false }))
                item.State.LastIncident = item.State.LastIncident! with { RecoveryReported = true };
            changed = true;
        }
        if (environmentState.LastIncident is { } last
            && (!last.RecoveryReported || last.RecoveryCommentReported != true || !last.RecoveryMoveReported))
        {
            environmentState.LastIncident = alerts.ReportRecovered(project, last, Ended(checks, last));
            changed = true;
        }
        if (environmentState.LastIncident is { EndedAt: not null } completed)
        {
            var index = environmentState.IncidentHistory.FindIndex(item => item.Id == completed.Id);
            if (index < 0 || environmentState.IncidentHistory[index] != completed)
            {
                if (index < 0) environmentState.IncidentHistory.Add(completed);
                else environmentState.IncidentHistory[index] = completed;
                changed = true;
            }
        }
        return changed;
    }

    private async Task NotifyDueAsync(ProjectEnvironments project, CancellationToken cancellationToken)
    {
        try
        {
            if (!webhooks.Get(project.Project).Configured) return;
            var state = store.Load(project.Project);
            foreach (var (environmentId, environmentState) in state.Environments)
            {
                var declaration = project.Valid.FirstOrDefault(item => item.Id == environmentId);
                foreach (var (last, recovered) in new[]
                {
                    (false, false), (true, false), (true, true),
                })
                {
                    var incident = last ? environmentState.LastIncident : environmentState.OpenIncident;
                    if (incident is null || (incident.EnvironmentKind ?? declaration?.Kind) != "production"
                        || (recovered && (incident.EndedAt is null || incident.RecoveryWebhookDelivered))
                        || (!recovered && incident.OpenedWebhookDelivered)) continue;
                    var signal = string.Join(", ", state.Checks
                        .Where(item => item.Key.StartsWith(environmentId + "/", StringComparison.Ordinal)
                            && (item.Value.OpenIncident?.StartedAt >= incident.StartedAt
                                || item.Value.LastIncident?.StartedAt >= incident.StartedAt))
                        .Select(item => item.Key[(environmentId.Length + 1)..])
                        .OrderBy(item => item, StringComparer.Ordinal));
                    if (string.IsNullOrEmpty(signal)) signal = "environment-health";
                    if (!await webhooks.DeliverAsync(project.Project, recovered ? "recovered" : "opened",
                        environmentId, incident.EnvironmentName, signal, incident.StartedAt, incident.CardId,
                        cancellationToken)) continue;
                    var delivered = recovered ? incident with { RecoveryWebhookDelivered = true }
                        : incident with { OpenedWebhookDelivered = true };
                    if (last) environmentState.LastIncident = delivered;
                    else environmentState.OpenIncident = delivered;
                    store.Save(project.Project, state);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "environment-health webhook reporting failed project={Project}; next tick retries",
                project.Project);
        }
    }

    /// <summary>The check incidents that ended within an environment incident.</summary>
    private static HealthIncident[] Ended(IEnumerable<(string CheckId, HealthCheckState State)> checks,
        EnvironmentIncident incident)
        => checks.Select(item => item.State.LastIncident)
            .OfType<HealthIncident>()
            .Where(item => item.EndedAt >= incident.StartedAt && (incident.EndedAt is null || item.EndedAt <= incident.EndedAt))
            .OrderBy(item => item.EndedAt).ToArray();

    private static string EnvironmentOf(string key) => key[..key.IndexOf('/', StringComparison.Ordinal)];

    private void RunRetention(string project, DateTimeOffset now)
    {
        var state = store.Load(project);
        if (state.LastRetentionAt is { } last && now - last < TimeSpan.FromHours(1)) return;
        var folded = store.ApplyRetention(project, now);
        if (folded > 0)
            logger.LogInformation("environment-health retention project={Project} foldedDays={Folded}", project, folded);
        foreach (var environment in state.Environments.Values)
            environment.IncidentHistory.RemoveAll(item => item.EndedAt < now.AddDays(-31));
        state.LastRetentionAt = now;
        store.Save(project, state);
    }

    /// <returns>Whether the action finished; a failure is logged and the next phase or project continues.</returns>
    private T? Isolated<T>(string project, string phase, Func<T> action) where T : struct
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "environment-health {Phase} failed project={Project}; the other projects continue",
                phase, project);
            return null;
        }
    }

    private void Isolated(string project, string phase, Action action)
        => Isolated(project, phase, () =>
        {
            action();
            return true;
        });

    public ProjectEnvironmentHealthResponse? Read(string project)
    {
        if (Environments(project) is not { } declarations) return null;
        var now = clock.GetUtcNow();
        return new(project, now, ProbeOrigin, Views(declarations, now));
    }

    public WorkspaceEnvironmentHealthResponse Overview()
    {
        var now = clock.GetUtcNow();
        return new(now, ProbeOrigin, Projects()
            .SelectMany(item => Views(item, now))
            .OrderBy(item => item.Status switch { "down" => 0, "invalid" => 1, "degraded" => 2, "unknown" => 3, NotDeployed => 4, _ => 5 })
            .ThenBy(item => item.Project, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.EnvironmentId, StringComparer.Ordinal).ToArray());
    }

    private IReadOnlyList<EnvironmentHealthView> Views(ProjectEnvironments project, DateTimeOffset now)
    {
        IReadOnlyList<EnvironmentHealthView> valid;
        try
        {
            valid = View(project, store.Load(project.Project), now);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "environment-health state unreadable project={Project}", project.Project);
            valid = project.Valid.Select(environment => Invalid(project.Project, environment.Id, environment.Name,
                environment.Kind, HealthTargets.Display(environment.Address), "The health state of this project could not be read.")).ToArray();
        }
        return valid.Concat(project.Broken.Select(file => Invalid(project.Project, file.EnvironmentId, file.EnvironmentId,
            "unknown", "", file.Error ?? "The declaration could not be read."))).ToArray();
    }

    private static EnvironmentHealthView Invalid(string project, string id, string name, string kind, string address,
        string error)
        => new(project, id, name, kind, address, "invalid", null, null, null, new(null, null, null), null, null, [], error);

    private IReadOnlyList<EnvironmentHealthView> View(ProjectEnvironments declarations, ProjectHealthState state,
        DateTimeOffset now)
        => declarations.Valid.Select(environment =>
        {
            var project = declarations.Project;
            var deployed = declarations.Deployed(environment);
            var definitions = EffectiveChecks(environment);
            var checks = definitions.Select(check =>
            {
                var checkState = state.Checks.GetValueOrDefault(HealthCheckStore.Key(environment.Id, check.Id));
                // 24 hours come from raw samples; 7 and 30 days from the hourly aggregates plus the raw tail.
                var hourly = store.Hourly(project, environment.Id, check.Id);
                var aggregated = HealthSeries.AggregatedUntil(hourly) ?? now - HealthSeries.RawRetention;
                var rawSince = new[] { aggregated, now - TimeSpan.FromHours(24) }.Min();
                var samples = store.Samples(project, environment.Id, check.Id,
                    rawSince < now - HealthSeries.RawRetention ? now - HealthSeries.RawRetention : rawSince);
                var status = !deployed ? NotDeployed
                    : checkState?.LastSample is not { } last ? check.Kind == "telemetry" ? "not-observed" : "unknown"
                    : checkState.OpenIncident is not null ? "down"
                    : last.Error == HealthSample.NotObserved ? "not-observed"
                    : last.Error == HealthSample.AddressBlocked ? "invalid"
                    : !last.MeasuresAvailability ? "unknown"
                    : last.Ok ? "up" : "degraded";
                return new EnvironmentHealthCheckView(check.Id, check.Kind, HealthTargets.Display(check.Target), check.Derived,
                    check.ExpectedStatus, check.BodyContains is not null, check.IntervalSeconds, check.TimeoutSeconds,
                    check.FailureThreshold, check.RecoveryThreshold, status, checkState?.LastSample,
                    checkState?.ConsecutiveFailures ?? 0,
                    HealthSeries.Uptime(samples, now, TimeSpan.FromHours(24)),
                    HealthSeries.Uptime(hourly, samples, now, TimeSpan.FromDays(7)),
                    HealthSeries.Uptime(hourly, samples, now, HealthSeries.RawRetention),
                    HealthSeries.Latency(samples, now, TimeSpan.FromHours(24)), checkState?.CertificateNotAfter);
            }).ToArray();
            var states = checks.Select(check => state.Checks.GetValueOrDefault(HealthCheckStore.Key(environment.Id, check.Id)))
                .OfType<HealthCheckState>().ToArray();
            // The environment is as available as its worst check; latency is the first HTTP check's.
            var primary = checks.FirstOrDefault(check => check.Kind == "http") ?? checks.FirstOrDefault();
            // A managed database before its first deploy has nothing to watch yet (AGT-3878).
            var counted = checks.Where(check => check.LastSample?.Error != HealthSample.NotApplicable).ToArray();
            var status = !deployed ? NotDeployed
                : counted.Length == 0 || counted.All(check => check.Status == "unknown") ? "unknown"
                : counted.Any(check => check.Status == "down") ? "down"
                : counted.Any(check => check.Status == "invalid") ? "invalid"
                : counted.Any(check => check.Status == "not-observed") ? "not-observed"
                : counted.Any(check => check.Status is "degraded" or "unknown") ? "degraded" : "up";
            var current = states.Select(item => item.OpenIncident).OfType<HealthIncident>().MinBy(item => item.StartedAt);
            return new EnvironmentHealthView(project, environment.Id, environment.Name, environment.Kind,
                HealthTargets.Display(environment.Address), status, Worst(checks.Select(check => check.Uptime24h)),
                Worst(checks.Select(check => check.Uptime7d)), Worst(checks.Select(check => check.Uptime30d)),
                primary?.Latency24h ?? new(null, null, null), current,
                states.Select(item => item.LastIncident).OfType<HealthIncident>().MaxBy(item => item.EndedAt),
                checks, status == "invalid" ? AddressProblem(environment, definitions, checks) : null,
                CardKey(project, current?.CardId));
        }).ToArray();

    /// <summary>The key the board shows for an incident card (NS-2), not its folder slug.</summary>
    public string? CardKey(string project, string? cardId)
    {
        if (cardId is null) return null;
        var watchPath = scanner.GetWatchPaths().FirstOrDefault(item =>
            string.Equals(item.Name, project, StringComparison.OrdinalIgnoreCase))?.Path;
        return watchPath is null ? null : scanner.FindJob(cardId, watchPath)?.Key;
    }

    /// <summary>The fix for each refused target; the card shows it instead of a bare Misconfigured.</summary>
    private static string? AddressProblem(EnvironmentDeclaration environment,
        IReadOnlyList<HealthCheckDefinition> definitions, IReadOnlyList<EnvironmentHealthCheckView> checks)
    {
        var problems = definitions.Zip(checks)
            .Where(pair => pair.Second.Status == "invalid" && pair.Second.LastSample?.Error == HealthSample.AddressBlocked)
            .Select(pair => HealthAddressProblems.Describe(environment, pair.First)).ToArray();
        return problems.Length == 0 ? null : string.Join(" ", problems);
    }

    private static IReadOnlyList<HealthCheckDefinition> EffectiveChecks(EnvironmentDeclaration environment)
        => environment.Kind == "production"
            ? [.. HealthCheckDeclarations.Effective(environment), .. environment.TelemetryAlerts.Rules.Select(TelemetryCheck)]
            : HealthCheckDeclarations.Effective(environment);

    private static HealthCheckDefinition TelemetryCheck(TelemetryAlertDefinition rule)
        => new("telemetry-" + rule.Id, "telemetry", null, rule.Id, null, 0, null, 15, 0, 1,
            rule.RecoveryEvaluations, Derived: true);

    private static double? Worst(IEnumerable<double?> values)
    {
        var known = values.OfType<double>().ToArray();
        return known.Length == 0 ? null : known.Min();
    }

    private IReadOnlyList<ProjectEnvironments> Projects()
        => scanner.GetWatchPaths().Select(watch => Environments(watch.Name))
            .OfType<ProjectEnvironments>()
            .Where(item => item.Valid.Count > 0 || item.Broken.Count > 0 || store.HasState(item.Project))
            .ToArray();

    /// <returns>Null for an unknown project; a project whose declarations fail reports that on one entry.</returns>
    private ProjectEnvironments? Environments(string project)
    {
        try
        {
            if (environments.DeclarationFiles(project) is not { } files) return null;
            var running = environments.RunningEnvironments(project);
            if (running is null)
                logger.LogWarning("environment-health deploy state unreadable project={Project}; every declaration stays watched", project);
            return new(project, files.Select(item => item.Declaration).OfType<EnvironmentDeclaration>().ToArray(),
                files.Where(item => item.Declaration is null).ToArray(), Readable: true, running);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "environment-health declarations unreadable project={Project}", project);
            return new(project, [], [new(DeclarationsId, null,
                $"The environment declarations could not be read ({ex.GetType().Name}).")], Readable: false);
        }
    }
}
