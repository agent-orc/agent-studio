using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentStudio.Tags;

public interface ITagMaintenanceWorkspace
{
    IReadOnlyList<string> Projects();
    TagMaintenanceSnapshot Capture(string project);
    TagMaintenanceSnapshot CaptureForClassification(string project) => Capture(project);
    string CreateCard(string project, TagMaintenanceDecision decision);
    string Read(TagMaintenanceChange change);
    bool Write(TagMaintenanceChange change, string? taggingStatus = null);
}

/// <summary>Uses the existing application writers; no maintenance-specific task or Dossier storage.</summary>
public sealed class TagMaintenanceWorkspace(TaskScannerService scanner, TaskMutationService tasks,
    TagRegistryService tags, AreaRegistryService areas, AreaGlossaryService glossaries,
    WorkbenchCatalogueService dossiers, WorkbenchTagService dossierTags, ProjectDocsService docs,
    ProjectRegistry registry, ManagedRepositoryMutationService repositoryMutations)
    : ITagMaintenanceWorkspace
{
    public IReadOnlyList<string> Projects() => scanner.GetWatchPaths().Select(p => p.Name)
        .Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>
    /// Workspace-wide capture for maintenance. Invalid Dossiers and unavailable inventories are skipped and
    /// listed in <see cref="TagMaintenanceSnapshot.Unreadable"/>, which marks usage counts incomplete.
    /// A project without a configured repository checkout has no Dossier, wiki or glossary files that could
    /// carry a tag; it is listed in <see cref="TagMaintenanceSnapshot.WithoutCheckout"/> and is not unreadable.
    /// A configured checkout that is missing on disk is unreadable.
    /// </summary>
    public TagMaintenanceSnapshot Capture(string project) => CaptureCore(project, includeOtherProjects: true);

    /// <summary>Project-only capture for classification; unavailable content is recorded and skipped.</summary>
    public TagMaintenanceSnapshot CaptureForClassification(string project) =>
        CaptureCore(project, includeOtherProjects: false);

    private TagMaintenanceSnapshot CaptureCore(string project, bool includeOtherProjects)
    {
        if (!Projects().Contains(project)) throw new ArgumentException("Unknown project.");
        var items = new List<TagMaintenanceItem>();
        var unreadable = new List<TagMaintenanceUnreadableItem>();
        var checkouts = (includeOtherProjects ? Projects() : [project])
            .ToDictionary(name => name, name => ProjectRepoResolver.ResolveForProject(name, scanner, registry));
        var withoutCheckout = checkouts.Where(pair => pair.Value == null).Select(pair => pair.Key)
            .ToHashSet(StringComparer.Ordinal);
        void Unreadable(string name, string kind, string id, string error)
        {
            unreadable.Add(new(name, kind, id, error));
        }
        foreach (var task in scanner.ScanAllJobsWithArchive().Where(task =>
            includeOtherProjects || task.ProjectName == project))
            items.Add(new(task.ProjectName, "card", task.Id, task.Title, [.. task.Tags ?? []],
                task.ProjectName == project ? scanner.ReadJobFile(task.Id, "prompt.md", task.WatchPath) ?? "" : "",
                !task.Fixture && !task.Id.StartsWith("tag-maintenance-", StringComparison.Ordinal) && task.State != TaskStates.Archive && task.State != TaskStates.Completed));
        var areaIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in includeOtherProjects ? Projects() : [project])
        {
            areaIds.UnionWith(areas.List(name).Select(area => area.Id));
            var entryPaths = new HashSet<string>(StringComparer.Ordinal);
            if (checkouts.GetValueOrDefault(name) is { } root && !Directory.Exists(root))
            {
                // An empty listing of a missing checkout would otherwise look like zero references.
                Unreadable(name, "checkout", "", $"Repository checkout missing for {name}.");
                continue;
            }
            var catalogue = dossiers.List(name, includeHistory: true);
            if (catalogue == null && !withoutCheckout.Contains(name))
                Unreadable(name, "dossier-inventory", "", $"Dossier inventory unavailable for {name}.");
            foreach (var dossier in catalogue?.Items ?? [])
            {
                if (!dossier.Valid)
                {
                    // Classification skips invalid Dossiers silently; maintenance reports them.
                    if (includeOtherProjects)
                        unreadable.Add(new(name, "dossier", dossier.Id, dossier.Error ?? $"Invalid Dossier: {name}/{dossier.Id}."));
                    continue;
                }
                entryPaths.Add(dossier.EntryPath.StartsWith("docs/") ? dossier.EntryPath[5..] : dossier.EntryPath);
                var html = name == project ? dossiers.Read(name, dossier.Id)?.Html : "";
                if (html == null)
                {
                    Unreadable(name, "dossier", dossier.Id, "Dossier content unavailable.");
                    continue;
                }
                items.Add(new(name, "dossier", dossier.Id, dossier.Title, dossier.Tags,
                    name == project ? TagMaintenancePolicy.Encode(dossier.Decision) + "\n" + html : "",
                    dossier.LifecycleState != "archived" && dossier.Status != "archived",
                    dossier.Decision?.ConfirmedAt != null));
            }
            docs.InvalidateWikiContent(name);
            var tree = docs.GetWikiTree(name);
            if (tree == null)
            {
                if (!withoutCheckout.Contains(name))
                    Unreadable(name, "wiki-inventory", "", $"Wiki inventory unavailable for {name}.");
                continue;
            }
            void Visit(WikiTreeNode node)
            {
                if (node.Type != "folder" && node.RelPath is { } path && !entryPaths.Contains(path))
                {
                    var file = docs.ReadWikiFile(name, path);
                    if (file == null) Unreadable(name, "wiki", path, $"Wiki content unavailable: {name}/{path}.");
                    else items.Add(new(name, "wiki", path, node.Title, ProjectDocsService.FrontmatterTags(file.Content),
                        name == project ? file.Content : "", !path.StartsWith("archive/", StringComparison.Ordinal)
                            && !string.Equals(node.Classification?.Status, "archived", StringComparison.OrdinalIgnoreCase)
                            && (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                                || path.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                                || path.EndsWith(".htm", StringComparison.OrdinalIgnoreCase)),
                        node.Classification?.Type == "adr" || path.Contains("/decisions/", StringComparison.Ordinal)));
                }
                foreach (var child in node.Children) Visit(child);
            }
            foreach (var node in tree.Root) Visit(node);
        }
        // Without a checkout there is no glossary page to read or write, so no proposal can be planned.
        var glossary = withoutCheckout.Contains(project) ||
            (!includeOtherProjects && unreadable.Any(item => item.Project == project && item.Kind == "checkout"))
            ? [] : areas.List(project).ToDictionary(area => area.Id, area =>
        {
            var result = glossaries.Read(project, area.Id);
            if (!result.Success) throw new InvalidOperationException(result.Error);
            return result.Glossary!.Terms.ToList();
        });
        return new(tags.GetAll().ToList(), areaIds.Order().ToList(), glossary,
            items.OrderBy(i => i.Project).ThenBy(i => i.Kind).ThenBy(i => i.Id).ToList(),
            unreadable.OrderBy(i => i.Project).ThenBy(i => i.Kind).ThenBy(i => i.Id).ToList())
        {
            WithoutCheckout = withoutCheckout.Order(StringComparer.Ordinal).ToList(),
        };
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
                var root = ProjectRepoResolver.ResolveForProject(change.Project, scanner, registry);
                if (root == null || !Directory.Exists(root))
                    throw new WikiTagWriteUnavailableException("Project checkout is unavailable.");
                var file = docs.ReadWikiFile(change.Project, change.Id)
                    ?? throw new WikiTagWriteUnavailableException(
                        docs.WikiWriteBlockReason(change.Project)
                        ?? "Affected wiki article is unavailable in the checkout.");
                return TagMaintenancePolicy.Encode(ProjectDocsService.FrontmatterTags(file.Content));
            default: throw new ArgumentException("Unknown change kind.");
        }
    }

    public bool Write(TagMaintenanceChange change, string? taggingStatus = null)
    {
        if (taggingStatus is not (null or "tagged" or "tags-proposed"))
            throw new ArgumentException("Unknown tagging status.");
        if (taggingStatus != null && change.Kind is not ("card" or "dossier" or "wiki"))
            throw new ArgumentException("Tagging status is only supported for classified items.");
        var current = Read(change);
        if (current == change.After && taggingStatus == null) return false;
        if (current != change.Before) throw new InvalidOperationException($"Stale {change.Kind}/{change.Id}.");
        switch (change.Kind)
        {
            case "registry":
                if (change.After == "null")
                {
                    // Check all projects again immediately before removing the shared handle.
                    var snapshot = Capture(change.Project);
                    if (!snapshot.UsageComplete)
                        throw new InvalidOperationException("The inventory has unreadable items; registry removal blocked.");
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
                    if (!tasks.SetJobTags(change.Id, ids, watch.Path, taggingStatus)) throw new InvalidOperationException("Card tag write failed.");
                }
                else if (change.Kind == "dossier")
                {
                    var result = dossierTags.Set(change.Project, change.Id, new() { Tags = ids.ToList(), TaggingStatus = taggingStatus });
                    if (!result.Success) throw new InvalidOperationException(result.Error);
                    docs.InvalidateWikiContent(change.Project);
                }
                else
                {
                    if (taggingStatus == "tags-proposed") return false;
                    if (docs.WikiWriteBlockReason(change.Project) is { } blocked)
                        throw new WikiTagWriteUnavailableException(blocked);
                    var root = ProjectRepoResolver.ResolveForProject(change.Project, scanner, registry);
                    if (root == null || !Directory.Exists(root))
                        throw new WikiTagWriteUnavailableException("Project checkout is unavailable.");
                    var relPath = "docs/" + change.Id.Replace('\\', '/');
                    var mutation = repositoryMutations.Execute(change.Project, root,
                        $"wiki-tags-{TagMaintenancePolicy.Fingerprint(relPath)}",
                        $"chore(wiki): set tags for {relPath}", [relPath], () =>
                        {
                            var file = docs.ReadWikiFile(change.Project, change.Id)
                                ?? throw new InvalidOperationException("Affected wiki article no longer exists.");
                            if (TagMaintenancePolicy.Encode(ProjectDocsService.FrontmatterTags(file.Content)) != change.Before)
                                throw new InvalidOperationException($"Stale wiki/{change.Id}.");
                            var updated = RewriteFrontmatter(file.Content, ids);
                            if (taggingStatus == "tagged") updated = RewriteTaggingStatus(updated, taggingStatus);
                            var saved = docs.WriteWikiFile(change.Project, change.Id, updated);
                            if (!saved.Success) throw new InvalidOperationException(saved.Error);
                        }, requireCleanCheckoutAndPush: true);
                    if (!mutation.Success)
                    {
                        if (mutation.PreflightBlocked)
                            throw new WikiTagWriteUnavailableException(mutation.Error ?? "Wiki tag commit is unavailable.");
                        throw new InvalidOperationException(mutation.Error ?? "Wiki tag commit failed.");
                    }
                }
                break;
        }
        return true;
    }

    public static string RewriteFrontmatter(string content, string[] tags)
    {
        var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        if (Regex.IsMatch(content, @"<head(?:\s[^>]*)?>", RegexOptions.IgnoreCase))
        {
            var meta = "<meta name=\"agent-studio-tags\" content=\"" + string.Join(", ", tags) + "\">";
            var pattern = "<meta\\s+name=[\"']agent-studio-tags[\"']\\s+content=[\"'][^\"']*[\"']\\s*/?>";
            return Regex.IsMatch(content, pattern, RegexOptions.IgnoreCase)
                ? Regex.Replace(content, pattern, meta, RegexOptions.IgnoreCase)
                : Regex.Replace(content, @"<head(?:\s[^>]*)?>", match => match.Value + newline + "  " + meta,
                    RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        }
        if (Regex.IsMatch(content, @"\A\s*(?:<!doctype\s+html|<html\b)", RegexOptions.IgnoreCase))
            throw new InvalidOperationException("HTML article has no head for tag metadata.");
        var match = Regex.Match(content, @"\A---\r?\n(?<body>.*?)\r?\n---(?:\r?\n|$)", RegexOptions.Singleline);
        if (!match.Success) return "---" + newline + "tags: [" + string.Join(", ", tags) + "]"
            + newline + "---" + newline + newline + content;
        var body = match.Groups["body"].Value;
        var replaced = Regex.Replace(body, @"(?im)^tags:[^\r\n]*(?:\r?\n[ \t]*-[^\r\n]*)*",
            "tags: [" + string.Join(", ", tags) + "]");
        if (replaced == body && !Regex.IsMatch(body, @"(?im)^tags:"))
            replaced = body + newline + "tags: [" + string.Join(", ", tags) + "]";
        return content[..match.Groups["body"].Index] + replaced
            + content[(match.Groups["body"].Index + match.Groups["body"].Length)..];
    }

    public static string RewriteTaggingStatus(string content, string status)
    {
        if (status is not ("tagged" or "tags-proposed")) throw new ArgumentException("Unknown tagging status.");
        var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        if (Regex.IsMatch(content, @"<head(?:\s[^>]*)?>", RegexOptions.IgnoreCase))
        {
            var meta = "<meta name=\"agent-studio-tagging-status\" content=\"" + status + "\">";
            var pattern = "<meta\\s+name=[\"']agent-studio-tagging-status[\"']\\s+content=[\"'][^\"']*[\"']\\s*/?>";
            return Regex.IsMatch(content, pattern, RegexOptions.IgnoreCase)
                ? Regex.Replace(content, pattern, meta, RegexOptions.IgnoreCase)
                : Regex.Replace(content, @"<head(?:\s[^>]*)?>", match => match.Value + newline + "  " + meta,
                    RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        }
        if (Regex.IsMatch(content, @"\A\s*(?:<!doctype\s+html|<html\b)", RegexOptions.IgnoreCase))
            throw new InvalidOperationException("HTML article has no head for tag metadata.");
        var match = Regex.Match(content, @"\A---\r?\n(?<body>.*?)\r?\n---(?:\r?\n|$)", RegexOptions.Singleline);
        if (!match.Success) return "---" + newline + "taggingStatus: " + status
            + newline + "---" + newline + newline + content;
        var body = match.Groups["body"].Value;
        var line = "taggingStatus: " + status;
        var replaced = Regex.IsMatch(body, @"(?im)^taggingStatus:")
            ? Regex.Replace(body, @"(?im)^taggingStatus:[^\r\n]*", line)
            : body + newline + line;
        return content[..match.Groups["body"].Index] + replaced
            + content[(match.Groups["body"].Index + match.Groups["body"].Length)..];
    }
}

public sealed class WikiTagWriteUnavailableException(string reason) : InvalidOperationException(reason);
