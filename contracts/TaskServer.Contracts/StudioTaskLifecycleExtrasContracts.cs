using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Wire contracts for the P1 "task lifecycle extras" bundle: task creation
/// from the unscoped legacy body shape, the reverse task-reference lookup,
/// bulk reorder/batch-move, the archive listing, and the handful of
/// task-lifecycle side-actions (concept dossier, context-usage refresh,
/// integration rebase, planning closure, promote-concept, promote-to-coding)
/// that have no dedicated backing subsystem in the standalone Task Server.
/// </summary>
public sealed record StudioCreateTaskRequest(
    string? ProjectId,
    string Title,
    string? Body = null,
    string? State = null,
    string? TaskId = null,
    string? TaskKey = null)
{
    public string? Project { get; init; }
    public string? PromptMarkdown { get; init; }
    public string? TargetState { get; init; }
    public string? Mode { get; init; }
    public string? Kind { get; init; }
    public MagazineDeliverableRequest? MagazineDeliverable { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? CompatibilityFields { get; init; }
}

public sealed record TaskDependentDto(string TaskId, string TaskKey, string Title, string State);

public sealed record ReferenceStatusRequest(IReadOnlyList<string> Keys);

public sealed record TaskReferenceStatusDto(
    string Key,
    bool Exists,
    string? TaskId = null,
    string? TaskKey = null,
    string? Title = null,
    string? State = null);

public sealed record ReferenceStatusResponse(IReadOnlyList<TaskReferenceStatusDto> Items);

/// <summary>
/// An ordered list of task identities (id or task key). Tasks are grouped by
/// their current <c>(projectId, state)</c> lane and each group is assigned
/// dense, zero-based, ascending ranks in the order its members appear in
/// <see cref="TaskIds"/> - the same "contiguous rank per lane" convention
/// <c>TaskServerStudioTaskLifecycleStore.PlaceInLaneAsync</c> uses for
/// move/move-to-top.
/// </summary>
public sealed record ReorderTasksRequest(IReadOnlyList<string> TaskIds);

public sealed record ReorderTasksResponse(IReadOnlyList<TaskDto> Tasks);

public sealed record ArchivedTasksResponse(IReadOnlyList<TaskDto> Items, long Total);

public sealed record BatchMoveItemRequest(string TaskId, string TargetState, int? TargetIndex = null, string? Reason = null);

public sealed record BatchMoveRequest(IReadOnlyList<BatchMoveItemRequest> Items);

public sealed record BatchMoveItemResult(
    string TaskId,
    bool Success,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    TaskDto? Task = null);

/// <summary>
/// The batch-move "job" is resolved synchronously (every item is moved
/// through <see cref="StudioLifecycleCoordinator.MoveTaskAsync"/> before the
/// HTTP response is produced), so <see cref="Status"/> is always
/// <c>"completed"</c> the moment a batch is created - there is no background
/// worker. The durable row exists so the legacy poll-by-id shape
/// (<c>GET /batch-move/{batchId}</c>) keeps working unchanged.
/// </summary>
public sealed record BatchMoveJobResponse(
    string BatchId,
    string Status,
    IReadOnlyList<BatchMoveItemResult> Items,
    DateTime CreatedAt,
    DateTime? CompletedAt);

/// <summary>
/// The <c>action</c> discriminator stored in <c>task_lifecycle_actions</c>.
/// </summary>
public static class TaskLifecycleActionKinds
{
    public const string ConceptDossier = "concept-dossier";
    public const string ContextUsageRefresh = "context-usage-refresh";
    public const string IntegrationRebase = "integration-rebase";
    public const string PlanningClosure = "planning-closure";
    public const string PromoteConcept = "promote-concept";
    public const string PromoteToCoding = "promote-to-coding";
}

public sealed record ConceptDossierRequest(string? Path = null, bool NoDossierNeeded = false, string? Reason = null);

public sealed record PlanningClosureRequest(bool Declared, string? Reason = null);

public sealed record PromoteConceptRequest(IReadOnlyList<int>? ItemIndexes = null);

/// <summary>
/// Current durable status of one <c>(taskId, action)</c> lifecycle side
/// action. <see cref="Status"/> is <c>"not-requested"</c> when no row exists
/// yet, <c>"requested"</c> once recorded but not resolvable inside the Task
/// Server (the common case - see <see cref="TaskLifecycleActionKinds"/>
/// members other than <see cref="TaskLifecycleActionKinds.PlanningClosure"/>),
/// or <c>"completed"</c> for the one action
/// (<see cref="TaskLifecycleActionKinds.PlanningClosure"/>) that is a pure
/// state-tag flip the Task Server can resolve itself.
/// </summary>
public sealed record TaskLifecycleActionStatusDto(
    string TaskId,
    string Action,
    string Status,
    string? DetailJson,
    string? ActorId,
    DateTime RequestedAt,
    DateTime? CompletedAt);

/// <summary>Inclusive reporting dates. Both dates are required when a window is supplied.</summary>
public sealed record MagazineReportingWindow(DateOnly Start, DateOnly End);

public sealed record MagazineDeliverableRequest(
    string Scope, string? Slug = null, string? PersonaId = null, string? RoleId = null,
    MagazineReportingWindow? ReportingWindow = null);

public sealed record MagazineDeliverableDeclaration(
    string Scope, string Slug, string? PersonaId, string? RoleId, MagazineReportingWindow? ReportingWindow,
    string AuthorizedBy, DateTimeOffset AuthorizedAt, string? AcceptedResultVersion = null)
{
    public string PublicationStatus => "awaiting-publication";
    public IReadOnlyList<string> ResultFiles => MagazineDeliverableContract.ResultFiles;
}

public sealed record AttachMagazineDeliverableRequest(
    MagazineDeliverableRequest MagazineDeliverable, string? AcceptedResultVersion = null,
    string? Confirmation = null);

public sealed record MagazineDeliverablePreview(
    string TaskKey, string Scope, string Slug, string? AcceptedResultVersion, string Confirmation);

/// <summary>Shared intake and preview rules for both card stores.</summary>
public static partial class MagazineDeliverableContract
{
    public const string EdgeAuthorHeader = "X-Studio-Magazine-Author";

    public static IReadOnlyList<string> ResultFiles { get; } =
        Array.AsReadOnly(new[] { "results/article.md", "results/article.json", "results/items.json" });

    [System.Text.RegularExpressions.GeneratedRegex(
        @"\A[a-z0-9](?:[a-z0-9-]{0,78}[a-z0-9])?\z",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    public static partial System.Text.RegularExpressions.Regex SlugPattern();

    [System.Text.RegularExpressions.GeneratedRegex(
        @"\A(?:workspace|project):[a-z0-9][a-z0-9._-]{0,63}\z",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex ScopedAudiencePattern();

    public static bool ValidScope(string? scope) =>
        string.Equals(scope, "installation", StringComparison.OrdinalIgnoreCase)
        || scope is not null && !scope.Contains("..", StringComparison.Ordinal) && ScopedAudiencePattern().IsMatch(scope);

    public static bool IsResearch(string? mode, string? kind) =>
        string.Equals(mode?.Trim(), "research", StringComparison.OrdinalIgnoreCase)
        && (kind is null || string.Equals(kind.Trim(), "task", StringComparison.OrdinalIgnoreCase));

    public static string? Validate(MagazineDeliverableRequest request, Func<string, bool> isCatalogueId)
    {
        if (!ValidScope(request.Scope?.Trim()))
            return "The scope must be installation, workspace:<id> or project:<id>.";
        if (request.Slug is { } slug && !SlugPattern().IsMatch(slug))
            return "The article slug must contain 1 to 80 lowercase letters, digits or hyphens and start and end with a letter or digit.";
        if (request.ReportingWindow is { } window &&
            (window.Start == default || window.End == default || window.Start > window.End))
            return "Supply both reporting dates with the start on or before the end.";
        if (request.PersonaId is { } persona && !isCatalogueId(persona)
            || request.RoleId is { } role && !isCatalogueId(role))
            return "Select persona and role IDs from the built-in catalogue.";
        return null;
    }

    public static MagazineDeliverableDeclaration Declare(
        MagazineDeliverableRequest request, string scope, string taskKey, string actor, DateTimeOffset at,
        string? acceptedResultVersion = null)
    {
        var slug = request.Slug ?? taskKey.ToLowerInvariant();
        if (!SlugPattern().IsMatch(slug))
            throw new ArgumentException("Supply an explicit article slug for this task key.");
        return new(scope, slug, request.PersonaId, request.RoleId, request.ReportingWindow, actor, at, acceptedResultVersion);
    }

    public static MagazineDeliverablePreview Preview(string taskKey, MagazineDeliverableDeclaration declaration)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            taskKey, declaration.Scope, declaration.Slug, declaration.PersonaId, declaration.RoleId,
            declaration.ReportingWindow, declaration.AuthorizedBy, declaration.AcceptedResultVersion,
        });
        var confirmation = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        return new(taskKey, declaration.Scope, declaration.Slug, declaration.AcceptedResultVersion, confirmation);
    }

    public static string Instructions(string? prompt, MagazineDeliverableDeclaration declaration) =>
        $"""
        {prompt?.TrimEnd()}

        ## Magazine deliverable

        Acceptance authorizes publication to `{declaration.Scope}/{declaration.Slug}`.
        Keep `results/report.html` as the primary research report. Also write these files:

        - `results/article.md` contains the article in Markdown.
        - `results/article.json` contains an object with `title`, `summary`, `kind`, `persona`, and `sources`.
          Use `digest`, `decision-brief`, `report`, or `note` for `kind`. Each source has a `kind` and `ref`.
        - `results/items.json` contains an array of up to eight scored items. Each item has `id`, `headline`,
          integer `importance` (0 to 100), `howDerived`, `evidence`, `proposal`, and Boolean `needsOwner`.
        """
        + (declaration.PersonaId is { } persona ? $"\n\nUse catalogue persona `{persona}` for the byline." : "")
        + (declaration.RoleId is { } role ? $"\n\nThe catalogue role is `{role}`." : "")
        + (declaration.ReportingWindow is { } window
            ? FormattableString.Invariant($"\n\nCover {window.Start:yyyy-MM-dd} to {window.End:yyyy-MM-dd}, inclusive.")
            : "");
}
