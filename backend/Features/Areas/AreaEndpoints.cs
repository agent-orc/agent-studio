using AgentStudio.Docs;

namespace AgentStudio.Areas;

/// <summary>Body of <c>PUT /api/projects/{project}/areas</c>: the project additions, replace-all.</summary>
public sealed record SetProjectAreasRequest
{
    public List<ProjectAreaInput> Areas { get; init; } = [];
}

/// <summary>One proposed project area.</summary>
public sealed record ProjectAreaInput
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public string Description { get; init; } = "";
}

/// <summary>
/// The areas registry and its glossaries under
/// <c>/api/projects/{projectName}/areas</c>, plus the project's effective tag
/// vocabulary under <c>/api/projects/{projectName}/tags</c>.
///
/// The flow is the standard one: the route validates its input, the service
/// resolves the project and collects the facts, <see cref="AreaTaxonomy"/>
/// decides, and only then does a write happen.
/// </summary>
public static class AreaEndpoints
{
    public static void MapAreaEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects/{projectName}/areas", (
            string projectName,
            AreaRegistryService areas,
            TaskScannerService scanner,
            ProjectRegistry registry) =>
        {
            if (!KnownProject(projectName, scanner, registry))
                return Results.NotFound(new { error = $"Unknown project '{projectName}'" });
            var items = areas.List(projectName);
            return Results.Ok(new { projectName, count = items.Count, items });
        });

        app.MapPut("/api/projects/{projectName}/areas", (
            string projectName,
            SetProjectAreasRequest body,
            AreaRegistryService areas,
            TaskScannerService scanner,
            ProjectRegistry registry,
            WorkbenchCatalogueService workbenches) =>
        {
            if (!KnownProject(projectName, scanner, registry))
                return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

            var proposed = (body?.Areas ?? [])
                .Select(area => new AreaDefinition
                {
                    Id = (area.Id ?? "").Trim(),
                    Label = area.Label ?? "",
                    Description = area.Description ?? "",
                    Source = AreaSources.Project,
                })
                .ToList();
            var current = areas.ProjectAreas(projectName).Select(area => area.Id).ToList();
            var referenced = ReferencedTagIds(projectName, scanner, workbenches);
            var facets = areas.EffectiveTags(projectName)
                .Where(entry => entry.Kind == TagKinds.Facet)
                .Select(entry => entry.Id)
                .ToList();
            var error = AreaTaxonomy.ValidateProjectAreas(proposed, current, referenced, facets);
            if (error != null) return Results.BadRequest(new { error });

            var items = areas.SetProjectAreas(projectName, proposed);
            return Results.Ok(new { projectName, count = items.Count, items });
        });

        app.MapGet("/api/projects/{projectName}/areas/{areaId}/glossary", (
            string projectName,
            string areaId,
            AreaGlossaryService glossaries) => GlossaryResult(glossaries.Read(projectName, areaId)));

        app.MapPut("/api/projects/{projectName}/areas/{areaId}/glossary", (
            string projectName,
            string areaId,
            SetAreaGlossaryRequest body,
            AreaGlossaryService glossaries,
            ProjectDocsService docs) =>
        {
            var result = glossaries.Write(projectName, areaId, body);
            // The glossary is a docs/ page, so the warm wiki cache must not keep
            // serving the bytes from before the write.
            if (result.Success) docs.InvalidateWikiContent(projectName);
            return GlossaryResult(result);
        });

        // The closed list every tag write of this project is validated against:
        // the workspace registry with resolved kinds plus the project areas.
        app.MapGet("/api/projects/{projectName}/tags", (
            string projectName,
            AreaRegistryService areas,
            TaskScannerService scanner,
            ProjectRegistry registry) =>
        {
            if (!KnownProject(projectName, scanner, registry))
                return Results.NotFound(new { error = $"Unknown project '{projectName}'" });
            var items = areas.EffectiveTags(projectName);
            return Results.Ok(new { projectName, count = items.Count, items });
        });
    }

    private static IResult GlossaryResult(AreaGlossaryResult result) =>
        result.Success ? Results.Ok(result.Glossary)
            : result.ErrorCode == "not-found" ? Results.NotFound(new { error = result.Error })
            : result.ErrorCode == "write-failed" ? Results.Conflict(new { error = result.Error })
            : Results.BadRequest(new { error = result.Error });

    private static bool KnownProject(string projectName, TaskScannerService scanner, ProjectRegistry registry) =>
        !string.IsNullOrWhiteSpace(projectName)
        && (scanner.GetWatchPaths().Any(entry =>
                string.Equals(entry.Name, projectName, StringComparison.OrdinalIgnoreCase))
            || registry.FindByIdOrDisplayName(projectName) != null);

    /// <summary>
    /// Every tag id this project's cards and Dossiers currently carry. An area
    /// that is still referenced cannot be removed from the registry, which is
    /// what makes area ids stable in practice rather than only on paper.
    /// </summary>
    private static IReadOnlyCollection<string> ReferencedTagIds(
        string projectName,
        TaskScannerService scanner,
        WorkbenchCatalogueService workbenches)
    {
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in scanner.ScanAllJobsWithArchive())
        {
            if (!string.Equals(job.ProjectName, projectName, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var tag in job.Tags ?? []) referenced.Add(tag);
        }
        foreach (var item in workbenches.List(projectName, includeHistory: true)?.Items ?? [])
            foreach (var tag in item.Tags) referenced.Add(tag);
        return referenced;
    }
}
