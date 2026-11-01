using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;

namespace AgentStudio.Proposals;

public sealed record ProjectProposal(
    string Id,
    string Generation,
    string Finding,
    string EvidenceScreenshot,
    string Proposal,
    string EstimatedEffort,
    string Severity,
    string Status,
    string? SpawnedTask,
    string Topic,
    IReadOnlyList<string> Categories,
    string Source,
    string? RejectionReason,
    string? RejectionReasonRaw,
    string RelPath,
    DateTime UpdatedAt);

public sealed record ProposalDecisionResult(ProjectProposal Proposal, string? SpawnedTask);

/// <summary>
/// Durable proposal catalogue backed by structured markdown in docs/concepts/proposals.
/// Generations are append-only; decisions update only status and spawnedTask.
/// </summary>
public sealed class ProjectProposalService
{
    private const string ProposalsRel = "docs/" + WikiProducerTargets.ProposalsFolder;
    private static readonly string[] AllowedStatuses = ["proposed", "approved", "rejected", "spawned"];
    private readonly TaskScannerService _scanner;
    private readonly ProjectRegistry _registry;
    private readonly TaskMutationService _mutations;
    private readonly ILogger<ProjectProposalService> _logger;
    private readonly ProjectProposalDraftingService? _drafting;
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> _evidenceByRoot =
        new(StringComparer.OrdinalIgnoreCase);

    public ProjectProposalService(TaskScannerService scanner, ProjectRegistry registry,
        TaskMutationService mutations, ILogger<ProjectProposalService> logger,
        ProjectProposalDraftingService? drafting = null)
    {
        _scanner = scanner;
        _registry = registry;
        _mutations = mutations;
        _logger = logger;
        _drafting = drafting;
    }

    public IReadOnlyList<ProjectProposal>? List(string projectName)
    {
        var root = ResolveRoot(projectName);
        if (root == null) return null;
        var dir = Path.Combine(root, ProposalsRel);
        if (!Directory.Exists(dir)) return [];
        var items = Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories)
            .Select(path => Parse(path, dir))
            .Where(p => p != null)
            .Cast<ProjectProposal>()
            .OrderByDescending(p => p.Generation, StringComparer.Ordinal)
            .ThenBy(p => SeverityRank(p.Severity))
            .ThenBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _evidenceByRoot[root] = items
            .Where(p => !string.IsNullOrWhiteSpace(p.EvidenceScreenshot))
            .GroupBy(p => NormalizeEvidencePath(p.EvidenceScreenshot), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => ResolveEvidencePath(root, group.First().EvidenceScreenshot), StringComparer.OrdinalIgnoreCase);
        return items;
    }

    /// <summary>Largest evidence screenshot the proposals screen serves.</summary>
    internal const long MaxEvidenceBytes = 16L * 1024 * 1024;

    /// <summary>
    /// Resolves evidence from the catalogue index produced by <see cref="List"/>.
    /// The proposals screen always loads that catalogue before requesting images,
    /// so image requests remain O(1) instead of reparsing every proposal per image.
    /// A direct image request lazily warms the same index once. Only a regular
    /// checkout file of at most <see cref="MaxEvidenceBytes"/> is returned.
    /// </summary>
    public string? GetEvidencePath(string projectName, string relPath)
    {
        var root = ResolveRoot(projectName);
        if (root == null) return null;
        var key = NormalizeEvidencePath(relPath);
        if (!_evidenceByRoot.TryGetValue(root, out var evidence) || !evidence.TryGetValue(key, out var full))
        {
            List(projectName);
            if (!_evidenceByRoot.TryGetValue(root, out evidence) || !evidence.TryGetValue(key, out full))
                return null;
        }
        return FilesystemPathBoundary.CheckCheckoutFile(root, full, MaxEvidenceBytes) == CheckoutFileStatus.Readable
            ? full
            : null;
    }

    public ProjectProposal? Get(string projectName, string id) =>
        List(projectName)?.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    public ProposalDecisionResult? Decide(string projectName, string id, string decision,
        string? rejectionReason = null, string? rejectionReasonRaw = null)
    {
        var started = Stopwatch.GetTimestamp();
        var root = ResolveRoot(projectName);
        var proposal = Get(projectName, id);
        if (root == null || proposal == null) return null;
        if (decision is not ("approve" or "reject")) throw new ArgumentException("Decision must be approve or reject.");

        string? spawnedTask = proposal.SpawnedTask;
        var status = decision == "reject" ? "rejected" : "approved";
        if (decision == "approve" && proposal.Status != "spawned")
        {
            var watchPath = _scanner.GetWatchPaths().FirstOrDefault(entry =>
                string.Equals(entry.Name, projectName, StringComparison.OrdinalIgnoreCase))?.Path;
            if (string.IsNullOrWhiteSpace(watchPath))
                throw new InvalidOperationException("Proposal project has no task storage path.");
            var prompt = BuildImplementationPrompt(proposal);
            var createdId = _mutations.CreateJob(new CreateTaskRequest
            {
                Id = $"proposal-{proposal.Id}",
                Title = $"[{proposal.Topic}] {proposal.Proposal}",
                WatchPath = watchPath,
                TargetState = TaskStates.Backlog,
                Agent = "codex",
                CliType = "codex",
                Mode = TaskModes.Coding,
                TaskType = TaskTypes.Feature,
                PromptMarkdown = prompt,
            });
            if (createdId == null) throw new InvalidOperationException("Implementation task could not be created.");
            spawnedTask = _scanner.ScanAllJobs()
                .FirstOrDefault(t => string.Equals(t.Id, createdId, StringComparison.OrdinalIgnoreCase))?.TaskKey
                ?? createdId;
            status = "spawned";
        }

        var updated = proposal with
        {
            Status = status,
            SpawnedTask = spawnedTask,
            RejectionReason = decision == "reject" ? NullIfBlank(rejectionReason) : proposal.RejectionReason,
            RejectionReasonRaw = decision == "reject" ? NullIfBlank(rejectionReasonRaw) : proposal.RejectionReasonRaw,
            UpdatedAt = DateTime.UtcNow,
        };
        Write(root, updated);
        _logger.LogInformation(
            "project-proposal-decision project={Project} proposalId={ProposalId} decision={Decision} status={Status} spawnedTask={SpawnedTask} elapsedMs={ElapsedMs}",
            projectName, id, decision, status, spawnedTask, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return new ProposalDecisionResult(updated, spawnedTask);
    }

    public async Task<ProjectProposal> GenerateAsync(string projectName, string topic, string guidance, CancellationToken ct)
    {
        var root = ResolveRoot(projectName) ?? throw new KeyNotFoundException("Project not found.");
        if (_drafting == null) throw new InvalidOperationException("Proposal drafting is not available.");
        var normalizedTopic = topic.Trim();
        if (normalizedTopic.Length == 0) throw new ArgumentException("Topic is required.");
        var draft = await _drafting.GenerateAsync(root, normalizedTopic, guidance, ct);
        var generation = DateTime.UtcNow.ToString("yyyy-MM-dd-HHmmssfff");
        var slug = Slug(normalizedTopic);
        var id = $"operator-{generation}-{slug}";
        var proposal = new ProjectProposal(id, generation, draft.Finding, "", draft.Proposal,
            draft.EstimatedEffort, draft.Severity, "proposed", null, normalizedTopic,
            draft.Categories.Count > 0 ? draft.Categories : [slug], $"Operator request: {normalizedTopic}",
            null, null, $"{generation}/{id}.md", DateTime.UtcNow);
        Write(root, proposal);
        return proposal;
    }

    public async Task<string> RefineFeedbackAsync(string feedback, CancellationToken ct)
    {
        if (_drafting == null) throw new InvalidOperationException("Proposal drafting is not available.");
        return await _drafting.RefineFeedbackAsync(feedback, ct);
    }

    public bool Remove(string projectName, string id)
    {
        var root = ResolveRoot(projectName);
        var proposal = Get(projectName, id);
        if (root == null || proposal == null) return false;
        DeleteProposalFiles(root, proposal);
        _evidenceByRoot.TryRemove(root, out _);
        return true;
    }

    public int RemoveOlderGenerations(string projectName, string keepGeneration)
    {
        var root = ResolveRoot(projectName) ?? throw new KeyNotFoundException("Project not found.");
        var older = List(projectName)!.Where(item =>
            string.Compare(item.Generation, keepGeneration, StringComparison.Ordinal) < 0).ToList();
        foreach (var proposal in older) DeleteProposalFiles(root, proposal);
        _evidenceByRoot.TryRemove(root, out _);
        return older.Count;
    }

    private string? ResolveRoot(string projectName) => ProjectRepoResolver.ResolveForProject(projectName, _scanner, _registry);

    private static string BuildImplementationPrompt(ProjectProposal p) => $"""
        # Implement approved project proposal {p.Id}

        Source document: `docs/concepts/proposals/{p.RelPath}`

        ## Finding

        {p.Finding}

        ## Provenance

        Topic: {p.Topic}
        Categories: {string.Join(", ", p.Categories)}
        Source: {p.Source}

        ## Evidence

        `{p.EvidenceScreenshot}`

        ## Approved proposal

        {p.Proposal}

        Estimated effort: {p.EstimatedEffort}
        Severity: {p.Severity}

        Implement the approved change, add proportionate regression coverage, and preserve the proposal document as history.
        """;

    private static void Write(string root, ProjectProposal p)
    {
        var full = Path.GetFullPath(Path.Combine(root, ProposalsRel, p.RelPath));
        var dir = Path.GetFullPath(Path.Combine(root, ProposalsRel));
        if (!full.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Proposal path escaped docs/concepts/proposals.");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, Render(p));
    }

    internal static string Render(ProjectProposal p) => $"""
        ---
        id: {Quote(p.Id)}
        generation: {Quote(p.Generation)}
        finding: {Quote(p.Finding)}
        evidenceScreenshot: {Quote(p.EvidenceScreenshot)}
        proposal: {Quote(p.Proposal)}
        estimatedEffort: {Quote(p.EstimatedEffort)}
        severity: {Quote(p.Severity)}
        status: {Quote(p.Status)}
        spawnedTask: {(p.SpawnedTask == null ? "null" : Quote(p.SpawnedTask))}
        topic: {Quote(p.Topic)}
        categories: {Quote(string.Join(",", p.Categories))}
        source: {Quote(p.Source)}
        rejectionReason: {(p.RejectionReason == null ? "null" : Quote(p.RejectionReason))}
        rejectionReasonRaw: {(p.RejectionReasonRaw == null ? "null" : Quote(p.RejectionReasonRaw))}
        ---

        # {p.Proposal}

        ## Finding

        {p.Finding}

        ## Source

        {p.Source}

        {(string.IsNullOrWhiteSpace(p.EvidenceScreenshot) ? "" : $"## Evidence\n\n![Evidence screenshot]({EvidenceLink(p)})")}

        ## Proposal

        {p.Proposal}

        Estimated effort: **{p.EstimatedEffort}**  
        Severity: **{p.Severity}**

        {(p.RejectionReason == null ? "" : $"## Rejection feedback\n\n{p.RejectionReason}\n\n<details><summary>Original operator feedback</summary>\n\n{p.RejectionReasonRaw ?? p.RejectionReason}\n\n</details>")}
        """;

    private static void DeleteProposalFiles(string root, ProjectProposal proposal)
    {
        var proposalsRoot = Path.GetFullPath(Path.Combine(root, ProposalsRel));
        var document = Path.GetFullPath(Path.Combine(proposalsRoot, proposal.RelPath));
        if (!document.StartsWith(proposalsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Proposal path escaped docs/concepts/proposals.");
        if (File.Exists(document)) File.Delete(document);
        if (!string.IsNullOrWhiteSpace(proposal.EvidenceScreenshot))
        {
            var evidence = ResolveEvidencePath(root, proposal.EvidenceScreenshot);
            var referencedElsewhere = Directory.EnumerateFiles(proposalsRoot, "*.md", SearchOption.AllDirectories)
                .Any(path => File.ReadAllText(path).Contains(proposal.EvidenceScreenshot, StringComparison.OrdinalIgnoreCase));
            if (!referencedElsewhere && File.Exists(evidence)) File.Delete(evidence);
        }
    }

    private static string EvidenceLink(ProjectProposal p)
    {
        var documentDir = Path.GetDirectoryName(p.RelPath.Replace('/', Path.DirectorySeparatorChar)) ?? "";
        return Path.GetRelativePath(documentDir, p.EvidenceScreenshot.Replace('/', Path.DirectorySeparatorChar)).Replace('\\', '/');
    }

    private static string NormalizeEvidencePath(string value) =>
        value.Replace('\\', '/').Trim().TrimStart('/');

    private static string ResolveEvidencePath(string root, string relPath)
    {
        var proposalsRoot = Path.GetFullPath(Path.Combine(root, ProposalsRel));
        var full = Path.GetFullPath(Path.Combine(proposalsRoot, relPath));
        if (!full.StartsWith(proposalsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Proposal evidence path escaped docs/concepts/proposals.");
        return full;
    }

    private static string Quote(string value) => JsonSerializer.Serialize(value);

    private static ProjectProposal? Parse(string path, string root)
    {
        try
        {
            var text = File.ReadAllText(path);
            if (!text.StartsWith("---", StringComparison.Ordinal)) return null;
            var end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (end < 0) return null;
            var values = text[3..end].Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => (line, colon: line.IndexOf(':')))
                .Where(x => x.colon > 0)
                .ToDictionary(x => x.line[..x.colon].Trim(), x => ParseValue(x.line[(x.colon + 1)..].Trim()), StringComparer.OrdinalIgnoreCase);
            string V(string key) => values.TryGetValue(key, out var value) ? value ?? "" : "";
            var status = V("status");
            if (!AllowedStatuses.Contains(status, StringComparer.OrdinalIgnoreCase)) return null;
            var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
            var finding = V("finding");
            var proposal = V("proposal");
            var topic = V("topic");
            if (string.IsNullOrWhiteSpace(topic)) topic = InferTopic($"{proposal} {finding} {V("id")}");
            var categories = V("categories").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (categories.Length == 0) categories = [Slug(topic)];
            return new ProjectProposal(V("id"), V("generation"), finding, V("evidenceScreenshot"),
                proposal, V("estimatedEffort"), V("severity"), status,
                values.TryGetValue("spawnedTask", out var task) ? task : null, topic, categories,
                string.IsNullOrWhiteSpace(V("source")) ? $"Visual survey: {V("generation")}" : V("source"),
                values.TryGetValue("rejectionReason", out var reason) ? reason : null,
                values.TryGetValue("rejectionReasonRaw", out var raw) ? raw : null,
                rel, File.GetLastWriteTimeUtc(path));
        }
        catch { return null; }
    }

    private static string? ParseValue(string value)
    {
        if (value == "null") return null;
        return value.StartsWith('"') ? JsonSerializer.Deserialize<string>(value) : value;
    }

    private static int SeverityRank(string severity) => severity.ToLowerInvariant() switch
    {
        "critical" => 0,
        "medium" => 1,
        _ => 2,
    };

    private static string InferTopic(string text)
    {
        var value = text.ToLowerInvariant();
        if (value.Contains("responsive") || value.Contains("narrow") || value.Contains("mobile") || value.Contains("430 px") || value.Contains("overflow")) return "Responsiveness";
        if (value.Contains("accessib") || value.Contains("keyboard") || value.Contains("screen reader")) return "Accessibility";
        if (value.Contains("performance") || value.Contains("loading") || value.Contains("latency")) return "Performance";
        if (value.Contains("security") || value.Contains("permission")) return "Security";
        if (value.Contains("navigation") || value.Contains("explorer")) return "Navigation";
        if (value.Contains("test")) return "Test quality";
        return "Product quality";
    }

    private static string Slug(string value)
    {
        var normalized = new string(value.ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray());
        var slug = string.Join('-', normalized.Split('-', StringSplitOptions.RemoveEmptyEntries).Take(8));
        return slug.Length == 0 ? "proposal" : slug;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
