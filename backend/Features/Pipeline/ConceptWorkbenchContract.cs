using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentStudio.Pipeline;

public sealed record ConceptWorkbenchReview(
    bool IsComplete,
    string? Topic,
    string? RepoRelativeDirectory,
    ConceptWorkbenchDescriptor? Descriptor,
    IReadOnlyList<string> Findings)
{
    public string Summary => IsComplete
        ? $"Concept Dossier '{Descriptor?.Title ?? Topic}' is complete and evidence-ready."
        : string.Join(" ", Findings);
}

/// <summary>
/// Deterministic concept-deliverable contract. This is the concept review:
/// structure, alternatives, recommendation, evidence, open decisions, and
/// implementation-card source data. It deliberately does not inspect builds,
/// tests, or product code.
/// </summary>
public static class ConceptWorkbenchContract
{
    public const string DossierPrefix = "docs/";
    public const string DescriptorFileName = "workbench.json";
    public const string EntryFileName = "index.html";
    private const string ArticleTemplateResource = "AgentStudio.Templates.ArticleDocumentV2";

    private static readonly string[] RequiredSections =
        ["alternatives", "recommendation", "evidence", "open-decisions"];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Creates a valid house-style Dossier skeleton. The concept agent owns the
    /// substantive content; this helper gives the pipeline a deterministic
    /// materialization contract and makes the required two-file deliverable
    /// directly testable.
    /// </summary>
    public static string CreateScaffold(
        string repositoryRoot,
        string topic,
        string title,
        string summary,
        string sourceTaskKey,
        string pattern = ArticlePatterns.Concept)
    {
        var safeTopic = NormalizeTopic(topic);
        var target = Path.Combine(repositoryRoot, "docs", safeTopic);
        Directory.CreateDirectory(target);

        var descriptor = new ConceptWorkbenchDescriptor
        {
            Id = safeTopic,
            Title = title.Trim(),
            Summary = summary.Trim(),
            Pattern = ArticlePatterns.Normalize(pattern),
            SourceTaskKeys = string.IsNullOrWhiteSpace(sourceTaskKey) ? [] : [sourceTaskKey.Trim()],
        };
        File.WriteAllText(
            Path.Combine(target, DescriptorFileName),
            JsonSerializer.Serialize(descriptor, JsonOptions) + Environment.NewLine);
        File.WriteAllText(
            Path.Combine(target, EntryFileName),
            RenderHouseStyleIndex(descriptor));
        return target;
    }

    public static ConceptWorkbenchReview ReviewChangedFiles(
        string checkoutRoot,
        IReadOnlyList<string> changedFiles,
        string? expectedSourceTaskKey = null)
    {
        var normalized = changedFiles
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizeRepoPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var findings = new List<string>();
        if (normalized.Count == 0)
            findings.Add("The concept run produced no Dossier files.");

        var outside = normalized.Where(path =>
            !path.StartsWith(DossierPrefix, StringComparison.OrdinalIgnoreCase)).ToList();
        if (outside.Count > 0)
            findings.Add("Concept runs may change only docs/<topic>/: " + string.Join(", ", outside));

        var topicRoots = normalized
            .Where(path => path.StartsWith(DossierPrefix, StringComparison.OrdinalIgnoreCase))
            .Select(path =>
            {
                var remainder = path[DossierPrefix.Length..];
                var slash = remainder.IndexOf('/');
                return slash <= 0 ? null : DossierPrefix + remainder[..slash];
            })
            .Where(root => root is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToList();
        if (topicRoots.Count != 1)
            findings.Add($"Exactly one concept Dossier is required; found {topicRoots.Count} topic directories.");

        if (findings.Count > 0)
            return new ConceptWorkbenchReview(false, null, null, null, findings);

        var rel = topicRoots[0];
        return ReviewDirectory(checkoutRoot, rel, expectedSourceTaskKey);
    }

    public static ConceptWorkbenchReview ReviewDirectory(
        string checkoutRoot,
        string repoRelativeDirectory,
        string? expectedSourceTaskKey = null)
    {
        var rel = NormalizeRepoPath(repoRelativeDirectory).TrimEnd('/');
        var findings = new List<string>();
        var segments = rel.Split('/');
        var isNewDossier = segments.Length == 2
            && string.Equals(segments[0], "docs", StringComparison.OrdinalIgnoreCase);
        var isLegacyWorkbench = segments.Length == 3
            && string.Equals(segments[0], "docs", StringComparison.OrdinalIgnoreCase)
            && string.Equals(segments[1], "operations", StringComparison.OrdinalIgnoreCase);
        if (!isNewDossier && !isLegacyWorkbench)
        {
            findings.Add("Dossier directory must be docs/<slug>/.");
            return new ConceptWorkbenchReview(false, null, rel, null, findings);
        }

        var topic = segments[^1];
        var directory = Path.GetFullPath(Path.Combine(checkoutRoot, rel.Replace('/', Path.DirectorySeparatorChar)));
        var root = Path.GetFullPath(checkoutRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!directory.StartsWith(root, PathComparison()))
        {
            findings.Add("Dossier directory escapes the repository root.");
            return new ConceptWorkbenchReview(false, topic, rel, null, findings);
        }

        var descriptorPath = Path.Combine(directory, DescriptorFileName);
        var entryPath = Path.Combine(directory, EntryFileName);
        ConceptWorkbenchDescriptor? descriptor = null;
        if (!File.Exists(descriptorPath))
        {
            findings.Add("workbench.json is missing.");
        }
        else
        {
            try
            {
                var descriptorJson = File.ReadAllText(descriptorPath);
                descriptor = JsonSerializer.Deserialize<ConceptWorkbenchDescriptor>(
                    descriptorJson, JsonOptions);
                using var document = JsonDocument.Parse(descriptorJson);
                ValidateDescriptorShape(document.RootElement, findings);
            }
            catch (Exception ex)
            {
                findings.Add("workbench.json is invalid: " + ex.Message);
            }
        }

        if (descriptor is not null)
        {
            if (descriptor.SchemaVersion < 1) findings.Add("workbench.json schemaVersion must be at least 1.");
            if (string.IsNullOrWhiteSpace(descriptor.Id)) findings.Add("workbench.json id is required.");
            if (string.IsNullOrWhiteSpace(descriptor.Title)) findings.Add("workbench.json title is required.");
            if (string.IsNullOrWhiteSpace(descriptor.Summary)) findings.Add("workbench.json summary is required.");
            if (!string.Equals(descriptor.Entrypoint, EntryFileName, StringComparison.OrdinalIgnoreCase))
                findings.Add("workbench.json entrypoint must be index.html.");
            if (isNewDossier
                && !string.Equals(descriptor.Status, "decision-pending", StringComparison.OrdinalIgnoreCase))
                findings.Add("workbench.json status must be decision-pending.");
            else if (isLegacyWorkbench && string.IsNullOrWhiteSpace(descriptor.Status))
                findings.Add("workbench.json status is required.");
            if (string.IsNullOrWhiteSpace(descriptor.Phase)) findings.Add("workbench.json phase is required.");
            if (descriptor.SourceTaskKeys.Count == 0)
                findings.Add("workbench.json sourceTaskKeys must identify the source concept card.");
            else if (isNewDossier
                && !string.IsNullOrWhiteSpace(expectedSourceTaskKey)
                && !descriptor.SourceTaskKeys.Contains(expectedSourceTaskKey.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                findings.Add($"workbench.json sourceTaskKeys must include the source concept card {expectedSourceTaskKey.Trim()}.");
            }
            for (var i = 0; i < descriptor.ImplementationTasks.Count; i++)
            {
                var item = descriptor.ImplementationTasks[i];
                if (item.Decision is not null)
                {
                    if (string.IsNullOrWhiteSpace(item.Title))
                        findings.Add($"implementationTasks[{i}] requires a title.");
                    findings.AddRange(DecisionCardPolicy.ValidateContent(item.Decision)
                        .Select(error => $"implementationTasks[{i}].decision: {error.Message}"));
                    foreach (var option in item.Decision.Options ?? [])
                    foreach (var requirement in option.Requirements ?? [])
                    {
                        if (!string.IsNullOrWhiteSpace(requirement.Title)
                            && !string.IsNullOrWhiteSpace(requirement.PromptMarkdown)
                            && DossierImplementationCardPolicy.Validate(requirement) is { } requirementFinding)
                            findings.Add($"implementationTasks[{i}].decision option '{option.Id}' requirement is not workflow-sized: {requirementFinding}");
                    }
                }
                else if (string.IsNullOrWhiteSpace(item.Title) || string.IsNullOrWhiteSpace(item.PromptMarkdown))
                    findings.Add($"implementationTasks[{i}] requires title and promptMarkdown.");
                else if (DossierImplementationCardPolicy.Validate(item) is { } scopeFinding)
                    findings.Add($"implementationTasks[{i}] is not workflow-sized: {scopeFinding}");
            }
        }

        if (!File.Exists(entryPath))
        {
            findings.Add("index.html is missing.");
        }
        else
        {
            var html = File.ReadAllText(entryPath);
            if (!html.Contains("<!DOCTYPE html>", StringComparison.OrdinalIgnoreCase))
                findings.Add("index.html must be a self-contained HTML document.");
            if (!html.Contains("<style", StringComparison.OrdinalIgnoreCase))
                findings.Add("index.html must include its house-style CSS.");
            foreach (var section in RequiredSections)
            {
                if (!HasSection(html, "data-document-section", section)
                    && !HasSection(html, "data-concept-section", section))
                    findings.Add($"index.html is missing the {section} concept section.");
            }
        }

        return new ConceptWorkbenchReview(
            findings.Count == 0, topic, rel, descriptor, findings);
    }

    private static void ValidateDescriptorShape(JsonElement root, List<string> findings)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            findings.Add("workbench.json must be a JSON object.");
            return;
        }

        var properties = root.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value, StringComparer.OrdinalIgnoreCase);
        string[] required =
        [
            "schemaVersion",
            "id",
            "title",
            "summary",
            "entrypoint",
            "status",
            "phase",
            "updatedAt",
            "sourceTaskKeys",
            "implementationTasks",
        ];
        foreach (var name in required)
        {
            if (!properties.ContainsKey(name))
                findings.Add($"workbench.json {name} is required.");
        }
        if (properties.TryGetValue("sourceTaskKeys", out var sources)
            && sources.ValueKind != JsonValueKind.Array)
            findings.Add("workbench.json sourceTaskKeys must be an array.");
        if (properties.TryGetValue("implementationTasks", out var tasks)
            && tasks.ValueKind != JsonValueKind.Array)
            findings.Add("workbench.json implementationTasks must be an array.");
    }

    public static string RenderHouseStyleIndex(ConceptWorkbenchDescriptor descriptor)
    {
        var title = WebUtility.HtmlEncode(descriptor.Title);
        var summary = WebUtility.HtmlEncode(descriptor.Summary);
        var pattern = ArticlePatterns.Normalize(descriptor.Pattern);
        using var stream = typeof(ConceptWorkbenchContract).Assembly
            .GetManifestResourceStream(ArticleTemplateResource)
            ?? throw new InvalidOperationException("The canonical article document template is unavailable.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd()
            .Replace("{{title}}", title, StringComparison.Ordinal)
            .Replace("{{summary}}", summary, StringComparison.Ordinal)
            .Replace("{{pattern}}", pattern, StringComparison.Ordinal)
            .Replace("{{status}}", WebUtility.HtmlEncode(descriptor.Status), StringComparison.Ordinal)
            .Replace("{{phase}}", WebUtility.HtmlEncode(descriptor.Phase), StringComparison.Ordinal);
    }

    private static bool HasSection(string html, string attribute, string section) =>
        html.Contains($"{attribute}=\"{section}\"", StringComparison.OrdinalIgnoreCase)
        || html.Contains($"{attribute}='{section}'", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeTopic(string topic)
    {
        var normalized = new string((topic ?? "").Trim().ToLowerInvariant()
            .Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '-').ToArray());
        while (normalized.Contains("--", StringComparison.Ordinal))
            normalized = normalized.Replace("--", "-", StringComparison.Ordinal);
        normalized = normalized.Trim('-');
        if (normalized.Length == 0) throw new ArgumentException("Concept topic is required.", nameof(topic));
        return normalized;
    }

    private static string NormalizeRepoPath(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    private static StringComparison PathComparison() =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
