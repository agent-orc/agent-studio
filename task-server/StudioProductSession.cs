using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>
/// The one Task Server decision on whether a request runs as a Product
/// session, and the lane and result-file rules that apply to it. Handlers
/// apply these rules to the bound request, so a body the contributor
/// middleware read differently cannot widen them. AGT-3228 replaces the role
/// check in <see cref="IsProduct(StudioAuthUserDto)"/> with role bindings.
/// </summary>
internal static class StudioProductSession
{
    private const string ItemKey = "StudioContributor";

    private static readonly HashSet<string> ReadableResultExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".avif", ".bmp", ".html", ".htm", ".md"
    };

    public static bool IsProduct(StudioAuthUserDto user) => user.Role == StudioUserRoles.Contributor;

    public static void Mark(HttpContext context) => context.Items[ItemKey] = true;

    public static bool IsProduct(HttpContext context) => context.Items[ItemKey] is true;

    public static bool AllowsCreate(string? state) => state is StudioTaskLanes.Backlog or StudioTaskLanes.Ready;

    /// <summary>A field edit or lane change that keeps the card in Backlog or Ready.</summary>
    public static bool AllowsEdit(string? currentState, string? targetState)
        => currentState is StudioTaskLanes.Backlog or StudioTaskLanes.Ready
            && AllowsCreate(targetState);

    /// <summary>Backlog and Ready among each other, and accepting a delivery in Human Review.</summary>
    public static bool AllowsMove(string? currentState, string? targetState)
        => AllowsEdit(currentState, targetState)
            || (currentState == StudioTaskLanes.HumanReview && targetState == StudioTaskLanes.Completed);

    /// <summary>Screenshots, HTML reports and Markdown; never logs, TRX, JSONL or other raw output.</summary>
    public static bool AllowsResult(string path)
        => ReadableResultExtensions.Contains(Path.GetExtension(path));

    public static async Task<bool> AllowsMoveAsync(
        HttpContext context, TaskServerStore store, string projectId, string taskIdentity, string? targetState,
        CancellationToken ct)
        => !IsProduct(context)
            || (await store.GetTaskAsync(projectId, taskIdentity, ct) is { } task && AllowsMove(task.State, targetState));

    public static async Task<bool> AllowsUpdateAsync(
        HttpContext context, TaskServerStore store, string projectId, string taskIdentity, UpdateTaskRequest request,
        CancellationToken ct)
        => !IsProduct(context)
            || (request.MechanicalDelta is null
                && await store.GetTaskAsync(projectId, taskIdentity, ct) is { } task
                && AllowsEdit(task.State, request.State ?? task.State));

    public static IResult Denied()
        => Results.Json(new { error = "contributor-role-denied" }, statusCode: StatusCodes.Status403Forbidden);
}
