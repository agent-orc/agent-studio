using AgentStudio.Shared;

namespace AgentStudio.Security;

/// <summary>Small, fail-closed browser route surface for the Product role.</summary>
public static class ContributorAccessPolicy
{
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
        if (segments.Any(segment => segment is "credentials" or "hosts" or "settings" or "quotas"
            or "security" or "tokens" or "usage" or "logs" or "raw-logs"
            or "merges" or "promotions" or "secrets" or "integration" or "output" or "trace")) return false;

        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method))
        {
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
