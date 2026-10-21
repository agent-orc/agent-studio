using AgentStudio.Shared;

namespace AgentStudio.Security;

/// <summary>
/// Small, fail-closed browser route surface for the Product role. The
/// Product-session decision lives in <see cref="IsProductSession(StudioUser)"/>
/// so AGT-3228 can replace the role check with role bindings.
/// </summary>
public static class ContributorAccessPolicy
{
    private static readonly HashSet<string> DeniedSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "credentials", "hosts", "settings", "quotas", "security", "tokens", "usage",
        "logs", "raw-logs", "merges", "promotions", "secrets", "integration",
        "output", "trace", "session-events", "agent-work-detail", "conversation"
    };

    private static readonly HashSet<string> ReadableResultExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".avif", ".bmp", ".html", ".htm", ".md"
    };

    public static bool IsProductSession(StudioUser user) => user.Role == StudioRoles.Contributor;

    public static StudioUser? ProductSessionUser(HttpContext context)
        => context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is HumanPrincipal human
            && IsProductSession(human.User)
            ? human.User
            : null;

    public static bool IsProductSession(HttpContext context) => ProductSessionUser(context) is not null;

    /// <summary>Screenshots, HTML reports and Markdown; never logs, TRX, JSONL or other raw output.</summary>
    public static bool AllowsResultFile(string path) => ReadableResultExtensions.Contains(Path.GetExtension(path));

    /// <summary>Only card Markdown and safe workspace result artifacts can be read through the file viewer.</summary>
    public static bool AllowsTaskFileRead(string path, string? scope)
    {
        if (scope is not null && !string.Equals(scope, "workspace", StringComparison.OrdinalIgnoreCase))
            return false;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 5 || !segments[0].Equals("api", StringComparison.OrdinalIgnoreCase)
            || !segments[1].Equals("tasks", StringComparison.OrdinalIgnoreCase)
            || !segments[3].Equals("files", StringComparison.OrdinalIgnoreCase))
            return false;
        return segments.Length == 5 && (segments[4].Equals("prompt.md", StringComparison.OrdinalIgnoreCase)
            || segments[4].Equals("status.md", StringComparison.OrdinalIgnoreCase))
            || segments.Length > 5 && segments[4].Equals("results", StringComparison.OrdinalIgnoreCase)
                && AllowsResultFile(segments[^1]);
    }

    /// <summary>
    /// A Product wish carries no run controls: project defaults choose the CLI,
    /// model, thinking level, mode, web access and integration, and the card
    /// records the signed-in user as its creator.
    /// </summary>
    public static CreateTaskRequest ForProductCreate(CreateTaskRequest request, string userId) => request with
    {
        Model = null,
        CliType = null,
        ThinkingLevel = null,
        ModelExplicit = null,
        ThinkingLevelExplicit = null,
        Mode = null,
        AllowWebAccess = null,
        NoBranchExpected = false,
        RequiresIntegration = null,
        Fixture = false,
        CreatedBy = userId,
    };

    public static bool AllowsTransition(string? currentState, string? targetState, bool operatorOverride, bool archiveOverride)
        => !operatorOverride && !archiveOverride
            && ((currentState is (TaskStates.Backlog or TaskStates.Ready)
                    && targetState is (TaskStates.Backlog or TaskStates.Ready))
                || (currentState == TaskStates.HumanReview && targetState == TaskStates.Completed));

    public static bool Allows(string method, string path, string? cardState, bool canApproveProduction)
    {
        if (path is "/api/auth/status" or "/api/auth/session" or "/api/auth/logout" or "/api/auth/change-password")
            return true;
        if (path.StartsWith("/api/auth/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/v1/", StringComparison.OrdinalIgnoreCase)) return false;

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(DeniedSegments.Contains)) return false;

        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method))
        {
            if (segments.Length >= 4 && segments[0].Equals("api", StringComparison.OrdinalIgnoreCase)
                && segments[1].Equals("tasks", StringComparison.OrdinalIgnoreCase))
            {
                if (segments[3].Equals("checkout", StringComparison.OrdinalIgnoreCase)) return false;
                if (segments[3].Equals("files", StringComparison.OrdinalIgnoreCase))
                    return AllowsTaskFileRead(path, null);
            }
            if (segments.Length >= 5 && path.StartsWith("/api/tasks/", StringComparison.OrdinalIgnoreCase)
                && segments[3].Equals("results", StringComparison.OrdinalIgnoreCase))
                return AllowsResultFile(path);
            if (path.StartsWith("/api/runner/", StringComparison.OrdinalIgnoreCase))
                return path.Contains("/orchestrator-chat", StringComparison.OrdinalIgnoreCase);
            if (path.StartsWith("/api/orchestrator/", StringComparison.OrdinalIgnoreCase))
                return path.StartsWith("/api/orchestrator/context/", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith("/api/orchestrator/sessions/", StringComparison.OrdinalIgnoreCase);
            return path.StartsWith("/api/tasks", StringComparison.OrdinalIgnoreCase)
                || path.Equals("/api/projects", StringComparison.OrdinalIgnoreCase)
                || (segments.Length >= 4 && segments[0] == "api" && segments[1] == "projects"
                    && segments[3] is "chat" or "docs" or "visual-evidence" or "snapshot"
                        or "throughput" or "cycle-time" or "completed-lane")
                || path.StartsWith("/api/registry", StringComparison.OrdinalIgnoreCase);
        }

        if (HttpMethods.IsPost(method) && path == "/api/tasks/reference-status") return true;
        if (HttpMethods.IsPost(method) && path == "/api/tasks") return true;
        if (HttpMethods.IsPost(method) && path.StartsWith("/api/runner/", StringComparison.OrdinalIgnoreCase))
            return path.Contains("/orchestrator-chat", StringComparison.OrdinalIgnoreCase)
                && !path.EndsWith("/attachments", StringComparison.OrdinalIgnoreCase);
        if (HttpMethods.IsPut(method) && path.StartsWith("/api/tasks/", StringComparison.OrdinalIgnoreCase))
        {
            if (path.EndsWith("/release", StringComparison.OrdinalIgnoreCase))
                return canApproveProduction;
            if (cardState is not (TaskStates.Backlog or TaskStates.Ready)) return false;
            return path.EndsWith("/title", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/tags", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/references", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/waits-on", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/files/prompt.md", StringComparison.OrdinalIgnoreCase);
        }
        if (HttpMethods.IsPost(method) && path.StartsWith("/api/tasks/", StringComparison.OrdinalIgnoreCase))
            return path.EndsWith("/preview-comments", StringComparison.OrdinalIgnoreCase)
                || (path.EndsWith("/move", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith("/state", StringComparison.OrdinalIgnoreCase))
                && cardState is (TaskStates.Backlog or TaskStates.Ready or TaskStates.HumanReview);
        return false;
    }
}
