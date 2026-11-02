using System.Text.Json;

namespace AgentStudio.Registry;

/// <summary>
/// F45a — in-memory cache plus persistence for the workspace catalog.
/// Workspaces are pure metadata (no folder per workspace); this service
/// reads and writes a single JSON file under
/// <c>&lt;TaskRepository&gt;/.metadata/workspaces.json</c>.
///
/// <para>F45a ships read access plus the boot-time "ensure default
/// workspace" path. Mutations (create / rename / delete / reorder) ship
/// in F45b alongside the corresponding CRUD endpoints.</para>
///
/// <para>This service is registered as a singleton. <see cref="EnsureLoaded"/>
/// is idempotent; the constructor is cheap and does not touch disk.</para>
/// </summary>
public sealed class WorkspaceRegistry
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly string? _taskRepositoryRoot;
    private readonly ILogger<WorkspaceRegistry> _logger;
    private readonly object _gate = new();
    private WorkspacesFile _state = new();
    private bool _loaded;
    private DateTime _lastWriteUtc;

    public WorkspaceRegistry(IConfiguration config, ILogger<WorkspaceRegistry> logger)
    {
        _logger = logger;
        var raw = config["TaskRepository"];
        _taskRepositoryRoot = string.IsNullOrWhiteSpace(raw) ? null : raw;
    }

    /// <summary>True when a backing TaskRepository is configured; false means in-memory-only operation.</summary>
    public bool IsPersistent => _taskRepositoryRoot != null;

    /// <summary>
    /// Loads the file on first call. Idempotent. If the file is missing
    /// or unreadable, leaves the registry empty so the caller can decide
    /// whether to seed defaults via <see cref="EnsureDefaultWorkspace"/>.
    /// </summary>
    public void EnsureLoaded()
    {
        lock (_gate)
        {
            if (_loaded) return;
            _loaded = true;
            LoadFromDiskLocked();
        }
    }

    /// <summary>
    /// Re-reads the backing file when another process has modified it
    /// since the last load or persist. Called from <see cref="List"/>
    /// and <see cref="Find"/> so reads always reflect the current disk
    /// state even when a sibling backend instance mutated the file.
    /// </summary>
    private void RefreshIfStaleLocked()
    {
        if (_taskRepositoryRoot == null) return;
        var path = RegistryPaths.WorkspacesFilePath(_taskRepositoryRoot);
        try
        {
            if (!File.Exists(path)) return;
            var diskMtime = File.GetLastWriteTimeUtc(path);
            if (diskMtime <= _lastWriteUtc) return;
            LoadFromDiskLocked();
            _logger.LogInformation(
                "workspace-registry-reloaded-stale path={Path} count={Count}",
                path, _state.Workspaces.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "workspace-registry-stale-check-failed path={Path} - serving cached state",
                path);
        }
    }

    private void LoadFromDiskLocked()
    {
        if (_taskRepositoryRoot == null) return;
        var path = RegistryPaths.WorkspacesFilePath(_taskRepositoryRoot);
        if (!File.Exists(path)) return;

        try
        {
            var json = File.ReadAllText(path);
            var parsed = JsonSerializer.Deserialize<WorkspacesFile>(json, JsonOpts);
            if (parsed != null)
            {
                _state = parsed;
                _lastWriteUtc = File.GetLastWriteTimeUtc(path);
                _logger.LogInformation(
                    "workspace-registry-loaded path={Path} count={Count}",
                    path, _state.Workspaces.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "workspace-registry-load-failed path={Path} - starting with empty list",
                path);
        }
    }

    /// <summary>
    /// Snapshot of the current workspaces, sorted by <see cref="WorkspaceRecord.SortOrder"/>
    /// then by display name. The returned list is a copy.
    /// </summary>
    public List<WorkspaceRecord> List()
    {
        EnsureLoaded();
        lock (_gate)
        {
            RefreshIfStaleLocked();
            return _state.Workspaces
                .OrderBy(w => w.SortOrder)
                .ThenBy(w => w.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>Returns the workspace with the given id, or null.</summary>
    public WorkspaceRecord? Find(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        EnsureLoaded();
        lock (_gate)
        {
            RefreshIfStaleLocked();
            return _state.Workspaces.FirstOrDefault(w =>
                string.Equals(w.Id, id, StringComparison.OrdinalIgnoreCase));
        }
    }

    public WorkspaceSystem UpsertSystem(string workspaceId, WorkspaceSystem system,
        IReadOnlyList<ProjectRecord> projects)
    {
        if (string.IsNullOrWhiteSpace(system.Id) || string.IsNullOrWhiteSpace(system.Name))
            throw new ArgumentException("System id and name are required.");
        if (system.Units.Count == 0 || system.Units.Any(unit =>
            string.IsNullOrWhiteSpace(unit.UnitId) ||
            !projects.Any(project => project.Id == unit.ProjectId && project.WorkspaceId == workspaceId)))
            throw new ArgumentException("Every system unit must name a unit in a project of this workspace.");
        EnsureLoaded();
        lock (_gate)
        {
            RefreshIfStaleLocked();
            var index = _state.Workspaces.FindIndex(workspace => workspace.Id == workspaceId);
            if (index < 0) throw new KeyNotFoundException($"Unknown workspaceId '{workspaceId}'");
            var workspace = _state.Workspaces[index];
            var normalized = system with { Id = system.Id.Trim(), Name = system.Name.Trim(),
                Units = system.Units.Distinct().ToArray() };
            var next = _state.Workspaces.ToList();
            next[index] = workspace with { Systems = workspace.Systems
                .Where(item => item.Id != normalized.Id).Append(normalized).OrderBy(item => item.Name).ToArray() };
            _state = _state with { Workspaces = next };
            PersistLocked();
            return normalized;
        }
    }

    /// <summary>
    /// Inserts <see cref="DefaultWorkspace"/> if no workspace exists yet
    /// and a TaskRepository is configured. Writes the file. Returns the
    /// default workspace record either way (so callers can rely on a
    /// non-null result post-boot).
    /// </summary>
    public WorkspaceRecord EnsureDefaultWorkspace(TimeProvider? clock = null)
    {
        clock ??= TimeProvider.System;
        EnsureLoaded();
        lock (_gate)
        {
            var existing = _state.Workspaces.FirstOrDefault(w =>
                string.Equals(w.Id, DefaultWorkspace.Id, StringComparison.OrdinalIgnoreCase));
            if (existing != null) return existing;

            var record = new WorkspaceRecord
            {
                Id = DefaultWorkspace.Id,
                DisplayName = DefaultWorkspace.DisplayName,
                SortOrder = 0,
                IsDefault = true,
                Color = DefaultWorkspace.Color,
                CreatedAt = clock.GetUtcNow().UtcDateTime,
            };
            _state = _state with { Workspaces = [.. _state.Workspaces, record] };
            PersistLocked();
            _logger.LogInformation(
                "workspace-registry-default-seeded id={Id} displayName={DisplayName}",
                record.Id, record.DisplayName);
            return record;
        }
    }

    /// <summary>
    /// Replaces the in-memory state and writes it to disk. Internal hook
    /// used by F45b mutation endpoints; F45a keeps it internal so reads
    /// stay the only public surface.
    /// </summary>
    internal void Replace(WorkspacesFile next)
    {
        ArgumentNullException.ThrowIfNull(next);
        EnsureLoaded();
        lock (_gate)
        {
            _state = next;
            PersistLocked();
        }
    }

    /// <summary>
    /// F45b — create a new workspace with the given display name and optional color.
    /// SortOrder is assigned to the end of the list. Id is derived from the display
    /// name with a slugify + uniqueness pass (e.g. <c>ws-frontend</c>,
    /// <c>ws-frontend-2</c> on collision). The default workspace is not affected.
    /// Returns the persisted record. Throws <see cref="ArgumentException"/> when
    /// the name is empty or contains only whitespace.
    /// </summary>
    public WorkspaceRecord Create(string displayName, string? color = null, TimeProvider? clock = null)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("displayName is required", nameof(displayName));
        clock ??= TimeProvider.System;
        EnsureLoaded();
        lock (_gate)
        {
            var id = AllocateWorkspaceIdLocked(displayName);
            var record = new WorkspaceRecord
            {
                Id = id,
                DisplayName = displayName.Trim(),
                SortOrder = _state.Workspaces.Count,
                IsDefault = false,
                Color = string.IsNullOrWhiteSpace(color) ? null : color,
                CreatedAt = clock.GetUtcNow().UtcDateTime,
            };
            _state = _state with { Workspaces = [.. _state.Workspaces, record] };
            PersistLocked();
            _logger.LogInformation(
                "workspace-registry-created id={Id} displayName={DisplayName}",
                record.Id, record.DisplayName);
            return record;
        }
    }

    /// <summary>F45b — rename a workspace. The id never changes.</summary>
    public WorkspaceRecord Rename(string id, string newDisplayName)
    {
        if (string.IsNullOrWhiteSpace(newDisplayName))
            throw new ArgumentException("newDisplayName is required", nameof(newDisplayName));
        return MutateLocked(id, w => w with { DisplayName = newDisplayName.Trim() }, "renamed");
    }

    /// <summary>F45b — set the accent color (CSS hex string). Pass null to clear.</summary>
    public WorkspaceRecord SetColor(string id, string? color)
        => MutateLocked(id, w => w with { Color = string.IsNullOrWhiteSpace(color) ? null : color }, "color-set");

    /// <summary>
    /// F45b — move a workspace one slot up or down in the sort order.
    /// <paramref name="direction"/> is -1 (up) or +1 (down). No-op if the
    /// workspace is already at the boundary.
    /// </summary>
    public List<WorkspaceRecord> Reorder(string id, int direction)
    {
        if (direction != -1 && direction != 1)
            throw new ArgumentException("direction must be -1 or +1", nameof(direction));
        EnsureLoaded();
        lock (_gate)
        {
            var sorted = _state.Workspaces
                .OrderBy(w => w.SortOrder)
                .ThenBy(w => w.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var idx = sorted.FindIndex(w => string.Equals(w.Id, id, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) throw new KeyNotFoundException($"Unknown workspaceId: {id}");
            var target = idx + direction;
            if (target < 0 || target >= sorted.Count) return sorted;
            (sorted[idx], sorted[target]) = (sorted[target], sorted[idx]);
            for (var i = 0; i < sorted.Count; i++) sorted[i] = sorted[i] with { SortOrder = i };
            _state = _state with { Workspaces = sorted };
            PersistLocked();
            _logger.LogInformation("workspace-registry-reordered id={Id} dir={Dir}", id, direction);
            return sorted;
        }
    }

    /// <summary>
    /// F66 — delete a workspace. Refuses to delete the default workspace, and
    /// refuses (throws <see cref="InvalidOperationException"/>) any workspace
    /// that still has projects assigned: the operator must move every project
    /// out first. There is no auto-rehome — silently relocating projects on a
    /// delete hides where work lives, so the delete blocks until the workspace
    /// is empty. Per ADR-0048 a workspace is a virtual grouping; deleting it
    /// never touches the filesystem and never affects project storage locations.
    /// </summary>
    public WorkspaceDeleteResult Delete(string id, ProjectRegistry projects)
    {
        EnsureLoaded();
        projects.EnsureLoaded();
        lock (_gate)
        {
            var existing = _state.Workspaces.FirstOrDefault(w =>
                string.Equals(w.Id, id, StringComparison.OrdinalIgnoreCase));
            if (existing == null) throw new KeyNotFoundException($"Unknown workspaceId: {id}");
            if (existing.IsDefault)
                throw new InvalidOperationException("Default workspace cannot be deleted");

            var assigned = projects.List()
                .Where(p => string.Equals(p.WorkspaceId, id, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Id)
                .ToList();
            if (assigned.Count > 0)
            {
                _logger.LogInformation(
                    "workspace-registry-delete-blocked id={Id} assignedProjects={Count} projectIds={ProjectIds}",
                    id, assigned.Count, string.Join(",", assigned));
                throw new InvalidOperationException(
                    $"Move all {assigned.Count} project{(assigned.Count == 1 ? "" : "s")} out of this workspace before deleting it.");
            }

            _state = _state with
            {
                Workspaces = [.. _state.Workspaces.Where(w =>
                    !string.Equals(w.Id, id, StringComparison.OrdinalIgnoreCase))],
            };
            PersistLocked();
            _logger.LogInformation("workspace-registry-deleted id={Id}", id);
            return new WorkspaceDeleteResult { DeletedId = id };
        }
    }

    private WorkspaceRecord MutateLocked(string id, Func<WorkspaceRecord, WorkspaceRecord> update, string op)
    {
        EnsureLoaded();
        lock (_gate)
        {
            var idx = _state.Workspaces.FindIndex(w =>
                string.Equals(w.Id, id, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) throw new KeyNotFoundException($"Unknown workspaceId: {id}");
            var updated = update(_state.Workspaces[idx]);
            var next = _state.Workspaces.ToList();
            next[idx] = updated;
            _state = _state with { Workspaces = next };
            PersistLocked();
            _logger.LogInformation("workspace-registry-{Op} id={Id}", op, id);
            return updated;
        }
    }

    private string AllocateWorkspaceIdLocked(string displayName)
    {
        var baseSlug = "ws-" + new string(displayName.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray())
            .Trim('-');
        if (baseSlug == "ws-") baseSlug = "ws-workspace";
        while (baseSlug.Contains("--")) baseSlug = baseSlug.Replace("--", "-");
        var candidate = baseSlug;
        var n = 2;
        while (_state.Workspaces.Any(w => string.Equals(w.Id, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{baseSlug}-{n++}";
        }
        return candidate;
    }

    private void PersistLocked()
    {
        if (_taskRepositoryRoot == null) return;
        try
        {
            Directory.CreateDirectory(RegistryPaths.MetadataDir(_taskRepositoryRoot));
            var path = RegistryPaths.WorkspacesFilePath(_taskRepositoryRoot);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_state, JsonOpts));
            File.Move(tmp, path, overwrite: true);
            _lastWriteUtc = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "workspace-registry-persist-failed root={Root}", _taskRepositoryRoot);
            throw;
        }
    }
}
