namespace AgentStudio.Areas;

/// <summary>Outcome of validating a proposed tag set against the effective registry.</summary>
public sealed record TagValidationResult(IReadOnlyList<string> TagIds, IReadOnlyList<string> Unknown)
{
    public bool Ok => Unknown.Count == 0;
    public string Error => $"Unknown tag ids: {string.Join(", ", Unknown)}.";
}

/// <summary>
/// The areas registry of one project (AGT-W55 §5, D3). The ten product-default
/// areas are inherited by every project; a project adds or re-labels areas
/// through its workspace project settings, because the active v1
/// <c>project.yml</c> is closed and the project-definition v2 plan records
/// <c>project.areas</c> as a later extension request.
///
/// The service also projects the effective tag vocabulary of a project: the
/// workspace tag registry with each row's kind resolved, plus any project area
/// that the workspace registry does not carry. That effective list is the
/// closed list every tag write is validated against.
/// </summary>
public sealed class AreaRegistryService
{
    private readonly TagRegistryService _tags;
    private readonly ProjectSettingsService _settings;

    public AreaRegistryService(TagRegistryService tags, ProjectSettingsService settings)
    {
        _tags = tags;
        _settings = settings;
    }

    /// <summary>Product defaults merged with this project's additions, product order first.</summary>
    public IReadOnlyList<AreaDefinition> List(string projectName) =>
        AreaTaxonomy.Merge(ReadProjectAreas(projectName));

    /// <summary>This project's stored additions, without the product defaults.</summary>
    public IReadOnlyList<AreaDefinition> ProjectAreas(string projectName) =>
        [.. ReadProjectAreas(projectName)];

    public AreaDefinition? Find(string projectName, string areaId) =>
        List(projectName).FirstOrDefault(area => string.Equals(area.Id, areaId, StringComparison.Ordinal));

    /// <summary>
    /// Replace-all write of the project additions. The caller validates through
    /// <see cref="AreaTaxonomy.ValidateProjectAreas"/> first; this method only
    /// normalizes and persists. A product-default row is kept because it
    /// re-labels the inherited area.
    /// </summary>
    public IReadOnlyList<AreaDefinition> SetProjectAreas(
        string projectName,
        IReadOnlyList<AreaDefinition> areas)
    {
        _settings.SetProjectAreas(projectName, [.. areas.Select(area => new ProjectAreaSetting
        {
            Id = area.Id,
            Label = (area.Label ?? "").Trim(),
            Description = (area.Description ?? "").Trim(),
        })]);
        return List(projectName);
    }

    /// <summary>
    /// The effective tag vocabulary of a project: every workspace registry row
    /// with its kind resolved against this project's areas, followed by the
    /// project areas the workspace registry has no row for.
    /// </summary>
    public IReadOnlyList<TagRegistryEntry> EffectiveTags(string projectName)
    {
        var areas = List(projectName);
        var areaIds = areas.Select(area => area.Id).ToHashSet(StringComparer.Ordinal);
        var entries = _tags.GetAll()
            .Select(entry => entry with { Kind = AreaTaxonomy.ResolveKind(entry.Id, entry.Kind, areaIds) })
            .ToList();
        var known = entries.Select(entry => entry.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        entries.AddRange(areas
            .Where(area => !known.Contains(area.Id))
            .Select(area => new TagRegistryEntry
            {
                Id = area.Id,
                Label = area.Label,
                Description = area.Description,
                Kind = TagKinds.Area,
            }));
        return entries;
    }

    /// <summary>Every tag id a card, Dossier, or article of this project may carry.</summary>
    public IReadOnlySet<string> KnownTagIds(string projectName) =>
        EffectiveTags(projectName).Select(entry => entry.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Boundary rule for every tag write: normalize the requested ids, keep the
    /// caller's order, drop duplicates, and refuse ids the registry does not
    /// know. Reads stay lenient by design - an id that leaves the registry
    /// after it was written renders as a ghost chip instead of invalidating the
    /// document that carries it.
    /// </summary>
    public TagValidationResult ValidateTags(string projectName, IEnumerable<string>? tagIds)
    {
        var requested = (tagIds ?? [])
            .Select(TaskMutationService.NormalizeTagId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var known = KnownTagIds(projectName);
        var unknown = requested.Where(id => !known.Contains(id)).ToList();
        return new TagValidationResult(requested, unknown);
    }

    private List<AreaDefinition> ReadProjectAreas(string projectName) =>
        [.. (_settings.Get(projectName).Areas ?? [])
            .Select(area => new AreaDefinition
            {
                Id = area.Id,
                Label = area.Label,
                Description = area.Description,
                Source = AreaSources.Project,
                GlossaryPath = AreaGlossaryDocument.RepoRelativePath(area.Id),
            })];
}
