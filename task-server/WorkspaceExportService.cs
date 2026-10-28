using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentStudio.TaskServer;

public sealed record WorkspaceExportProjectCount(string Project, int Tasks, int Files);

public sealed record WorkspaceExportResult(
    string Root,
    int Projects,
    int Tasks,
    int FilesWritten,
    int FilesUnchanged,
    int FilesRemoved,
    int SkippedPaths,
    int PointerOnlyArtifacts,
    long Bytes,
    IReadOnlyList<WorkspaceExportProjectCount> ProjectCounts);

/// <summary>
/// Pure path and <c>task.json</c> rules of the workspace export. The export
/// writes the flat layout today's tools read
/// (<c>projects/&lt;project&gt;/tasks/&lt;bucket&gt;/&lt;KEY&gt;/</c>, bucket =
/// key number / 1000 in three digits, lane in <c>task.json.state</c>), the same
/// layout as the monolith's <c>TaskStorageLayout</c>.
/// </summary>
public static class WorkspaceExportLayout
{
    public const string ManifestFileName = "workspace-export.json";
    public const string ProjectsDirName = "projects";
    public const string TasksDirName = "tasks";
    public const string ResultsDirName = "results";

    private static readonly JsonSerializerOptions TaskJsonOptions = new() { WriteIndented = true };

    public static string TaskFolder(string projectName, string taskKey)
    {
        var dash = taskKey.LastIndexOf('-');
        var tail = dash >= 0 ? taskKey[(dash + 1)..] : taskKey;
        var number = int.TryParse(tail, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : 0;
        return $"{ProjectsDirName}/{SafeSegment(projectName)}/{TasksDirName}/{number / 1000:D3}/{SafeSegment(taskKey)}";
    }

    /// <summary>A run artifact lands under <c>results/</c> unless its name already starts there.</summary>
    public static string ArtifactPath(string artifactName)
    {
        var normalized = TaskServerStore.NormalizeTaskFilePath(artifactName);
        return normalized.StartsWith(ResultsDirName + "/", StringComparison.Ordinal)
            ? normalized
            : $"{ResultsDirName}/{normalized}";
    }

    /// <summary>
    /// The exported <c>task.json</c>: the mirrored file when the card has one,
    /// else the imported legacy payload, else an empty object; then the Task
    /// Server's card core wins for identity, title, lane and timestamps,
    /// because after the write cutover the row is the authority.
    /// </summary>
    public static byte[] RenderTaskJson(WorkspaceExportTask task, byte[]? mirroredTaskJson)
    {
        var node = ParseObject(mirroredTaskJson) ?? ParseObject(task.LegacyTaskJson is null ? null : Encoding.UTF8.GetBytes(task.LegacyTaskJson))
                   ?? new JsonObject();
        if (node["id"] is not JsonValue id || id.GetValueKind() != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetValue<string>()))
            node["id"] = task.TaskKey;
        node["key"] = task.TaskKey;
        node["title"] = task.Title;
        node["state"] = task.State;
        node["createdAt"] ??= task.CreatedAt.ToString("O", CultureInfo.InvariantCulture);
        node["updatedAt"] = task.UpdatedAt.ToString("O", CultureInfo.InvariantCulture);
        if (node["order"] is null && task.Rank is >= int.MinValue and <= int.MaxValue)
            node["order"] = (int)task.Rank;
        return Encoding.UTF8.GetBytes(node.ToJsonString(TaskJsonOptions) + "\n");
    }

    private static JsonObject? ParseObject(byte[]? content)
    {
        if (content is null || content.Length == 0) return null;
        try
        {
            return JsonNode.Parse(content) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string SafeSegment(string value)
    {
        var segment = value.Trim();
        if (segment.Length == 0 || segment is "." or ".." || segment.IndexOfAny(['/', '\\', ':']) >= 0
            || segment.Equals(".git", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"'{value}' cannot name an exported folder.");
        return segment;
    }
}

/// <summary>
/// Periodic export of the Task Server's cards and artefacts into a clone of
/// the workspace repository (pipeline delivery target, migration M6, decision
/// D9 A): task folders for audit and offline reading. Nothing reads the export
/// in a request path. A file is rewritten only when its bytes changed, and a
/// file under <c>projects/*/tasks/</c> that no card holds any more is removed,
/// so an unchanged store produces an unchanged tree and no commit.
/// </summary>
public sealed class WorkspaceExportService(TaskServerStore store)
{
    public async Task<WorkspaceExportResult> ExportAsync(string root, CancellationToken ct)
    {
        var target = Path.GetFullPath(root);
        Directory.CreateDirectory(target);
        var expected = new HashSet<string>(StringComparer.Ordinal);
        var projects = new SortedDictionary<string, (int Tasks, int Files)>(StringComparer.Ordinal);
        int tasks = 0, written = 0, unchanged = 0, skipped = 0, pointerOnly = 0;
        long bytes = 0;

        await foreach (var task in store.ReadWorkspaceExportAsync(ct))
        {
            var folder = WorkspaceExportLayout.TaskFolder(task.ProjectName, task.TaskKey);
            var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
            byte[]? mirroredTaskJson = null;
            foreach (var file in task.Files)
            {
                string path;
                try { path = TaskServerStore.NormalizeTaskFilePath(file.Path); }
                catch (ArgumentException) { skipped++; continue; }
                if (string.Equals(path, "task.json", StringComparison.Ordinal)) { mirroredTaskJson = file.Content; continue; }
                files[path] = file.Content;
            }
            files["task.json"] = WorkspaceExportLayout.RenderTaskJson(task, mirroredTaskJson);
            if (!files.ContainsKey("prompt.md") && !string.IsNullOrEmpty(task.Body))
                files["prompt.md"] = Encoding.UTF8.GetBytes(task.Body);
            foreach (var artifact in task.Artifacts)
            {
                string path;
                try { path = WorkspaceExportLayout.ArtifactPath(artifact.Path); }
                catch (ArgumentException) { skipped++; continue; }
                files.TryAdd(path, artifact.Content);
            }

            foreach (var (relative, content) in files)
            {
                var exportPath = folder + "/" + relative;
                expected.Add(exportPath);
                bytes += content.LongLength;
                if (WriteIfChanged(Path.Combine(target, exportPath.Replace('/', Path.DirectorySeparatorChar)), content))
                    written++;
                else
                    unchanged++;
            }
            tasks++;
            pointerOnly += task.PointerOnlyArtifacts;
            var count = projects.GetValueOrDefault(task.ProjectName);
            projects[task.ProjectName] = (count.Tasks + 1, count.Files + files.Count);
        }

        var removed = RemoveStaleTaskFiles(target, expected);
        var counts = projects.Select(pair => new WorkspaceExportProjectCount(pair.Key, pair.Value.Tasks, pair.Value.Files)).ToList();
        // No timestamp in the manifest: an unchanged store must leave the tree unchanged.
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            layout = "projects/<project>/tasks/<bucket>/<KEY>",
            tasks,
            pointerOnlyArtifacts = pointerOnly,
            projects = counts,
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        WriteIfChanged(Path.Combine(target, WorkspaceExportLayout.ManifestFileName), [.. manifest, (byte)'\n']);
        return new WorkspaceExportResult(target, projects.Count, tasks, written, unchanged, removed, skipped,
            pointerOnly, bytes, counts);
    }

    private static bool WriteIfChanged(string path, byte[] content)
    {
        var info = new FileInfo(path);
        if (info.Exists && info.Length == content.LongLength && File.ReadAllBytes(path).AsSpan().SequenceEqual(content))
            return false;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".export-tmp";
        File.WriteAllBytes(temporary, content);
        File.Move(temporary, path, overwrite: true);
        return true;
    }

    /// <summary>Removes export-owned files no card holds any more; nothing outside <c>projects/*/tasks/</c> is touched.</summary>
    private static int RemoveStaleTaskFiles(string root, IReadOnlySet<string> expected)
    {
        var projectsRoot = Path.Combine(root, WorkspaceExportLayout.ProjectsDirName);
        if (!Directory.Exists(projectsRoot)) return 0;
        var removed = 0;
        foreach (var project in Directory.EnumerateDirectories(projectsRoot))
        {
            var tasksRoot = Path.Combine(project, WorkspaceExportLayout.TasksDirName);
            if (!Directory.Exists(tasksRoot)) continue;
            foreach (var file in Directory.EnumerateFiles(tasksRoot, "*", SearchOption.AllDirectories).ToList())
            {
                var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
                if (expected.Contains(relative)) continue;
                File.Delete(file);
                removed++;
            }
            foreach (var directory in Directory.EnumerateDirectories(tasksRoot, "*", SearchOption.AllDirectories)
                         .OrderByDescending(path => path.Length).ToList())
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            }
        }
        return removed;
    }
}
