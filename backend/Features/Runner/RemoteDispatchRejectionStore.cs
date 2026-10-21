using System.Text.Json;

namespace AgentStudio.Runner;

/// <summary>
/// Persists the latest reason a runner refused an offered Ready task. Repeated
/// polls with the same reason are idempotent so an unhealthy fleet does not
/// rewrite every blocked task on every claim tick.
/// </summary>
public sealed class RemoteDispatchRejectionStore
{
    public const string FieldName = "remoteDispatchRejection";

    private readonly ILogger<RemoteDispatchRejectionStore> _logger;
    private readonly TaskScannerService? _scanner;

    public RemoteDispatchRejectionStore(
        ILogger<RemoteDispatchRejectionStore> logger,
        TaskScannerService? scanner = null)
    {
        _logger = logger;
        _scanner = scanner;
    }

    public RemoteDispatchRejection Record(
        TaskInfo task,
        string? runnerId,
        string? runnerName,
        string code,
        string? reason,
        DateTime? rejectedAtUtc = null)
    {
        var next = new RemoteDispatchRejection
        {
            Code = Normalize(code, 100, "remote-dispatch-rejected"),
            RunnerId = Normalize(runnerId, 200, "unknown-runner"),
            RunnerName = Normalize(runnerName, 200, "unknown runner"),
            Reason = Normalize(reason, 1000, "Remote dispatch was rejected without a diagnostic."),
            RejectedAtUtc = (rejectedAtUtc ?? DateTime.UtcNow).ToUniversalTime(),
        };
        var previous = Read(task.FolderPath);
        if (previous is not null
            && previous.RejectedAtUtc >= task.EnteredLaneAt.ToUniversalTime()
            && string.Equals(previous.Code, next.Code, StringComparison.Ordinal)
            && string.Equals(previous.RunnerId, next.RunnerId, StringComparison.Ordinal)
            && string.Equals(previous.RunnerName, next.RunnerName, StringComparison.Ordinal)
            && string.Equals(previous.Reason, next.Reason, StringComparison.Ordinal))
            return previous;

        EnsureLaneEntryAnchor(task);
        TaskJsonFile.UpdateFieldOrThrow(task.FolderPath, FieldName, next);
        _scanner?.InvalidateCache();
        _scanner?.PublishCoreFromKnownFolder(task.FolderPath);
        _logger.LogWarning(
            "remote-dispatch-rejected project={Project} task={TaskKey} runner={Runner} code={Code} reason={Reason}",
            task.ProjectName,
            task.Key ?? task.TaskKey ?? task.Id,
            next.RunnerName,
            next.Code,
            next.Reason);
        return next;
    }

    public void Clear(TaskInfo task)
    {
        if (TaskJsonFile.RemoveField(task.FolderPath, FieldName, _logger))
        {
            _scanner?.InvalidateCache();
            _scanner?.PublishCoreFromKnownFolder(task.FolderPath);
        }
    }

    internal static RemoteDispatchRejection? Read(string folderPath)
    {
        var path = Path.Combine(folderPath, "task.json");
        if (!File.Exists(path)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty(FieldName, out var value)
                || value.ValueKind != JsonValueKind.Object)
                return null;
            return value.Deserialize<RemoteDispatchRejection>(TaskJsonFile.ReadOpts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void EnsureLaneEntryAnchor(TaskInfo task)
    {
        if (task.EnteredLaneAt == default) return;
        var path = Path.Combine(task.FolderPath, "task.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.TryGetProperty("enteredLaneAt", out _)) return;
        TaskJsonFile.UpdateFieldOrThrow(
            task.FolderPath,
            "enteredLaneAt",
            task.EnteredLaneAt.ToUniversalTime());
    }

    private static string Normalize(string? value, int maximumLength, string fallback)
    {
        var normalized = (value ?? string.Empty)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        if (normalized.Length == 0) return fallback;
        return normalized.Length <= maximumLength ? normalized : normalized[..maximumLength];
    }
}
