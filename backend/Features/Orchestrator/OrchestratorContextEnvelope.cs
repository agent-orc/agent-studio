using System.Security.Cryptography;
using System.Text;
using AgentStudio.Runner;

namespace AgentStudio.Orchestrator;

public static class OrchestratorContextReferenceKinds
{
    public const string Task = "task";
    public const string Page = "page";
    public const string RepositoryFile = "repository-file";
    public const string Commit = "commit";

    public static bool IsSupported(string kind)
        => kind is Task or Page or RepositoryFile or Commit;
}

public sealed record OrchestratorConversationScope(
    string Kind,
    string ContextKey,
    string ProjectId,
    string? TaskKey = null,
    string? WorkbenchKey = null);

public sealed record OrchestratorActiveSurface(
    string Kind,
    string? Reference = null,
    string? Title = null,
    string? Revision = null,
    string? TaskKey = null,
    IReadOnlyList<string>? Selection = null);

public sealed record OrchestratorContextReference(
    string Kind,
    string Reference,
    string? ProjectId = null,
    string? Revision = null);

public sealed record OrchestratorContextBudget(
    int AutomaticSoftCapTokens = 4000,
    int AutomaticHardCapTokens = 6000,
    int TotalHardCapTokens = 8000,
    int CharactersPerEstimatedToken = 4);

public sealed record OrchestratorContextEnvelope(
    OrchestratorConversationScope Scope,
    OrchestratorActiveSurface? ActiveSurface,
    IReadOnlyList<OrchestratorContextReference> ExplicitReferences,
    OrchestratorContextBudget Budget,
    DateTime CapturedAt);

public sealed record OrchestratorContextSourceReceipt(
    string SourceId,
    string Kind,
    string? Revision,
    string? Sha256,
    string Freshness,
    int IncludedCharacters,
    int EstimatedTokens,
    string Status,
    string? Reason = null);

public sealed record OrchestratorContextBudgetReceipt(
    int AutomaticSoftCapTokens,
    int AutomaticHardCapTokens,
    int TotalHardCapTokens,
    int EstimatedIncludedTokens);

public sealed class OrchestratorContextEnvelopeException : Exception
{
    public OrchestratorContextEnvelopeException(string code, string message) : base(message)
        => Code = code;

    public string Code { get; }
}

public static class OrchestratorContextEnvelopePolicy
{
    public static OrchestratorContextEnvelope Snapshot(
        string projectName,
        OrchestratorContextKey? routeContext,
        SendOrchestratorChatRequest request,
        DateTime capturedAt)
    {
        var routeScope = RouteScope(projectName, routeContext);
        var supplied = request.ContextEnvelope;
        if (supplied is not null)
            ValidateSuppliedScope(routeScope, supplied.Scope);
        var budget = supplied?.Budget ?? new OrchestratorContextBudget();
        ValidateBudget(budget);
        var references = (supplied?.ExplicitReferences ?? [])
            .Select(reference => reference with
            {
                Kind = reference.Kind.Trim().ToLowerInvariant(),
                Reference = reference.Reference.Trim(),
                ProjectId = string.IsNullOrWhiteSpace(reference.ProjectId)
                    ? routeScope.ProjectId
                    : reference.ProjectId.Trim(),
            })
            .ToArray();
        if (references.Length > 20)
            throw new OrchestratorContextEnvelopeException(
                "context-reference-limit-exceeded",
                "At most 20 explicit context references may be sent in one turn.");
        foreach (var reference in references)
        {
            if (!OrchestratorContextReferenceKinds.IsSupported(reference.Kind))
                throw new OrchestratorContextEnvelopeException(
                    "context-reference-kind-unsupported",
                    $"Context reference kind '{reference.Kind}' is not supported.");
            if (string.IsNullOrWhiteSpace(reference.Reference))
                throw new OrchestratorContextEnvelopeException(
                    "context-reference-empty",
                    "Context references require a stable reference value.");
            if (!string.Equals(reference.ProjectId, routeScope.ProjectId, StringComparison.OrdinalIgnoreCase))
                throw new OrchestratorContextEnvelopeException(
                    "context-reference-cross-project",
                    "A context reference cannot cross the active conversation project.");
        }

        var surface = supplied?.ActiveSurface ?? SurfaceFromNavigation(request.NavigationContext);
        if (surface?.Kind.Equals("task", StringComparison.OrdinalIgnoreCase) == true
            && routeScope.Kind == "task"
            && !string.Equals(
                surface.TaskKey ?? surface.Reference,
                routeScope.TaskKey,
                StringComparison.OrdinalIgnoreCase))
            throw new OrchestratorContextEnvelopeException(
                "context-active-task-mismatch",
                "The active task surface does not match the task conversation scope.");
        return new OrchestratorContextEnvelope(
            routeScope,
            surface,
            references,
            budget,
            supplied?.CapturedAt ?? capturedAt);
    }

    private static OrchestratorConversationScope RouteScope(
        string projectName,
        OrchestratorContextKey? routeContext)
    {
        if (routeContext?.Kind == OrchestratorContextKey.WorkbenchKind)
        {
            var workbenchKey = routeContext.WorkbenchKey;
            return new OrchestratorConversationScope(
                "workbench", $"workbench:{projectName}/{workbenchKey}", projectName, null, workbenchKey);
        }
        var taskKey = routeContext?.Kind == OrchestratorContextKey.TaskKind
            ? routeContext.TaskKey
            : null;
        var kind = string.IsNullOrWhiteSpace(taskKey) ? "project" : "task";
        var contextKey = kind == "task"
            ? $"task:{projectName}/{taskKey}"
            : $"project:{projectName}";
        return new OrchestratorConversationScope(kind, contextKey, projectName, taskKey);
    }

    private static void ValidateSuppliedScope(
        OrchestratorConversationScope expected,
        OrchestratorConversationScope supplied)
    {
        if (!string.Equals(expected.Kind, supplied.Kind, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(expected.ProjectId, supplied.ProjectId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(expected.TaskKey, supplied.TaskKey, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(expected.WorkbenchKey, supplied.WorkbenchKey, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(expected.ContextKey, supplied.ContextKey, StringComparison.OrdinalIgnoreCase))
            throw new OrchestratorContextEnvelopeException(
                "context-scope-mismatch",
                "The submitted context envelope does not match the active conversation route.");
    }

    private static void ValidateBudget(OrchestratorContextBudget budget)
    {
        if (budget.AutomaticSoftCapTokens is < 1 or > 4000
            || budget.AutomaticHardCapTokens is < 1 or > 6000
            || budget.TotalHardCapTokens is < 1 or > 8000
            || budget.AutomaticSoftCapTokens > budget.AutomaticHardCapTokens
            || budget.AutomaticHardCapTokens > budget.TotalHardCapTokens
            || budget.CharactersPerEstimatedToken is < 1 or > 8)
            throw new OrchestratorContextEnvelopeException(
                "context-budget-invalid",
                "Context budget exceeds the 4,000 automatic soft cap, 6,000 automatic hard cap, or 8,000 total hard cap.");
    }

    private static OrchestratorActiveSurface? SurfaceFromNavigation(ChatNavigationContext? navigation)
    {
        if (navigation is null) return null;
        if (!string.IsNullOrWhiteSpace(navigation.CurrentTaskKey)
            || !string.IsNullOrWhiteSpace(navigation.CurrentTaskId))
            return new OrchestratorActiveSurface(
                "task",
                navigation.CurrentTaskKey ?? navigation.CurrentTaskId,
                navigation.CurrentTaskTitle,
                TaskKey: navigation.CurrentTaskKey ?? navigation.CurrentTaskId);
        if (!string.IsNullOrWhiteSpace(navigation.PageRef))
            return new OrchestratorActiveSurface(
                navigation.PageType?.Equals("workbench", StringComparison.OrdinalIgnoreCase) == true
                    ? "workbench"
                    : "page",
                navigation.PageRef,
                navigation.PageTitle);
        if (!string.IsNullOrWhiteSpace(navigation.ObservedSurface)
            || !string.IsNullOrWhiteSpace(navigation.CurrentPage))
            return new OrchestratorActiveSurface(
                navigation.ObservedSurface ?? navigation.CurrentPage ?? "project");
        return null;
    }

    public static string Sha256(string content)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}
