using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentStudio.Tasks;

/// <summary>
/// Read model for previous Results. It merges snapshots created by
/// <see cref="ResultVersionStore"/> with legacy status.md versions from the
/// workspace repository, so existing cards need no migration.
/// </summary>
public sealed partial class ResultHistoryQueryService
{
    private readonly TaskScannerService _scanner;
    private readonly TaskFileHistoryService _files;
    private readonly ResultVersionStore _versions;

    public ResultHistoryQueryService(
        TaskScannerService scanner,
        TaskFileHistoryService files,
        ResultVersionStore versions)
    {
        _scanner = scanner;
        _files = files;
        _versions = versions;
    }

    public TaskFileLookupResult<IReadOnlyList<ResultHistoryEntry>> List(string jobId, string? watchPath)
    {
        var task = _scanner.FindJob(jobId, watchPath);
        if (task is null)
            return TaskFileLookupResult<IReadOnlyList<ResultHistoryEntry>>.Fail(404, "Job not found.");

        var current = ReadOrEmpty(Path.Combine(task.FolderPath, "status.md"));
        var excludedHashes = new HashSet<string>(StringComparer.Ordinal);
        if (current.Length > 0) excludedHashes.Add(Hash(current));
        var result = new List<ResultHistoryEntry>();

        foreach (var local in _versions.ReadLocalHistory(task.FolderPath))
        {
            var markdown = _versions.ReadLocalVersion(task.FolderPath, local.Number);
            if (markdown is null) continue;
            excludedHashes.Add(Hash(markdown));
            var fields = ParseFields(markdown);
            result.Add(new ResultHistoryEntry(
                Id: $"local-{local.Number:0000}",
                Timestamp: local.ProducedAtUtc,
                Producer: local.Producer.Label,
                ProducerKind: local.Producer.Kind,
                Lane: local.Lane,
                Result: fields.Result,
                Case: fields.Case,
                Source: "task-folder",
                Version: local.Number));
        }

        var history = _files.GetWorkspaceResultHistory(jobId, watchPath);
        if (history.Success)
        {
            foreach (var version in history.Value ?? [])
            {
                var git = version.Entry;
                var hash = Hash(version.Content);
                if (!excludedHashes.Add(hash)) continue;
                var producer = InferGitProducer(version.Content, git);
                var fields = ParseFields(version.Content);
                result.Add(new ResultHistoryEntry(
                    Id: "git-" + git.Sha,
                    Timestamp: git.At ?? DateTime.MinValue,
                    Producer: producer.Label,
                    ProducerKind: producer.Kind,
                    Lane: ReadLane(version.TaskJson) ?? "unknown",
                    Result: fields.Result,
                    Case: fields.Case,
                    Source: "workspace-history",
                    Version: null));
            }
        }

        return TaskFileLookupResult<IReadOnlyList<ResultHistoryEntry>>.Ok(
            result.OrderByDescending(item => item.Timestamp).ThenByDescending(item => item.Version).ToList(),
            TaskFileSources.Workspace);
    }

    public TaskFileLookupResult<ResultHistoryDocument> Read(string jobId, string? watchPath, string versionId)
    {
        var task = _scanner.FindJob(jobId, watchPath);
        if (task is null)
            return TaskFileLookupResult<ResultHistoryDocument>.Fail(404, "Job not found.");

        var entry = List(jobId, watchPath).Value?.SingleOrDefault(item =>
            string.Equals(item.Id, versionId, StringComparison.Ordinal));
        if (entry is null)
            return TaskFileLookupResult<ResultHistoryDocument>.Fail(404, "Previous result not found.");

        string? markdown;
        if (versionId.StartsWith("local-", StringComparison.Ordinal)
            && int.TryParse(versionId["local-".Length..], out var number))
        {
            markdown = _versions.ReadLocalVersion(task.FolderPath, number);
        }
        else if (versionId.StartsWith("git-", StringComparison.Ordinal))
        {
            markdown = _files.GetWorkspaceResultHistory(jobId, watchPath).Value?
                .SingleOrDefault(item => string.Equals(
                    item.Entry.Sha,
                    versionId["git-".Length..],
                    StringComparison.OrdinalIgnoreCase))?.Content;
        }
        else
        {
            markdown = null;
        }

        return markdown is null
            ? TaskFileLookupResult<ResultHistoryDocument>.Fail(404, "Previous result content was not found.")
            : TaskFileLookupResult<ResultHistoryDocument>.Ok(new ResultHistoryDocument(entry, markdown), entry.Source);
    }

    private static string? ReadLane(string? taskJson)
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

    private static ResultProducer InferGitProducer(string markdown, TaskFileHistoryEntry entry)
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

    private static (string Result, string Case) ParseFields(string markdown)
    {
        var result = ResultLine().Match(markdown).Groups[1].Value.Trim();
        var resultCase = CaseLine().Match(markdown).Groups[1].Value.Trim();
        return (
            result.Length == 0 ? "Not recorded" : result,
            resultCase.Length == 0 ? "generic" : resultCase);
    }

    private static string ReadOrEmpty(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : string.Empty; }
        catch { return string.Empty; }
    }

    private static string Hash(string content)
    {
        // Git may store LF while a Windows worktree materializes CRLF. Treat
        // those as the same Result when excluding the live/local versions from
        // read-time Git backfill.
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    [GeneratedRegex(@"^\s*(?:-\s*)?Result:\s*(.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex ResultLine();

    [GeneratedRegex(@"^\s*(?:-\s*)?Case:\s*(.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex CaseLine();
}

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
