using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentStudio.Pipeline;

/// <summary>
/// Maintains the deliberately non-transactional, deletion-tolerant association
/// between task.json and adjacent wiki .meta.json companions.
/// </summary>
public sealed class WikiTaskCrossReferenceService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly ILogger<WikiTaskCrossReferenceService> _logger;
    private readonly ManagedRepositoryMutationService? _repositoryMutations;

    public WikiTaskCrossReferenceService(
        ILogger<WikiTaskCrossReferenceService> logger,
        ManagedRepositoryMutationService? repositoryMutations = null)
    {
        _logger = logger;
        _repositoryMutations = repositoryMutations;
    }

    public int LinkAuto(string repositoryRoot, TaskInfo task, IEnumerable<string> changedFiles)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot) || !Directory.Exists(repositoryRoot)) return 0;
        var docsRoot = Path.Combine(repositoryRoot, "docs");
        if (!Directory.Exists(docsRoot)) return 0;

        var candidates = changedFiles
            .Select(Normalize)
            .Where(IsWikiPage)
            .Where(path => File.Exists(Path.Combine(repositoryRoot, path.Replace('/', Path.DirectorySeparatorChar))))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Wiki-maintenance post-steps create/update pages after the agent SHA
        // range was recorded. Their pages carry the task key/id as evidence,
        // so include those deterministically without relying on mtimes.
        var needles = new[] { task.Key, task.Id }.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
        foreach (var fullPath in Directory.EnumerateFiles(docsRoot, "*", SearchOption.AllDirectories))
        {
            var rel = Normalize(Path.GetRelativePath(repositoryRoot, fullPath));
            if (!IsWikiPage(rel) || rel.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                // Every wiki page is inspected per task; an oversized page is
                // skipped instead of loaded whole (AGT-2991).
                if (!BoundedFileRead.TryReadAllText(fullPath, BoundedFileRead.EvidenceTextBytes, out var content))
                {
                    _logger.LogDebug("Skipped wiki page {Path} for task evidence: larger than {MaxBytes} bytes",
                        fullPath, BoundedFileRead.EvidenceTextBytes);
                    continue;
                }
                if (needles.Any(n => content.Contains(n!, StringComparison.OrdinalIgnoreCase))) candidates.Add(rel);
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Could not inspect wiki page {Path} for task evidence", fullPath);
            }
        }

        var now = DateTime.UtcNow;
        var existing = task.RelatedWikiPages ?? [];
        var taskPages = existing.ToList();
        var added = 0;
        foreach (var relPath in candidates.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var fullPath = Path.Combine(repositoryRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
            var title = ReadTitle(fullPath, relPath);
            var sidecarPath = fullPath + ".meta.json";
            var persisted = _repositoryMutations?.Execute(
                task.ProjectName,
                repositoryRoot,
                $"wiki-task-reference-{task.Id}-{relPath}",
                $"chore(wiki): link {task.Key ?? task.Id} to {relPath}",
                [Path.GetRelativePath(repositoryRoot, sidecarPath).Replace('\\', '/')],
                () => UpsertSidecar(sidecarPath, task.Key ?? task.Id, task.Title, now));
            if (persisted is { Success: false })
            {
                _logger.LogWarning(
                    "wiki-task-cross-reference persistence failed project={Project} job={JobId} page={Page} error={Error}",
                    task.ProjectName, task.Id, relPath, persisted.Error);
                continue;
            }
            if (persisted == null)
                UpsertSidecar(sidecarPath, task.Key ?? task.Id, task.Title, now);

            if (!taskPages.Any(p => string.Equals(p.RelPath, relPath, StringComparison.OrdinalIgnoreCase)))
            {
                taskPages.Add(new RelatedWikiPage { RelPath = relPath, Title = title, LinkedAt = now, Source = WikiTaskReferenceSources.Auto });
                added++;
            }
        }

        if (added > 0) TaskJsonFile.UpdateField(task.FolderPath, "relatedWikiPages", taskPages, _logger);
        _logger.LogInformation("wiki-task-cross-references-linked project={Project} job={JobId} pages={Pages} added={Added}",
            task.ProjectName, task.Id, candidates.Count, added);
        return added;
    }

    private void UpsertSidecar(string sidecarPath, string key, string title, DateTime linkedAt)
    {
        JsonObject root;
        try { root = File.Exists(sidecarPath) ? JsonNode.Parse(File.ReadAllText(sidecarPath)) as JsonObject ?? new() : new(); }
        catch (JsonException) { root = new(); }
        var refs = root["relatedTasks"] as JsonArray ?? new JsonArray();
        if (!refs.OfType<JsonObject>().Any(x => string.Equals(x["key"]?.GetValue<string>(), key, StringComparison.OrdinalIgnoreCase)))
        {
            refs.Add(new JsonObject { ["key"] = key, ["title"] = title, ["linkedAt"] = linkedAt, ["source"] = WikiTaskReferenceSources.Auto });
            root["relatedTasks"] = refs;
            Directory.CreateDirectory(Path.GetDirectoryName(sidecarPath)!);
            File.WriteAllText(sidecarPath, root.ToJsonString(JsonOptions));
        }
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');
    private static bool IsWikiPage(string path) => path.StartsWith("docs/", StringComparison.OrdinalIgnoreCase)
        && !path.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase)
        && Path.GetExtension(path).ToLowerInvariant() is ".md" or ".html" or ".htm" or ".json";

    private string ReadTitle(string fullPath, string relPath)
    {
        try
        {
            foreach (var line in File.ReadLines(fullPath).Take(40))
                if (line.StartsWith("# ", StringComparison.Ordinal)) return line[2..].Trim();
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Could not read wiki title from {Path}", fullPath);
        }
        return Path.GetFileNameWithoutExtension(relPath);
    }
}
