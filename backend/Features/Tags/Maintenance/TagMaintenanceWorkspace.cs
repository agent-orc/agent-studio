using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentStudio.Tags;

public interface ITagMaintenanceWorkspace
{
    IReadOnlyList<string> Projects();
    TagMaintenanceSnapshot Capture(string project);
    string CreateCard(string project, TagMaintenanceDecision decision);
    string Read(TagMaintenanceChange change);
    void Write(TagMaintenanceChange change);
}

/// <summary>Uses the existing application writers; no maintenance-specific task or Dossier storage.</summary>
public sealed class TagMaintenanceWorkspace(TaskScannerService scanner, TaskMutationService tasks,
    TagRegistryService tags, AreaRegistryService areas, AreaGlossaryService glossaries,
    WorkbenchCatalogueService dossiers, WorkbenchTagService dossierTags, ProjectDocsService docs)
    : ITagMaintenanceWorkspace
{
    public IReadOnlyList<string> Projects() => scanner.GetWatchPaths().Select(p => p.Name)
        .Distinct(StringComparer.Ordinal).ToArray();

    public TagMaintenanceSnapshot Capture(string project)
    {
        if (!Projects().Contains(project)) throw new ArgumentException("Unknown project.");
        var items = new List<TagMaintenanceItem>();
        foreach (var task in scanner.ScanAllJobsWithArchive())
            items.Add(new(task.ProjectName, "card", task.Id, task.Title, [.. task.Tags ?? []],
                task.ProjectName == project ? scanner.ReadJobFile(task.Id, "prompt.md", task.WatchPath) ?? "" : "",
                !task.Fixture && !task.Id.StartsWith("tag-maintenance-", StringComparison.Ordinal) && task.State != TaskStates.Archive && task.State != TaskStates.Completed));
        var areaIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in Projects())
        {
            areaIds.UnionWith(areas.List(name).Select(area => area.Id));
            var catalogue = dossiers.List(name, includeHistory: true)
                ?? throw new InvalidOperationException($"Dossier inventory unavailable for {name}.");
            var entryPaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var dossier in catalogue.Items)
            {
                if (!dossier.Valid) throw new InvalidOperationException($"Invalid Dossier: {name}/{dossier.Id}.");
                entryPaths.Add(dossier.EntryPath.StartsWith("docs/") ? dossier.EntryPath[5..] : dossier.EntryPath);
                items.Add(new(name, "dossier", dossier.Id, dossier.Title, dossier.Tags,
                    name == project ? TagMaintenancePolicy.Encode(dossier.Decision) + "\n" +
                        (dossiers.Read(name, dossier.Id)?.Html
                        ?? throw new InvalidOperationException("Dossier content unavailable.")) : "",
                    dossier.LifecycleState != "archived" && dossier.Status != "archived",
                    dossier.Decision?.ConfirmedAt != null));
            }
            docs.InvalidateWikiContent(name);
            var tree = docs.GetWikiTree(name)
                ?? throw new InvalidOperationException($"Wiki inventory unavailable for {name}.");
            void Visit(WikiTreeNode node)
            {
                if (node.Type != "folder" && node.RelPath is { } path && !entryPaths.Contains(path))
                {
                    var file = docs.ReadWikiFile(name, path)
                        ?? throw new InvalidOperationException($"Wiki content unavailable: {name}/{path}.");
                    items.Add(new(name, "wiki", path, node.Title, ProjectDocsService.FrontmatterTags(file.Content),
                        name == project ? file.Content : "", !path.StartsWith("archive/", StringComparison.Ordinal),
                        node.Classification?.Type == "adr" || path.Contains("/decisions/", StringComparison.Ordinal)));
                }
                foreach (var child in node.Children) Visit(child);
            }
            foreach (var node in tree.Root) Visit(node);
        }
        var glossary = areas.List(project).ToDictionary(area => area.Id, area =>
        {
            var result = glossaries.Read(project, area.Id);
            if (!result.Success) throw new InvalidOperationException(result.Error);
            return result.Glossary!.Terms.ToList();
        });
        return new(tags.GetAll().ToList(), areaIds.Order().ToList(), glossary,
            items.OrderBy(i => i.Project).ThenBy(i => i.Kind).ThenBy(i => i.Id).ToList());
    }

    public string CreateCard(string project, TagMaintenanceDecision decision)
    {
        var watch = scanner.GetWatchPaths().Single(p => p.Name == project);
        var id = "tag-maintenance-" + decision.Id;
        if (scanner.FindJob(id, watch.Path) != null) return id;
        return tasks.CreateJob(new CreateTaskRequest
        {
            Id = id, WatchPath = watch.Path, Title = $"Tag maintenance: {decision.Proposal.Kind} {decision.Proposal.Source}{decision.Proposal.Target}",
            TargetState = TaskStates.Backlog, PromptMarkdown = TagMaintenancePolicy.Card(project, decision),
            CreationSource = "tag-maintenance", CreatedBy = "Tag maintenance", Tags = ["decision"],
        }) ?? throw new InvalidOperationException("Decision card creation failed.");
    }

    public string Read(TagMaintenanceChange change)
    {
        switch (change.Kind)
        {
            case "registry":
                return TagMaintenancePolicy.Encode(tags.GetAll().SingleOrDefault(t => t.Id == change.Id));
            case "glossary":
                var glossary = glossaries.Read(change.Project, change.Id);
                if (!glossary.Success) throw new InvalidOperationException(glossary.Error);
                return TagMaintenancePolicy.Encode(glossary.Glossary!.Terms);
            case "card":
                var watch = scanner.GetWatchPaths().Single(p => p.Name == change.Project);
                var task = scanner.FindJob(change.Id, watch.Path)
                    ?? throw new InvalidOperationException("Affected card no longer exists.");
                return TagMaintenancePolicy.Encode(task.Tags?.ToArray() ?? []);
            case "dossier":
                var dossier = dossiers.Read(change.Project, change.Id)
                    ?? throw new InvalidOperationException("Affected Dossier no longer exists.");
                return TagMaintenancePolicy.Encode(dossier.Workbench.Tags);
            case "wiki":
                var file = docs.ReadWikiFile(change.Project, change.Id)
                    ?? throw new InvalidOperationException("Affected wiki article no longer exists.");
                return TagMaintenancePolicy.Encode(ProjectDocsService.FrontmatterTags(file.Content));
            default: throw new ArgumentException("Unknown change kind.");
        }
    }

    public void Write(TagMaintenanceChange change)
    {
        var current = Read(change);
        if (current == change.After) return;
        if (current != change.Before) throw new InvalidOperationException($"Stale {change.Kind}/{change.Id}.");
        switch (change.Kind)
        {
            case "registry":
                if (change.After == "null")
                {
                    // Check all projects again immediately before removing the shared handle.
                    var snapshot = Capture(change.Project);
                    if (snapshot.AreaIds.Contains(change.Id) || snapshot.Items.Any(i => i.Tags.Contains(change.Id)))
                        throw new InvalidOperationException("Tag gained a reference or became an area; registry removal blocked.");
                    if (!tags.Delete(change.Id, requireDurable: true)) throw new InvalidOperationException("Tag deletion failed.");
                }
                else
                {
                    var tag = JsonSerializer.Deserialize<TagRegistryEntry>(change.After, TagMaintenancePolicy.Json)!;
                    tags.Create(tag.Id, tag.Label, tag.Color, tag.Description, tag.Kind, requireDurable: true);
                }
                break;
            case "glossary":
                var glossary = glossaries.Write(change.Project, change.Id, new()
                {
                    Terms = JsonSerializer.Deserialize<List<GlossaryTerm>>(change.After, TagMaintenancePolicy.Json)!,
                });
                if (!glossary.Success) throw new InvalidOperationException(glossary.Error);
                docs.InvalidateWikiContent(change.Project);
                break;
            default:
                var ids = JsonSerializer.Deserialize<string[]>(change.After, TagMaintenancePolicy.Json)!;
                if (!areas.ValidateTags(change.Project, ids).Ok) throw new InvalidOperationException("Target tag no longer exists.");
                if (change.Kind == "card")
                {
                    var watch = scanner.GetWatchPaths().Single(p => p.Name == change.Project);
                    if (!tasks.SetJobTags(change.Id, ids, watch.Path)) throw new InvalidOperationException("Card tag write failed.");
                }
                else if (change.Kind == "dossier")
                {
                    var result = dossierTags.Set(change.Project, change.Id, new() { Tags = ids.ToList() });
                    if (!result.Success) throw new InvalidOperationException(result.Error);
                    docs.InvalidateWikiContent(change.Project);
                }
                else
                {
                    var file = docs.ReadWikiFile(change.Project, change.Id)!;
                    var result = docs.WriteWikiFile(change.Project, change.Id, RewriteFrontmatter(file.Content, ids));
                    if (!result.Success) throw new InvalidOperationException(result.Error);
                }
                break;
        }
        if (Read(change) != change.After) throw new InvalidOperationException($"Write verification failed: {change.Kind}/{change.Id}.");
    }

    public static string RewriteFrontmatter(string content, string[] tags)
    {
        var match = Regex.Match(content, @"\A---\r?\n(?<body>.*?)\r?\n---(?:\r?\n|$)", RegexOptions.Singleline);
        if (!match.Success) throw new InvalidOperationException("Tagged article has no supported front matter.");
        var body = match.Groups["body"].Value;
        var replaced = Regex.Replace(body, @"(?im)^tags:[^\r\n]*(?:\r?\n[ \t]*-[^\r\n]*)*",
            "tags: [" + string.Join(", ", tags) + "]");
        if (replaced == body && !Regex.IsMatch(body, @"(?im)^tags:"))
            throw new InvalidOperationException("Article tag field disappeared.");
        return content[..match.Groups["body"].Index] + replaced
            + content[(match.Groups["body"].Index + match.Groups["body"].Length)..];
    }
}
