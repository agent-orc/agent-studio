namespace AgentStudio.Areas;

/// <summary>The glossary of one area as served by the API.</summary>
public sealed record AreaGlossary(
    string ProjectName,
    string AreaId,
    string Label,
    string Path,
    bool Exists,
    IReadOnlyList<GlossaryTerm> Terms);

public sealed record AreaGlossaryResult(
    bool Success,
    string? ErrorCode,
    string? Error,
    AreaGlossary? Glossary,
    string? Revision);

/// <summary>Body of <c>PUT /api/projects/{project}/areas/{areaId}/glossary</c>.</summary>
public sealed record SetAreaGlossaryRequest
{
    public List<GlossaryTerm> Terms { get; init; } = [];
}

/// <summary>
/// Reads and writes the glossary wiki page of an area. The page is a normal
/// wiki article at <c>docs/areas/&lt;area-id&gt;/glossary.md</c>, so it is
/// searchable, reviewable, and editable by hand; this service only owns the
/// round trip between that page and the structured term list.
///
/// The write is a bounded side effect: validate at the boundary, resolve the
/// area and the repository, render the page, then persist through the managed
/// repository mutation so the commit and the file write share one gate.
/// </summary>
public sealed class AreaGlossaryService
{
    private readonly AreaRegistryService _areas;
    private readonly TaskScannerService _scanner;
    private readonly ProjectRegistry _registry;
    private readonly ManagedRepositoryMutationService _mutations;
    private readonly ILogger<AreaGlossaryService> _logger;

    public AreaGlossaryService(
        AreaRegistryService areas,
        TaskScannerService scanner,
        ProjectRegistry registry,
        ManagedRepositoryMutationService mutations,
        ILogger<AreaGlossaryService> logger)
    {
        _areas = areas;
        _scanner = scanner;
        _registry = registry;
        _mutations = mutations;
        _logger = logger;
    }

    public AreaGlossaryResult Read(string projectName, string areaId)
    {
        var area = _areas.Find(projectName, areaId);
        if (area == null) return Failure("not-found", $"Unknown area '{areaId}'.");
        var path = ResolveFullPath(projectName, areaId);
        if (path == null) return Failure("not-found", $"Project '{projectName}' has no repository checkout.");

        var exists = File.Exists(path);
        var terms = exists ? AreaGlossaryDocument.Parse(ReadText(path)) : [];
        return new AreaGlossaryResult(true, null, null, new AreaGlossary(
            projectName, area.Id, area.Label, area.GlossaryPath, exists, terms), null);
    }

    public AreaGlossaryResult Write(string projectName, string areaId, SetAreaGlossaryRequest body)
    {
        var validation = AreaGlossaryDocument.ValidateTerms(body?.Terms);
        if (validation != null) return Failure("validation", validation);

        var area = _areas.Find(projectName, areaId);
        if (area == null) return Failure("not-found", $"Unknown area '{areaId}'.");
        var root = ProjectRepoResolver.ResolveForProject(projectName, _scanner, _registry);
        if (root == null) return Failure("not-found", $"Project '{projectName}' has no repository checkout.");

        var relPath = area.GlossaryPath;
        var fullPath = Path.GetFullPath(Path.Combine(root, relPath.Replace('/', Path.DirectorySeparatorChar)));
        var markdown = AreaGlossaryDocument.Render(area, body!.Terms);

        var mutation = _mutations.Execute(
            projectName,
            root,
            $"area-glossary-{area.Id}",
            $"docs(areas): update the {area.Id} glossary",
            [relPath],
            () =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                File.WriteAllText(fullPath, markdown);
            });
        if (!mutation.Success)
            return Failure("write-failed", $"The glossary could not be persisted: {mutation.Error}");

        _logger.LogInformation(
            "area-glossary-written project={Project} area={Area} terms={Terms} changed={Changed} sha={Sha}",
            projectName, area.Id, body.Terms.Count, mutation.Changed, mutation.CommitSha);

        return new AreaGlossaryResult(true, null, null, new AreaGlossary(
                projectName, area.Id, area.Label, relPath, true,
                AreaGlossaryDocument.Parse(markdown)),
            mutation.CommitSha);
    }

    private string? ResolveFullPath(string projectName, string areaId)
    {
        var root = ProjectRepoResolver.ResolveForProject(projectName, _scanner, _registry);
        if (root == null || !AreaTaxonomy.IsValidId(areaId)) return null;
        return Path.GetFullPath(Path.Combine(
            root, AreaGlossaryDocument.RepoRelativePath(areaId).Replace('/', Path.DirectorySeparatorChar)));
    }

    private string? ReadText(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SilentCatch.Note(ex, "Area glossary page could not be read; the area reports an empty glossary.");
            return null;
        }
    }

    private static AreaGlossaryResult Failure(string code, string error) =>
        new(false, code, error, null, null);
}
