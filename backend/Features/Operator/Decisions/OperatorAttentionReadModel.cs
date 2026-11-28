using AgentStudio.Pipeline;
using AgentStudio.Registry;
using AgentStudio.Runner;
using AgentStudio.Security;
using AgentStudio.Tasks;

namespace AgentStudio.Operator;

public sealed class OperatorAttentionReadModel(OperatorDecisionPublisher publisher, ProjectRegistry projects,
    TaskScannerService scanner, AttemptAuthorityService attempts, OperatorAttentionDismissals dismissals)
{
    public async Task<OperatorAttentionResponse> Read(HttpContext context, CancellationToken ct)
    {
        var facts = await publisher.CurrentFactsAsync(ct);
        var registry = projects.List();
        var archived = registry.Where(project => project.Archived)
            .SelectMany(project => new[] { project.Id, project.DisplayName, project.ShortCode })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var starts = new Lazy<IReadOnlyDictionary<string, DateTime>>(attempts.LatestRunStarts);
        var board = scanner.TryGetPublishedBoard()?.Live is { } live
            ? new OperatorAttentionBoard(live,
                card => starts.Value.TryGetValue(OperatorDecisionFacts.CardKey(card), out var started) ? started : null,
                card => BatchGateOwnershipStore.Read(card.FolderPath) is { ContainedInSha: null, FallbackIntegrated: false } ownership
                    ? ownership.BatchId : null, registry) : null;
        var open = OperatorAttention.Project(facts, OperatorEndpoints.ReaderFor(context, projects),
            project => !archived.Contains(project), board);
        var hidden = dismissals.Hidden();
        var items = open.Items.Where(item => !hidden.Contains(item.Id))
            .Select(item => item with { CanDismiss = CanDismiss(context, item.Project) }).ToArray();
        return new(items, items.Length);
    }

    private bool CanDismiss(HttpContext context, string? project)
        => project is not null
            ? ProjectAccessAuthorization.AllowsTasks(context, [project], IamPermissions.ProjectRunsOperate, projects)
            : context.Items[AccessSecurityMiddleware.RequestAccessItem] is RequestAccess access
                ? access.Allows(IamPermissions.ProjectRunsOperate, PermissionTarget.WorkspaceWide)
                : OperatorEndpoints.ReaderFor(context, projects).Unrestricted;
}
