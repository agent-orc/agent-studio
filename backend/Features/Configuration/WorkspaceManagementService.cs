using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentStudio.Configuration;

/// <summary>
/// Create / delete watch-path entries in <c>appsettings.Local.json</c>'s
/// <c>WatchPaths</c> array. A watch path is the unit the rest of the app
/// calls a workspace: every entry surfaces as a project on the kanban,
/// owns its own lane folders under
/// <c>{TaskRepository}/projects/{slug}/</c>, and shows up in the project
/// picker. The service is the only path that touches the array; callers
/// (HTTP endpoints, future automation) go through here so the on-disk
/// folder layout and the JSON entry stay in lockstep.
///
/// <para><b>Persistence model:</b> writes go to <c>appsettings.Local.json</c>
/// next to the running checkout (same target file as
/// <see cref="OrchestratorConfigService"/>), under a process-wide lock.
/// After every successful write the in-process <see cref="IConfiguration"/>
/// is reloaded so subsequent <c>GetWatchPaths()</c> calls observe the
/// change synchronously without a backend restart. Stable and dev each
/// own their own <c>appsettings.Local.json</c>; the service writes to
/// whichever copy belongs to the running checkout.</para>
///
/// <para><b>Delete safety:</b> a workspace can only be removed when its
/// resolved watch path holds zero job folders across every lane. This is
/// the API mirror of the UI "block delete when non-empty" rule: it keeps
/// us from silently orphaning per-project work when the user is in the
/// wrong panel. The folder itself is left on disk so a re-create with
/// the same name picks up where the previous workspace left off.</para>
/// </summary>
public sealed class WorkspaceManagementService
{
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _env;
    private readonly TaskScannerService _scanner;
    private readonly TaskIndexCache? _indexCache;
    private readonly ILogger<WorkspaceManagementService> _logger;
    private static readonly object FileLock = new();

    public const int MaxNameLength = 64;

    public WorkspaceManagementService(
        IConfiguration configuration,
        IHostEnvironment env,
        TaskScannerService scanner,
        ILogger<WorkspaceManagementService> logger,
        TaskIndexCache? indexCache = null)
    {
        _configuration = configuration;
        _env = env;
        _scanner = scanner;
        _logger = logger;
        _indexCache = indexCache;
    }

    public string OverrideFilePath =>
        Path.Combine(_env.ContentRootPath, "appsettings.Local.json");

    /// <summary>
    /// Creates a new (empty) workspace. The <paramref name="name"/> is
    /// trimmed and must be unique against existing
    /// <see cref="WatchPathEntry.Name"/> values (case-insensitive). The
    /// resolved path is <c>{TaskRepository}/projects/{slug}</c>; the
    /// folder and its lane subdirectories are created on disk before
    /// the config entry is appended so the very first
    /// <see cref="TaskScannerService.GetWatchPaths"/> call after this
    /// returns sees a fully prepared workspace.
    /// </summary>
    public WorkspaceManagementResult Create(string? name)
    {
        var trimmed = (name ?? "").Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return WorkspaceManagementResult.BadRequest("Name is required.");
        }
        if (trimmed.Length > MaxNameLength)
        {
            return WorkspaceManagementResult.BadRequest(
                $"Name must be {MaxNameLength} characters or fewer.");
        }

        var taskRepository = _configuration["TaskRepository"];
        if (string.IsNullOrWhiteSpace(taskRepository))
        {
            return WorkspaceManagementResult.BadRequest(
                "TaskRepository is not configured; cannot create a new workspace.");
        }

        // Case-insensitive collision check across the existing array.
        if (_scanner.GetWatchPaths().Any(e =>
                string.Equals(e.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return WorkspaceManagementResult.Conflict(
                $"A workspace named '{trimmed}' already exists.");
        }

        var slug = Slugify(trimmed);
        if (string.IsNullOrEmpty(slug))
        {
            return WorkspaceManagementResult.BadRequest(
                "Name must contain at least one letter or digit.");
        }
        var resolvedPath = Path.GetFullPath(Path.Combine(taskRepository, "projects", slug));

        // Slug collision against existing paths (e.g. two workspaces whose
        // names slug down to the same folder). Catches the case where a
        // workspace called "My Workspace" already exists and the user
        // tries to create "my-workspace".
        if (_scanner.GetWatchPaths().Any(e =>
                string.Equals(e.Path, resolvedPath, StringComparison.OrdinalIgnoreCase)))
        {
            return WorkspaceManagementResult.Conflict(
                $"A workspace already maps to the folder '{resolvedPath}'.");
        }

        try
        {
            // Only the workspace root is materialised here. Lane subfolders
            // (1-preparation / 2-ready / ...) are created on demand by
            // TaskMutationService.CreateJob the first time a job lands in
            // each lane, so workspace creation stays a thin filesystem
            // operation and the per-lane structural mutations remain owned
            // by the storage authority (ADR-0024).
            Directory.CreateDirectory(resolvedPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create workspace folder {Path}", resolvedPath);
            return WorkspaceManagementResult.BadRequest(
                $"Could not create workspace folder: {ex.Message}");
        }

        var entry = new WatchPathEntry
        {
            Name = trimmed,
            Path = resolvedPath,
            RootPath = "",
            RepositoryPath = ""
        };

        lock (FileLock)
        {
            var root = ReadOrCreateRoot();
            var array = (root["WatchPaths"] as JsonArray) ?? new JsonArray();
            array.Add(new JsonObject
            {
                ["Name"] = trimmed,
                ["Path"] = resolvedPath,
            });
            root["WatchPaths"] = array;
            WriteAtomic(root);
            ReloadConfiguration();
        }

        _indexCache?.Invalidate(TaskIndexCache.InvalidationSource.Mutation, clientId: AgentStudio.Clients.AmbientClient.Id);
        _logger.LogInformation(
            "Created workspace '{Name}' at {Path}", trimmed, resolvedPath);

        return WorkspaceManagementResult.Created(entry);
    }

    /// <summary>
    /// Removes a workspace by name. The watch path must hold zero job
    /// folders across every lane; an attempt to delete a non-empty
    /// workspace returns <see cref="WorkspaceManagementOutcome.Conflict"/>
    /// with the live job count so the caller can render a clear error.
    /// The on-disk folder is left in place so re-create-with-same-name
    /// is reversible.
    /// </summary>
    public WorkspaceManagementResult Delete(string? name)
    {
        var trimmed = (name ?? "").Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return WorkspaceManagementResult.BadRequest("Name is required.");
        }

        var existing = _scanner.GetWatchPaths()
            .FirstOrDefault(e =>
                string.Equals(e.Name, trimmed, StringComparison.OrdinalIgnoreCase));
        if (existing == null)
        {
            return WorkspaceManagementResult.NotFound(
                $"No workspace named '{trimmed}'.");
        }

        var jobCount = CountJobs(existing);
        if (jobCount > 0)
        {
            return WorkspaceManagementResult.Conflict(
                $"Workspace '{existing.Name}' still contains {jobCount} " +
                $"job{(jobCount == 1 ? "" : "s")}. Move or delete them first.",
                jobCount);
        }

        lock (FileLock)
        {
            var root = ReadOrCreateRoot();
            if (root["WatchPaths"] is JsonArray array)
            {
                for (int i = array.Count - 1; i >= 0; i--)
                {
                    var node = array[i];
                    if (node is not JsonObject obj) continue;
                    var nodeName = obj["Name"]?.GetValue<string>() ?? "";
                    if (string.Equals(nodeName, existing.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        array.RemoveAt(i);
                    }
                }
                root["WatchPaths"] = array;
                WriteAtomic(root);
                ReloadConfiguration();
            }
        }

        _indexCache?.Invalidate(TaskIndexCache.InvalidationSource.Mutation, clientId: AgentStudio.Clients.AmbientClient.Id);
        _logger.LogInformation(
            "Deleted workspace '{Name}' (path {Path} left on disk)",
            existing.Name, existing.Path);

        return WorkspaceManagementResult.Ok(existing);
    }

    /// <summary>
    /// F46 — destructive project deletion support behind the registry
    /// <c>DELETE /api/projects/{PROJ-NNN}</c> endpoint. Removes the on-disk
    /// project storage folder at <paramref name="storageLocation"/>
    /// (recursively, including every lane and task) and drops any
    /// <c>WatchPaths</c> entry whose resolved path points at it, so the
    /// project leaves both the kanban and the project picker with no ghost
    /// row. Unlike <see cref="Delete"/> this does not guard on emptiness —
    /// the caller has already gated the action behind a typed two-stage
    /// confirmation.
    ///
    /// <para><b>Ordering (no orphan folders):</b> the folder is deleted
    /// first. If that fails the method aborts before touching any metadata,
    /// so a configuration pointer is never left aimed at a half-deleted
    /// folder. The registry record itself is removed separately by the
    /// caller after this returns.</para>
    /// </summary>
    public WorkspaceManagementResult DeleteProjectStorage(string? storageLocation)
    {
        var target = (storageLocation ?? "").Trim();
        if (string.IsNullOrEmpty(target))
        {
            return WorkspaceManagementResult.BadRequest("storageLocation is required.");
        }

        var normalizedTarget = NormalizePath(target);
        var folderRemoved = false;
        if (Directory.Exists(target))
        {
            try
            {
                Directory.Delete(target, recursive: true);
                folderRemoved = true;
                var projectContainer = Directory.GetParent(target)?.FullName;
                var taskRepository = _configuration["TaskRepository"];
                var projectsRoot = string.IsNullOrWhiteSpace(taskRepository)
                    ? null
                    : Path.GetFullPath(Path.Combine(taskRepository, "projects"));
                if (string.Equals(Path.GetFileName(target), "tasks", StringComparison.OrdinalIgnoreCase)
                    && projectContainer != null
                    && projectsRoot != null
                    && string.Equals(Directory.GetParent(projectContainer)?.FullName, projectsRoot, StringComparison.OrdinalIgnoreCase)
                    && Directory.Exists(projectContainer)
                    && !Directory.EnumerateFileSystemEntries(projectContainer).Any())
                {
                    Directory.Delete(projectContainer);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "project-storage-delete-failed path={Path}", target);
                return WorkspaceManagementResult.BadRequest(
                    $"Could not delete project folder: {ex.Message}");
            }
        }

        // Drop any WatchPaths entry resolving to this folder so the project
        // stops surfacing as a known-project ghost row after the registry
        // record is gone. Entries are matched by resolved Path; the raw JSON
        // array is keyed by Name, so we resolve, then remove by Name.
        var match = _scanner.GetWatchPaths().FirstOrDefault(e =>
            string.Equals(NormalizePath(e.Path), normalizedTarget, StringComparison.OrdinalIgnoreCase));
        if (match != null)
        {
            lock (FileLock)
            {
                var root = ReadOrCreateRoot();
                if (root["WatchPaths"] is JsonArray array)
                {
                    for (int i = array.Count - 1; i >= 0; i--)
                    {
                        if (array[i] is not JsonObject obj) continue;
                        var nodeName = obj["Name"]?.GetValue<string>() ?? "";
                        if (string.Equals(nodeName, match.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            array.RemoveAt(i);
                        }
                    }
                    root["WatchPaths"] = array;
                    WriteAtomic(root);
                    ReloadConfiguration();
                }
            }
        }

        _indexCache?.Invalidate(TaskIndexCache.InvalidationSource.Mutation, clientId: AgentStudio.Clients.AmbientClient.Id);
        _logger.LogInformation(
            "project-storage-deleted path={Path} folderRemoved={Removed} watchPathRemoved={WatchPath}",
            target, folderRemoved, match?.Name ?? "(none)");

        return WorkspaceManagementResult.Ok(
            match ?? new WatchPathEntry { Name = "", Path = target, RootPath = "" });
    }

    /// <summary>
    /// Creates the central task-store folder for a registry project. Runtime
    /// discovery is registry-backed, so this never edits WatchPaths settings.
    /// </summary>
    public WorkspaceManagementResult CreateProjectStorage(string displayName, string projectId)
    {
        var name = (displayName ?? "").Trim();
        var id = (projectId ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name)) return WorkspaceManagementResult.BadRequest("displayName is required.");
        if (string.IsNullOrWhiteSpace(id)) return WorkspaceManagementResult.BadRequest("projectId is required.");

        var taskRepository = _configuration["TaskRepository"];
        if (string.IsNullOrWhiteSpace(taskRepository))
        {
            return WorkspaceManagementResult.BadRequest(
                "TaskRepository is not configured; cannot create a new project.");
        }

        var resolvedPath = Path.GetFullPath(Path.Combine(taskRepository, "projects", id, "tasks"));
        if (_scanner.GetWatchPaths().Any(e =>
                string.Equals(NormalizePath(e.Path), NormalizePath(resolvedPath), StringComparison.OrdinalIgnoreCase)))
        {
            return WorkspaceManagementResult.Conflict(
                $"A project already maps to the folder '{resolvedPath}'.");
        }

        try
        {
            Directory.CreateDirectory(resolvedPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "project-storage-create-failed id={Id} path={Path}", id, resolvedPath);
            return WorkspaceManagementResult.BadRequest(
                $"Could not create project folder: {ex.Message}");
        }

        var entry = new WatchPathEntry
        {
            Name = name,
            Path = resolvedPath,
            RootPath = "",
            RepositoryPath = "",
        };

        _indexCache?.Invalidate(TaskIndexCache.InvalidationSource.Mutation, clientId: AgentStudio.Clients.AmbientClient.Id);
        _logger.LogInformation(
            "project-storage-created id={Id} displayName={DisplayName} path={Path}",
            id, name, resolvedPath);

        return WorkspaceManagementResult.Created(entry);
    }

    private static string NormalizePath(string? p) =>
        string.IsNullOrWhiteSpace(p) ? "" : p.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();

    /// <summary>
    /// Counts every job folder under each lane of the workspace path.
    /// Mirrors the lane walk in
    /// <see cref="TaskScannerService.ScanAllJobsRaw"/> so the answer
    /// matches what the user sees on the kanban: lane subdir present,
    /// folder name does not start with <c>_</c>, <c>task.json</c> exists.
    /// </summary>
    private static int CountJobs(WatchPathEntry entry)
    {
        if (!Directory.Exists(entry.Path)) return 0;
        var count = 0;
        foreach (var state in TaskStates.All)
        {
            var stateDir = Path.Combine(entry.Path, state);
            if (!Directory.Exists(stateDir)) continue;
            foreach (var jobDir in Directory.GetDirectories(stateDir))
            {
                var dirName = Path.GetFileName(jobDir);
                if (dirName.StartsWith('_')) continue;
                if (!File.Exists(Path.Combine(jobDir, "task.json"))) continue;
                count++;
            }
        }
        return count;
    }

    /// <summary>
    /// Converts a free-form workspace name into a stable folder slug:
    /// lowercase, letters/digits/hyphens only, collapsed dashes,
    /// trimmed of leading/trailing dashes. Two different names can
    /// collapse to the same slug (e.g. "My Workspace" and "my-workspace");
    /// the caller checks for that collision before claiming the folder.
    /// </summary>
    internal static string Slugify(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        var lastDash = true;
        foreach (var ch in name.ToLowerInvariant())
        {
            if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
            {
                sb.Append(ch);
                lastDash = false;
            }
            else if (!lastDash)
            {
                sb.Append('-');
                lastDash = true;
            }
        }
        var result = sb.ToString().Trim('-');
        return result;
    }

    private JsonObject ReadOrCreateRoot()
    {
        var path = OverrideFilePath;
        if (!File.Exists(path)) return new JsonObject();
        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
        return JsonNode.Parse(text) as JsonObject ?? new JsonObject();
    }

    private void WriteAtomic(JsonObject root)
    {
        var path = OverrideFilePath;
        var serialized = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var temp = path + ".tmp";
        File.WriteAllText(temp, serialized);
        try { File.Replace(temp, path, destinationBackupFileName: null); }
        catch (FileNotFoundException) { File.Move(temp, path); }
    }

    private void ReloadConfiguration()
    {
        if (_configuration is IConfigurationRoot rootConfig)
        {
            rootConfig.Reload();
        }
    }
}

public enum WorkspaceManagementOutcome
{
    Created,
    Ok,
    BadRequest,
    NotFound,
    Conflict,
}

public sealed record WorkspaceManagementResult(
    WorkspaceManagementOutcome Outcome,
    string? Error = null,
    WatchPathEntry? Entry = null,
    int? TaskCount = null)
{
    public static WorkspaceManagementResult Created(WatchPathEntry entry) =>
        new(WorkspaceManagementOutcome.Created, Entry: entry);
    public static WorkspaceManagementResult Ok(WatchPathEntry entry) =>
        new(WorkspaceManagementOutcome.Ok, Entry: entry);
    public static WorkspaceManagementResult BadRequest(string error) =>
        new(WorkspaceManagementOutcome.BadRequest, Error: error);
    public static WorkspaceManagementResult NotFound(string error) =>
        new(WorkspaceManagementOutcome.NotFound, Error: error);
    public static WorkspaceManagementResult Conflict(string error, int? jobCount = null) =>
        new(WorkspaceManagementOutcome.Conflict, Error: error, TaskCount: jobCount);
}
