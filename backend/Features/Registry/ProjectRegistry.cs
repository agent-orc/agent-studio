using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.Security;
using AgentStudio.Tasks;

namespace AgentStudio.Registry;

/// <summary>
/// F45a — in-memory cache plus persistence for the project catalog under
/// <c>&lt;TaskRepository&gt;/.metadata/projects.json</c>. Owns:
///
/// <list type="bullet">
/// <item>Allocating new <c>PROJ-NNN</c> ids from the global
/// <c>nextProjectIdSeq</c> counter (monotonic, never re-used).</item>
/// <item>Resolving an existing project by id, by storage path, or by
/// (display-name or id) string - the lookup that backs the existing
/// <c>?project=...</c> query semantics.</item>
/// <item>Issuing the next per-project task key (the F33 counter, now
/// reparented onto the project record itself instead of a sidecar
/// <c>.task-counter.json</c> per watch-path folder).</item>
/// </list>
///
/// <para>F45a ships the read surface plus first-boot auto-discovery
/// (<see cref="EnsureProjectForStorage"/>) that populates a missing registry
/// from the configured <c>WatchPaths</c>. Project creation via REST, rename,
/// archive, and workspace reassignment land in F45b.</para>
/// </summary>
public sealed class ProjectRegistry
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        RespectNullableAnnotations = true,
        WriteIndented = true,
    };

    private static readonly Regex ProjIdFormat = new(@"^PROJ-\d{3,}$", RegexOptions.Compiled);

    private readonly string? _taskRepositoryRoot;
    private readonly ILogger<ProjectRegistry> _logger;
    private readonly IAtomicJsonFileWriter _fileWriter;
    private readonly object _gate = new();
    private readonly bool _networked;
    private readonly TimeProvider _clock;
    private ProjectsFile _state = new();
    private bool _loaded;
    private bool _registryFileWasMissingAtLoad;

    public ProjectRegistry(
        IConfiguration config,
        ILogger<ProjectRegistry> logger,
        IAtomicJsonFileWriter? fileWriter = null, TimeProvider? clock = null)
    {
        _logger = logger;
        _networked = SecurityProfiles.IsNetworked(config);
        _clock = clock ?? TimeProvider.System;
        _fileWriter = (fileWriter ?? new AtomicJsonFileWriter()).WithLastGoodBackup();
        var raw = config["TaskRepository"];
        _taskRepositoryRoot = string.IsNullOrWhiteSpace(raw) ? null : raw;
    }

    public bool IsPersistent => _taskRepositoryRoot != null;

    public void EnsureLoaded()
    {
        lock (_gate)
        {
            if (_loaded) return;
            if (_taskRepositoryRoot == null)
            {
                _loaded = true;
                return;
            }

            var path = RegistryPaths.ProjectsFilePath(_taskRepositoryRoot);
            if (!File.Exists(path) && !File.Exists(path + ".bak"))
            {
                _registryFileWasMissingAtLoad = true;
                _loaded = true;
                return;
            }

            try
            {
                var json = AtomicJsonFileWriter.ReadAllTextWithBackup(path,
                    source => _logger.LogWarning("project-registry-recovered source={Source}", source));
                var parsed = JsonSerializer.Deserialize<ProjectsFile>(json, JsonOpts)
                    ?? throw new JsonException("The project registry JSON root is null.");
                ValidateLoadedState(parsed);
                _state = parsed;
                _loaded = true;
                _logger.LogInformation(
                    "project-registry-loaded path={Path} count={Count} nextSeq={Next}",
                    path, _state.Projects.Count, _state.NextProjectIdSeq);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "project-registry-load-failed path={Path} action=fail-closed",
                    path);
                throw new ProjectRegistryLoadException(path, ex);
            }
        }
    }

    /// <summary>
    /// True only when this service first loaded while <c>projects.json</c> did
    /// not exist. Legacy WatchPaths discovery may seed records only in this
    /// state. An existing empty file is still an authoritative registry.
    /// </summary>
    public bool RegistryFileWasMissingAtLoad
    {
        get
        {
            EnsureLoaded();
            lock (_gate)
            {
                return _registryFileWasMissingAtLoad;
            }
        }
    }

    /// <summary>
    /// Snapshot of the current projects, sorted by sort-order then display
    /// name. Archived entries are included; callers filter as needed.
    /// </summary>
    public List<ProjectRecord> List()
    {
        EnsureLoaded();
        lock (_gate)
        {
            return _state.Projects
                .OrderBy(p => p.SortOrder)
                .ThenBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>Apply a reviewed path import in one registry write.</summary>
    public void ApplyPathImport(IReadOnlyList<ProjectPathImportRow> rows)
    {
        EnsureLoaded();
        lock (_gate)
        {
            var byId = rows.Where(row => row.Status == "ready")
                .ToDictionary(row => row.ProjectId, StringComparer.OrdinalIgnoreCase);
            var next = _state.Projects.Select(project =>
            {
                if (!byId.TryGetValue(project.Id, out var row)) return project;
                return project with
                {
                    RepositoryPath = row.RepositoryPath is null ? project.RepositoryPath : ValidateRepositoryPath(row.RepositoryPath),
                    RootPath = row.RootPath is null ? project.RootPath : ValidateRootPath(row.RootPath),
                    StorageLocation = row.StorageLocation ?? project.StorageLocation,
                };
            }).ToList();
            ReplaceStateAndPersistLocked(_state with { Projects = next });
        }
    }

    public ProjectRecord? FindById(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        EnsureLoaded();
        lock (_gate)
        {
            return _state.Projects.FirstOrDefault(p =>
                string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        }
    }

    public IReadOnlyList<ProjectRepositoryAuditEvent> RepositoryAudit()
    {
        EnsureLoaded();
        lock (_gate) return _state.RepositoryAudit.ToArray();
    }

    /// <summary>
    /// Returns the project whose <see cref="ProjectRecord.StorageLocation"/>
    /// matches the supplied absolute path (case-insensitive, slash-normalised).
    /// Backs the F45 jobKey shim that translates legacy
    /// <c>&lt;path&gt;::&lt;slug&gt;</c> keys into <c>PROJ-NNN::&lt;slug&gt;</c>.
    /// </summary>
    public ProjectRecord? FindByStorageLocation(string? storageLocation)
    {
        if (string.IsNullOrWhiteSpace(storageLocation)) return null;
        var normalised = NormalisePath(storageLocation);
        EnsureLoaded();
        lock (_gate)
        {
            return _state.Projects.FirstOrDefault(p =>
                string.Equals(NormalisePath(p.StorageLocation), normalised, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Returns the project whose <see cref="ProjectRecord.ShortCode"/> equals
    /// the supplied Kürzel (e.g. <c>ASS</c>), matched case-insensitively. Short
    /// codes are the stable, filesystem-agnostic external handle for a project
    /// (2–6 chars of A–Z/0–9), so this backs the API-contract goal of
    /// addressing projects by Kürzel instead of a leaked absolute
    /// <c>watchPath</c>. Returns null when nothing matches.
    /// </summary>
    public ProjectRecord? FindByShortCode(string? shortCode)
    {
        if (string.IsNullOrWhiteSpace(shortCode)) return null;
        var normalised = shortCode.Trim();
        EnsureLoaded();
        lock (_gate)
        {
            return _state.Projects.FirstOrDefault(p =>
                !string.IsNullOrWhiteSpace(p.ShortCode) &&
                string.Equals(p.ShortCode, normalised, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Convenience lookup that accepts either a project id (<c>PROJ-001</c>)
    /// or a display name. Display-name match is case-insensitive. Returns
    /// null when nothing matches.
    /// </summary>
    public ProjectRecord? FindByIdOrDisplayName(string? idOrDisplayName)
    {
        if (string.IsNullOrWhiteSpace(idOrDisplayName)) return null;
        EnsureLoaded();
        lock (_gate)
        {
            var byId = _state.Projects.FirstOrDefault(p =>
                string.Equals(p.Id, idOrDisplayName, StringComparison.OrdinalIgnoreCase));
            if (byId != null) return byId;

            return _state.Projects.FirstOrDefault(p =>
                string.Equals(p.DisplayName, idOrDisplayName, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Boot-time hook used by the discovery pass. If a project with the
    /// given <paramref name="storageLocation"/> already exists, returns it.
    /// Otherwise allocates the next PROJ id, derives a short code from
    /// <paramref name="initialDisplayName"/>, appends a new record, and
    /// persists.
    /// </summary>
    public ProjectRecord EnsureProjectForStorage(
        string storageLocation,
        string initialDisplayName,
        string workspaceId,
        TimeProvider? clock = null)
    {
        if (string.IsNullOrWhiteSpace(storageLocation))
            throw new ArgumentException("storageLocation is required", nameof(storageLocation));
        if (string.IsNullOrWhiteSpace(workspaceId))
            throw new ArgumentException("workspaceId is required", nameof(workspaceId));

        clock ??= TimeProvider.System;
        EnsureLoaded();
        lock (_gate)
        {
            var normalised = NormalisePath(storageLocation);
            var existing = _state.Projects.FirstOrDefault(p =>
                string.Equals(NormalisePath(p.StorageLocation), normalised, StringComparison.OrdinalIgnoreCase));
            if (existing != null) return existing;

            var (newId, allocatedState) = PlanNextIdLocked();
            var existingCodes = _state.Projects.Select(p => p.ShortCode);
            var shortCode = ShortCodeGenerator.Derive(initialDisplayName, existingCodes);
            var record = new ProjectRecord
            {
                Id = newId,
                DisplayName = string.IsNullOrWhiteSpace(initialDisplayName) ? newId : initialDisplayName,
                ShortCode = shortCode,
                WorkspaceId = workspaceId,
                SortOrder = _state.Projects.Count,
                NextTaskKeySeq = 1,
                NextWorkbenchKeySeq = 1,
                StorageLocation = storageLocation,
                CreatedAt = clock.GetUtcNow().UtcDateTime,
            };
            ReplaceStateAndPersistLocked(allocatedState with
            {
                Projects = [.. allocatedState.Projects, record],
            });
            _logger.LogInformation(
                "project-registry-auto-discovered id={Id} displayName={DisplayName} shortCode={ShortCode} storage={Storage}",
                record.Id, record.DisplayName, record.ShortCode, record.StorageLocation);
            return record;
        }
    }

    /// <summary>
    /// Atomically reserves the next task key for <paramref name="projectId"/>
    /// and persists. The counter is monotonic and per-project; nothing
    /// rolls it back. Throws <see cref="KeyNotFoundException"/> when the
    /// project id is unknown.
    /// </summary>
    public int IssueNextTaskKey(string projectId)
    {
        EnsureLoaded();
        lock (_gate)
        {
            var idx = _state.Projects.FindIndex(p =>
                string.Equals(p.Id, projectId, StringComparison.OrdinalIgnoreCase));
            if (idx < 0)
                throw new KeyNotFoundException($"Unknown projectId: {projectId}");

            var current = _state.Projects[idx];
            var issued = current.NextTaskKeySeq;
            var updated = current with { NextTaskKeySeq = issued + 1 };
            var next = _state.Projects.ToList();
            next[idx] = updated;
            ReplaceStateAndPersistLocked(_state with { Projects = next });
            return issued;
        }
    }

    /// <summary>
    /// Atomically reserves and persists the next document reference sequence
    /// for <paramref name="projectId"/>. The counter is independent from task
    /// keys so both namespaces remain monotonic without creating gaps in one
    /// another.
    /// </summary>
    public int IssueNextWorkbenchKey(string projectId)
    {
        EnsureLoaded();
        lock (_gate)
        {
            var idx = _state.Projects.FindIndex(p =>
                string.Equals(p.Id, projectId, StringComparison.OrdinalIgnoreCase));
            if (idx < 0)
                throw new KeyNotFoundException($"Unknown projectId: {projectId}");

            var current = _state.Projects[idx];
            var issued = current.NextWorkbenchKeySeq;
            var updated = current with { NextWorkbenchKeySeq = issued + 1 };
            var next = _state.Projects.ToList();
            next[idx] = updated;
            ReplaceStateAndPersistLocked(_state with { Projects = next });
            return issued;
        }
    }

    /// <summary>
    /// Raises <see cref="ProjectRecord.NextWorkbenchKeySeq"/> to at least the
    /// supplied floor. Discovery derives this floor from descriptors before it
    /// mints, preventing a rewound registry snapshot from reusing a live key.
    /// </summary>
    public void EnsureWorkbenchKeyFloor(string projectId, int floor)
    {
        if (floor < 1) return;
        EnsureLoaded();
        lock (_gate)
        {
            var idx = _state.Projects.FindIndex(p =>
                string.Equals(p.Id, projectId, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) throw new KeyNotFoundException($"Unknown projectId: {projectId}");
            var current = _state.Projects[idx];
            if (current.NextWorkbenchKeySeq >= floor) return;
            var next = _state.Projects.ToList();
            next[idx] = current with { NextWorkbenchKeySeq = floor };
            ReplaceStateAndPersistLocked(_state with { Projects = next });
        }
    }

    /// <summary>
    /// Raises <see cref="ProjectRecord.NextTaskKeySeq"/> for the project
    /// to at least <paramref name="floor"/>. Used by the F45c migration
    /// after stamping existing tasks. No-op when already at or above.
    /// </summary>
    public void EnsureTaskKeyFloor(string projectId, int floor)
    {
        if (floor < 1) return;
        EnsureLoaded();
        lock (_gate)
        {
            var idx = _state.Projects.FindIndex(p =>
                string.Equals(p.Id, projectId, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) throw new KeyNotFoundException($"Unknown projectId: {projectId}");
            var current = _state.Projects[idx];
            if (current.NextTaskKeySeq >= floor) return;
            var next = _state.Projects.ToList();
            next[idx] = current with { NextTaskKeySeq = floor };
            ReplaceStateAndPersistLocked(_state with { Projects = next });
        }
    }

    /// <summary>F45b — rename a project's display name. Id is immutable.</summary>
    public ProjectRecord Rename(string id, string newDisplayName)
    {
        var normalized = ValidateDisplayName(newDisplayName);
        EnsureLoaded();
        lock (_gate)
        {
            ThrowIfDisplayNameCollisionLocked(id, normalized);
            return MutateLocked(id, p => p with { DisplayName = normalized }, "renamed");
        }
    }

    /// <summary>
    /// F45b — change the short code (2-6 chars, A-Z and 0-9). Validates and
    /// rejects duplicates against other active projects.
    /// </summary>
    public ProjectRecord SetShortCode(string id, string newShortCode)
    {
        if (string.IsNullOrWhiteSpace(newShortCode))
            throw new ArgumentException("newShortCode is required", nameof(newShortCode));
        var normalized = newShortCode.Trim().ToUpperInvariant();
        if (!ShortCodeGenerator.ValidateFormat(normalized))
            throw new ArgumentException(
                "shortCode must be 2-6 chars, start with A-Z, and use A-Z or 0-9", nameof(newShortCode));
        EnsureLoaded();
        lock (_gate)
        {
            ThrowIfShortCodeCollisionLocked(id, normalized);
            return MutateLocked(id, p => p with { ShortCode = normalized }, "short-code-set");
        }
    }

    /// <summary>
    /// Applies all editable project basics as one registry mutation. Every
    /// supplied value is normalized and validated before the record is
    /// replaced and <c>projects.json</c> is written once. Stable identity and
    /// storage fields are deliberately absent from the request contract.
    /// </summary>
    public ProjectRecord Update(string id, UpdateProjectRequest update, WorkspaceRegistry workspaces,
        StudioUser? actor = null)
        => Update(id, update, workspaces, out _, actor);

    public ProjectRecord Update(string id, UpdateProjectRequest update, WorkspaceRegistry workspaces,
        out ProjectRecord previous, StudioUser? actor = null)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(workspaces);

        // Keep the not-found response deterministic even when the submitted
        // patch also contains an invalid field.
        if (FindById(id) == null)
            throw new KeyNotFoundException($"Unknown projectId: {id}");

        var updateDisplayName = update.DisplayName != null;
        var displayName = updateDisplayName ? ValidateDisplayName(update.DisplayName) : null;

        var updateShortCode = update.ShortCode != null;
        var shortCode = updateShortCode ? update.ShortCode!.Trim().ToUpperInvariant() : null;
        if (updateShortCode && !ShortCodeGenerator.ValidateFormat(shortCode))
            throw new ArgumentException(
                "shortCode must be 2-6 chars, start with A-Z, and use A-Z or 0-9",
                nameof(update.ShortCode));

        var updateColor = update.Color != null || update.ClearColor == true;
        var color = update.ClearColor == true || string.IsNullOrWhiteSpace(update.Color)
            ? null
            : update.Color.Trim();

        var updateWorkspace = update.WorkspaceId != null;
        var workspaceId = updateWorkspace ? update.WorkspaceId!.Trim() : null;
        if (updateWorkspace)
        {
            if (string.IsNullOrWhiteSpace(workspaceId))
                throw new ArgumentException("workspaceId is required", nameof(update.WorkspaceId));
            if (workspaces.Find(workspaceId) == null)
                throw new KeyNotFoundException($"Unknown workspaceId: {workspaceId}");
        }

        var updateRepositoryPath = update.RepositoryPath != null || update.ClearRepositoryPath == true;
        var repositoryPath = updateRepositoryPath
            ? ValidateRepositoryPath(update.ClearRepositoryPath == true ? null : update.RepositoryPath)
            : null;

        var updateRootPath = update.RootPath != null || update.ClearRootPath == true;
        var rootPath = updateRootPath
            ? ValidateRootPath(update.ClearRootPath == true ? null : update.RootPath)
            : null;

        var updateRepositoryUrl = update.RepositoryUrl != null || update.ClearRepositoryUrl == true;
        var repositoryUrl = updateRepositoryUrl
            ? ValidateRepositoryUrl(update.ClearRepositoryUrl == true ? null : update.RepositoryUrl)
            : null;

        var updateWikiSourceBranch = update.WikiSourceBranch != null || update.ClearWikiSourceBranch == true;
        var wikiSourceBranch = updateWikiSourceBranch
            ? ValidateWikiSourceBranch(update.ClearWikiSourceBranch == true ? null : update.WikiSourceBranch)
            : null;

        var updateCliDefault = update.CliDefault != null || update.ClearCliDefault == true;
        var cliDefault = update.ClearCliDefault == true || string.IsNullOrWhiteSpace(update.CliDefault)
            ? null
            : update.CliDefault.Trim();

        var updateModelDefault = update.ModelDefault != null || update.ClearModelDefault == true;
        var modelDefault = update.ClearModelDefault == true || string.IsNullOrWhiteSpace(update.ModelDefault)
            ? null
            : update.ModelDefault.Trim();

        EnsureLoaded();
        lock (_gate)
        {
            var idx = _state.Projects.FindIndex(p =>
                string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) throw new KeyNotFoundException($"Unknown projectId: {id}");

            if (updateDisplayName) ThrowIfDisplayNameCollisionLocked(id, displayName!);
            if (updateShortCode) ThrowIfShortCodeCollisionLocked(id, shortCode!);

            var current = _state.Projects[idx];
            previous = current;
            var updated = current with
            {
                DisplayName = updateDisplayName ? displayName! : current.DisplayName,
                ShortCode = updateShortCode ? shortCode! : current.ShortCode,
                Color = updateColor ? color : current.Color,
                WorkspaceId = updateWorkspace ? workspaceId! : current.WorkspaceId,
                RepositoryPath = updateRepositoryPath ? repositoryPath : current.RepositoryPath,
                RootPath = updateRootPath ? rootPath : current.RootPath,
                WikiSourceBranch = updateWikiSourceBranch ? wikiSourceBranch : current.WikiSourceBranch,
                Urls = updateRepositoryUrl
                    ? ApplyRepositoryUrl(current.Urls, repositoryUrl)
                    : current.Urls,
                CliDefault = updateCliDefault ? cliDefault : current.CliDefault,
                ModelDefault = updateModelDefault ? modelDefault : current.ModelDefault,
                Archived = update.Archived ?? current.Archived,
            };

            var next = _state.Projects.ToList();
            next[idx] = updated;
            ReplaceProjectAndAuditLocked(current, updated, next, actor);
            _logger.LogInformation("project-registry-basics-updated id={Id}", id);
            return updated;
        }
    }

    /// <summary>
    /// Best-effort compensation used when the separate project-settings write
    /// fails after a registry update committed. The expected record reference
    /// prevents this request from overwriting a concurrent later mutation.
    /// </summary>
    internal void RollbackUpdate(ProjectRecord expected, ProjectRecord previous)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(previous);
        EnsureLoaded();
        lock (_gate)
        {
            var idx = _state.Projects.FindIndex(project =>
                string.Equals(project.Id, expected.Id, StringComparison.OrdinalIgnoreCase));
            if (idx < 0 || !ReferenceEquals(_state.Projects[idx], expected))
                throw new InvalidOperationException(
                    $"Project '{expected.Id}' changed concurrently; rollback was refused.");

            var next = _state.Projects.ToList();
            next[idx] = previous;
            var audit = _state.RepositoryAudit.ToList();
            var auditIndex = audit.FindLastIndex(row => row.ProjectId == expected.Id);
            var beforeUrl = RemoteProjectRepositoryResolver.ResolveRepositoryUrl(previous);
            var afterUrl = RemoteProjectRepositoryResolver.ResolveRepositoryUrl(expected);
            if (auditIndex >= 0 && !string.Equals(beforeUrl, afterUrl, StringComparison.Ordinal)
                && audit[auditIndex].RepositoryUrl == afterUrl
                && audit[auditIndex].PreviousRepositoryUrl == beforeUrl)
                audit.RemoveAt(auditIndex);
            ReplaceStateAndPersistLocked(_state with { Projects = next, RepositoryAudit = audit });
            _logger.LogWarning("project-registry-basics-rolled-back id={Id}", expected.Id);
        }
    }

    /// <summary>Compensates a just-appended onboarding record.</summary>
    internal void RollbackAppend(ProjectRecord expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        EnsureLoaded();
        lock (_gate)
        {
            var idx = _state.Projects.FindIndex(project =>
                string.Equals(project.Id, expected.Id, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) return;
            if (!ReferenceEquals(_state.Projects[idx], expected))
                throw new InvalidOperationException(
                    $"Project '{expected.Id}' changed concurrently; create rollback was refused.");

            ReplaceStateAndPersistLocked(_state with
            {
                Projects = [.. _state.Projects.Where((_, index) => index != idx)],
            });
            _logger.LogWarning("project-registry-create-rolled-back id={Id}", expected.Id);
        }
    }

    /// <summary>F45b — set the accent color (CSS hex). Pass null to clear.</summary>
    public ProjectRecord SetColor(string id, string? color)
        => MutateLocked(id, p => p with { Color = string.IsNullOrWhiteSpace(color) ? null : color }, "color-set");

    /// <summary>F45b — reassign a project to a different workspace.</summary>
    public ProjectRecord SetWorkspace(string id, string newWorkspaceId, WorkspaceRegistry workspaces)
    {
        if (string.IsNullOrWhiteSpace(newWorkspaceId))
            throw new ArgumentException("newWorkspaceId is required", nameof(newWorkspaceId));
        if (workspaces.Find(newWorkspaceId) == null)
            throw new KeyNotFoundException($"Unknown workspaceId: {newWorkspaceId}");
        return MutateLocked(id, p => p with { WorkspaceId = newWorkspaceId }, "workspace-set");
    }

    /// <summary>F45b — archive or un-archive a project.</summary>
    public ProjectRecord SetArchived(string id, bool archived)
        => MutateLocked(id, p => p with { Archived = archived }, archived ? "archived" : "unarchived");

    /// <summary>
    /// Set (or clear, with null) the project's repository checkout path.
    /// Lives on the registry record so the project↔repo association survives
    /// configuration loss; the wiki root is always
    /// <c>&lt;RepositoryPath&gt;/docs</c> by convention, never configured
    /// separately.
    /// </summary>
    public ProjectRecord SetRepositoryPath(string id, string? repositoryPath)
    {
        var trimmed = ValidateRepositoryPath(repositoryPath);
        return MutateLocked(id, p => p with { RepositoryPath = trimmed },
            trimmed == null ? "repository-path-cleared" : "repository-path-set");
    }

    /// <summary>
    /// Create or replace one component ownership declaration. The declaration
    /// must live on its primary project, carries that project's ticket prefix,
    /// and advances an append-only audit version on every edit.
    /// </summary>
    public ProjectRecord UpsertOwnershipMapping(string id, ComponentOwnershipMapping mapping, string? actor = null)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        if (string.IsNullOrWhiteSpace(mapping.Id))
            throw new ArgumentException("mapping id is required", nameof(mapping));
        if (string.IsNullOrWhiteSpace(mapping.Component))
            throw new ArgumentException("component is required", nameof(mapping));

        EnsureLoaded();
        lock (_gate)
        {
            var idx = _state.Projects.FindIndex(p =>
                string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) throw new KeyNotFoundException($"Unknown projectId: {id}");
            var owner = _state.Projects[idx];
            if (!string.IsNullOrWhiteSpace(mapping.PrimaryProjectId)
                && !string.Equals(mapping.PrimaryProjectId, owner.Id, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("primaryProjectId must match the storage project", nameof(mapping));
            if (!string.IsNullOrWhiteSpace(mapping.AllowedTicketPrefix)
                && !string.Equals(mapping.AllowedTicketPrefix, owner.ShortCode, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"allowedTicketPrefix must be '{owner.ShortCode}'", nameof(mapping));
            foreach (var consumerId in mapping.ConsumerProjectIds)
            {
                if (!_state.Projects.Any(p => string.Equals(p.Id, consumerId, StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException($"Unknown consumer projectId: {consumerId}", nameof(mapping));
            }

            var previous = owner.OwnershipMappings.FirstOrDefault(row =>
                string.Equals(row.Id, mapping.Id, StringComparison.OrdinalIgnoreCase));
            var now = DateTime.UtcNow;
            var changedBy = string.IsNullOrWhiteSpace(actor) ? "local-default" : actor.Trim();
            var normalized = mapping with
            {
                Id = mapping.Id.Trim(),
                Component = mapping.Component.Trim(),
                PrimaryProjectId = owner.Id,
                AllowedTicketPrefix = owner.ShortCode,
                Confidence = Math.Clamp(mapping.Confidence, 0, 1),
                Version = (previous?.Version ?? 0) + 1,
                UpdatedAt = now,
                UpdatedBy = changedBy,
            };
            var mappings = owner.OwnershipMappings
                .Where(row => !string.Equals(row.Id, normalized.Id, StringComparison.OrdinalIgnoreCase))
                .Append(normalized)
                .OrderBy(row => row.Component, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var audit = owner.OwnershipMappingAudit.Append(new ComponentOwnershipMappingAudit
            {
                MappingId = normalized.Id,
                Version = normalized.Version,
                ChangedAt = now,
                ChangedBy = changedBy,
                Action = previous == null ? "created" : "updated",
                Snapshot = normalized,
            }).ToList();
            var updated = owner with { OwnershipMappings = mappings, OwnershipMappingAudit = audit };
            var next = _state.Projects.ToList();
            next[idx] = updated;
            ReplaceStateAndPersistLocked(_state with { Projects = next });
            _logger.LogInformation(
                "project-ownership-mapping-upserted id={Id} mappingId={MappingId} version={Version} actor={Actor}",
                id, normalized.Id, normalized.Version, changedBy);
            return updated;
        }
    }

    internal static string? ValidateRepositoryPath(string? repositoryPath)
    {
        var trimmed = string.IsNullOrWhiteSpace(repositoryPath) ? null : repositoryPath.Trim();
        if (trimmed != null)
        {
            // Containment: the value becomes the authoritative base dir for
            // wiki (docs/) reads AND writes, so reject anything that is not a
            // local, existing git checkout. The UNC check runs before any
            // filesystem probe so validation itself cannot be used to drive
            // SMB connections to attacker-chosen hosts.
            if (trimmed.StartsWith(@"\\", StringComparison.Ordinal) || trimmed.StartsWith("//", StringComparison.Ordinal))
                throw new ArgumentException(
                    "repositoryPath must be a local path, not a UNC share", nameof(repositoryPath));
            if (!Path.IsPathRooted(trimmed))
                throw new ArgumentException(
                    "repositoryPath must be an absolute path", nameof(repositoryPath));
            trimmed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
            if (string.Equals(trimmed, Path.GetPathRoot(trimmed), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(
                    "repositoryPath must not be a filesystem root", nameof(repositoryPath));
            if (!Directory.Exists(trimmed))
                throw new ArgumentException(
                    $"repositoryPath does not exist: {trimmed}", nameof(repositoryPath));
            if (!Directory.Exists(Path.Combine(trimmed, ".git")) && !File.Exists(Path.Combine(trimmed, ".git")))
                throw new ArgumentException(
                    "repositoryPath must point at a git checkout (no .git found)", nameof(repositoryPath));
        }
        return trimmed;
    }

    /// <summary>Sets the optional read-only git ref used by the whole wiki.</summary>
    public ProjectRecord SetWikiSourceBranch(string id, string? branch)
    {
        var trimmed = ValidateWikiSourceBranch(branch);
        return MutateLocked(id, p => p with { WikiSourceBranch = trimmed },
            trimmed == null ? "wiki-source-checkout" : "wiki-source-branch-set");
    }

    internal static string? ValidateWikiSourceBranch(string? branch)
    {
        var trimmed = string.IsNullOrWhiteSpace(branch) ? null : branch.Trim();
        if (trimmed != null)
        {
            if (trimmed.Length > 200
                || trimmed.StartsWith("-", StringComparison.Ordinal)
                || trimmed.Contains("..", StringComparison.Ordinal)
                || trimmed.Any(char.IsWhiteSpace)
                || trimmed.IndexOfAny(['~', '^', ':', '?', '*', '[', '\\']) >= 0)
                throw new ArgumentException("wikiSourceBranch must be a plain git branch or remote-tracking ref", nameof(branch));
        }
        return trimmed;
    }

    /// <summary>
    /// Set (or clear, with null) the project's CLI working directory. Same
    /// validation as <see cref="SetRepositoryPath"/> minus the git-checkout
    /// requirement: unlike a repository path, a working directory can
    /// legitimately be a subfolder of the checkout (e.g. Runbook's
    /// <c>&lt;repo&gt;/App</c>) rather than the checkout root itself.
    /// </summary>
    public ProjectRecord SetRootPath(string id, string? rootPath)
    {
        var trimmed = ValidateRootPath(rootPath);
        return MutateLocked(id, p => p with { RootPath = trimmed },
            trimmed == null ? "root-path-cleared" : "root-path-set");
    }

    internal static string? ValidateRootPath(string? rootPath)
    {
        var trimmed = string.IsNullOrWhiteSpace(rootPath) ? null : rootPath.Trim();
        if (trimmed != null)
        {
            if (trimmed.StartsWith(@"\\", StringComparison.Ordinal) || trimmed.StartsWith("//", StringComparison.Ordinal))
                throw new ArgumentException(
                    "rootPath must be a local path, not a UNC share", nameof(rootPath));
            if (!Path.IsPathRooted(trimmed))
                throw new ArgumentException(
                    "rootPath must be an absolute path", nameof(rootPath));
            trimmed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
            if (string.Equals(trimmed, Path.GetPathRoot(trimmed), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(
                    "rootPath must not be a filesystem root", nameof(rootPath));
            if (!Directory.Exists(trimmed))
                throw new ArgumentException(
                    $"rootPath does not exist: {trimmed}", nameof(rootPath));
        }
        return trimmed;
    }

    internal static string ValidateDisplayName(string? displayName)
    {
        var trimmed = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
        if (trimmed == null)
            throw new ArgumentException("displayName is required", nameof(displayName));
        if (trimmed.Length > WorkspaceManagementService.MaxNameLength)
            throw new ArgumentException(
                $"displayName must be {WorkspaceManagementService.MaxNameLength} characters or fewer",
                nameof(displayName));
        return trimmed;
    }

    internal static string? ValidateRepositoryUrl(string? repositoryUrl)
    {
        var trimmed = string.IsNullOrWhiteSpace(repositoryUrl) ? null : repositoryUrl.Trim();
        if (trimmed == null) return null;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(parsed.UserInfo)
            || !string.IsNullOrEmpty(parsed.Query)
            || !string.IsNullOrEmpty(parsed.Fragment))
            throw new ArgumentException(
                "repositoryUrl must be an absolute http or https URL without credentials, query, or fragment", nameof(repositoryUrl));
        return trimmed;
    }

    private static IReadOnlyList<ProjectUrlRecord> ApplyRepositoryUrl(
        IReadOnlyList<ProjectUrlRecord> urls,
        string? repositoryUrl)
    {
        var next = urls
            .Where(url => !string.Equals(url.Id, "repo", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (repositoryUrl == null) return next;

        var existing = urls.FirstOrDefault(url =>
            string.Equals(url.Id, "repo", StringComparison.OrdinalIgnoreCase));
        next.Add(new ProjectUrlRecord
        {
            Id = "repo",
            Label = "Repository",
            Url = repositoryUrl,
            SortOrder = existing?.SortOrder ?? (next.Count == 0 ? 0 : next.Max(url => url.SortOrder) + 1),
        });
        return next.OrderBy(url => url.SortOrder).ToList();
    }

    // ------------------------------------------------------------------
    // Project URLs — ordered list of watchable dev-server / preview URLs.
    // Mirrors the SetRepositoryPath pattern: validate, MutateLocked, persist.
    // ------------------------------------------------------------------

    /// <summary>
    /// Append a URL to the project. <paramref name="label"/> and
    /// <paramref name="url"/> are required; the URL must be an absolute
    /// http/https address. A stable per-project id (<c>url-N</c>) is allocated
    /// and the new row is placed last. Returns the updated project record.
    /// </summary>
    public ProjectRecord AddUrl(string id, string label, string url, ProjectUrlStartRule? startRule = null,
        StudioUser? actor = null)
    {
        var cleanLabel = (label ?? "").Trim();
        if (cleanLabel.Length == 0)
            throw new ArgumentException("label is required", nameof(label));
        var cleanUrl = ValidateUrl(url);
        var rule = NormaliseStartRule(startRule);
        return MutateLocked(id, p =>
        {
            var nextSort = p.Urls.Count == 0 ? 0 : p.Urls.Max(u => u.SortOrder) + 1;
            var record = new ProjectUrlRecord
            {
                Id = AllocateUrlId(p.Urls),
                Label = cleanLabel,
                Url = cleanUrl,
                SortOrder = nextSort,
                StartRule = rule,
            };
            return p with { Urls = [.. p.Urls, record] };
        }, "url-added", actor);
    }

    /// <summary>
    /// Update an existing URL's label, address, and start rule. The id and
    /// sort order are preserved. Throws <see cref="KeyNotFoundException"/>
    /// when the project or the url id is unknown.
    /// </summary>
    public ProjectRecord UpdateUrl(string id, string urlId, string label, string url, ProjectUrlStartRule? startRule,
        StudioUser? actor = null)
    {
        var cleanLabel = (label ?? "").Trim();
        if (cleanLabel.Length == 0)
            throw new ArgumentException("label is required", nameof(label));
        var cleanUrl = ValidateUrl(url);
        var rule = NormaliseStartRule(startRule);
        return MutateLocked(id, p =>
        {
            var idx = IndexOfUrl(p.Urls, urlId);
            var next = p.Urls.ToList();
            next[idx] = next[idx] with { Label = cleanLabel, Url = cleanUrl, StartRule = rule };
            return p with { Urls = next };
        }, "url-updated", actor);
    }

    /// <summary>Remove a URL by id. Throws when the project or url id is unknown.</summary>
    public ProjectRecord RemoveUrl(string id, string urlId, StudioUser? actor = null,
        Action? beforePersist = null)
    {
        return MutateLocked(id, p =>
        {
            var idx = IndexOfUrl(p.Urls, urlId);
            return p with { Urls = [.. p.Urls.Where((_, i) => i != idx)] };
        }, "url-removed", actor, beforePersist);
    }

    /// <summary>
    /// Reassign sort order from an explicit id sequence. Every existing url id
    /// must appear exactly once in <paramref name="orderedUrlIds"/>; the new
    /// <see cref="ProjectUrlRecord.SortOrder"/> is the index in that sequence.
    /// </summary>
    public ProjectRecord ReorderUrls(string id, IReadOnlyList<string> orderedUrlIds, StudioUser? actor = null)
    {
        ArgumentNullException.ThrowIfNull(orderedUrlIds);
        return MutateLocked(id, p =>
        {
            var existing = p.Urls.Select(u => u.Id).ToHashSet(StringComparer.Ordinal);
            if (orderedUrlIds.Count != existing.Count || !orderedUrlIds.All(existing.Contains) ||
                orderedUrlIds.Distinct(StringComparer.Ordinal).Count() != orderedUrlIds.Count)
                throw new ArgumentException(
                    "orderedUrlIds must be a permutation of the project's url ids", nameof(orderedUrlIds));
            var byId = p.Urls.ToDictionary(u => u.Id, StringComparer.Ordinal);
            var reordered = orderedUrlIds
                .Select((uid, i) => byId[uid] with { SortOrder = i })
                .ToList();
            return p with { Urls = reordered };
        }, "urls-reordered", actor);
    }

    private static string ValidateUrl(string? url)
    {
        var trimmed = (url ?? "").Trim();
        if (trimmed.Length == 0)
            throw new ArgumentException("url is required", nameof(url));
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException(
                "url must be an absolute http or https address", nameof(url));
        return trimmed;
    }

    private static ProjectUrlStartRule? NormaliseStartRule(ProjectUrlStartRule? rule)
    {
        if (rule == null) return null;
        var command = (rule.Command ?? "").Trim();
        if (command.Length == 0) return null; // a rule with no command is no rule
        var source = string.IsNullOrWhiteSpace(rule.Source) ? "manual" : rule.Source.Trim();
        var cwd = string.IsNullOrWhiteSpace(rule.Cwd) ? null : rule.Cwd.Trim();
        if (cwd != null)
        {
            if (!Path.IsPathRooted(cwd))
                throw new ArgumentException("startRule.cwd must be an absolute path", nameof(rule));
            cwd = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd));
            if (!Directory.Exists(cwd))
                throw new ArgumentException($"startRule.cwd does not exist: {cwd}", nameof(rule));
        }
        return new ProjectUrlStartRule
        {
            Command = command,
            Cwd = cwd,
            Port = rule.Port,
            HealthUrl = string.IsNullOrWhiteSpace(rule.HealthUrl) ? null : ValidateUrl(rule.HealthUrl),
            ReadinessTimeoutSeconds = Math.Clamp(rule.ReadinessTimeoutSeconds <= 0 ? 30 : rule.ReadinessTimeoutSeconds, 2, 300),
            StartupTimeoutSeconds = Math.Clamp(rule.StartupTimeoutSeconds <= 0 ? 600 : rule.StartupTimeoutSeconds, 2, 3600),
            Source = source,
        };
    }

    private static int IndexOfUrl(IReadOnlyList<ProjectUrlRecord> urls, string urlId)
    {
        for (var i = 0; i < urls.Count; i++)
            if (string.Equals(urls[i].Id, urlId, StringComparison.Ordinal))
                return i;
        throw new KeyNotFoundException($"Unknown url id: {urlId}");
    }

    private static string AllocateUrlId(IReadOnlyList<ProjectUrlRecord> urls)
    {
        var max = 0;
        foreach (var u in urls)
        {
            if (u.Id.StartsWith("url-", StringComparison.Ordinal) &&
                int.TryParse(u.Id.AsSpan(4), out var n) && n > max)
                max = n;
        }
        return $"url-{max + 1}";
    }

    /// <summary>
    /// F46 — permanently remove a project record from the registry and
    /// return the removed record so the caller can act on its
    /// <see cref="ProjectRecord.StorageLocation"/>. Throws
    /// <see cref="KeyNotFoundException"/> when the id is unknown. This drops
    /// the metadata row only; deleting the on-disk project storage is the
    /// caller's responsibility (see <c>WorkspaceManagementService</c>) so the
    /// metadata authority stays decoupled from the filesystem authority.
    /// </summary>
    public ProjectRecord Delete(string id)
    {
        EnsureLoaded();
        lock (_gate)
        {
            var idx = _state.Projects.FindIndex(p =>
                string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) throw new KeyNotFoundException($"Unknown projectId: {id}");
            var removed = _state.Projects[idx];
            ReplaceStateAndPersistLocked(_state with
            {
                Projects = [.. _state.Projects.Where((_, i) => i != idx)],
            });
            _logger.LogInformation(
                "project-registry-deleted id={Id} displayName={DisplayName} storage={Storage}",
                removed.Id, removed.DisplayName, removed.StorageLocation);
            return removed;
        }
    }

    private void ThrowIfDisplayNameCollisionLocked(string id, string displayName)
    {
        var collision = _state.Projects.FirstOrDefault(project =>
            !string.Equals(project.Id, id, StringComparison.OrdinalIgnoreCase)
            && string.Equals(project.DisplayName, displayName, StringComparison.OrdinalIgnoreCase));
        if (collision != null)
            throw new InvalidOperationException(
                $"displayName '{displayName}' is already used by {collision.Id}.");
    }

    private void ThrowIfShortCodeCollisionLocked(string id, string shortCode)
    {
        var collision = _state.Projects.FirstOrDefault(project =>
            !string.Equals(project.Id, id, StringComparison.OrdinalIgnoreCase)
            && string.Equals(project.ShortCode, shortCode, StringComparison.OrdinalIgnoreCase));
        if (collision != null)
            throw new InvalidOperationException(
                $"shortCode '{shortCode}' is already used by {collision.Id}.");
    }

    private ProjectRecord MutateLocked(string id, Func<ProjectRecord, ProjectRecord> update, string op,
        StudioUser? actor = null, Action? beforePersist = null)
    {
        EnsureLoaded();
        lock (_gate)
        {
            var idx = _state.Projects.FindIndex(p =>
                string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) throw new KeyNotFoundException($"Unknown projectId: {id}");
            var current = _state.Projects[idx];
            var updated = update(current);
            var next = _state.Projects.ToList();
            next[idx] = updated;
            ReplaceProjectAndAuditLocked(current, updated, next, actor, beforePersist);
            _logger.LogInformation("project-registry-{Op} id={Id}", op, id);
            return updated;
        }
    }

    private void ReplaceProjectAndAuditLocked(ProjectRecord previous, ProjectRecord updated,
        List<ProjectRecord> projects, StudioUser? actor, Action? beforePersist = null)
    {
        var selected = updated.Urls.FirstOrDefault(url =>
            string.Equals(url.Id, "repo", StringComparison.OrdinalIgnoreCase)
            || string.Equals(url.Label, "repo", StringComparison.OrdinalIgnoreCase)
            || string.Equals(url.Label, "repository", StringComparison.OrdinalIgnoreCase));
        if (selected is not null) ValidateRepositoryUrl(selected.Url);
        var before = RemoteProjectRepositoryResolver.ResolveRepositoryUrl(previous);
        var after = RemoteProjectRepositoryResolver.ResolveRepositoryUrl(updated);
        if (string.Equals(before, after, StringComparison.Ordinal))
        {
            beforePersist?.Invoke();
            ReplaceStateAndPersistLocked(_state with { Projects = projects });
            return;
        }

        if (_networked && actor?.Role != StudioRoles.Owner)
            throw new UnauthorizedAccessException("owner-required");
        var audit = _state.RepositoryAudit.ToList();
        if (_networked)
            audit.Add(new ProjectRepositoryAuditEvent(_clock.GetUtcNow().UtcDateTime,
                previous.Id, actor!.Id, actor.Username, before, after));
        beforePersist?.Invoke();
        ReplaceStateAndPersistLocked(_state with { Projects = projects, RepositoryAudit = audit });
    }

    /// <summary>F45b hook: append a fully-formed record (used by the CRUD endpoints).</summary>
    internal ProjectRecord Append(ProjectRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!ProjIdFormat.IsMatch(record.Id))
            throw new ArgumentException($"Invalid project id format: {record.Id}", nameof(record));
        EnsureLoaded();
        lock (_gate)
        {
            if (_state.Projects.Any(p => string.Equals(p.Id, record.Id, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Project id already exists: {record.Id}");
            if (_state.Projects.Any(p => string.Equals(p.DisplayName, record.DisplayName, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"displayName '{record.DisplayName}' is already used.");
            if (_state.Projects.Any(p => string.Equals(p.ShortCode, record.ShortCode, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"shortCode '{record.ShortCode}' is already used.");
            ReplaceStateAndPersistLocked(_state with { Projects = [.. _state.Projects, record] });
            return record;
        }
    }

    /// <summary>F45b hook: reserve the next PROJ id without inserting a record.</summary>
    internal string AllocateNextId()
    {
        EnsureLoaded();
        lock (_gate)
        {
            var (id, next) = PlanNextIdLocked();
            ReplaceStateAndPersistLocked(next);
            return id;
        }
    }

    private (string Id, ProjectsFile Next) PlanNextIdLocked()
    {
        var seq = _state.NextProjectIdSeq;
        // Defensive: in case the file was hand-edited and seq collides
        // with an existing record, walk forward until we find a free id.
        while (_state.Projects.Any(p => string.Equals(p.Id, FormatId(seq), StringComparison.OrdinalIgnoreCase)))
        {
            seq++;
        }
        var id = FormatId(seq);
        return (id, _state with { NextProjectIdSeq = seq + 1 });
    }

    private static string FormatId(int seq) => $"PROJ-{seq:D3}";

    private void ReplaceStateAndPersistLocked(ProjectsFile next)
    {
        var previous = _state;
        _state = next;
        try { PersistLocked(previous.Projects.Count); }
        catch
        {
            _state = previous;
            throw;
        }
    }

    private void PersistLocked(int previousProjectCount)
    {
        if (_taskRepositoryRoot == null) return;
        try
        {
            var path = RegistryPaths.ProjectsFilePath(_taskRepositoryRoot);
            if (_state.Projects.Count < previousProjectCount && File.Exists(path))
            {
                var quarantinePath = NextQuarantinePath(path);
                File.Copy(path, quarantinePath);
                _logger.LogWarning(
                    "project-registry-shrink-quarantined path={Path} quarantinePath={QuarantinePath} previousCount={PreviousCount} nextCount={NextCount}",
                    path,
                    quarantinePath,
                    previousProjectCount,
                    _state.Projects.Count);
            }
            _fileWriter.Write(path, JsonSerializer.Serialize(_state, JsonOpts));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "project-registry-persist-failed root={Root}", _taskRepositoryRoot);
            throw new ProjectPersistenceException(
                $"Could not persist the project registry at '{_taskRepositoryRoot}'.", ex);
        }
    }

    private static string NextQuarantinePath(string registryPath)
    {
        var timestamp = DateTime.UtcNow.ToString(
            "yyyyMMdd'T'HHmmssfffffff'Z'",
            CultureInfo.InvariantCulture);
        var basePath = $"{registryPath}.quarantine-{timestamp}";
        var candidate = basePath;
        var suffix = 1;
        while (File.Exists(candidate))
        {
            candidate = $"{basePath}-{suffix}";
            suffix++;
        }
        return candidate;
    }

    private static void ValidateLoadedState(ProjectsFile state)
    {
        if (state.Projects == null)
            throw new JsonException("Projects must not be null.");
        if (state.RepositoryAudit == null)
            throw new JsonException("RepositoryAudit must not be null.");

        for (var index = 0; index < state.Projects.Count; index++)
        {
            var project = state.Projects[index];
            if (project == null)
                throw new JsonException($"Projects[{index}] must not be null.");
            if (project.Urls == null)
                throw new JsonException($"Projects[{index}].Urls must not be null.");
            if (project.OwnershipMappings == null)
                throw new JsonException($"Projects[{index}].OwnershipMappings must not be null.");
            if (project.OwnershipMappingAudit == null)
                throw new JsonException($"Projects[{index}].OwnershipMappingAudit must not be null.");
        }
    }

    private static string NormalisePath(string? p)
    {
        if (string.IsNullOrWhiteSpace(p)) return "";
        return p.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
    }
}

/// <summary>
/// The durable project registry exists but could not be read as its declared
/// schema. Callers must abort startup or reject the operation without writing.
/// </summary>
public sealed class ProjectRegistryLoadException : IOException
{
    public ProjectRegistryLoadException(string path, Exception innerException)
        : base($"Could not load the project registry at '{path}'. No registry writes are allowed.", innerException)
    {
    }
}
