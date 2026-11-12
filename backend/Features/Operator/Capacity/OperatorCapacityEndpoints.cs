using AgentStudio.Cli;
using AgentStudio.Pipeline;
using AgentStudio.Projects;
using AgentStudio.Registry;
using AgentStudio.Runner;
using AgentStudio.Security;
using AgentStudio.Shared;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.Tasks;

namespace AgentStudio.Operator;

public sealed record CapacityQuota(string CliType, QuotaSnapshot Snapshot, bool Fresh,
    DateTime? FreshUntil, bool Exhausted);
public sealed record CapacityWaitPolicy(bool Enabled, int ThresholdMinutes, string Scope);
public sealed record CapacityWait(string Project, string CardKey, string CliType, string Reason,
    DateTime StartedAt, DateTime ResetAt, CapacityWaitPolicy WaitPolicy);
public sealed record CapacityFallback(string? Project, string? CardKey, string? SourceCli,
    string? SourceModel, string? TargetCli, string? TargetModel, string Reason,
    DateTimeOffset? ActivatedAt, string SettingKey, string SettingHref);
public sealed record CapacityPin(string Project, string Consumer, string? PipelineType,
    string? StepId, string? CliType, string? Model, string? ThinkingLevel, string PinMode);
/// <summary>
/// A Ready card whose next pickup would not run its configured route, as the
/// selection service answers it now. <c>Outcome</c> is <c>fallback</c>,
/// <c>wait</c> or <c>throttle</c>; the target is the route a fallback would
/// launch, or the configured route a wait holds for. <c>NextResetAt</c> is when a
/// wait asks again or a fallback may return to the configured route.
/// </summary>
public sealed record CapacityQueuedCard(string Project, string CardKey, string? Title,
    string? ConfiguredCli, string? ConfiguredModel, string? ConfiguredThinkingLevel, string Outcome,
    string? TargetCli, string? TargetModel, string? TargetThinkingLevel, string Reason, DateTime? NextResetAt);
public sealed record OperatorCapacityResponse(IReadOnlyList<CapacityQuota> Quota,
    IReadOnlyList<CapacityFallback> ActiveFallbacks, IReadOnlyList<CapacityWait> WaitingCards,
    IReadOnlyList<CapacityPin> ModelRoutingPins)
{
    public IReadOnlyList<CapacityQueuedCard> QueuedCards { get; init; } = [];
}

/// <summary>The configured route of a card and the admission plan its next pickup would get.</summary>
public sealed record CapacityForecast(SelectionRoute Configured, QuotaAdmissionPlan Plan);

/// <summary>Read-only capacity projection over the quota cache and existing card and selection records.</summary>
public static class OperatorCapacityEndpoints
{
    public static void MapOperatorCapacityEndpoints(this WebApplication app)
    {
        app.MapGet("/api/operator/capacity", (HttpContext context, QuotaService quota,
            CliQuotaCapsService caps, TaskScannerService scanner, ProjectRegistry registry,
            TaskRunnerService runners, ModelSelectionLedger ledger, ModelSelectionPolicyStore policy,
            ProjectSettingsService settings, IModelSelectionService selection, TimeProvider clock,
            CancellationToken ct) =>
        {
            var scoped = context.Items[AccessSecurityMiddleware.RequestAccessItem]
                is RequestAccess access
                && !access.Allows(IamPermissions.WorkspaceOperate, PermissionTarget.WorkspaceWide);
            var report = scoped ? ScopedQuotaProjection.Redact(quota.GetCached())
                : quota.GetWithBackgroundRefresh(ct);
            var projects = scanner.GetWatchPaths().Select(entry => entry.Name)
                .Where(project => ProjectAccessAuthorization.AllowsTasks(
                    context, [project], IamPermissions.ProjectRead, registry))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var tasks = scanner.GetResidentIdentityFacts()
                .Where(task => projects.Contains(task.ProjectName)).ToArray();
            var active = ledger.Active().Where(entry =>
                entry.Kind is "switched" or "operator-switch"
                && (entry.Project is null ? !scoped : projects.Contains(entry.Project))).ToArray();
            var projectSettings = projects.ToDictionary(project => project, settings.Get,
                StringComparer.OrdinalIgnoreCase);
            var now = clock.GetUtcNow().UtcDateTime;
            var qualification = context.RequestServices.GetService<ModelQualificationService>();
            return Results.Ok(Build(report, tasks, active, policy.Get(), projectSettings,
                task => runners.GetQuotaFallbackForJob(task.Id, task.ProjectName) ?? task.QuotaFallback,
                snapshot => caps.Evaluate(snapshot).Blocked, now,
                task => Forecast(selection, qualification, task, tasks, now)));
        }).RequirePermission(IamPermissions.ProjectRead);
    }

    /// <summary>
    /// Asks the selection service the question the next local pickup asks, as a
    /// preview: no ledger receipt is written, so reading capacity never changes routing.
    /// </summary>
    internal static CapacityForecast Forecast(IModelSelectionService selection,
        ModelQualificationService? qualification, TaskInfo task, IReadOnlyList<TaskInfo> tasks, DateTime now)
    {
        var occupied = tasks.Count(other => other.State == TaskStates.Progress
            && string.Equals(other.ProjectName, task.ProjectName, StringComparison.OrdinalIgnoreCase));
        var decision = selection.Select(CodingSelectionCalls.For(task, task.ProjectName, occupied, now,
            qualification is null ? null : () => qualification.CorrectnessFloor(task,
                ProjectRunner.ReadPromptText(Path.Combine(task.FolderPath, "prompt.md"))),
            preview: true));
        return new CapacityForecast(decision.Result.Primary,
            ProjectRunner.ApplyPendingModelRoute(decision.Admission, task.PendingIntent?.ModelFallback));
    }

    internal static OperatorCapacityResponse Build(QuotaReport report, IReadOnlyList<TaskInfo> tasks,
        IReadOnlyList<ModelSelectionEvent> active, ModelSelectionPolicy policy,
        IReadOnlyDictionary<string, ProjectSettings> settings,
        Func<TaskInfo, QuotaFallbackStatus?> fallbackOf,
        Func<QuotaSnapshot, bool> exhausted, DateTime now,
        Func<TaskInfo, CapacityForecast?>? forecastOf = null)
    {
        var quota = report.Snapshots.Select(snapshot => new CapacityQuota(snapshot.CliType, snapshot,
            snapshot.FetchedAt != default && snapshot.Error is null
                && now - snapshot.FetchedAt <= TimeSpan.FromSeconds(report.TtlSeconds),
            snapshot.FetchedAt == default ? null : snapshot.FetchedAt.AddSeconds(report.TtlSeconds),
            exhausted(snapshot))).ToArray();
        var waits = tasks.Where(task => task.QuotaWait is not null).Select(task =>
        {
            var wait = task.QuotaWait!;
            return new CapacityWait(task.ProjectName, CardKey(task), wait.CliType, wait.Reason,
                wait.StartedAt, wait.ResetAt,
                new CapacityWaitPolicy(true, wait.ThresholdMinutes, wait.Scope));
        }).OrderBy(wait => wait.Project).ThenBy(wait => wait.CardKey).ToArray();

        var fallbacks = new List<CapacityFallback>();
        var represented = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in tasks.Where(task => task.State == TaskStates.Progress))
        {
            var status = fallbackOf(task);
            if (status is null) continue;
            var card = CardKey(task);
            var eventRow = active.Where(entry => entry.Project == task.ProjectName && entry.TaskKey == card)
                .OrderByDescending(entry => entry.At).FirstOrDefault();
            var marker = QuotaFallbackMarker.TryRead(task.FolderPath);
            var sourceCli = eventRow?.From?.CliType
                ?? (marker?.ModelFallback is null ? null : CliForModel(marker.ModelFallback.From));
            var sourceModel = eventRow?.From?.Model ?? marker?.ModelFallback?.From;
            if (sourceCli is null)
            {
                var profile = policy.LegacyProfiles.Values.FirstOrDefault(profile =>
                    string.Equals(profile.FallbackCliType, status.CliType, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(profile.FallbackModel, status.Model, StringComparison.OrdinalIgnoreCase));
                sourceCli = profile?.CliType;
                sourceModel ??= profile?.PrimaryModel;
            }
            var settingKey = eventRow is not null ? ConsumerSettingKey(policy, eventRow.Consumer)
                : sourceCli is not null && policy.LegacyProfiles.ContainsKey(sourceCli)
                    ? "legacyProfiles." + sourceCli : "consumers.coding";
            fallbacks.Add(new CapacityFallback(task.ProjectName, card, sourceCli, sourceModel,
                status.CliType, status.Model, status.Reason ?? eventRow?.Reason ?? "quota fallback",
                status.ActivatedAt is { } at ? new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc)) : eventRow?.At,
                settingKey, "/api/model-selection/policy"));
            represented.Add(task.ProjectName + "|" + card);
        }
        foreach (var entry in active.Where(entry => entry.Project is null || entry.TaskKey is null
                     || !represented.Contains(entry.Project + "|" + entry.TaskKey)))
            fallbacks.Add(new CapacityFallback(entry.Project, entry.TaskKey, entry.From?.CliType,
                entry.From?.Model, entry.To?.CliType, entry.To?.Model, entry.Reason, entry.At,
                ConsumerSettingKey(policy, entry.Consumer), "/api/model-selection/policy"));

        var pins = settings.SelectMany(pair => Pins(pair.Key, pair.Value))
            .OrderBy(pin => pin.Project).ThenBy(pin => pin.Consumer)
            .ThenBy(pin => pin.PipelineType).ThenBy(pin => pin.StepId).ToArray();
        return new OperatorCapacityResponse(quota, fallbacks, waits, pins)
        {
            QueuedCards = forecastOf is null ? [] : QueuedCards(tasks, forecastOf),
        };
    }

    private static CapacityQueuedCard[] QueuedCards(IReadOnlyList<TaskInfo> tasks,
        Func<TaskInfo, CapacityForecast?> forecastOf)
    {
        var rows = new List<CapacityQueuedCard>();
        foreach (var task in tasks.Where(task => task.State == TaskStates.Ready))
        {
            var forecast = forecastOf(task);
            var outcome = forecast?.Plan.Outcome switch
            {
                QuotaAdmissionOutcome.LaunchFallback => "fallback",
                QuotaAdmissionOutcome.Wait => "wait",
                QuotaAdmissionOutcome.Throttle => "throttle",
                _ => null,
            };
            if (forecast is null || outcome is null) continue;
            var (configured, plan) = (forecast.Configured, forecast.Plan);
            rows.Add(new CapacityQueuedCard(task.ProjectName, CardKey(task), task.Title,
                configured.CliType, configured.Model, configured.ThinkingLevel, outcome,
                plan.CliType, plan.Model, plan.ThinkingLevel, plan.Reason, plan.NextResetAt));
        }
        return rows.OrderBy(row => row.Project, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.CardKey, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IEnumerable<CapacityPin> Pins(string project, ProjectSettings settings)
    {
        if (settings.ModelSelectionOverrides is { } overrides)
            foreach (var (consumer, value) in overrides)
                foreach (var pin in OverridePins(project, consumer, null, null, value)) yield return pin;
        if (settings.PipelineStepsByType is { } byType)
            foreach (var (pipelineType, steps) in byType)
                foreach (var (stepId, step) in steps)
                    if (step.Model is not null || step.CliType is not null)
                        yield return new CapacityPin(project, "pipeline-step", pipelineType, stepId,
                            step.CliType, step.Model, step.ThinkingLevel, step.PinMode);
        if (settings.PipelineSteps is { } legacy)
            foreach (var (stepId, step) in legacy)
                if (step.Model is not null || step.CliType is not null)
                    yield return new CapacityPin(project, "pipeline-step", null, stepId,
                        step.CliType, step.Model, step.ThinkingLevel, step.PinMode);
    }

    private static IEnumerable<CapacityPin> OverridePins(string project, string consumer,
        string? pipelineType, string? stepId, ModelSelectionOverride value)
    {
        if (value.Primary is { } primary)
            yield return new CapacityPin(project, consumer, pipelineType, stepId,
                primary.CliType, primary.Model, primary.ThinkingLevel, value.PinMode);
        if (value.ByPipelineType is { } byType)
            foreach (var (type, nested) in byType)
                foreach (var pin in OverridePins(project, consumer, type, stepId, nested)) yield return pin;
        if (value.ByStep is { } byStep)
            foreach (var (step, nested) in byStep)
                foreach (var pin in OverridePins(project, consumer, pipelineType, step, nested)) yield return pin;
    }

    private static string CardKey(TaskInfo task) => task.Key ?? (task.TaskKey.Length > 0 ? task.TaskKey : task.Id);
    private static string ConsumerSettingKey(ModelSelectionPolicy policy, string consumer)
        => "consumers." + (policy.Consumers.ContainsKey(consumer) ? consumer
            : consumer.StartsWith("one-shot:", StringComparison.Ordinal) ? "one-shot" : consumer);
    private static string? CliForModel(string model) => model.StartsWith("claude-", StringComparison.OrdinalIgnoreCase)
        ? CliTypes.Claude : model.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase) ? CliTypes.Codex : null;
}
