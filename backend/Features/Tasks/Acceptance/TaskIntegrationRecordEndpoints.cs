using static AgentStudio.Tasks.TaskEndpointHelpers;

namespace AgentStudio.Tasks;

/// <summary>
/// Operator-reviewed append-only integration bookkeeping. This is the HTTP
/// boundary for historical classifications and explicit curated SHA mappings.
/// Historical rows use the same record shape written by
/// <see cref="HistoricalIntegrationVerificationSweep"/>. It never changes a
/// lane, task branch, commit chain, or Git history.
/// </summary>
public static class TaskIntegrationRecordEndpoints
{
    public static void MapTaskIntegrationRecordEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/{jobId}/integration-records", (
            string jobId,
            string? project,
            string? watchPath,
            AppendTaskIntegrationRecordRequest request,
            TaskScannerService scanner,
            TaskMutationService mutations,
            ProjectSettingsService settings,
            AgentStudio.Registry.ProjectRegistry projects) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var resolution = TaskIntegrationRecordAddressResolver.Resolve(scanner, jobId, watchPath);
            if (resolution.Ambiguous)
            {
                return Results.Conflict(new
                {
                    error = "The task id is not unique. Address the record by the task key or folder name within the project.",
                });
            }
            var task = resolution.Task;
            if (task is null) return Results.NotFound(new { error = "Task not found." });

            var validation = TaskIntegrationRecordAppendPolicy.Validate(task.State, request);
            if (!validation.Allowed)
            {
                return validation.InFlight
                    ? Results.Conflict(new { error = validation.Error })
                    : Results.BadRequest(new { error = validation.Error });
            }

            var record = new TaskIntegrationRecord
            {
                Id = request.Id.Trim().ToLowerInvariant(),
                Version = 1,
                Classification = request.Classification.Trim().ToLowerInvariant(),
                RecordedAtUtc = DateTime.UtcNow,
                AcceptedAtUtc = NormalizeUtc(request.AcceptedAtUtc),
                IntegrationBranch = TaskIntegrationBranch.Name(
                    string.IsNullOrWhiteSpace(request.IntegrationBranch)
                        ? settings.Get(task.ProjectName).IntegrationBranch
                        : request.IntegrationBranch),
                CommitShas = NormalizeValues(request.CommitShas, lower: true),
                FenceRefs = NormalizeValues(request.FenceRefs, lower: false),
                Evidence = request.Evidence.Trim(),
                SourceSha = request.SourceSha?.Trim().ToLowerInvariant(),
                IntegrationSha = request.IntegrationSha?.Trim().ToLowerInvariant(),
                DeliveryEpoch = request.DeliveryEpoch?.Trim(),
            };

            var write = mutations.AppendIntegrationRecordOnFolder(task.FolderPath, record);
            if (!write.Succeeded)
            {
                return Results.Json(
                    new { error = "Failed to append the integration record." },
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            var persisted = TaskIntegrationRecordAddressResolver.RefreshFolder(scanner, task)?.IntegrationRecords
                .FirstOrDefault(item => string.Equals(item.Id, record.Id, StringComparison.OrdinalIgnoreCase))
                ?? record;
            return Results.Ok(new { appended = write.Appended, record = persisted });
        });
    }

    private static DateTime? NormalizeUtc(DateTime? value)
    {
        if (value is null) return null;
        return value.Value.Kind switch
        {
            DateTimeKind.Utc => value.Value,
            DateTimeKind.Local => value.Value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc),
        };
    }

    private static List<string> NormalizeValues(IEnumerable<string>? values, bool lower)
        => (values ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Select(value => lower ? value.ToLowerInvariant() : value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}

/// <summary>Request to append one stable integration bookkeeping row.</summary>
public sealed record AppendTaskIntegrationRecordRequest
{
    public string Id { get; init; } = "";
    public string Classification { get; init; } = "";
    public DateTime? AcceptedAtUtc { get; init; }
    public string? IntegrationBranch { get; init; }
    public List<string> CommitShas { get; init; } = [];
    public List<string> FenceRefs { get; init; } = [];
    public string Evidence { get; init; } = "";
    public string? SourceSha { get; init; }
    public string? IntegrationSha { get; init; }
    public string? DeliveryEpoch { get; init; }
}

/// <summary>Pure boundary validation for append-only integration records.</summary>
public static class TaskIntegrationRecordAppendPolicy
{
    private static readonly HashSet<string> AcceptedLanes = new(StringComparer.Ordinal)
    {
        TaskStates.HumanReview,
        TaskStates.Escalated,
        TaskStates.Completed,
        TaskStates.Archive,
        TaskStates.AutoReview,
    };

    public static IntegrationRecordAppendValidation Validate(
        string state,
        AppendTaskIntegrationRecordRequest? request)
    {
        if (!AcceptedLanes.Contains(state)
            || state == TaskStates.AutoReview
                && !string.Equals(request?.Classification?.Trim(),
                    IntegrationRecordClasses.CuratedMapping, StringComparison.OrdinalIgnoreCase))
        {
            return new(false, true,
                $"Integration records cannot be appended while a task is in-flight in '{state}'.");
        }

        if (request is null)
            return new(false, false, "A request body is required.");

        var id = request.Id.Trim();
        if (id.Length is < 3 or > 96 || !IsRecordId(id))
            return new(false, false, "id must be 3-96 lowercase letters, digits, or hyphens.");

        var classification = request.Classification.Trim().ToLowerInvariant();
        if (!IntegrationRecordClasses.All.Contains(classification, StringComparer.Ordinal))
            return new(false, false, "classification is not a supported integration record class.");

        if (classification == IntegrationRecordClasses.CuratedMapping)
        {
            if (!IsFullCommitSha(request.SourceSha)
                || !IsFullCommitSha(request.IntegrationSha)
                || string.IsNullOrWhiteSpace(request.DeliveryEpoch)
                || request.DeliveryEpoch.Trim().Length > 128)
                return new(false, false, "A curated mapping needs full sourceSha and integrationSha values and a deliveryEpoch.");
        }
        else if (request.SourceSha is not null || request.IntegrationSha is not null
                 || request.DeliveryEpoch is not null)
            return new(false, false, "SHA mapping fields require curated-mapping classification.");

        var evidence = request.Evidence.Trim();
        if (evidence.Length is < 8 or > 4000)
            return new(false, false, "evidence must contain 8-4000 characters.");

        if ((request.CommitShas?.Count ?? 0) > 100
            || (request.CommitShas?.Any(sha => !IsCommitSha(sha)) ?? false))
            return new(false, false, "commitShas must contain at most 100 hexadecimal SHAs of 7-40 characters.");

        if ((request.FenceRefs?.Count ?? 0) > 100
            || (request.FenceRefs?.Any(reference =>
                string.IsNullOrWhiteSpace(reference) || reference.Trim().Length > 512) ?? false))
        {
            return new(false, false, "fenceRefs must contain at most 100 non-empty refs of at most 512 characters.");
        }

        if (!string.IsNullOrWhiteSpace(request.IntegrationBranch)
            && !IsBranchName(TaskIntegrationBranch.Name(request.IntegrationBranch)))
        {
            return new(false, false, "integrationBranch is invalid.");
        }

        return new(true, false, null);
    }

    private static bool IsRecordId(string value)
        => value.All(character => character is >= 'a' and <= 'z'
            || character is >= '0' and <= '9'
            || character == '-');

    private static bool IsCommitSha(string value)
    {
        var trimmed = value?.Trim() ?? "";
        return trimmed.Length is >= 7 and <= 40 && trimmed.All(Uri.IsHexDigit);
    }

    private static bool IsFullCommitSha(string? value)
        => value is not null && value.Trim().Length == 40
           && value.Trim().All(Uri.IsHexDigit);

    private static bool IsBranchName(string value)
        => value.Length is > 0 and <= 255
           && !value.StartsWith('/')
           && !value.EndsWith('/')
           && !value.EndsWith('.')
           && !value.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
           && !value.Contains("..", StringComparison.Ordinal)
           && !value.Contains("//", StringComparison.Ordinal)
           && !value.Contains("@{", StringComparison.Ordinal)
           && value.All(character => char.IsAsciiLetterOrDigit(character)
               || character is '-' or '_' or '.' or '/');
}

public sealed record IntegrationRecordAppendValidation(bool Allowed, bool InFlight, string? Error);

internal static class TaskIntegrationRecordAddressResolver
{
    public static TaskIntegrationRecordAddressResolution Resolve(
        TaskScannerService scanner,
        string address,
        string? watchPath)
    {
        var candidates = scanner.ScanAllJobsRaw()
            .Where(task => string.IsNullOrWhiteSpace(watchPath)
                || WatchPathComparison.PathsEqual(task.WatchPath, watchPath))
            .ToList();
        var folderOrKeyMatches = candidates
            .Where(task => string.Equals(task.Key, address, StringComparison.OrdinalIgnoreCase)
                || string.Equals(task.TaskKey, address, StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    Path.GetFileName(task.FolderPath),
                    address,
                    StringComparison.OrdinalIgnoreCase))
            .DistinctBy(task => task.FolderPath, FolderPathComparer)
            .ToList();
        if (folderOrKeyMatches.Count == 1)
            return new(folderOrKeyMatches[0], false);
        if (folderOrKeyMatches.Count > 1)
            return new(null, true);

        var idMatches = candidates
            .Where(task => string.Equals(task.Id, address, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(task => task.FolderPath, FolderPathComparer)
            .ToList();
        return idMatches.Count switch
        {
            0 => new(null, false),
            1 => new(idMatches[0], false),
            _ => new(null, true),
        };
    }

    public static TaskInfo? RefreshFolder(TaskScannerService scanner, TaskInfo task)
        => scanner.ScanAllJobsRaw().FirstOrDefault(candidate => FolderPathComparer.Equals(
            candidate.FolderPath,
            task.FolderPath));

    private static StringComparer FolderPathComparer { get; } = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}

internal sealed record TaskIntegrationRecordAddressResolution(TaskInfo? Task, bool Ambiguous);
