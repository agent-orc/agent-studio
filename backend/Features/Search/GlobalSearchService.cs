using System.Diagnostics;
using System.Text;
using AgentStudio.Docs;
using AgentStudio.Git;
using AgentStudio.Registry;
using AgentStudio.Shared;

namespace AgentStudio.Search;

public sealed record GlobalSearchItem(
    string Domain,
    string ProjectName,
    string ProjectColor,
    string Title,
    string Subtitle,
    string? TaskKey = null,
    string? Lane = null,
    string? Sha = null,
    string? Path = null,
    bool IsWiki = false,
    string? DossierKey = null,
    string? DossierId = null,
    string? Summary = null);

public sealed record GlobalSearchResponse(
    string Query,
    IReadOnlyList<GlobalSearchItem> Tasks,
    IReadOnlyList<GlobalSearchItem> Commits,
    IReadOnlyList<GlobalSearchItem> Files,
    IReadOnlyList<GlobalSearchItem> Dossiers,
    IReadOnlyDictionary<string, string> Errors,
    long DurationMs);

/// <summary>Bounded, read-only workspace search. Git results reuse GitService's HEAD-keyed LRU.</summary>
public sealed class GlobalSearchService(
    TaskScannerService scanner,
    GitService git,
    ProjectRegistry registry,
    ProjectDocsService docs,
    WorkbenchCatalogueService workbenches,
    ILogger<GlobalSearchService> logger)
{
    private const int MaxPerDomain = 30;

    public GlobalSearchResponse Search(string query, ISet<string> domains, int limit)
    {
        var timer = Stopwatch.StartNew();
        limit = Math.Clamp(limit, 1, MaxPerDomain);
        var tasks = new List<GlobalSearchItem>();
        var commits = new List<GlobalSearchItem>();
        var files = new List<GlobalSearchItem>();
        var dossiers = new List<GlobalSearchItem>();
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var registered = registry.List().Where(p => !p.Archived).ToList();
        var colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in registered) colors[project.DisplayName] = project.Color ?? "#6e6e6e";
        foreach (var watchPath in scanner.GetWatchPaths()) colors.TryAdd(watchPath.Name, "#6e6e6e");

        if (domains.Contains("tasks"))
        {
            try { tasks = SearchTasks(query, limit, colors); }
            catch (Exception ex) { Degrade("tasks", ex, errors); }
        }

        if (domains.Contains("dossiers"))
        {
            try { dossiers = SearchDossiers(ReadDossierCatalogue(), query, limit, colors); }
            catch (Exception ex) { Degrade("dossiers", ex, errors); }
        }

        var repositories = registered
            .Select(p => (Name: p.DisplayName, Root: p.RepositoryPath ?? p.RootPath))
            .Where(p => !string.IsNullOrWhiteSpace(p.Root) && Directory.Exists(p.Root))
            .Select(p => (p.Name, Root: p.Root!))
            .Concat(scanner.GetWatchPaths().Select(p => (p.Name, Root: p.RepositoryPath.Length > 0 ? p.RepositoryPath : p.RootPath)))
            .Where(p => !string.IsNullOrWhiteSpace(p.Root) && Directory.Exists(p.Root))
            .GroupBy(p => Path.GetFullPath(p.Root), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First());

        foreach (var project in repositories)
        {
            var root = project.Root;
            if (domains.Contains("commits"))
            {
                try
                {
                    var cached = git.MemoizeByHead(root, $"global-search-commits|{root}|{query.ToLowerInvariant()}",
                        () => ReadCommits(root, project.Name, query, colors.GetValueOrDefault(project.Name, "#6e6e6e")));
                    commits.AddRange(cached);
                }
                catch (Exception ex) { Degrade("commits", ex, errors, project.Name); }
            }
            if (domains.Contains("files"))
            {
                try
                {
                    var cached = git.MemoizeByHead(root, $"global-search-files|{root}|{query.ToLowerInvariant()}",
                        () => ReadFiles(root, project.Name, query, colors.GetValueOrDefault(project.Name, "#6e6e6e")));
                    files.AddRange(cached);
                }
                catch (Exception ex) { Degrade("files", ex, errors, project.Name); }
            }
        }

        timer.Stop();
        logger.LogInformation(
            "global-search-completed queryLength={QueryLength} domains={Domains} tasks={Tasks} commits={Commits} files={Files} dossiers={Dossiers} errors={Errors} durationMs={DurationMs}",
            query.Length, string.Join(',', domains), tasks.Count, commits.Count, files.Count, dossiers.Count, errors.Count, timer.ElapsedMilliseconds);
        return new(query,
            tasks.Take(limit).ToList(),
            RankItems(commits, query).Take(limit).ToList(),
            RankItems(files, query).Take(limit).ToList(),
            dossiers,
            errors, timer.ElapsedMilliseconds);
    }

    /// <summary>
    /// Feeds the Dossier domain from the same cached Wiki snapshot the Dossier
    /// overview reads, so both surfaces agree on which projects own a catalogue
    /// (archived registry projects own none) and search never triggers its own
    /// recursive docs/ scan. History is included: a decided or archived Dossier
    /// stays findable by its key.
    /// </summary>
    private IReadOnlyList<WorkbenchOverviewItem> ReadDossierCatalogue() =>
        docs.GetWikiWorkbenchOverview(workbenches.ListProjectNames()).Items;

    /// <summary>
    /// Ranks the catalogue for one query: an exact document key first, then a
    /// title match, then a summary match, then the remaining id, status, and
    /// phase word matches. Invalid descriptors stay out because the viewer
    /// refuses to open them.
    /// </summary>
    internal static List<GlobalSearchItem> SearchDossiers(
        IEnumerable<WorkbenchOverviewItem> catalogue,
        string query,
        int limit,
        IReadOnlyDictionary<string, string> colors) => catalogue
        .Where(entry => entry.Workbench.Valid)
        .Select(entry => (Entry: entry, Rank: DossierRank(entry.Workbench, query)))
        .Where(x => x.Rank < NoDossierMatch)
        .OrderBy(x => x.Rank)
        .ThenByDescending(x => x.Entry.Workbench.UpdatedAtUtc)
        .Take(limit)
        .Select(x => new GlobalSearchItem("dossiers", x.Entry.ProjectName,
            colors.GetValueOrDefault(x.Entry.ProjectName, "#6e6e6e"),
            x.Entry.Workbench.Title,
            DossierSubtitle(x.Entry.Workbench),
            DossierKey: x.Entry.Workbench.Key,
            DossierId: x.Entry.Workbench.Id,
            Summary: x.Entry.Workbench.Summary))
        .ToList();

    private const int NoDossierMatch = 4;

    private static int DossierRank(WorkbenchListItem item, string query) =>
        string.Equals(item.Key, query, StringComparison.OrdinalIgnoreCase) ? 0
        : Contains(item.Title, query) ? 1
        : Contains(item.Summary, query) ? 2
        : Contains(item.Key, query) || Contains(item.Id, query)
            || Contains(item.Status, query) || Contains(item.Phase, query) ? 3
        : NoDossierMatch;

    private static string DossierSubtitle(WorkbenchListItem item) =>
        string.IsNullOrWhiteSpace(item.Phase) ? item.Status : $"{item.Status} · {item.Phase}";

    private List<GlobalSearchItem> SearchTasks(string query, int limit, IReadOnlyDictionary<string, string> colors)
    {
        return scanner.ScanAllAutomationJobsWithArchive()
            .Select(task => (Task: task, Text: ReadTaskText(task)))
            .Where(x => Contains(x.Task.Key, query) || Contains(x.Task.Title, query) || Contains(x.Text, query) || Contains(x.Task.State, query))
            .OrderBy(x => string.Equals(x.Task.Key, query, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenByDescending(x => x.Task.LastActivity)
            .Take(limit)
            .Select(x => new GlobalSearchItem("tasks", x.Task.ProjectName,
                colors.GetValueOrDefault(x.Task.ProjectName, "#6e6e6e"), x.Task.Title,
                FirstMatchingLine(x.Text, query) ?? x.Task.State, x.Task.TaskKey, x.Task.State))
            .ToList();
    }

    private static string ReadTaskText(TaskInfo task)
    {
        var text = new StringBuilder();
        foreach (var name in new[] { "prompt.md", "status.md" })
        {
            var path = Path.Combine(task.FolderPath, name);
            if (File.Exists(path)) text.AppendLine(File.ReadAllText(path));
        }
        return text.ToString();
    }

    internal static List<GlobalSearchItem> ReadCommits(string root, string project, string query, string color)
    {
        var output = RunGit(root, ["log", "--all", "--no-merges", "--max-count=250", "--pretty=format:%H%x1f%h%x1f%s"]);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r').Split('\x1f'))
            .Where(p => p.Length >= 3 && (Contains(p[0], query) || Contains(p[1], query) || Contains(p[2], query)))
            .Select(p => new GlobalSearchItem("commits", project, color, p[2], p[1], Sha: p[0]))
            .ToList();
    }

    internal static List<GlobalSearchItem> ReadFiles(string root, string project, string query, string color)
    {
        var output = RunGit(root, ["ls-files", "--cached", "--others", "--exclude-standard"]);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(path => path.TrimEnd('\r').Replace('\\', '/'))
            .Where(path => Contains(path, query))
            .Select(path => new GlobalSearchItem("files", project, color, Path.GetFileName(path), path,
                Path: path, IsWiki: path.StartsWith("docs/", StringComparison.OrdinalIgnoreCase)
                    // docs/app/ is a code contract, not a wiki page: never route it into the wiki viewer.
                    && !path.StartsWith("docs/app/", StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    internal static IEnumerable<GlobalSearchItem> RankItems(IEnumerable<GlobalSearchItem> items, string query) => items
        .OrderBy(i => string.Equals(i.Title, query, StringComparison.OrdinalIgnoreCase) || string.Equals(i.Subtitle, query, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
        .ThenBy(i => i.Title.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
        .ThenBy(i => i.Title.Length);

    private void Degrade(string domain, Exception ex, IDictionary<string, string> errors, string? project = null)
    {
        errors[domain] = "Some results could not be loaded.";
        logger.LogWarning(ex, "global-search-domain-failed domain={Domain} project={Project}", domain, project);
    }

    private static bool Contains(string? value, string query) => value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
    private static string? FirstMatchingLine(string text, string query) => text.Split('\n').Select(x => x.Trim()).FirstOrDefault(x => Contains(x, query));

    private static string RunGit(string root, IReadOnlyList<string> args)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo("git") {
            WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        }};
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(10_000))
        {
            process.Kill(true);
            throw new TimeoutException("git search exceeded 10 seconds");
        }
        if (process.ExitCode != 0) throw new InvalidOperationException(stderr.Trim());
        return stdout;
    }
}
