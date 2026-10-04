using System.Text;
using System.Text.RegularExpressions;
using AgentStudio.Prompts;

namespace AgentStudio.Runtime;

/// <summary>
/// Validates whether a watched project exposes the Agent Software Studio
/// skill lookup section described in <c>docs/concepts/skills-architecture.md</c>
/// and surfaces the catalog of standard + project-specific skills the
/// task processor knows about. The check is deliberately naive in v1:
/// stable heading detection plus a small set of required phrases is
/// enough to tell pass / warning / fail apart, and the fix path queues a
/// normal task in the watched project rather than auto-editing its docs
/// (per ADR-0005 / docs/concepts/skills-architecture.md "First Product Step").
///
/// The agent owns README content; this service only inspects it and
/// builds the prompt for a follow-up task. No file under the watched
/// project is ever written from here.
/// </summary>
public class SkillReadinessService
{
    private readonly TaskScannerService _scanner;
    private readonly TaskMutationService _mutations;
    private readonly ILogger<SkillReadinessService> _logger;

    /// <summary>
    /// Files inspected, in order. The first file that contains the
    /// skill heading wins. README.md is the canonical lookup surface
    /// (every CLI reads it); AGENTS.md and the Copilot shim are
    /// fallbacks so projects that already moved their agent rules
    /// there are not flagged as missing.
    /// </summary>
    private static readonly string[] LookupFiles =
    [
        "README.md",
        "AGENTS.md",
        ".github/copilot-instructions.md",
    ];

    /// <summary>
    /// Stable heading detector. Matches an H2 (or H3) whose text
    /// contains the word "skill" and either "Agent Software Studio",
    /// "Agent Task Processor", or "Portable" — covers the canonical
    /// "## Agent Software Studio Skills" plus the close synonyms the
    /// docs use today. Case-insensitive. Body of the section is
    /// "everything from this heading until the next heading at the
    /// same or higher level".
    /// </summary>
    private static readonly Regex HeadingRegex = new(
        @"^(?<hashes>#{2,3})\s+(?<title>[^\r\n]*\bskill[^\r\n]*)$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Required phrases inside the matched section body. Each entry is
    /// a (label, regex) pair. Status is derived from how many of these
    /// hit:
    ///   pass     - heading + all required phrases hit
    ///   warning  - heading + at least 1 phrase hit, but not all
    ///   fail     - no heading (file may exist or not)
    /// The phrases are intentionally narrow: the canonical lookup
    /// snippet from <c>docs/concepts/skills-architecture.md</c> contains every
    /// one, and a hand-written variant that drops them all is almost
    /// always stale rather than a deliberate format choice.
    /// </summary>
    private static readonly (string Label, Regex Pattern)[] RequiredPhrases =
    [
        ("standardSkills", new Regex(@"\bstandard\s+skills?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("projectSkills", new Regex(@"\bproject(?:[\s-]specific)?\s+skills?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("skillsPath", new Regex(@"\.agents[/\\]+skills[/\\]+", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("processorReference", new Regex(@"\b(?:agent\s+software\s+studio|agent\s+task\s+processor|task\s+processor)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
    ];

    public SkillReadinessService(
        TaskScannerService scanner,
        TaskMutationService mutations,
        ILogger<SkillReadinessService> logger)
    {
        _scanner = scanner;
        _mutations = mutations;
        _logger = logger;
    }

    /// <summary>
    /// Returns the skill readiness verdict for the named project, or
    /// <c>null</c> if no watched project resolves to that name.
    /// </summary>
    public SkillReadinessReport? CheckProject(string projectName)
    {
        var entry = FindProject(projectName);
        if (entry == null) return null;

        var baseDir = ResolveBaseDir(entry);
        if (string.IsNullOrEmpty(baseDir))
        {
            return new SkillReadinessReport
            {
                ProjectName = projectName,
                Status = SkillReadinessStatus.Fail,
                Summary = "Project has no RootPath / RepositoryPath configured; cannot check.",
                CheckedFiles = [],
                MatchedFile = null,
                Heading = null,
                MissingPhrases = RequiredPhrases.Select(p => p.Label).ToList(),
                MatchedPhrases = [],
            };
        }

        var checkedFiles = new List<SkillReadinessFile>();
        SkillReadinessFile? matched = null;
        string? sectionBody = null;
        string? matchedHeading = null;

        foreach (var rel in LookupFiles)
        {
            var fullPath = Path.Combine(baseDir, rel);
            var fileEntry = new SkillReadinessFile
            {
                RelPath = rel.Replace('\\', '/'),
                FullPath = fullPath,
                Exists = File.Exists(fullPath),
                HeadingFound = false,
            };

            if (fileEntry.Exists && matched == null)
            {
                try
                {
                    var content = File.ReadAllText(fullPath);
                    var (heading, body) = ExtractSection(content);
                    if (heading != null && body != null)
                    {
                        fileEntry.HeadingFound = true;
                        matched = fileEntry;
                        sectionBody = body;
                        matchedHeading = heading;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "skill-readiness: failed to read {Path}", fullPath);
                }
            }

            checkedFiles.Add(fileEntry);
        }

        var matchedPhrases = new List<string>();
        var missingPhrases = new List<string>();

        if (matched != null && sectionBody != null)
        {
            foreach (var (label, pattern) in RequiredPhrases)
            {
                if (pattern.IsMatch(sectionBody)) matchedPhrases.Add(label);
                else missingPhrases.Add(label);
            }
        }
        else
        {
            foreach (var (label, _) in RequiredPhrases) missingPhrases.Add(label);
        }

        SkillReadinessStatus status;
        string summary;
        if (matched == null)
        {
            status = SkillReadinessStatus.Fail;
            summary = "No skill lookup heading found in README, AGENTS, or the Copilot shim. Add the section described in docs/concepts/skills-architecture.md.";
        }
        else if (missingPhrases.Count == 0)
        {
            status = SkillReadinessStatus.Pass;
            summary = $"Skill lookup section found in {matched.RelPath} with all expected phrases.";
        }
        else
        {
            status = SkillReadinessStatus.Warning;
            summary = $"Skill lookup heading found in {matched.RelPath}, but the section is missing {missingPhrases.Count} expected phrase{(missingPhrases.Count == 1 ? "" : "s")}.";
        }

        return new SkillReadinessReport
        {
            ProjectName = projectName,
            Status = status,
            Summary = summary,
            CheckedFiles = checkedFiles,
            MatchedFile = matched?.RelPath,
            Heading = matchedHeading,
            MatchedPhrases = matchedPhrases,
            MissingPhrases = missingPhrases,
        };
    }

    /// <summary>
    /// Builds (without persisting) the <see cref="CreateTaskRequest"/>
    /// payload used by the fix path. Surfaced separately from
    /// <see cref="CreateFixTask"/> so the frontend can preview the
    /// proposed title and prompt before queueing the task.
    /// </summary>
    public SkillReadinessFixTaskPreview? PreviewFixTask(string projectName)
    {
        var report = CheckProject(projectName);
        if (report == null) return null;

        var entry = FindProject(projectName)!;
        var (title, prompt) = BuildFixTaskContent(report);

        return new SkillReadinessFixTaskPreview
        {
            WatchPath = entry.Path,
            Title = title,
            PromptMarkdown = prompt,
            TargetState = TaskStates.Ready,
            TaskType = TaskTypes.Chore,
            Report = report,
        };
    }

    /// <summary>
    /// Queues a normal 2-ready task in the project that owns the
    /// missing or stale lookup section, returning the new job id.
    /// Returns <c>null</c> when the project name is unknown or the
    /// preview cannot be built (caller should treat that as 404 /
    /// 400 respectively).
    /// </summary>
    public SkillReadinessFixTaskResult? CreateFixTask(string projectName, string? ownerClientId)
    {
        var preview = PreviewFixTask(projectName);
        if (preview == null) return null;

        var req = new CreateTaskRequest
        {
            Title = preview.Title,
            WatchPath = preview.WatchPath,
            PromptMarkdown = preview.PromptMarkdown,
            TargetState = preview.TargetState,
            TaskType = preview.TaskType,
            Agent = "claude",
            OwnerClientId = string.IsNullOrWhiteSpace(ownerClientId) ? null : ownerClientId,
        };

        var jobId = _mutations.CreateJob(req);
        if (jobId == null)
        {
            _logger.LogWarning("skill-readiness: CreateJob returned null for project {Project}", projectName);
            return null;
        }

        return new SkillReadinessFixTaskResult
        {
            JobId = jobId,
            WatchPath = preview.WatchPath,
            TargetState = preview.TargetState,
            Title = preview.Title,
        };
    }

    /// <summary>
    /// Returns the catalog of skills the task processor exposes for
    /// this project: the central standard library (under the
    /// repository's <c>.agents/skills/</c> tree) plus any
    /// project-specific skills under
    /// <c>.agents/projects/&lt;project-key&gt;/skills/</c>. Also
    /// surfaces which skills are <em>selected</em> (always-attached)
    /// vs. <em>suggested</em> (optional / opt-in). Selection metadata
    /// is naive in v1: every standard skill is "suggested" and every
    /// project-specific skill is "selected" for that project. A future
    /// pass will read explicit per-project selection from settings.
    /// </summary>
    public SkillCatalog GetCatalog(string projectName)
    {
        var entry = FindProject(projectName);
        var standard = ScanStandardSkills();
        var projectSpecific = ScanProjectSpecificSkills(entry?.Name ?? projectName);

        return new SkillCatalog
        {
            ProjectName = projectName,
            Standard = standard,
            ProjectSpecific = projectSpecific,
        };
    }

    private List<SkillEntry> ScanStandardSkills()
    {
        var results = new List<SkillEntry>();
        var root = ResolveAgentsRoot();
        if (root == null) return results;

        var skillsDir = Path.Combine(root, "skills");
        if (!Directory.Exists(skillsDir)) return results;

        foreach (var dir in Directory.EnumerateDirectories(skillsDir))
        {
            var name = Path.GetFileName(dir);
            var skillFile = Path.Combine(dir, "SKILL.md");
            if (!File.Exists(skillFile)) continue;

            var meta = ReadSkillFrontmatter(skillFile);
            results.Add(new SkillEntry
            {
                Id = name,
                Name = meta.Name ?? name,
                Description = meta.Description ?? "",
                Category = SkillCategory.Standard,
                Selection = SkillSelection.Suggested,
                RelPath = ToForwardSlash(Path.GetRelativePath(root, skillFile)),
            });
        }

        results.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));
        return results;
    }

    private List<SkillEntry> ScanProjectSpecificSkills(string projectKey)
    {
        var results = new List<SkillEntry>();
        var root = ResolveAgentsRoot();
        if (root == null) return results;

        var projectDir = Path.Combine(root, "projects", projectKey, "skills");
        if (!Directory.Exists(projectDir)) return results;

        foreach (var dir in Directory.EnumerateDirectories(projectDir))
        {
            var name = Path.GetFileName(dir);
            var skillFile = Path.Combine(dir, "SKILL.md");
            if (!File.Exists(skillFile)) continue;

            var meta = ReadSkillFrontmatter(skillFile);
            results.Add(new SkillEntry
            {
                Id = name,
                Name = meta.Name ?? name,
                Description = meta.Description ?? "",
                Category = SkillCategory.ProjectSpecific,
                Selection = SkillSelection.Selected,
                RelPath = ToForwardSlash(Path.GetRelativePath(root, skillFile)),
            });
        }

        results.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));
        return results;
    }

    /// <summary>
    /// Resolves the <c>.agents/</c> root in the task processor's
    /// repository. Walks up from the running binary's location until
    /// it finds a directory containing both <c>.agents/</c> and
    /// <c>backend/</c> (the repo root). Returns the <c>.agents</c>
    /// path or <c>null</c> when the layout doesn't match (e.g. a
    /// packaged build).
    /// </summary>
    private static string? ResolveAgentsRoot()
    {
        var current = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && !string.IsNullOrEmpty(current); i++)
        {
            var candidate = Path.Combine(current, ".agents");
            if (Directory.Exists(candidate) && Directory.Exists(Path.Combine(current, "backend")))
            {
                return candidate;
            }
            var parent = Path.GetDirectoryName(current);
            if (parent == current) break;
            current = parent ?? "";
        }
        return null;
    }

    private static (string? Name, string? Description) ReadSkillFrontmatter(string path)
    {
        try
        {
            var lines = File.ReadAllLines(path);
            if (lines.Length == 0 || !lines[0].Trim().Equals("---", StringComparison.Ordinal))
            {
                return (null, null);
            }
            string? name = null, description = null;
            for (int i = 1; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.Trim().Equals("---", StringComparison.Ordinal)) break;
                var idx = line.IndexOf(':');
                if (idx < 0) continue;
                var key = line[..idx].Trim();
                var value = line[(idx + 1)..].Trim().Trim('"');
                if (key.Equals("name", StringComparison.OrdinalIgnoreCase)) name = value;
                else if (key.Equals("description", StringComparison.OrdinalIgnoreCase)) description = value;
            }
            return (name, description);
        }
        catch
        {
            return (null, null);
        }
    }

    private WatchPathEntry? FindProject(string projectName) =>
        _scanner.GetWatchPaths().FirstOrDefault(e =>
            string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Repository checkout root if configured, otherwise the watch
    /// path itself, otherwise the watched root path. Same fallback
    /// chain other project-doc services use.
    /// </summary>
    private static string? ResolveBaseDir(WatchPathEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.RepositoryPath)) return entry.RepositoryPath;
        if (!string.IsNullOrWhiteSpace(entry.RootPath)) return entry.RootPath;
        if (!string.IsNullOrWhiteSpace(entry.Path)) return entry.Path;
        return null;
    }

    /// <summary>
    /// Pulls the matched section out of a markdown document. Returns
    /// (heading-line, body-without-heading) when the heading is found,
    /// else (null, null). The body is everything between the matched
    /// heading and the next heading at the same or higher level (so a
    /// subsection list inside the skill section is included, but the
    /// next top-level "## Foo" stops the scan).
    /// </summary>
    public static (string? Heading, string? Body) ExtractSection(string markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return (null, null);
        var match = HeadingRegex.Match(markdown);
        if (!match.Success) return (null, null);

        var headingLevel = match.Groups["hashes"].Value.Length;
        var bodyStart = match.Index + match.Length;
        var rest = markdown[bodyStart..];

        // Find the next heading at the same or higher level (= fewer or
        // equal #s). A regex with a backreference is overkill; scan line
        // by line.
        var stop = rest.Length;
        var lines = rest.Split('\n');
        var offset = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("#"))
            {
                int hashes = 0;
                while (hashes < trimmed.Length && trimmed[hashes] == '#') hashes++;
                if (hashes >= 1 && hashes <= headingLevel
                    && hashes < trimmed.Length
                    && trimmed[hashes] == ' ')
                {
                    stop = offset;
                    break;
                }
            }
            offset += line.Length + 1; // +1 for the consumed '\n'
        }

        var body = rest[..Math.Min(stop, rest.Length)];
        return (match.Value.Trim(), body);
    }

    private static (string Title, string PromptMarkdown) BuildFixTaskContent(SkillReadinessReport report)
    {
        var verb = report.Status == SkillReadinessStatus.Fail ? "Add" : "Update";
        var title = $"{verb} Agent Software Studio skills lookup section";

        var sb = new StringBuilder();
        sb.AppendLf($"# {title}");
        sb.AppendLf();
        sb.AppendLf("## Why");
        sb.AppendLf();
        sb.AppendLf("This watched project should expose a small skill lookup section so direct CLI work in Codex, Claude Code, Copilot, or Gemini can find the same standard skills the orchestrator attaches during managed task runs. The contract is documented in `docs/concepts/skills-architecture.md` (in the Agent Software Studio repo).");
        sb.AppendLf();
        sb.AppendLf("## Current state");
        sb.AppendLf();
        sb.AppendLf($"- Status: **{report.Status.ToString().ToLowerInvariant()}**");
        sb.AppendLf($"- Summary: {report.Summary}");
        if (!string.IsNullOrEmpty(report.MatchedFile))
        {
            sb.AppendLf($"- Section located in: `{report.MatchedFile}`");
        }
        if (!string.IsNullOrEmpty(report.Heading))
        {
            sb.AppendLf($"- Heading found: `{report.Heading}`");
        }
        if (report.MissingPhrases.Count > 0)
        {
            sb.AppendLf($"- Missing expected phrases: {string.Join(", ", report.MissingPhrases.Select(p => "`" + p + "`"))}");
        }
        sb.AppendLf();
        sb.AppendLf("## What to do");
        sb.AppendLf();
        sb.AppendLf("Edit this project's `README.md` (or `AGENTS.md` if that is where agent rules already live) and add or update an `## Agent Software Studio Skills` section that follows this naive shape:");
        sb.AppendLf();
        sb.AppendLf("```markdown");
        sb.AppendLf("## Agent Software Studio Skills");
        sb.AppendLf();
        sb.AppendLf("This project is managed by Agent Software Studio (the task processor).");
        sb.AppendLf();
        sb.AppendLf("Core task lifecycle rules live in the task processor and are applied during managed task runs.");
        sb.AppendLf();
        sb.AppendLf("When working directly in a CLI, use these skill references:");
        sb.AppendLf();
        sb.AppendLf("- Standard skills: `<task-processor-root>/.agents/skills/<skill-name>/SKILL.md`");
        sb.AppendLf("- Project skills: `<task-processor-root>/.agents/projects/<project-key>/skills/<skill-name>/SKILL.md`");
        sb.AppendLf();
        sb.AppendLf("Do not move `.orchestrator` job folders or edit task state manually.");
        sb.AppendLf("```");
        sb.AppendLf();
        sb.AppendLf("Replace the placeholder paths with the concrete skill names that apply to this project. Do not invent skills that do not exist in the task processor's `.agents/` tree.");
        sb.AppendLf();
        sb.AppendLf("## Definition of done");
        sb.AppendLf();
        sb.AppendLf("- A heading whose text contains \"skill\" exists in `README.md` or `AGENTS.md`.");
        sb.AppendLf("- The section mentions standard skills, project skills, the task processor, and the `.agents/skills/` path.");
        sb.AppendLf("- No queue state, lifecycle field, or job folder is changed by this task.");
        sb.AppendLf();
        sb.AppendLf("End the run with `[[TASK_DONE]]` once the section is in place.");

        return (title, sb.ToString());
    }

    private static string ToForwardSlash(string path) => path.Replace('\\', '/');
}
