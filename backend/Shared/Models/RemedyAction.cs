namespace AgentStudio.Shared;

/// <summary>
/// AGT-3580 - the typed way an operator acts on a remedy. A way out of a
/// pickup hold or a readiness remedy used to be a sentence with an HTTP line
/// or a shell command in it, so the operator copied it into a terminal. The
/// UI keys a button or a link off this record instead; command text, where
/// a remedy has no UI equivalent, travels separately as a copyable detail.
/// </summary>
/// <param name="Kind">One of <see cref="RemedyActionKinds"/>.</param>
/// <param name="Target">
/// The route id (<see cref="RemedyRoutes"/>), the project setting key
/// (<see cref="ProjectSettingKeys"/>), or the API action id
/// (<see cref="RemedyApiActions"/>), depending on <paramref name="Kind"/>.
/// </param>
/// <param name="Project">The project the action is about, when it has one.</param>
/// <param name="TaskKey">
/// The stable key of the task the action acts on, when it is another task than
/// the one the remedy is shown on; null means the task in view.
/// </param>
public sealed record RemedyAction(
    string Kind,
    string Target,
    string? Project = null,
    string? TaskKey = null)
{
    public static RemedyAction Route(string route, string? project = null, string? taskKey = null)
        => new(RemedyActionKinds.Route, route, project, taskKey);

    public static RemedyAction Setting(string settingKey, string project)
        => new(RemedyActionKinds.Setting, settingKey, project);

    public static RemedyAction Api(string apiAction, string? project = null, string? taskKey = null)
        => new(RemedyActionKinds.Api, apiAction, project, taskKey);
}

public static class RemedyActionKinds
{
    /// <summary>Navigate to a Studio surface (<see cref="RemedyRoutes"/>).</summary>
    public const string Route = "route";

    /// <summary>Open the project setting that owns the fix (<see cref="ProjectSettingKeys"/>).</summary>
    public const string Setting = "setting";

    /// <summary>Call an existing API route through a button (<see cref="RemedyApiActions"/>).</summary>
    public const string Api = "api";
}

/// <summary>Studio surfaces a remedy can navigate to.</summary>
public static class RemedyRoutes
{
    /// <summary>The task named by <see cref="RemedyAction.TaskKey"/>.</summary>
    public const string Task = "task";

    /// <summary>Workspace settings, Execution Hosts.</summary>
    public const string ExecutionHosts = "execution-hosts";

    /// <summary>Workspace settings, GitHub.</summary>
    public const string GitHubSettings = "github-settings";
}

/// <summary>Project settings a remedy can open, keyed as the settings page marks them.</summary>
public static class ProjectSettingKeys
{
    /// <summary>Execution placement, <c>PUT /api/projects/{name}/execution-runner</c>.</summary>
    public const string ExecutionRunner = "execution-runner";

    /// <summary><c>PUT /api/projects/{name}/repository-visibility</c>.</summary>
    public const string RepositoryVisibility = "repository-visibility";

    /// <summary><c>PUT /api/projects/{name}/integration-branch</c>.</summary>
    public const string IntegrationBranch = "integration-branch";
}

/// <summary>Existing API routes a remedy button calls.</summary>
public static class RemedyApiActions
{
    /// <summary><c>PUT /api/projects/{name}/execution-runner</c> with a runner picked in the UI.</summary>
    public const string SetExecutionRunner = "set-execution-runner";

    /// <summary><c>PUT /api/tasks/{id}/waits-on</c>: drop or re-point one edge.</summary>
    public const string EditWaitsOn = "edit-waits-on";

    /// <summary>Move the task to <c>7-archive</c>.</summary>
    public const string ArchiveTask = "archive-task";

    /// <summary><c>PUT /api/tasks/{id}/release</c> on <see cref="RemedyAction.TaskKey"/>.</summary>
    public const string ReleaseTask = "release-task";

    /// <summary><c>DELETE /api/tasks/{id}/dispatch-rejection</c>: clear the refusal so the next tick re-offers.</summary>
    public const string ClearDispatchRejection = "clear-dispatch-rejection";

    /// <summary><c>POST /api/v1/.../tasks/{id}/start</c>: start the run by hand.</summary>
    public const string StartRun = "start-run";

    /// <summary><c>PUT /api/tasks/{id}/cli-type</c> with a CLI picked in the UI; also sets the agent.</summary>
    public const string SetCliType = "set-cli-type";

    /// <summary><c>POST /api/tasks/{id}/intake</c>: run intake on the card now.</summary>
    public const string RunIntake = "run-intake";

    /// <summary>Move the task to <c>5e-escalated</c>, where the decision is recorded.</summary>
    public const string MoveToEscalated = "move-to-escalated";
}
