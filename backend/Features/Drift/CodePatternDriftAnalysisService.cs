using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentStudio.Drift;

/// <summary>
/// Detects code-pattern drift: where one canonical implementation pattern
/// is duplicated across the codebase and at least one site has diverged
/// from the canonical shape.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this service exists.</b> On 2026-05-11 a Windows-prompt-via-argv
/// regression caused every auto-review aspect verdict to fall back to
/// "concerns" with an empty model reply. Three call sites had drifted
/// from the canonical stdin-piped one-shot pattern; five had not; the
/// drift was invisible until the user spotted the false-positive chips
/// on the board. This service is the standing detector for that class
/// of failure — it walks the source tree, matches each rule's candidate
/// + deviation regexes, and surfaces sites that disagree with the rest.
/// </para>
/// <para>
/// <b>Scope.</b> Deterministic only — no LLM call. Each rule is a small
/// triple (file glob, "this is a candidate" regex, "this is the wrong
/// variant" regex). The MVP rule set ships with three rules; new rules
/// are one record addition in <see cref="DefaultRules"/>.
/// </para>
/// <para>
/// <b>Output.</b> A <see cref="CodePatternDriftReport"/> + a rendered
/// Markdown sibling. Callers persist via <see cref="DriftReportStore"/>
/// (same shape as the other drift services) and emit a
/// <c>kind:observation</c> bus message via
/// <see cref="AgentStudio.Bus.AgentMessageBusBridge"/>.
/// </para>
/// </remarks>
public sealed class CodePatternDriftAnalysisService
{
    /// <summary>Topic slug used to label the report on bus + on disk.</summary>
    public const string Topic = "code-pattern-drift";

    /// <summary>Runtime prompt template that frames the Phase-2 LLM verdict.</summary>
    public const string ReviewTemplate = "code-pattern-drift-review.md";

    /// <summary>Conditional sub-block listing the canonical reference sites.</summary>
    public const string CanonicalSitesTemplate = "code-pattern-drift-canonical-sites.md";

    private readonly ILogger<CodePatternDriftAnalysisService> _logger;
    private readonly IReadOnlyList<CodePatternRule> _rules;
    private readonly RuntimePromptService _prompts;

    public CodePatternDriftAnalysisService(
        ILogger<CodePatternDriftAnalysisService> logger,
        IReadOnlyList<CodePatternRule>? rules = null,
        RuntimePromptService? prompts = null)
    {
        _logger = logger;
        _rules = rules ?? DefaultRules;
        _prompts = prompts ?? new RuntimePromptService(
            new ConfigurationBuilder().Build(), NullLogger<RuntimePromptService>.Instance);
    }

    /// <summary>
    /// Build the effective rule set: the hardcoded <see cref="DefaultRules"/>
    /// plus any extra rules parsed from <c>docs/system/contracts/code-patterns.md</c> under
    /// <paramref name="repoRoot"/>. Hardcoded rules win on id collisions so
    /// a docs-only override cannot relax a load-bearing rule by accident.
    /// </summary>
    public static IReadOnlyList<CodePatternRule> LoadEffectiveRules(string repoRoot, ILogger? logger = null)
    {
        var path = Path.Combine(repoRoot, "docs", "system", "contracts", "code-patterns.md");
        var fromDocs = CodePatternRuleLoader.LoadFromFile(path, logger);
        if (fromDocs.Count == 0) return DefaultRules;
        var ids = new HashSet<string>(DefaultRules.Select(r => r.Id), StringComparer.OrdinalIgnoreCase);
        var merged = new List<CodePatternRule>(DefaultRules);
        foreach (var r in fromDocs)
        {
            if (ids.Add(r.Id)) merged.Add(r);
            else logger?.LogInformation("Docs rule '{Id}' shadowed by hardcoded default", r.Id);
        }
        return merged;
    }

    /// <summary>
    /// The initial rule set. Adding a pattern is a one-record edit — keep
    /// the rules small and obvious; complicated detection belongs in the
    /// LLM-assisted phase 2.
    /// </summary>
    public static readonly IReadOnlyList<CodePatternRule> DefaultRules = new[]
    {
        // Rule 1: Claude one-shot CLI invocation must pipe the prompt via
        // stdin (the 2026-05-11 incident root cause). Candidate signal:
        // the file references "claude" in a ProcessStartInfo context.
        // Bad variant: ArgumentList.Add("-p") followed soon after by
        // ArgumentList.Add(<varName>) where varName looks prompt-shaped.
        new CodePatternRule(
            Id: "cli-one-shot-stdin",
            Title: "Claude one-shot CLI invocation must pipe prompt via stdin",
            CanonicalDescription:
                "Use AgentStudio.Cli.ICliOneShot (or stdin-piped Process.Start where DI is unavailable). " +
                "Multi-KB prompts passed as `-p <prompt>` argv fail silently on Windows shim layers — verified 2026-05-11.",
            FilePattern: @"\.cs$",
            ExcludeFilePattern: @"(?:OneShot[/\\]ClaudeOneShot\.cs|backend\.Tests[/\\]|/bin/|/obj/|UsersrmiscAppDataLocalTemp)",
            CandidateMarker: new Regex(@"ProcessStartInfo\b[\s\S]{0,400}?FileName\s*=", RegexOptions.Compiled),
            BadVariant: new Regex(
                @"ArgumentList\.Add\(\s*""-p""\s*\)\s*;\s*(?:[\s\S]{0,200}?)ArgumentList\.Add\(\s*prompt\b",
                RegexOptions.Compiled),
            GoodVariant: new Regex(
                @"(StandardInput\.WriteAsync\(\s*prompt|ICliOneShot|CliOneShotRegistry)",
                RegexOptions.Compiled),
            SeverityIfBad: DriftSeverity.High),

        // Rule 2: JSONL append should use the canonical IJsonlAppender or
        // at least hold a per-file SemaphoreSlim. The canonical helper
        // gives newline-guarantee + lock + parent-dir-create for free;
        // legacy sites with their own SemaphoreSlim are accepted but
        // flagged when neither is present.
        new CodePatternRule(
            Id: "jsonl-append-locked",
            Title: "JSONL append uses IJsonlAppender or a per-file semaphore",
            CanonicalDescription:
                "Append to a JSONL file should go through AgentStudio.Persistence.IJsonlAppender " +
                "(or wrap FileMode.Append in a per-path SemaphoreSlim). Without locking, concurrent writers can " +
                "interleave bytes when records exceed the OS atomic-write threshold.",
            FilePattern: @"\.cs$",
            ExcludeFilePattern: @"(?:backend\.Tests[/\\]|/bin/|/obj/|UsersrmiscAppDataLocalTemp|Persistence[/\\]IJsonlAppender\.cs|Persistence[/\\]JsonlAppender\.cs|update-service[/\\])",
            // Three flavours of "appending to a JSONL file" we want to surface:
            //  - new FileStream(..., FileMode.Append, ...)
            //  - File.AppendAllText(...) on a .jsonl path
            //  - File.AppendAllLines(...) / WriteAllLinesAsync with FileMode.Append
            CandidateMarker: new Regex(
                @"FileMode\.Append\b|File\.AppendAll(?:Text|Lines)(?:Async)?\b[\s\S]{0,200}?\.jsonl",
                RegexOptions.Compiled),
            BadVariant: null,
            // Canonical signals (any one is enough):
            //  - SemaphoreSlim (workspace pattern: per-path lock for short-lived appends)
            //  - IJsonlAppender / _appender / AppendLineAsync (the new helper)
            //  - lock (_lock)/lock(_sync) — per-instance object monitor, used by
            //    CliOutputLogStore for its long-lived stream design
            //  - File.AppendAllText/Lines is *not* in this list: those calls do
            //    not lock and can interleave; flag them.
            GoodVariant: new Regex(
                @"(?:SemaphoreSlim\b|IJsonlAppender\b|_appender\b|\.AppendLineAsync\b|lock\s*\(\s*_(?:lock|sync)\b)",
                RegexOptions.Compiled),
            SeverityIfBad: DriftSeverity.Warn),

        // Rule 3: Frontend file uploads must include X-Client-Id (or go
        // through HttpClient so the Angular interceptor adds it). Two
        // raw fetch() calls bypassed it 2026-05-11 → 401 on screenshot
        // attachment.
        new CodePatternRule(
            Id: "frontend-fetch-xclientid",
            Title: "Frontend fetch() calls to /api include X-Client-Id",
            CanonicalDescription:
                "Raw `fetch('/api/...')` calls bypass the Angular HttpClient interceptor that adds X-Client-Id. " +
                "Either route through HttpClient or pass the header explicitly. Scope: production runtime " +
                "(src/) — e2e infrastructure is excluded because Playwright fixtures do not go through Angular.",
            FilePattern: @"\.ts$",
            ExcludeFilePattern: @"(?:node_modules[/\\]|\.spec\.ts$|/dist/|client-id\.interceptor\.ts|frontend[/\\]e2e[/\\])",
            CandidateMarker: new Regex(
                @"\bfetch\s*\(\s*[`'""][^`'""]*?/api/",
                RegexOptions.Compiled),
            BadVariant: null, // GoodVariant absence flags
            GoodVariant: new Regex(
                @"X-Client-Id|clientIdInterceptor|setHeaders",
                RegexOptions.Compiled),
            SeverityIfBad: DriftSeverity.High),
    };

    /// <summary>
    /// Walks the repo, applies every rule, and returns the assembled
    /// report. The walk is deterministic and bounded — it skips bin/obj,
    /// node_modules, dist, and the test tree.
    /// </summary>
    public CodePatternDriftReport Analyze(string repoRoot, DateTime? now = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        if (!Directory.Exists(repoRoot))
            throw new DirectoryNotFoundException($"Repo root not found: {repoRoot}");

        var capturedAt = now ?? DateTime.UtcNow;
        var findings = new List<CodePatternFinding>();
        var totalDrift = 0;

        // Merge: ctor-injected rules (used by tests) take priority; for the
        // production path the ctor passes DefaultRules and we layer the
        // docs/system/contracts/code-patterns.md additions on top.
        IReadOnlyList<CodePatternRule> activeRules = _rules;
        if (ReferenceEquals(_rules, DefaultRules))
        {
            activeRules = LoadEffectiveRules(repoRoot, _logger);
        }

        foreach (var rule in activeRules)
        {
            try
            {
                var finding = AnalyzeRule(repoRoot, rule);
                findings.Add(finding);
                totalDrift += finding.DriftSites;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Code-pattern rule '{Rule}' threw; skipping", rule.Id);
            }
        }

        return new CodePatternDriftReport(
            CapturedAt: capturedAt,
            RepoRoot: repoRoot,
            Findings: findings,
            TotalDriftSites: totalDrift);
    }

    private CodePatternFinding AnalyzeRule(string repoRoot, CodePatternRule rule)
    {
        var fileGlob = new Regex(rule.FilePattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
        var excludeGlob = rule.ExcludeFilePattern is null
            ? null
            : new Regex(rule.ExcludeFilePattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);

        var hits = new List<CodePatternHit>();
        var canonicalCount = 0;
        var driftCount = 0;

        foreach (var file in EnumerateFiles(repoRoot))
        {
            var rel = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
            if (!fileGlob.IsMatch(rel)) continue;
            if (excludeGlob != null && excludeGlob.IsMatch(rel)) continue;

            string content;
            try { content = File.ReadAllText(file); }
            catch { continue; } // binary, locked, etc — skip silently

            if (!rule.CandidateMarker.IsMatch(content)) continue;

            // Decision logic:
            //  - If BadVariant matches: it's drift.
            //  - Else if GoodVariant matches: it's canonical.
            //  - Else (candidate but neither variant matches): it's
            //    "indeterminate" — surface as a low-severity hit so a
            //    reviewer can classify it.
            bool isBad;
            string evidence;
            if (rule.BadVariant != null && rule.BadVariant.IsMatch(content))
            {
                isBad = true;
                evidence = "bad-variant";
            }
            else if (rule.GoodVariant != null && rule.GoodVariant.IsMatch(content))
            {
                isBad = false;
                evidence = "canonical";
            }
            else
            {
                // No clear signal either way. Treat as drift candidate
                // when only GoodVariant is defined (the rule expects to
                // see the canonical signal); ignore otherwise.
                if (rule.GoodVariant != null && rule.BadVariant == null)
                {
                    isBad = true;
                    evidence = "missing-canonical";
                }
                else
                {
                    continue;
                }
            }

            var snippet = ExtractSnippet(content, rule.CandidateMarker);
            var line = LocateLine(content, rule.CandidateMarker);
            hits.Add(new CodePatternHit(rel, line, snippet, isBad, evidence));
            if (isBad) driftCount++;
            else canonicalCount++;
        }

        var overall = driftCount switch
        {
            0 => DriftSeverity.Info,
            _ => rule.SeverityIfBad,
        };

        return new CodePatternFinding(
            RuleId: rule.Id,
            Title: rule.Title,
            CanonicalDescription: rule.CanonicalDescription,
            TotalSites: canonicalCount + driftCount,
            CanonicalSites: canonicalCount,
            DriftSites: driftCount,
            Hits: hits,
            OverallSeverity: overall);
    }

    private static IEnumerable<string> EnumerateFiles(string repoRoot)
    {
        var stack = new Stack<string>();
        stack.Push(repoRoot);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            var name = Path.GetFileName(dir);
            if (name is "bin" or "obj" or "node_modules" or "dist" or ".git" or "artifacts") continue;

            IEnumerable<string> subDirs;
            IEnumerable<string> files;
            try
            {
                subDirs = Directory.EnumerateDirectories(dir);
                files = Directory.EnumerateFiles(dir);
            }
            catch { continue; }

            foreach (var f in files) yield return f;
            foreach (var d in subDirs) stack.Push(d);
        }
    }

    private static int LocateLine(string content, Regex marker)
    {
        var match = marker.Match(content);
        if (!match.Success) return 0;
        var prefix = content.AsSpan(0, match.Index);
        int line = 1;
        foreach (var c in prefix) if (c == '\n') line++;
        return line;
    }

    private static string ExtractSnippet(string content, Regex marker)
    {
        var match = marker.Match(content);
        if (!match.Success) return string.Empty;
        var start = Math.Max(0, match.Index - 40);
        var len = Math.Min(160, content.Length - start);
        var raw = content.Substring(start, len);
        return raw.Replace('\r', ' ').Replace('\n', ' ');
    }

    /// <summary>
    /// Optional Phase-2 enrichment: for every finding with at least one drift
    /// site, ask a fast Claude model whether the deterministic detector got
    /// it right. The LLM verdict is purely advisory — it adds a per-finding
    /// `LlmVerdict` (real-drift / false-positive / unclear) and a one-sentence
    /// reasoning to the report. The deterministic verdict is never overwritten;
    /// the LLM is an explainer, not the source of truth.
    /// </summary>
    /// <remarks>
    /// Bounded cost: one call per finding with drift (typically &lt;5). The
    /// per-call timeout is 30 s; a failure leaves the finding without an
    /// LLM verdict. Token spend lands on the AdHocUsageRecorder via the
    /// OneShot pipeline.
    /// </remarks>
    public async Task<CodePatternDriftReport> EnrichWithLlmVerdictsAsync(
        CodePatternDriftReport report,
        ICliOneShot oneShot,
        string? model = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(oneShot);
        model ??= ModelFamilyResolver.Resolve(ModelFamilies.ClaudeHaiku);

        var enrichedFindings = new List<CodePatternFinding>(report.Findings.Count);
        foreach (var finding in report.Findings)
        {
            if (finding.DriftSites == 0)
            {
                enrichedFindings.Add(finding);
                continue;
            }

            var prompt = BuildLlmPrompt(finding, report.RepoRoot);
            var result = await oneShot.RunAsync(new CliOneShotRequest(
                CliType: "claude", Model: model, Prompt: prompt)
            {
                Timeout = TimeSpan.FromSeconds(30),
                Source = AdHocUsageSources.ReviewDecision,
            }, ct).ConfigureAwait(false);

            var verdict = result.Ok ? ParseLlmVerdict(result.ParsedText) : null;
            enrichedFindings.Add(finding with { LlmVerdict = verdict });
        }

        return report with { Findings = enrichedFindings };
    }

    private string BuildLlmPrompt(CodePatternFinding finding, string repoRoot)
    {
        var drifters = finding.Hits.Where(h => h.IsDrift).Take(5).ToList();
        var canonicals = finding.Hits.Where(h => !h.IsDrift).Take(2).ToList();

        var canonicalBlock = canonicals.Count > 0
            ? _prompts.Render(CanonicalSitesTemplate, new Dictionary<string, string?>
              {
                  ["canonical_sites"] = FormatSites(canonicals),
              })
            : string.Empty;

        return _prompts.Render(ReviewTemplate, new Dictionary<string, string?>
        {
            ["pattern_title"] = finding.Title,
            ["canonical_description"] = finding.CanonicalDescription,
            ["canonical_sites_block"] = canonicalBlock,
            ["drift_sites"] = FormatSites(drifters),
        });
    }

    private static string FormatSites(IEnumerable<CodePatternHit> hits) =>
        string.Join("\n", hits.Select(h => $"- {h.FilePath}:{h.LineNumber}  →  {h.Snippet}"));

    private static readonly Regex LlmVerdictRegex = new(
        @"\[\[VERDICT:\s*(?<body>[^\]]+)\]\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static CodePatternLlmVerdict? ParseLlmVerdict(string? response)
    {
        if (string.IsNullOrWhiteSpace(response)) return null;
        var matches = LlmVerdictRegex.Matches(response);
        if (matches.Count == 0) return null;
        var body = matches[^1].Groups["body"].Value;
        string? status = null;
        string? reasoning = null;
        foreach (var part in body.Split(';'))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var key = part[..eq].Trim().ToLowerInvariant();
            var value = part[(eq + 1)..].Trim();
            if (key == "status") status = value.ToLowerInvariant();
            else if (key == "reasoning") reasoning = value;
        }
        if (status is null) return null;
        return new CodePatternLlmVerdict(
            Status: status switch
            {
                "real-drift" or "real" or "drift" => CodePatternLlmStatus.RealDrift,
                "false-positive" or "false" or "fp" => CodePatternLlmStatus.FalsePositive,
                _ => CodePatternLlmStatus.Unclear,
            },
            Reasoning: reasoning ?? "(no reasoning supplied)");
    }

    /// <summary>Render the report as Markdown for the human-facing
    /// sibling stored under <c>logs/drift/code-pattern-drift/</c>.</summary>
    public string RenderMarkdown(CodePatternDriftReport report, string? project = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        var sb = new StringBuilder();
        sb.AppendLine($"# Code pattern drift report");
        sb.AppendLine();
        sb.AppendLine($"- **Generated:** {report.CapturedAt:O}");
        if (!string.IsNullOrWhiteSpace(project)) sb.AppendLine($"- **Project:** {project}");
        sb.AppendLine($"- **Repo root:** `{report.RepoRoot}`");
        sb.AppendLine($"- **Rules evaluated:** {report.Findings.Count}");
        sb.AppendLine($"- **Total drift sites:** {report.TotalDriftSites}");
        sb.AppendLine();

        var overall = report.Findings.Max(f => (int)f.OverallSeverity);
        var overallBand = overall switch
        {
            (int)DriftSeverity.Critical => "CRITICAL",
            (int)DriftSeverity.High => "HIGH",
            (int)DriftSeverity.Warn => "WARN",
            _ => "OK",
        };
        sb.AppendLine($"**Overall:** {overallBand}");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        foreach (var finding in report.Findings)
        {
            sb.AppendLine($"## {finding.Title}");
            sb.AppendLine();
            sb.AppendLine($"- Rule: `{finding.RuleId}`");
            sb.AppendLine($"- Sites total: {finding.TotalSites} (canonical: {finding.CanonicalSites}, drift: {finding.DriftSites})");
            sb.AppendLine($"- Severity: **{finding.OverallSeverity}**");
            sb.AppendLine();
            sb.AppendLine($"> {finding.CanonicalDescription}");
            sb.AppendLine();

            var drifters = finding.Hits.Where(h => h.IsDrift).ToList();
            if (drifters.Count == 0)
            {
                sb.AppendLine("_No drift detected._");
                sb.AppendLine();
                continue;
            }

            sb.AppendLine("### Drift sites");
            sb.AppendLine();
            foreach (var hit in drifters.OrderBy(h => h.FilePath))
            {
                sb.AppendLine($"- `{hit.FilePath}:{hit.LineNumber}` — *{hit.Evidence}*");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }
}

/// <summary>One rule in the watchlist. Adding a rule = adding a record
/// to <see cref="CodePatternDriftAnalysisService.DefaultRules"/>.</summary>
/// <param name="Id">Stable slug used as the finding id.</param>
/// <param name="Title">Short human-readable title.</param>
/// <param name="CanonicalDescription">One- or two-line description of
/// the canonical pattern + the failure mode if a site drifts.</param>
/// <param name="FilePattern">Regex of file paths to consider, applied
/// to the path relative to the repo root with forward slashes.</param>
/// <param name="ExcludeFilePattern">Optional regex of paths to skip
/// (the canonical implementation itself, test files, build artefacts).</param>
/// <param name="CandidateMarker">Regex that signals "this file is a
/// candidate for the rule". Cheap; runs first.</param>
/// <param name="BadVariant">Optional regex for the wrong shape. When
/// it matches, the site is reported as drift.</param>
/// <param name="GoodVariant">Optional regex for the canonical shape.
/// When the candidate matches and GoodVariant does not, the site is
/// also reported as drift (with evidence "missing-canonical").</param>
/// <param name="SeverityIfBad">Severity to use for drift hits.</param>
public sealed record CodePatternRule(
    string Id,
    string Title,
    string CanonicalDescription,
    string FilePattern,
    string? ExcludeFilePattern,
    Regex CandidateMarker,
    Regex? BadVariant,
    Regex? GoodVariant,
    DriftSeverity SeverityIfBad);

/// <summary>One match in the report. <see cref="IsDrift"/> is true when
/// this site deviates from the rule's canonical shape.</summary>
public sealed record CodePatternHit(
    string FilePath,
    int LineNumber,
    string Snippet,
    bool IsDrift,
    string Evidence);

public sealed record CodePatternFinding(
    string RuleId,
    string Title,
    string CanonicalDescription,
    int TotalSites,
    int CanonicalSites,
    int DriftSites,
    IReadOnlyList<CodePatternHit> Hits,
    DriftSeverity OverallSeverity)
{
    /// <summary>Optional phase-2 enrichment from a Claude verdict pass.
    /// Null on findings that were not LLM-reviewed or where the LLM call
    /// failed.</summary>
    public CodePatternLlmVerdict? LlmVerdict { get; init; }
}

public sealed record CodePatternLlmVerdict(
    CodePatternLlmStatus Status,
    string Reasoning);

public enum CodePatternLlmStatus
{
    RealDrift,
    FalsePositive,
    Unclear,
}

public sealed record CodePatternDriftReport(
    DateTime CapturedAt,
    string RepoRoot,
    IReadOnlyList<CodePatternFinding> Findings,
    int TotalDriftSites);
