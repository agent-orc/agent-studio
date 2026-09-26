using AgentStudio.Bus;
using AgentStudio.Cli;
using AgentStudio.Clients;
using AgentStudio.Registry;
using AgentStudio.Runner;
using AgentStudio.Security;
using AgentStudio.Shared;
using AgentStudio.Tasks;

namespace AgentStudio.Tokens;

public sealed record UsageCliWindow(string Id, string Label, double? UsedPct, DateTime? ResetAtUtc,
    string? ResetLabel, string? SuspiciousReason);
public sealed record UsageCli(string CliId, bool Primary, string? Plan, string? Source,
    IReadOnlyList<UsageCliWindow> Windows, DateTime? FetchedAt, int TtlSeconds,
    bool Suspicious, string? SuspiciousReason, DateTime? ProbeFailedAt,
    bool? Limited, string? LimitedReason, UsageSourceState Availability);
public sealed record UsageRun(string Id, string ProjectId, string TaskId, string? TaskKey,
    DateTime? StartedAt, decimal? ProvisionalCostUsd, bool IncludedInTotals,
    string ConfiguredCli, string ConfiguredModel, string ConfiguredReasoning,
    string EffectiveCli, string EffectiveModel, string EffectiveReasoning, string? FallbackReason);
public sealed record UsageSlotPool(string Name, int? Occupied, int? Capacity, UsageSourceState Availability);
public sealed record UsageCockpitResponse(int SnapshotVersion, string WorkspaceId, string TimeZone,
    DayOfWeek WeekStart, DateTime GeneratedAt, UsageCalendar Calendar,
    IReadOnlyList<UsageCli> Clis, UsageCostProjection Cost, IReadOnlyList<UsageRun> Runs,
    IReadOnlyList<UsageSlotPool> Slots, IReadOnlyDictionary<string, UsageSourceState> Sources);

public static class UsageCockpitEndpoints
{
    public static void MapUsageCockpitEndpoints(this WebApplication app)
    {
        app.MapGet("/api/usage/cockpit", (HttpContext context, string? workspaceId,
            string? primaryCli, WorkspaceRegistry workspaces, WorkspaceSettingsService settings,
            ProjectRegistry projects, TaskScannerService scanner, QuotaService quota,
            BusBackedProjectTokenUsageReader ledger, AgentMessageBusStore bus,
            TaskRunnerService runner, ClientIdentityStore clients,
            AttemptAuthorityService reviews,
            IConfiguration configuration) =>
        {
            var workspace = string.IsNullOrWhiteSpace(workspaceId)
                ? workspaces.List().FirstOrDefault(item => item.IsDefault) ?? workspaces.List().FirstOrDefault()
                : workspaces.Find(workspaceId);
            if (workspace is null) return Results.NotFound(new { error = "Unknown workspace." });
            var allProjects = projects.List().Where(project => !project.Archived
                && string.Equals(project.WorkspaceId, workspace.Id, StringComparison.OrdinalIgnoreCase)).ToList();
            var human = context.Items[AccessSecurityMiddleware.HumanPrincipalItem] as HumanPrincipal;
            var visibleProjects = allProjects.Where(project => human is null
                || ProjectAccessAuthorization.Allows(human.User, project.Id, projects)).ToList();
            if (human is not null && human.User.Role != StudioRoles.Owner
                && human.User.Projects.Count > 0 && visibleProjects.Count == 0)
                return Results.Forbid();

            var now = DateTime.UtcNow;
            var sourceStates = new Dictionary<string, UsageSourceState>(StringComparer.Ordinal);
            UsageCalendar calendar;
            try
            {
                var calendarSettings = settings.Get(workspace.Id);
                calendar = UsageCockpitProjection.Calendar(now, calendarSettings.UsageTimeZone,
                    calendarSettings.UsageWeekStart);
            }
            catch (TimeZoneNotFoundException)
            {
                calendar = UsageCockpitProjection.Calendar(now, null, null);
                sourceStates["calendar"] = new UsageSourceState("partial", now, null,
                    "The configured workspace time zone is unavailable; UTC was used.");
            }
            catch (InvalidTimeZoneException)
            {
                calendar = UsageCockpitProjection.Calendar(now, null, null);
                sourceStates["calendar"] = new UsageSourceState("partial", now, null,
                    "The configured workspace time zone is invalid; UTC was used.");
            }
            catch (Exception)
            {
                calendar = UsageCockpitProjection.Calendar(now, null, null);
                sourceStates["calendar"] = new UsageSourceState("unavailable", null, null,
                    "The workspace calendar could not be read; UTC was used.");
            }
            sourceStates.TryAdd("calendar", new UsageSourceState("complete", now, null));
            var costInputs = new List<UsageCostInput>();
            foreach (var project in visibleProjects)
            {
                try
                {
                    var snapshot = ledger.LoadSnapshot(project.DisplayName, project.StorageLocation);
                    costInputs.Add(new UsageCostInput(project.Id, project.DisplayName,
                        snapshot.Entries, snapshot.Freshness,
                        snapshot.ReceiptSummaries.Values.Select(summary => summary.LastUpdate).Max()));
                }
                catch (Exception)
                {
                    costInputs.Add(new UsageCostInput(project.Id, project.DisplayName, [],
                        new ProjectTokenDataFreshness { Status = "unavailable",
                            Warning = "The project ledger could not be read." }));
                }
            }
            // Workspace-scoped one-shot usage has no project attribution. It is a
            // visible child, never a hidden addition to the workspace total.
            try
            {
                var root = configuration["TaskRepository"];
                if (string.IsNullOrWhiteSpace(root)) throw new InvalidOperationException();
                var messages = bus.Query(root, AgentMessageBusPaths.WorkspaceScope,
                    new AgentMessageQuery(Kind: "token-usage", Since: calendar.WeekStartUtc));
                var unattributed = new List<OrchestratorLogEntry>();
                var unassignedCount = 0;
                var canAssignUnattributed = workspace.IsDefault
                    && (human is null || human.User.Role == StudioRoles.Owner || human.User.Projects.Count == 0);
                foreach (var message in messages)
                {
                    var entry = BusTokenEntryConverter.ToEntry(message);
                    if (string.IsNullOrWhiteSpace(message.Project))
                    {
                        if (canAssignUnattributed) unattributed.Add(entry);
                        else unassignedCount++;
                        continue;
                    }
                    var project = visibleProjects.FirstOrDefault(project =>
                        string.Equals(project.Id, message.Project, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(project.DisplayName, message.Project, StringComparison.OrdinalIgnoreCase));
                    if (project is null) continue;
                    var index = costInputs.FindIndex(input => input.ProjectId == project.Id);
                    var current = costInputs[index];
                    costInputs[index] = current with
                    {
                        Entries = ProjectTokenReceiptReader.MergeWithoutDuplicates(current.Entries, [entry])
                    };
                }
                costInputs.Add(new UsageCostInput(null, "Unattributed", unattributed,
                    new ProjectTokenDataFreshness
                    {
                        Status = unassignedCount == 0 ? "complete" : "partial",
                        Sources = ["workspace-token-bus"],
                        Warning = unassignedCount == 0 ? null
                            : "Projectless workspace bus events cannot be assigned to this view."
                    }));
            }
            catch (Exception)
            {
                costInputs.Add(new UsageCostInput(null, "Unattributed", [],
                    new ProjectTokenDataFreshness { Status = "unavailable",
                        Warning = "The workspace token bus could not be read." }));
            }
            var cost = UsageCockpitProjection.Cost(calendar, costInputs);
            sourceStates["cost"] = cost.Coverage;

            var runnerState = TryRead(() => runner.GetStatus(), now);
            sourceStates["runs"] = runnerState.State;
            var visibleNames = visibleProjects.Select(project => project.DisplayName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var active = runnerState.Value?.Projects
                .Where(pair => visibleNames.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, ProjectRunnerStatus>(StringComparer.OrdinalIgnoreCase);
            var runs = new List<UsageRun>();
            foreach (var project in visibleProjects)
            {
                if (!active.TryGetValue(project.DisplayName, out var status)) continue;
                IReadOnlyList<ActiveRunStatus> activeRuns = status.ActiveRuns.Count > 0 ? status.ActiveRuns
                    : string.IsNullOrWhiteSpace(status.ActiveJobId) ? []
                    : [new ActiveRunStatus(status.ActiveJobId, null, status.ActiveExecution,
                        status.QuotaFallbackReason)];
                foreach (var activeRun in activeRuns)
                {
                    TaskInfo? task = null;
                    try { task = scanner.FindJob(activeRun.JobId, project.StorageLocation); }
                    catch (Exception) { sourceStates["runs"] = new UsageSourceState("partial", now, null,
                        "One active task could not be read."); }
                    var entries = costInputs.First(input => input.ProjectId == project.Id).Entries;
                    runs.Add(UsageCockpitProjection.Run(project,
                        status with { ActiveJobId = activeRun.JobId,
                            ActiveExecution = activeRun.Execution,
                            QuotaFallbackReason = activeRun.FallbackReason },
                        task, calendar, entries, activeRun.CliType));
                }
            }

            var quotaRead = TryRead(() => quota.GetCached(), now);
            sourceStates["quota"] = quotaRead.State;
            var primary = !string.IsNullOrWhiteSpace(primaryCli) && CliTypes.IsValid(primaryCli)
                ? primaryCli : visibleProjects.Select(project => project.CliDefault)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "codex";
            var limits = active.Values.SelectMany(status => status.ProviderLimits).ToList();
            var clis = quotaRead.Value?.Snapshots.Select(snapshot =>
            {
                var limited = limits.FirstOrDefault(limit => string.Equals(limit.CliType,
                    snapshot.CliType, StringComparison.OrdinalIgnoreCase));
                return ProjectCli(snapshot, quotaRead.Value.TtlSeconds,
                    string.Equals(snapshot.CliType, primary, StringComparison.OrdinalIgnoreCase),
                    now, limited, runnerState.Value is not null);
            }).ToList() ?? [];

            var remoteRead = TryRead(() => clients.ListAll().Where(client => client.Kind != ClientIdentityKind.Retired
                && string.Equals(client.RunnerDaemonState, "running", StringComparison.OrdinalIgnoreCase)
                && client.LastSeenAt is { } seen && now - seen.ToUniversalTime() <= TimeSpan.FromMinutes(2)
                && client.RunnerEffectiveMaxParallelism is > 0).ToList(), now);
            var remoteState = remoteRead.State with
            {
                ObservedAt = remoteRead.Value?.Select(client => client.LastSeenAt).Max(),
                TtlSeconds = 120
            };
            var reviewRead = TryRead(() => reviews.ListActiveReviewAttempts(), now);
            var slots = new List<UsageSlotPool>
            {
                new("remote", remoteRead.Value?.Sum(client => client.RunnerActiveSlots ?? 0),
                    remoteRead.Value?.Sum(client => client.RunnerEffectiveMaxParallelism ?? 0), remoteState),
                new("review", reviewRead.Value?.Count,
                    Math.Max(1, configuration.GetValue("ReviewDecisionOrchestrator:MaxParallelReviews",
                        ReviewDecisionOrchestrator.DefaultMaxParallelReviews)), reviewRead.State),
                new("auto", runnerState.Value is null ? null : active.Values.Sum(status => status.OccupiedSlots),
                    runnerState.Value is null ? null : active.Values.Sum(status => status.MaxParallelism),
                    runnerState.State),
            };
            sourceStates["slots"] = slots.All(slot => slot.Availability.Status == "complete")
                ? new UsageSourceState("complete", now, null)
                : new UsageSourceState("partial", now, null, "One slot source is unavailable.");
            return Results.Ok(new UsageCockpitResponse(1, workspace.Id, calendar.TimeZone,
                calendar.WeekStart, now, calendar, clis, cost, runs, slots, sourceStates));
        });
    }

    internal static (T? Value, UsageSourceState State) TryRead<T>(Func<T> read, DateTime now) where T : class
    {
        try { return (read(), new UsageSourceState("complete", now, null)); }
        catch (Exception) { return (null, new UsageSourceState("unavailable", null, null,
            "The source could not be read.")); }
    }

    internal static UsageCli ProjectCli(QuotaSnapshot snapshot, int configuredTtlSeconds,
        bool primary, DateTime now, ProviderLimitStatus? limited, bool limitSourceAvailable)
    {
        var ttl = configuredTtlSeconds > 0 ? configuredTtlSeconds : 600;
        var hasObservation = snapshot.Windows.Count > 0 || !string.IsNullOrWhiteSpace(snapshot.Plan)
            || !string.IsNullOrWhiteSpace(snapshot.Source)
            || !string.IsNullOrWhiteSpace(snapshot.RawSample);
        var available = !hasObservation || snapshot.FetchedAt == default ? "unavailable"
            : snapshot.Suspicious ? "suspicious"
            : snapshot.ProbeFailedAt is not null || now - snapshot.FetchedAt > TimeSpan.FromSeconds(ttl)
                ? "stale" : "complete";
        var windowIds = new Dictionary<string, int>(StringComparer.Ordinal);
        var windows = snapshot.Windows.Select(window =>
        {
            var key = snapshot.CliType + "/" + window.Label.Trim().ToLowerInvariant().Replace(' ', '-');
            windowIds.TryGetValue(key, out var ordinal);
            windowIds[key] = ordinal + 1;
            return new UsageCliWindow(ordinal == 0 ? key : $"{key}-{ordinal + 1}",
                window.Label, window.UsedPct, window.ResetAt, window.ResetLabel,
                window.ProjectionSuspiciousReason);
        }).ToList();
        return new UsageCli(snapshot.CliType, primary, snapshot.Plan, snapshot.Source,
            windows,
            !hasObservation || snapshot.FetchedAt == default ? null : snapshot.FetchedAt, ttl,
            snapshot.Suspicious, snapshot.SuspiciousReason, snapshot.ProbeFailedAt,
            limitSourceAvailable ? limited is not null : null, limited?.Reason,
            new UsageSourceState(available,
                !hasObservation || snapshot.FetchedAt == default ? null : snapshot.FetchedAt,
                ttl, snapshot.Error));
    }
}
