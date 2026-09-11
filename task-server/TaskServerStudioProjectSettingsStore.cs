using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>
/// Per-project CLI permission/context modes, lane-sort strategy, quota wait
/// policy, and the all-projects settings map. Every route here is GET-only in
/// this bundle (the matching PUT/write routes are a later bundle), so every
/// project currently resolves to its documented platform default - the same
/// defaults the legacy resolver falls back to when a project has no explicit
/// override on disk. "Known project" reuses <see cref="RequireProjectAsync"/>,
/// the same project-identity check the P0 bundle already established, instead
/// of the legacy filesystem watch-paths list.
/// </summary>
public sealed partial class TaskServerStore
{
    public async Task<StudioProjectCliModesResponse> GetProjectCliModesAsync(string projectIdentity, CancellationToken ct)
    {
        await RequireProjectAsync(projectIdentity, ct);
        var resolved = StudioCliTypes.All.ToDictionary(
            cli => cli,
            cli => new StudioCliModeResolution(StudioCliPermissionModes.Yolo, "default", StudioCliPermissionFlags.For(cli, StudioCliPermissionModes.Yolo)),
            StringComparer.OrdinalIgnoreCase);
        return new StudioProjectCliModesResponse(resolved, new Dictionary<string, string>(StringComparer.Ordinal), StudioCliPermissionModes.UserVisible);
    }

    public async Task<StudioProjectCliContextModesResponse> GetProjectCliContextModesAsync(string projectIdentity, CancellationToken ct)
    {
        await RequireProjectAsync(projectIdentity, ct);
        var resolved = StudioCliTypes.All.ToDictionary(
            cli => cli,
            cli => new StudioCliContextModeResolution(StudioCliContextModes.Clean, "default", StudioCliContextModes.SupportsClean(cli)),
            StringComparer.OrdinalIgnoreCase);
        return new StudioProjectCliContextModesResponse(resolved, new Dictionary<string, string>(StringComparer.Ordinal), StudioCliContextModes.UserVisible);
    }

    public async Task<StudioLaneSortStrategiesResponse> GetLaneSortStrategiesAsync(string projectIdentity, CancellationToken ct)
    {
        await RequireProjectAsync(projectIdentity, ct);
        var resolved = StudioTaskLanes.All.ToDictionary(
            lane => lane, _ => StudioLaneSortStrategies.LaneEntry, StringComparer.Ordinal);
        return new StudioLaneSortStrategiesResponse(resolved, new Dictionary<string, string>(StringComparer.Ordinal), StudioLaneSortStrategies.UserVisible);
    }

    public async Task<StudioProjectCliQuotaWaitPolicy> GetProjectQuotaWaitPolicyAsync(string projectIdentity, CancellationToken ct)
    {
        await RequireProjectAsync(projectIdentity, ct);
        var global = await GetCliQuotaWaitPolicyAsync(ct);
        return new StudioProjectCliQuotaWaitPolicy(
            global.Enabled, global.ThresholdMinutes, Source: "global",
            ProjectEnabled: null, ProjectThresholdMinutes: null,
            GlobalEnabled: global.Enabled, GlobalThresholdMinutes: global.ThresholdMinutes);
    }

    public async Task<IReadOnlyDictionary<string, StudioProjectSettingsEntry>> GetAllProjectSettingsAsync(CancellationToken ct)
    {
        var projects = await ListProjectsAsync(null, ct);
        var laneDefaults = StudioTaskLanes.All.ToDictionary(lane => lane, _ => StudioLaneSortStrategies.LaneEntry, StringComparer.Ordinal);
        var cliDefaults = StudioCliTypes.All.ToDictionary(
            cli => cli,
            cli => new StudioCliModeResolution(StudioCliPermissionModes.Yolo, "default", StudioCliPermissionFlags.For(cli, StudioCliPermissionModes.Yolo)),
            StringComparer.OrdinalIgnoreCase);
        return projects.ToDictionary(
            project => project.Name,
            _ => new StudioProjectSettingsEntry(
                AutoCommit: true,
                CrashRecoveryEnabled: true,
                AutoPushStrategy: "on-completed",
                RunnerMode: null,
                PickupMode: "manual",
                ExecutionLocation: "local",
                ExecutionRunner: null,
                RemoteExecutionEnabled: false,
                IntegrationBranch: "develop",
                MaxParallelism: 1,
                OrchestratorModel: null,
                LaneSortStrategies: laneDefaults,
                CliModes: cliDefaults),
            StringComparer.Ordinal);
    }

    public async Task<StudioPipelineCatalogue> GetPipelineCatalogueAsync(
        string? projectName, string? pipelineType, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(projectName))
            await RequireProjectAsync(projectName, ct);
        return new StudioPipelineCatalogue(
            StudioPipelineCatalogue_Steps.StandardPipelineId,
            pipelineType,
            DetectedStacks: [],
            Steps: StudioPipelineCatalogue_Steps.StandardSteps);
    }
}

/// <summary>
/// Ported from <c>backend/Shared/Models/ProjectSettings.cs</c>'s
/// <c>LaneSortStrategies</c>: strategy ids for the per-lane sort control.
/// Every lane defaults to <see cref="LaneEntry"/> (most-recently-entered on
/// top, drag-pinned cards clustered) until a project sets an override.
/// </summary>
internal static class StudioLaneSortStrategies
{
    public const string Manual = "manual";
    public const string NewestFirst = "newest-first";
    public const string OldestFirst = "oldest-first";
    public const string LastActivity = "last-activity";
    public const string LaneEntry = "lane-entry";

    public static readonly string[] UserVisible = [LaneEntry, Manual, NewestFirst, OldestFirst, LastActivity];
}

/// <summary>
/// A reduced, real subset of <c>backend/Features/Pipeline/PipelineCatalogue.cs</c>'s
/// standard-task-pipeline: the loop guard, the core agent run, and the four
/// parallel aspect verdicts - the load-bearing steps named in that file's own
/// class doc comment. The full 1108-line step catalogue (per-stack tool
/// steps, read-only/concept/UI pipeline variants, per-step model/prompt
/// resolution against a live checkout) has not moved to the standalone Task
/// Server; <c>applicable</c> is left at its default (true) rather than
/// probing a repository checkout the Task Server does not have.
/// </summary>
internal static class StudioPipelineCatalogue_Steps
{
    public const string StandardPipelineId = "standard-task-pipeline";

    public static readonly IReadOnlyList<StudioPipelineCatalogueStep> StandardSteps =
    [
        new("pre-loop-guard", "Auto-mode loop guard", "module", "pre",
            UsesModel: false, UsesPrompt: false, SupportsMode: false, CanDisable: false,
            DefaultEnabled: true, SupportsCondition: false),
        new("core-agent-run", "Coding agent run", "core", "core",
            UsesModel: true, UsesPrompt: true, SupportsMode: false, CanDisable: false,
            DefaultEnabled: true, SupportsCondition: false),
        new("aspect-requirement-fit", "Aspect: requirement fit", "aspect", "post",
            UsesModel: true, UsesPrompt: true, SupportsMode: false, CanDisable: true,
            DefaultEnabled: true, SupportsCondition: true),
        new("aspect-code-quality", "Aspect: code quality", "aspect", "post",
            UsesModel: true, UsesPrompt: true, SupportsMode: false, CanDisable: true,
            DefaultEnabled: true, SupportsCondition: true),
        new("aspect-documentation-impact", "Aspect: documentation impact", "aspect", "post",
            UsesModel: true, UsesPrompt: true, SupportsMode: false, CanDisable: true,
            DefaultEnabled: true, SupportsCondition: true),
        new("aspect-tests-and-evidence", "Aspect: tests & evidence", "aspect", "post",
            UsesModel: true, UsesPrompt: true, SupportsMode: false, CanDisable: true,
            DefaultEnabled: true, SupportsCondition: true),
        new("post-review-decision", "Orchestrator review decision", "orchestrator", "post",
            UsesModel: true, UsesPrompt: true, SupportsMode: false, CanDisable: false,
            DefaultEnabled: true, SupportsCondition: false),
    ];
}
