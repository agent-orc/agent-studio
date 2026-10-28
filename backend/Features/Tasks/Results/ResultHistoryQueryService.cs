using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.Git;

namespace AgentStudio.Tasks;

/// <summary>
/// Read model for previous Results. It merges snapshots created by
/// <see cref="ResultVersionStore"/> with legacy status.md versions from the
/// workspace repository, so existing cards need no migration.
///
/// <para>Neither read starts a git process (AGT-3086). Legacy versions come
/// from the card's persisted <see cref="LegacyResultHistoryFile"/>, which
/// <see cref="LegacyResultHistoryBackfill"/> fills in the background; until it
/// is complete the listing says so through
/// <see cref="ResultHistoryListing.LegacyState"/> instead of passing an
/// unfinished walk off as an empty history.</para>
/// </summary>
public sealed class ResultHistoryQueryService
{
    private readonly TaskScannerService _scanner;
    private readonly LegacyResultHistoryBackfill _legacy;
    private readonly GitFactStore _facts;

    public ResultHistoryQueryService(
        TaskScannerService scanner,
        LegacyResultHistoryBackfill legacy,
        GitFactStore facts)
    {
        _scanner = scanner;
        _legacy = legacy;
        _facts = facts;
    }

    public TaskFileLookupResult<ResultHistoryListing> List(string jobId, string? watchPath)
    {
        var task = _scanner.FindJob(jobId, watchPath);
        if (task is null)
            return TaskFileLookupResult<ResultHistoryListing>.Fail(404, "Job not found.");

        var (state, snapshot) = _legacy.Observe(task, jobId, watchPath);
        var rows = _facts.ReadResultVersions(task.TaskKey);
        var entries = Project(task, state == LegacyResultHistoryStates.Complete
            ? rows : rows.Where(row => row.Source != "workspace-history").ToArray());
        if (state == LegacyResultHistoryStates.Complete &&
            ((!_legacy.IsImportComplete(task) && rows.Count == 0)
             || snapshot.Record is { } record && record.Entries.Any(version =>
                 rows.All(row => row.VersionId != GitIdPrefix + version.Sha))))
            state = LegacyResultHistoryStates.Pending;
        return TaskFileLookupResult<ResultHistoryListing>.Ok(
            new ResultHistoryListing(entries, state),
            TaskFileSources.Workspace);
    }

    public TaskFileLookupResult<ResultHistoryDocument> Read(string jobId, string? watchPath, string versionId)
    {
        var task = _scanner.FindJob(jobId, watchPath);
        if (task is null)
            return TaskFileLookupResult<ResultHistoryDocument>.Fail(404, "Job not found.");

        LegacyResultHistorySnapshot? snapshot = null;
        if (versionId.StartsWith(GitIdPrefix, StringComparison.Ordinal))
        {
            snapshot = LegacyResultHistoryFile.Read(task.FolderPath);
            if (snapshot.State == LegacyResultHistoryStates.Missing)
            {
                _legacy.Observe(task, jobId, watchPath);
                return TaskFileLookupResult<ResultHistoryDocument>.Fail(202, "Legacy history is warming.");
            }
            if (snapshot.State != LegacyResultHistoryStates.Complete)
                return TaskFileLookupResult<ResultHistoryDocument>.Fail(
                    404, "Legacy history could not be loaded: " + snapshot.Reason);
        }
        else if (!versionId.StartsWith(LocalIdPrefix, StringComparison.Ordinal))
        {
            return TaskFileLookupResult<ResultHistoryDocument>.Fail(404, "Previous result not found.");
        }
        else
        {
            _legacy.Observe(task, jobId, watchPath);
        }

        var rows = _facts.ReadResultVersions(task.TaskKey);
        var row = rows.FirstOrDefault(item => string.Equals(item.VersionId, versionId, StringComparison.OrdinalIgnoreCase));
        if (row is null && !_legacy.IsImportComplete(task))
        {
            return TaskFileLookupResult<ResultHistoryDocument>.Fail(202, "Previous result is warming.");
        }
        if (row is null)
            return TaskFileLookupResult<ResultHistoryDocument>.Fail(404, "Previous result not found.");
        var markdown = ReadContent(task, row, snapshot);
        if (markdown is null)
            return TaskFileLookupResult<ResultHistoryDocument>.Fail(
                404, "This previous result was too large to keep in the legacy history file.");
        return TaskFileLookupResult<ResultHistoryDocument>.Ok(
            new ResultHistoryDocument(ToEntry(row), markdown), row.Source);
    }

    private const string LocalIdPrefix = "local-";
    private const string GitIdPrefix = "git-";

    private static List<ResultHistoryEntry> Project(TaskInfo task, IReadOnlyList<GitResultVersionFact> rows)
    {
        var current = ReadOrEmpty(Path.Combine(task.FolderPath, "status.md"));
        var excludedHashes = new HashSet<string>(StringComparer.Ordinal);
        if (current.Length > 0) excludedHashes.Add(ResultHistoryContent.Hash(current));
        // Local versions win over a matching legacy commit even when the
        // local timestamp is older. Both sources are already indexed.
        foreach (var row in rows.Where(row => row.Source == "task-folder"))
            excludedHashes.Add(row.ContentHash);
        return rows.Where(row => row.Source == "task-folder"
                    ? !string.Equals(row.ContentHash, ResultHistoryContent.Hash(current), StringComparison.Ordinal)
                    : excludedHashes.Add(row.ContentHash))
            .Select(ToEntry)
            .ToList();
    }

    private static ResultHistoryEntry ToEntry(GitResultVersionFact row)
        => new(row.VersionId, row.Timestamp, row.Producer, row.ProducerKind, row.Lane,
            row.Result, row.Case, row.Source, row.Version);

    private static string? ReadContent(TaskInfo task, GitResultVersionFact row,
        LegacyResultHistorySnapshot? snapshot)
    {
        if (row.Source == "workspace-history")
        {
            snapshot ??= LegacyResultHistoryFile.Read(task.FolderPath);
            return snapshot.Record?.Entries.FirstOrDefault(entry => "git-" + entry.Sha == row.VersionId)?.Markdown;
        }
        if (string.IsNullOrWhiteSpace(row.StatusPath)) return null;
        var path = Path.GetFullPath(Path.Combine(task.FolderPath,
            row.StatusPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!FilesystemPathBoundary.IsRegularFileUnder(task.FolderPath, path)) return null;
        return BoundedFileRead.ReadAllTextOrNull(path, BoundedFileRead.SidecarBytes);
    }

    private static string ReadOrEmpty(string path)
    {
        try { return BoundedFileRead.ReadAllTextOrNull(path, BoundedFileRead.SidecarBytes) ?? string.Empty; }
        catch { return string.Empty; }
    }
}

/// <summary>
/// Content rules shared by the read model and the legacy backfill, so a hash
/// computed when the backfill ran still excludes the same document at read
/// time.
/// </summary>
public static partial class ResultHistoryContent
{
    public static string Hash(string content)
    {
        // Git may store LF while a Windows worktree materializes CRLF. Treat
        // those as the same Result when excluding the live/local versions from
        // the legacy Git backfill.
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    public static (string Result, string Case) ParseFields(string markdown)
    {
        var result = ResultLine().Match(markdown).Groups[1].Value.Trim();
        var resultCase = CaseLine().Match(markdown).Groups[1].Value.Trim();
        return (
            result.Length == 0 ? "Not recorded" : result,
            resultCase.Length == 0 ? "generic" : resultCase);
    }

    public static string? ReadLane(string? taskJson)
    {
        if (string.IsNullOrWhiteSpace(taskJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(taskJson);
            return doc.RootElement.TryGetProperty("state", out var state) ? state.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    public static ResultProducer InferGitProducer(string markdown, TaskFileHistoryEntry entry)
    {
        if (markdown.Contains(TaskTransitionService.ResultScaffoldMarker, StringComparison.Ordinal))
            return ResultProducer.Scaffold();
        if (markdown.Contains("Completed out-of-band", StringComparison.OrdinalIgnoreCase)
            || entry.Message.Contains("external completion", StringComparison.OrdinalIgnoreCase))
            return ResultProducer.ExternalCompletion();
        if (entry.Message.Contains("review", StringComparison.OrdinalIgnoreCase))
            return ResultProducer.ReviewAttempt(entry.RunIndex?.ToString());
        return ResultProducer.RunAttempt(entry.RunIndex?.ToString());
    }

    [GeneratedRegex(@"^\s*(?:-\s*)?Result:\s*(.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex ResultLine();

    [GeneratedRegex(@"^\s*(?:-\s*)?Case:\s*(.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex CaseLine();
}

/// <summary>
/// The list answer: entries keep the existing array wire shape, and
/// <see cref="LegacyState"/> travels as the
/// <see cref="LegacyResultHistoryStates.HeaderName"/> header.
/// </summary>
public sealed record ResultHistoryListing(IReadOnlyList<ResultHistoryEntry> Entries, string LegacyState);

public sealed record ResultHistoryEntry(
    string Id,
    DateTime Timestamp,
    string Producer,
    string ProducerKind,
    string Lane,
    string Result,
    string Case,
    string Source,
    int? Version);

public sealed record ResultHistoryDocument(ResultHistoryEntry Entry, string Markdown);
