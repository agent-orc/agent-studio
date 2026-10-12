using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

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
    private readonly ResultVersionStore _versions;
    private readonly LegacyResultHistoryBackfill _legacy;

    public ResultHistoryQueryService(
        TaskScannerService scanner,
        ResultVersionStore versions,
        LegacyResultHistoryBackfill legacy)
    {
        _scanner = scanner;
        _versions = versions;
        _legacy = legacy;
    }

    public TaskFileLookupResult<ResultHistoryListing> List(string jobId, string? watchPath)
    {
        var task = _scanner.FindJob(jobId, watchPath);
        if (task is null)
            return TaskFileLookupResult<ResultHistoryListing>.Fail(404, "Job not found.");

        var (state, snapshot) = _legacy.Observe(task, jobId, watchPath);
        var entries = Collect(task, snapshot)
            .Select(item => item.Entry)
            .ToList();
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
                return TaskFileLookupResult<ResultHistoryDocument>.Fail(404, "Legacy history not loaded yet.");
            if (snapshot.State != LegacyResultHistoryStates.Complete)
                return TaskFileLookupResult<ResultHistoryDocument>.Fail(
                    404, "Legacy history could not be loaded: " + snapshot.Reason);
        }
        else if (!versionId.StartsWith(LocalIdPrefix, StringComparison.Ordinal))
        {
            return TaskFileLookupResult<ResultHistoryDocument>.Fail(404, "Previous result not found.");
        }

        var match = Collect(task, snapshot).FirstOrDefault(item =>
            string.Equals(item.Entry.Id, versionId, StringComparison.OrdinalIgnoreCase));
        if (match.Entry is null)
            return TaskFileLookupResult<ResultHistoryDocument>.Fail(404, "Previous result not found.");
        if (match.Markdown is null)
            return TaskFileLookupResult<ResultHistoryDocument>.Fail(
                404, "This previous result was too large to keep in the legacy history file.");
        return TaskFileLookupResult<ResultHistoryDocument>.Ok(
            new ResultHistoryDocument(match.Entry, match.Markdown), match.Entry.Source);
    }

    private const string LocalIdPrefix = "local-";
    private const string GitIdPrefix = "git-";

    /// <summary>
    /// Local versions (read once each) plus, when <paramref name="legacy"/> is
    /// complete, the legacy versions whose content is neither the current
    /// Result nor a local version, newest first.
    /// </summary>
    private List<(ResultHistoryEntry Entry, string? Markdown)> Collect(
        TaskInfo task,
        LegacyResultHistorySnapshot? legacy)
    {
        var current = ReadOrEmpty(Path.Combine(task.FolderPath, "status.md"));
        var excludedHashes = new HashSet<string>(StringComparer.Ordinal);
        if (current.Length > 0) excludedHashes.Add(ResultHistoryContent.Hash(current));
        var result = new List<(ResultHistoryEntry Entry, string? Markdown)>();

        foreach (var (local, markdown) in _versions.ReadLocalVersions(task.FolderPath))
        {
            excludedHashes.Add(ResultHistoryContent.Hash(markdown));
            var fields = ResultHistoryContent.ParseFields(markdown);
            result.Add((new ResultHistoryEntry(
                Id: $"{LocalIdPrefix}{local.Number:0000}",
                Timestamp: local.ProducedAtUtc,
                Producer: local.Producer.Label,
                ProducerKind: local.Producer.Kind,
                Lane: local.Lane,
                Result: fields.Result,
                Case: fields.Case,
                Source: "task-folder",
                Version: local.Number), markdown));
        }

        if (legacy?.Record is { Outcome: LegacyResultHistoryStates.Complete } record)
        {
            foreach (var git in record.Entries)
            {
                if (!excludedHashes.Add(git.ContentHash)) continue;
                result.Add((new ResultHistoryEntry(
                    Id: GitIdPrefix + git.Sha,
                    Timestamp: git.Timestamp,
                    Producer: git.Producer,
                    ProducerKind: git.ProducerKind,
                    Lane: git.Lane,
                    Result: git.Result,
                    Case: git.Case,
                    Source: "workspace-history",
                    Version: null), git.Markdown));
            }
        }

        return result
            .OrderByDescending(item => item.Entry.Timestamp)
            .ThenByDescending(item => item.Entry.Version)
            .ToList();
    }

    private static string ReadOrEmpty(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : string.Empty; }
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
