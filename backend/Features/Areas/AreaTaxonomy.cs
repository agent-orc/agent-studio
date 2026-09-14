using System.Text.RegularExpressions;

namespace AgentStudio.Areas;

/// <summary>Where an area definition comes from.</summary>
public static class AreaSources
{
    /// <summary>One of the product defaults every project inherits.</summary>
    public const string Product = "product";
    /// <summary>A project addition configured in the workspace project settings.</summary>
    public const string Project = "project";
}

/// <summary>
/// One area of the classification vocabulary: a stable id, a display label, a
/// one-line description, and (through <see cref="AreaGlossaryDocument"/>) a
/// glossary page that carries the area's ubiquitous language.
/// </summary>
public sealed record AreaDefinition
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public string Description { get; init; } = "";
    public string Source { get; init; } = AreaSources.Product;
    /// <summary>Repository-relative path of the area's glossary wiki page.</summary>
    public string GlossaryPath { get; init; } = "";
}

/// <summary>
/// The product-default classification vocabulary and the pure rules that keep
/// it stable. Everything here is a deterministic function of its inputs: no
/// filesystem, no configuration, no clock. The project-scoped registry
/// (<see cref="AreaRegistryService"/>) reads the project additions and applies
/// these rules; the endpoints validate against them before any write.
/// </summary>
public static partial class AreaTaxonomy
{
    /// <summary>Stable id grammar shared by area ids and tag ids.</summary>
    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex StableId();

    public const int MaxIdLength = 32;
    public const int MaxAreasPerProject = 100;

    /// <summary>
    /// The ten product areas decided in AGT-W55 D3 (option A, "the ten areas
    /// from §5, refined per project"). Every project inherits this list; a
    /// project may add areas and re-label an inherited one, but it can never
    /// remove or rename a product id.
    /// </summary>
    public static readonly IReadOnlyList<AreaDefinition> ProductDefaults =
    [
        Product("execution-and-runner", "Execution and runner",
            "Task pickup, the CLI run loop, runners, worktrees, and the isolated execution world."),
        Product("delivery-chain", "Delivery chain",
            "Integration, merge, rebase, recovery, and the robustness of the path from a finished run to the target branch."),
        Product("gates-and-review", "Gates and review",
            "Batch gate, remote gate, review evidence, grading, and the acceptance rails."),
        Product("observation", "Observation",
            "Watcher probes, telemetry, the project feed, and the signals an operator watches."),
        Product("task-and-board-ui", "Task and board UI",
            "Board lanes, task detail, timeline, escalation views, and the surfaces an operator works in."),
        Product("dossiers-and-documentation", "Dossiers and documentation",
            "Dossiers, the wiki, ADRs, style guides, and the documentation lifecycle."),
        Product("websites", "Websites",
            "Public websites, publishing targets, and the copy that ships with them."),
        Product("security", "Security",
            "Authentication, secrets, data boundaries, sandboxing, and project access scoping."),
        Product("retention", "Retention",
            "Storage lifecycle, archive, pruning, and the retention service."),
        Product("token-economy", "Token economy",
            "Token ledgers, model cost, quota handling, and the price of a run."),
    ];

    /// <summary>
    /// Quality facet ids. Per the project-definition v2 plan these are the
    /// Quality Studio domain ids, not a second list: a facet that names a
    /// quality aspect reuses the catalogue id so both systems speak one
    /// vocabulary. An id already claimed by an area (today: <c>security</c>)
    /// is not duplicated here - it resolves to that area instead.
    /// </summary>
    public static readonly IReadOnlyList<(string Id, string Label, string Description)> QualityFacets =
    [
        ("architecture", "Architecture", "Load-bearing structure decisions; ADR-worthy changes."),
        ("correctness", "Correctness", "Behavior matches the specified contract."),
        ("testing", "Testing", "Test coverage, regression tests, and test design."),
        ("privacy", "Privacy", "Personal data handling, retention of identifying data, and consent."),
        ("payments", "Payments", "Billing, payment flows, and money-handling correctness."),
        ("reliability", "Reliability", "Failure handling, retries, degradation, and service observations."),
        ("performance", "Performance", "Long-task budgets, API latency, polling load, render speed."),
        ("accessibility", "Accessibility", "Keyboard paths, contrast, semantics, and assistive-technology support."),
        ("localization", "Localization", "Language coverage, translation, and locale-dependent formatting."),
        ("operations", "Operations", "Deployment, runbooks, configuration, and operator procedures."),
        ("review-prioritization", "Review prioritization", "How review effort is ordered and budgeted."),
        ("seo", "SEO", "Search-engine visibility of published websites."),
    ];

    /// <summary>
    /// Document facets from AGT-W55 §5: the cross-cutting kind of a card,
    /// Dossier, or article, orthogonal to its area.
    /// </summary>
    public static readonly IReadOnlyList<(string Id, string Label, string Description)> DocumentFacets =
    [
        ("decision", "Decision", "Records a decision that was taken, with its date and owner."),
        ("incident", "Incident", "Reconstructs a failure that happened in production or in a run."),
        ("evidence", "Evidence", "Carries measurements or reproductions that other documents cite."),
        ("guideline", "Guideline", "States a rule agents and contributors must follow."),
        ("migration", "Migration", "Moves the product from one contract or topology to the next."),
    ];

    private static readonly HashSet<string> ProductAreaIds =
        ProductDefaults.Select(area => area.Id).ToHashSet(StringComparer.Ordinal);

    public static bool IsValidId(string? id) =>
        !string.IsNullOrWhiteSpace(id)
        && id.Length <= MaxIdLength
        && StableId().IsMatch(id);

    public static bool IsProductArea(string? id) =>
        id != null && ProductAreaIds.Contains(id);

    /// <summary>
    /// The kind of a registry id. An area id always wins over a facet id of the
    /// same name so the flat tag namespace holds exactly one entry per id; the
    /// quality domain <c>security</c> therefore resolves to the security area.
    /// </summary>
    public static string ResolveKind(string id, string? storedKind, IReadOnlyCollection<string>? projectAreaIds = null)
        => IsProductArea(id)
           || (projectAreaIds != null && projectAreaIds.Contains(id, StringComparer.Ordinal))
            ? TagKinds.Area
            : TagKinds.Normalize(storedKind);

    /// <summary>
    /// Product defaults plus the project additions, in product order first and
    /// then project order. A project entry whose id matches a product default
    /// re-labels that area in place; it never adds a second row and never
    /// changes its id or its source.
    /// </summary>
    public static IReadOnlyList<AreaDefinition> Merge(IEnumerable<AreaDefinition>? projectAreas)
    {
        var additions = (projectAreas ?? [])
            .Where(area => IsValidId(area.Id))
            .GroupBy(area => area.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);

        var merged = new List<AreaDefinition>();
        foreach (var product in ProductDefaults)
        {
            merged.Add(additions.TryGetValue(product.Id, out var overriden)
                ? product with
                {
                    Label = Blank(overriden.Label) ? product.Label : overriden.Label.Trim(),
                    Description = Blank(overriden.Description) ? product.Description : overriden.Description.Trim(),
                }
                : product);
        }
        foreach (var addition in additions.Values.Where(area => !IsProductArea(area.Id)))
        {
            merged.Add(new AreaDefinition
            {
                Id = addition.Id,
                Label = Blank(addition.Label) ? addition.Id : addition.Label.Trim(),
                Description = addition.Description?.Trim() ?? "",
                Source = AreaSources.Project,
                GlossaryPath = AreaGlossaryDocument.RepoRelativePath(addition.Id),
            });
        }
        return merged;
    }

    /// <summary>
    /// Boundary rule for <c>PUT /api/projects/{project}/areas</c>. Returns null
    /// when the proposed project additions may be stored, otherwise the single
    /// reason to refuse them. Area ids are stable: a product id can never be
    /// dropped or renamed, a project area that is still carried as a tag by a
    /// card, a Dossier, or an article cannot disappear from the registry, and a
    /// new area may not claim an id the registry already uses as a facet -
    /// otherwise declaring an area would silently retype a facet that items
    /// already carry.
    /// </summary>
    public static string? ValidateProjectAreas(
        IReadOnlyList<AreaDefinition>? proposed,
        IReadOnlyCollection<string> currentProjectAreaIds,
        IReadOnlyCollection<string> referencedAreaIds,
        IReadOnlyCollection<string>? reservedFacetIds = null)
    {
        var areas = proposed ?? [];
        if (areas.Count > MaxAreasPerProject)
            return $"A project may declare at most {MaxAreasPerProject} areas.";

        foreach (var area in areas)
        {
            if (!IsValidId(area.Id))
                return $"Invalid area id '{area.Id}'. Allowed: lowercase words joined by single hyphens, at most {MaxIdLength} characters.";
            if (Blank(area.Label))
                return $"Area '{area.Id}' needs a label.";
            if (area.Label.Trim().Length > 80)
                return $"Area '{area.Id}' label must be at most 80 characters.";
            if ((area.Description ?? "").Trim().Length > 300)
                return $"Area '{area.Id}' description must be at most 300 characters.";
        }

        var ids = areas.Select(area => area.Id).ToList();
        var duplicate = ids.GroupBy(id => id, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate != null)
            return $"Area id '{duplicate.Key}' is declared twice.";

        var kept = ids.ToHashSet(StringComparer.Ordinal);
        var dropped = currentProjectAreaIds
            .Where(id => !IsProductArea(id) && !kept.Contains(id))
            .Where(referencedAreaIds.Contains)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
        if (dropped.Count > 0)
            return "Area ids are stable: "
                   + string.Join(", ", dropped)
                   + " is still carried as a tag. Re-tag the items before removing the area.";

        var alreadyDeclared = currentProjectAreaIds.ToHashSet(StringComparer.Ordinal);
        var claimed = ids
            .Where(id => !IsProductArea(id) && !alreadyDeclared.Contains(id))
            .FirstOrDefault(id => reservedFacetIds?.Contains(id, StringComparer.OrdinalIgnoreCase) == true);
        if (claimed != null)
            return $"'{claimed}' is already a facet tag. Retire the facet before declaring an area with that id.";
        return null;
    }

    private static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);

    private static AreaDefinition Product(string id, string label, string description) => new()
    {
        Id = id,
        Label = label,
        Description = description,
        Source = AreaSources.Product,
        GlossaryPath = AreaGlossaryDocument.RepoRelativePath(id),
    };
}
