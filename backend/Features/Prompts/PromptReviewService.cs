using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentStudio.Prompts;

/// <summary>
/// Reviews runtime prompts against registered consumers, repository references,
/// and project-level pipeline prompt overrides. Results are stored beside the
/// shipped Markdown as <c>&lt;name&gt;.md.meta.json</c>.
/// </summary>
public sealed partial class PromptReviewService
{
    private static readonly JsonSerializerOptions SidecarJson = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly RuntimePromptService _prompts;
    private readonly ProjectSettingsService _projectSettings;
    private readonly ILogger<PromptReviewService> _logger;
    private readonly ManagedRepositoryMutationService? _repositoryMutations;

    public PromptReviewService(
        RuntimePromptService prompts,
        ProjectSettingsService projectSettings,
        ILogger<PromptReviewService> logger,
        ManagedRepositoryMutationService? repositoryMutations = null)
    {
        _prompts = prompts;
        _projectSettings = projectSettings;
        _logger = logger;
        _repositoryMutations = repositoryMutations;
    }

    public PromptReviewSnapshot GetSnapshot(IReadOnlyList<string> names)
    {
        var revisions = ReadGitRevisions(names);
        var reviews = names.ToDictionary(
            name => name,
            ReadSidecar,
            StringComparer.OrdinalIgnoreCase);
        return new PromptReviewSnapshot(revisions, reviews, ReadProjectOverrides());
    }

    public PromptReviewResult? Review(string name, string? reviewedBy)
    {
        if (!_prompts.EnumerateTemplateNames().Contains(name, StringComparer.OrdinalIgnoreCase))
            return null;
        return ReviewCore(name, reviewedBy, ReadProjectOverrides());
    }

    public PromptReviewRunResponse ReviewAll(string? reviewedBy)
    {
        var overrides = ReadProjectOverrides();
        var results = _prompts.EnumerateTemplateNames()
            .Select(name => ReviewCore(name, reviewedBy, overrides))
            .Where(result => result is not null)
            .Cast<PromptReviewResult>()
            .ToList();

        return new PromptReviewRunResponse
        {
            ReviewedAt = results.Count == 0
                ? DateTimeOffset.UtcNow
                : results.Max(result => result.Metadata.LastReviewedAt),
            ReviewedCount = results.Count,
            FindingCount = results.Sum(result => result.Metadata.Findings.Count),
            Results = results,
            OrphanedOverrides = overrides.Where(item => item.Orphaned).ToList(),
        };
    }

    private PromptReviewResult? ReviewCore(
        string name,
        string? reviewedBy,
        IReadOnlyList<PromptProjectOverride> allOverrides)
    {
        var content = _prompts.TryReadDefault(name);
        if (content is null) return null;

        var findings = new List<PromptReviewFinding>();
        var usages = PromptUsageCatalog.For(name);
        if (PromptUsageCatalog.TryGetUnreachableReason(name, out var unreachableReason))
        {
            findings.Add(new PromptReviewFinding
            {
                Code = "dead-prompt",
                Severity = "error",
                Message = unreachableReason,
            });
        }
        else if (usages.Count == 0)
        {
            findings.Add(new PromptReviewFinding
            {
                Code = "unregistered-usage",
                Severity = "error",
                Message = "No live consumer is registered in PromptUsageCatalog.",
            });
        }

        findings.AddRange(FindBrokenRepositoryReferences(content));

        var promptOverrides = allOverrides
            .Where(item => string.Equals(item.PromptName, name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var item in promptOverrides.Where(item => !item.MatchesDefault))
        {
            findings.Add(new PromptReviewFinding
            {
                Code = "project-override-differs",
                Severity = "info",
                Message = $"Project '{item.ProjectName}' has a prompt override for step '{item.StepId}' ({item.AddedLines} added, {item.RemovedLines} removed lines).",
                ProjectName = item.ProjectName,
                StepId = item.StepId,
            });
        }

        var status = findings.Any(finding => finding.Severity == "error")
            ? "stale"
            : findings.Any(finding => finding.Severity == "warning")
                ? "needs-review"
                : "current";
        var metadata = new PromptReviewMetadata
        {
            LastReviewedAt = DateTimeOffset.UtcNow,
            ReviewedBy = string.IsNullOrWhiteSpace(reviewedBy) ? "prompt-admin" : reviewedBy.Trim(),
            Status = status,
            Findings = findings,
        };
        WriteSidecar(name, metadata);

        return new PromptReviewResult
        {
            Name = name,
            Metadata = metadata,
            ProjectOverrides = promptOverrides,
        };
    }

    private IReadOnlyList<PromptReviewFinding> FindBrokenRepositoryReferences(string content)
    {
        var repoRoot = DriftRepoRootLocator.Resolve();
        var findings = new List<PromptReviewFinding>();
        foreach (Match match in RepositoryPathRegex().Matches(content))
        {
            var relative = match.Groups["path"].Value.TrimEnd('.', ',', ':', ';', ')');
            var full = Path.GetFullPath(Path.Combine(
                repoRoot,
                relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(
                    Path.GetFullPath(repoRoot) + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal)
                || File.Exists(full)
                || Directory.Exists(full))
                continue;

            findings.Add(new PromptReviewFinding
            {
                Code = "missing-repository-reference",
                Severity = "warning",
                Message = $"Prompt references repository path '{relative}', but it does not exist.",
            });
        }

        return findings
            .GroupBy(finding => finding.Message, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
    }

    private IReadOnlyList<PromptProjectOverride> ReadProjectOverrides()
    {
        var result = new List<PromptProjectOverride>();
        foreach (var (projectName, settings) in _projectSettings.GetAll())
        {
            if (settings.PipelineSteps is null) continue;
            foreach (var (configuredStepId, setting) in settings.PipelineSteps)
            {
                if (string.IsNullOrWhiteSpace(setting.Prompt)) continue;
                var promptName = PromptPipelineBindings.ForStep(configuredStepId);
                var defaultContent = promptName is null
                    ? null
                    : _prompts.TryReadDefault(promptName);
                var currentDefaultSha = defaultContent is null
                    ? null
                    : RuntimePromptService.ContentSha(defaultContent);
                var (added, removed) = DiffCounts(defaultContent, setting.Prompt);
                result.Add(new PromptProjectOverride
                {
                    ProjectName = projectName,
                    StepId = configuredStepId,
                    PromptName = promptName,
                    Content = setting.Prompt!,
                    Orphaned = promptName is null || defaultContent is null,
                    MatchesDefault = defaultContent is not null
                        && Normalize(defaultContent) == Normalize(setting.Prompt),
                    AddedLines = added,
                    RemovedLines = removed,
                    BaseDefaultSha = setting.PromptBaseDefaultSha,
                    DefaultChangedSinceOverride =
                        setting.PromptBaseDefaultSha is not null
                        && currentDefaultSha is not null
                        && !string.Equals(
                            setting.PromptBaseDefaultSha,
                            currentDefaultSha,
                            StringComparison.OrdinalIgnoreCase),
                });
            }
        }

        return result
            .OrderBy(item => item.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.StepId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private Dictionary<string, PromptSourceRevision> ReadGitRevisions(IReadOnlyList<string> names)
    {
        var wanted = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, PromptSourceRevision>(StringComparer.OrdinalIgnoreCase);
        var repoRoot = DriftRepoRootLocator.Resolve();
        var gitMarker = Path.Combine(repoRoot, ".git");
        if (!Directory.Exists(gitMarker) && !File.Exists(gitMarker)) return result;

        try
        {
            var start = new ProcessStartInfo("git")
            {
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("log");
            start.ArgumentList.Add("--format=%x1e%H%x1f%cI");
            start.ArgumentList.Add("--name-only");
            start.ArgumentList.Add("--");
            start.ArgumentList.Add("prompts/runtime");

            AgentStudio.Git.HostGitProcessHardening.Apply(start);
            using var process = Process.Start(start);
            if (process is null) return result;
            var stdout = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
                _logger.LogWarning("prompt-git-history-timeout");
                return result;
            }
            if (process.ExitCode != 0) return result;

            foreach (var block in stdout.Split('\u001e', StringSplitOptions.RemoveEmptyEntries))
            {
                var lines = block.Replace("\r\n", "\n")
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length < 2) continue;
                var header = lines[0].Split('\u001f');
                if (header.Length != 2
                    || !DateTimeOffset.TryParse(header[1], out var changedAt))
                    continue;

                foreach (var path in lines.Skip(1))
                {
                    var name = Path.GetFileName(path.Trim());
                    if (!wanted.Contains(name) || result.ContainsKey(name)) continue;
                    result[name] = new PromptSourceRevision
                    {
                        ChangedAt = changedAt,
                        CommitSha = header[0],
                    };
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "prompt-git-history-failed");
        }

        return result;
    }

    private PromptReviewMetadata? ReadSidecar(string name)
    {
        var path = SidecarPath(name);
        if (path is null || !File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<PromptReviewMetadata>(
                File.ReadAllText(path),
                SidecarJson);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "prompt-review-sidecar-read-failed template={Template}",
                name);
            return new PromptReviewMetadata
            {
                Status = "needs-review",
                Findings =
                [
                    new PromptReviewFinding
                    {
                        Code = "invalid-review-sidecar",
                        Severity = "warning",
                        Message = "The prompt review sidecar could not be parsed.",
                    },
                ],
            };
        }
    }

    private void WriteSidecar(string name, PromptReviewMetadata metadata)
    {
        var path = SidecarPath(name)
            ?? throw new InvalidOperationException(
                $"No shipped prompt path exists for '{name}'.");
        void Write()
        {
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(metadata, SidecarJson));
            File.Move(temp, path, overwrite: true);
        }

        var repoRoot = Path.GetFullPath(DriftRepoRootLocator.Resolve());
        var fullPath = Path.GetFullPath(path);
        var rootPrefix = repoRoot.EndsWith(Path.DirectorySeparatorChar)
            ? repoRoot
            : repoRoot + Path.DirectorySeparatorChar;
        if (_repositoryMutations == null
            || !fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            Write();
            return;
        }

        var relativePath = Path.GetRelativePath(repoRoot, fullPath).Replace('\\', '/');
        var mutation = _repositoryMutations.Execute(
            "runtime-prompts",
            repoRoot,
            $"prompt-review-{name}",
            $"chore(prompts): review {name}",
            [relativePath],
            Write);
        if (!mutation.Success)
            throw new InvalidOperationException(
                $"Prompt review metadata could not be persisted: {mutation.Error}");
    }

    private string? SidecarPath(string name)
        => _prompts.TryGetReviewCompanionPath(name);

    private static (int Added, int Removed) DiffCounts(
        string? defaultContent,
        string overrideContent)
    {
        if (defaultContent is null)
            return (Normalize(overrideContent).Split('\n').Length, 0);

        var leftCounts = Normalize(defaultContent)
            .Split('\n')
            .GroupBy(line => line)
            .ToDictionary(group => group.Key, group => group.Count());
        var rightCounts = Normalize(overrideContent)
            .Split('\n')
            .GroupBy(line => line)
            .ToDictionary(group => group.Key, group => group.Count());
        var added = 0;
        var removed = 0;
        foreach (var line in leftCounts.Keys.Concat(rightCounts.Keys).Distinct())
        {
            leftCounts.TryGetValue(line, out var before);
            rightCounts.TryGetValue(line, out var after);
            if (after > before) added += after - before;
            if (before > after) removed += before - after;
        }
        return (added, removed);
    }

    private static string Normalize(string value) =>
        value.Replace("\r\n", "\n").Trim();

    [GeneratedRegex(@"(?<path>(?:backend|frontend|docs|prompts|runner)/[A-Za-z0-9_./-]+\.(?:cs|ts|md|json|html|scss))")]
    private static partial Regex RepositoryPathRegex();
}

public sealed record PromptReviewSnapshot(
    IReadOnlyDictionary<string, PromptSourceRevision> Revisions,
    IReadOnlyDictionary<string, PromptReviewMetadata?> Reviews,
    IReadOnlyList<PromptProjectOverride> ProjectOverrides);

public sealed class PromptSourceRevision
{
    public DateTimeOffset ChangedAt { get; set; }
    public string CommitSha { get; set; } = "";
}

public sealed class PromptReviewMetadata
{
    public DateTimeOffset LastReviewedAt { get; set; }
    public string ReviewedBy { get; set; } = "";
    public string Status { get; set; } = "needs-review";
    public List<PromptReviewFinding> Findings { get; set; } = [];
}

public sealed class PromptReviewFinding
{
    public string Code { get; set; } = "";
    public string Severity { get; set; } = "";
    public string Message { get; set; } = "";
    public string? ProjectName { get; set; }
    public string? StepId { get; set; }
}

public sealed class PromptProjectOverride
{
    public string ProjectName { get; set; } = "";
    public string StepId { get; set; } = "";
    public string? PromptName { get; set; }
    public string Content { get; set; } = "";
    public bool Orphaned { get; set; }
    public bool MatchesDefault { get; set; }
    public int AddedLines { get; set; }
    public int RemovedLines { get; set; }
    public string? BaseDefaultSha { get; set; }
    public bool DefaultChangedSinceOverride { get; set; }
}

public sealed class PromptReviewResult
{
    public string Name { get; set; } = "";
    public PromptReviewMetadata Metadata { get; set; } = new();
    public List<PromptProjectOverride> ProjectOverrides { get; set; } = [];
}

public sealed class PromptReviewRunResponse
{
    public DateTimeOffset ReviewedAt { get; set; }
    public int ReviewedCount { get; set; }
    public int FindingCount { get; set; }
    public List<PromptReviewResult> Results { get; set; } = [];
    public List<PromptProjectOverride> OrphanedOverrides { get; set; } = [];
}
