using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentStudio.Tags;

/// <summary>
/// Workspace-level tag registry. Tags are a flat namespace shared across the
/// watched projects in one workspace and stored as a single JSON array at
/// <c>&lt;TaskRepository&gt;/tags.json</c>. On boot the file is merged-by-id
/// with the seeded vocabulary: the ten product-default areas, the quality and
/// document facets, and the curated legacy rows (ui-ux, quality, docs,
/// observability) plus the system provenance tags, so a fresh workspace
/// already has the standard taxonomy. Existing rows are never overwritten: a
/// user's custom label / colour / description for a seed id wins over the seed.
/// Explicitly deleted seed ids are remembered in a sidecar tombstone file so
/// later seed merges do not resurrect tags the user removed.
///
/// Each row carries a kind (AGT-2803). The kind of an area id is not stored
/// data: <see cref="AreaTaxonomy.ResolveKind"/> answers it from the area
/// vocabulary, so a row written before the field existed reads correctly and a
/// quality domain that shares an id with an area resolves to that one area
/// instead of splitting the flat namespace. Project-level area additions are
/// overlaid per project by <see cref="AreaRegistryService"/>.
/// </summary>
/// <remarks>
/// Concurrency: a process-wide lock protects the in-memory cache and the
/// disk write. Writes are read-modify-write because adding a tag must
/// reject duplicate ids and the registry is rarely written compared to read.
/// </remarks>
public sealed class TagRegistryService
{
    private const string FileName = "tags.json";
    private const string DeletedSeedsFileName = "tags.deleted-seeds.json";
    private static readonly Regex IdPattern = new("^[a-z0-9-]{1,32}$", RegexOptions.Compiled);

    /// <summary>
    /// Colours for the seeded vocabulary. Ids without an entry fall back to the
    /// neutral default, so adding an area or a facet never requires a palette
    /// change.
    /// </summary>
    private static readonly Dictionary<string, string> SeedColors = new(StringComparer.Ordinal)
    {
        // Areas
        ["execution-and-runner"] = "#89b4fa",
        ["delivery-chain"] = "#74c7ec",
        ["gates-and-review"] = "#b4befe",
        ["observation"] = "#f9e2af",
        ["task-and-board-ui"] = "#cba6f7",
        ["dossiers-and-documentation"] = "#94e2d5",
        ["websites"] = "#a6e3a1",
        ["security"] = "#f38ba8",
        ["retention"] = "#eba0ac",
        ["token-economy"] = "#fab387",
        // Facets that existed before the area vocabulary keep their colour.
        ["architecture"] = "#89b4fa",
        ["performance"] = "#fab387",
    };

    /// <summary>
    /// Curated rows that predate the area vocabulary and are owned by no other
    /// list. They stay in the seed so an existing workspace keeps its labels,
    /// colours, and the two system provenance tags.
    /// </summary>
    private static readonly TagRegistryEntry[] LegacySeed =
    [
        new() { Id = "ui-ux",         Label = "UI / UX",       Color = "#cba6f7", Description = "Frontend look-and-feel, layout, click paths, visual polish." },
        new() { Id = "quality",       Label = "Quality",       Color = "#a6e3a1", Description = "Tests, regressions, robustness, logging, observability of bugs." },
        new() { Id = "docs",          Label = "Docs",          Color = "#94e2d5", Description = "README / AGENTS / ADR / skill files / lookup index updates." },
        new() { Id = "observability", Label = "Observability", Color = "#f9e2af", Description = "Logs, metrics, drift reports, token aggregates, supervisor signals." },
        new() { Id = "orchestrator-moved", Label = "Orchestrator: moved", Color = "#b4befe", Description = "The orchestrator advanced this task toward Completed (accept-as-done), as opposed to a human accepting it." },
        new() { Id = "outcome-silent-finish", Label = "Outcome: silent finish", Color = "#f9e2af", Description = "Codex stopped after its final tool call without a closing sentinel; the runner detected the silent-completion shape and finalized the run. The work is likely complete but the sign-off is missing - double-check before promoting." }
    ];

    /// <summary>
    /// The seeded vocabulary: the product-default areas and the facets, both
    /// projected from <see cref="AreaTaxonomy"/> so the registry never holds a
    /// second copy of either list, plus the curated legacy rows. An id claimed
    /// by an area is never seeded a second time as a facet.
    /// </summary>
    private static readonly TagRegistryEntry[] Seed = BuildSeed();

    private static TagRegistryEntry[] BuildSeed()
    {
        var seed = new List<TagRegistryEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string id, string label, string description, string kind, string? color = null)
        {
            if (!seen.Add(id)) return;
            seed.Add(new TagRegistryEntry
            {
                Id = id,
                Label = label,
                Description = description,
                Color = color ?? (SeedColors.TryGetValue(id, out var seeded) ? seeded : "#94a3b8"),
                Kind = kind,
            });
        }
        foreach (var area in AreaTaxonomy.ProductDefaults)
            Add(area.Id, area.Label, area.Description, TagKinds.Area);
        foreach (var (id, label, description) in AreaTaxonomy.QualityFacets)
            Add(id, label, description, TagKinds.Facet);
        foreach (var (id, label, description) in AreaTaxonomy.DocumentFacets)
            Add(id, label, description, TagKinds.Facet);
        foreach (var legacy in LegacySeed)
            Add(legacy.Id, legacy.Label, legacy.Description, TagKinds.Facet, legacy.Color);
        return [.. seed];
    }

    private readonly ILogger<TagRegistryService> _logger;
    private readonly IConfiguration _config;
    private readonly object _lock = new();
    private List<TagRegistryEntry>? _cache;
    private HashSet<string>? _deletedSeedIds;

    public TagRegistryService(ILogger<TagRegistryService> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    public IReadOnlyList<TagRegistryEntry> GetAll()
    {
        EnsureLoaded();
        lock (_lock)
        {
            return _cache!.Select(Clone).ToList();
        }
    }

    /// <summary>
    /// Add a new tag. When <paramref name="id"/> is empty, derive it from
    /// <paramref name="label"/>. Returns the stored entry on success,
    /// throws <see cref="InvalidOperationException"/> on duplicate id, and
    /// throws <see cref="ArgumentException"/> on an invalid id or empty
    /// label.
    /// </summary>
    public TagRegistryEntry Create(string? id, string label, string? color, string? description, string? kind = null)
    {
        if (string.IsNullOrWhiteSpace(label))
            throw new ArgumentException("Label is required");
        if (kind != null && TagKinds.Normalize(kind) == TagKinds.Area)
            throw new ArgumentException("Area tags are declared in the project areas registry, not through the tag registry.");

        var resolvedId = string.IsNullOrWhiteSpace(id)
            ? TaskMutationService.NormalizeTagId(label)
            : TaskMutationService.NormalizeTagId(id);

        if (!IdPattern.IsMatch(resolvedId))
            throw new ArgumentException($"Invalid tag id '{resolvedId}'. Allowed: [a-z0-9-]{{1,32}}.");

        EnsureLoaded();
        lock (_lock)
        {
            if (_cache!.Any(t => string.Equals(t.Id, resolvedId, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Tag '{resolvedId}' already exists");

            if (AreaTaxonomy.IsProductArea(resolvedId))
                throw new InvalidOperationException($"'{resolvedId}' is an area id and is owned by the areas registry.");

            var entry = new TagRegistryEntry
            {
                Id = resolvedId,
                Label = label.Trim(),
                Color = NormalizeColor(color),
                Description = description?.Trim() ?? string.Empty,
                Kind = TagKinds.Facet
            };
            _cache!.Add(entry);
            _deletedSeedIds!.Remove(resolvedId);
            Persist();
            return Clone(entry);
        }
    }

    /// <summary>
    /// Soft-delete: drop the registry entry. Per-job tag arrays are NOT
    /// rewritten; the FE renders unknown ids as a faint ghost chip until
    /// the user re-tags. Product-default area ids are stable and cannot be
    /// deleted here - they are owned by the areas registry.
    /// </summary>
    public bool Delete(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        if (AreaTaxonomy.IsProductArea(id))
            throw new InvalidOperationException($"Area id '{id}' is stable and cannot be deleted from the tag registry.");
        EnsureLoaded();
        lock (_lock)
        {
            var idx = _cache!.FindIndex(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) return false;
            if (IsSeedId(_cache[idx].Id))
                _deletedSeedIds!.Add(_cache[idx].Id);
            _cache.RemoveAt(idx);
            Persist();
            return true;
        }
    }

    public bool Exists(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        EnsureLoaded();
        lock (_lock)
        {
            return _cache!.Any(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void EnsureLoaded()
    {
        lock (_lock)
        {
            if (_cache != null) return;
            var path = ResolveStorePath();
            if (path == null)
            {
                _cache = Seed.Select(Clone).ToList();
                _deletedSeedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                return;
            }
            _deletedSeedIds = LoadDeletedSeedIds(path);

            if (!File.Exists(path))
            {
                _cache = Seed.Select(Clone).ToList();
                Persist();
                _logger.LogInformation("Seeded tag registry at {Path} with {Count} default tags", path, _cache.Count);
                return;
            }

            try
            {
                var json = File.ReadAllText(path);
                var doc = JsonSerializer.Deserialize<List<TagRegistryEntry>>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                _cache = doc?.Where(t => !string.IsNullOrWhiteSpace(t.Id) && IdPattern.IsMatch(t.Id))
                              .ToList()
                          ?? Seed.Select(Clone).ToList();

                // Merge new seed defaults by id: existing rows are never
                // overwritten (the user's label / colour / description for a
                // seed id wins), but missing seed ids are appended so a
                // workspace from before the expanded taxonomy gains the new
                // standard tags on next boot.
                if (MergeMissingSeeds())
                {
                    Persist();
                    _logger.LogInformation("Merged missing default tags into registry at {Path}", path);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read tag registry at {Path}; using defaults", path);
                _cache = Seed.Select(Clone).ToList();
            }
        }
    }

    /// <summary>
    /// Append every seed entry whose id is not already in <see cref="_cache"/>.
    /// Returns true when at least one row was added so the caller can persist.
    /// Pure addition: rows already present in the cache are left untouched
    /// (the user's customisations win over the seed defaults).
    /// </summary>
    private bool MergeMissingSeeds()
    {
        if (_cache == null) return false;
        var existing = new HashSet<string>(
            _cache.Select(e => e.Id),
            StringComparer.OrdinalIgnoreCase);
        var added = false;
        foreach (var seed in Seed)
        {
            if (existing.Contains(seed.Id)) continue;
            if (_deletedSeedIds?.Contains(seed.Id) == true) continue;
            _cache.Add(Clone(seed));
            added = true;
        }
        return added;
    }

    private void Persist()
    {
        var path = ResolveStorePath();
        if (path == null || _cache == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path,
                JsonSerializer.Serialize(_cache,
                    new JsonSerializerOptions { WriteIndented = true }));
            PersistDeletedSeedIds(path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist tag registry to {Path}", path);
        }
    }

    private string? ResolveStorePath()
    {
        var taskRepo = _config["TaskRepository"];
        if (!string.IsNullOrWhiteSpace(taskRepo))
            return Path.Combine(taskRepo, FileName);

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local)) return null;
        return Path.Combine(local, "agent-taskboard", FileName);
    }

    private static string NormalizeColor(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "#94a3b8";
        var s = raw.Trim();
        if (!s.StartsWith('#')) s = "#" + s;
        // Accept #rgb, #rrggbb, #rrggbbaa loosely; otherwise default.
        return Regex.IsMatch(s, "^#[0-9a-fA-F]{3,8}$") ? s : "#94a3b8";
    }

    private static bool IsSeedId(string id) =>
        Seed.Any(seed => string.Equals(seed.Id, id, StringComparison.OrdinalIgnoreCase));

    private HashSet<string> LoadDeletedSeedIds(string registryPath)
    {
        var tombstonePath = ResolveDeletedSeedsPath(registryPath);
        if (!File.Exists(tombstonePath))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var ids = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(tombstonePath))
                      ?? [];
            return ids
                .Where(id => IsSeedId(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read deleted seed tag registry at {Path}; deleted seed tags may be re-merged", tombstonePath);
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void PersistDeletedSeedIds(string registryPath)
    {
        var tombstonePath = ResolveDeletedSeedsPath(registryPath);
        var ids = _deletedSeedIds?.OrderBy(id => id, StringComparer.Ordinal).ToArray()
                  ?? [];
        if (ids.Length == 0)
        {
            if (File.Exists(tombstonePath))
                File.Delete(tombstonePath);
            return;
        }

        File.WriteAllText(tombstonePath,
            JsonSerializer.Serialize(ids, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string ResolveDeletedSeedsPath(string registryPath) =>
        Path.Combine(Path.GetDirectoryName(registryPath)!, DeletedSeedsFileName);

    private static TagRegistryEntry Clone(TagRegistryEntry e) => new()
    {
        Id = e.Id,
        Label = e.Label,
        Color = e.Color,
        Description = e.Description,
        Kind = AreaTaxonomy.ResolveKind(e.Id, e.Kind)
    };
}
