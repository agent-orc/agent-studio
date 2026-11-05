using AgentStudio.Cli;
using AgentStudio.Projects;
using AgentStudio.Registry;
using AgentStudio.Runner;
using AgentStudio.Security;
using AgentStudio.Shared;
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
public sealed record OperatorCapacityResponse(IReadOnlyList<CapacityQuota> Quota,
    IReadOnlyList<CapacityFallback> ActiveFallbacks, IReadOnlyList<CapacityWait> WaitingCards,
    IReadOnlyList<CapacityPin> ModelRoutingPins);

/// <summary>Read-only capacity projection over the quota cache and existing card and selection records.</summary>
public static class OperatorCapacityEndpoints
{
    public static void MapOperatorCapacityEndpoints(this WebApplication app)
    {
        app.MapGet("/api/operator/capacity", (HttpContext context, QuotaService quota,
            CliQuotaCapsService caps, TaskScannerService scanner, ProjectRegistry registry,
            TaskRunnerService runners, ModelSelectionLedger ledger, ModelSelectionPolicyStore policy,
            ProjectSettingsService settings, TimeProvider clock, CancellationToken ct) =>
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
            return Results.Ok(Build(report, tasks, active, policy.Get(), projectSettings,
                task => runners.GetQuotaFallbackForJob(task.Id, task.ProjectName) ?? task.QuotaFallback,
                snapshot => caps.Evaluate(snapshot).Blocked, clock.GetUtcNow().UtcDateTime));
        }).RequirePermission(IamPermissions.ProjectRead);
    }

    internal static OperatorCapacityResponse Build(QuotaReport report, IReadOnlyList<TaskInfo> tasks,
        IReadOnlyList<ModelSelectionEvent> active, ModelSelectionPolicy policy,
        IReadOnlyDictionary<string, ProjectSettings> settings,
        Func<TaskInfo, QuotaFallbackStatus?> fallbackOf,
        Func<QuotaSnapshot, bool> exhausted, DateTime now)
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
        return new OperatorCapacityResponse(quota, fallbacks, waits, pins);
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
