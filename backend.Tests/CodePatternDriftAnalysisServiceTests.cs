using Microsoft.Extensions.Logging.Abstractions;

using Xunit;
using Xunit.Abstractions;
using AgentStudio.TestSupport;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the CodePatternDriftAnalysisService contract: rule detection
/// classifies sites as canonical / drift / missing-canonical correctly,
/// and the live repo scan (when run against the dev checkout) reports
/// zero drift on the rules our CLI-OneShot fix just closed.
/// </summary>
public class CodePatternDriftAnalysisServiceTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _root;

    public CodePatternDriftAnalysisServiceTests(ITestOutputHelper output)
    {
        _out = output;
        _root = Path.Combine(Path.GetTempPath(), "code-pattern-drift-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { }
    }

    [Fact]
    public void Analyze_DetectsBadVariant_AndPassesCanonical()
    {
        // Drift site: ArgumentList.Add("-p") immediately followed by Add(prompt).
        WriteFile("BadSite.cs", """
            using System.Diagnostics;
            public class BadSite {
              public void Run(string prompt, string model) {
                var psi = new ProcessStartInfo { FileName = "claude" };
                psi.ArgumentList.Add("--dangerously-skip-permissions");
                psi.ArgumentList.Add("-p");
                psi.ArgumentList.Add(prompt);
              }
            }
            """);

        // Canonical site: stdin-piped prompt via StandardInput.WriteAsync.
        WriteFile("GoodSite.cs", """
            using System.Diagnostics;
            public class GoodSite {
              public void Run(string prompt) {
                var psi = new ProcessStartInfo { FileName = "claude", RedirectStandardInput = true };
                using var p = Process.Start(psi);
                p.StandardInput.WriteAsync(prompt);
              }
            }
            """);

        var svc = new CodePatternDriftAnalysisService(NullLogger<CodePatternDriftAnalysisService>.Instance);
        var report = svc.Analyze(_root);

        var cliFinding = report.Findings.Single(f => f.RuleId == "cli-one-shot-stdin");
        Assert.Equal(2, cliFinding.TotalSites);
        Assert.Equal(1, cliFinding.CanonicalSites);
        Assert.Equal(1, cliFinding.DriftSites);
        Assert.Equal(DriftSeverity.High, cliFinding.OverallSeverity);
        Assert.Contains(cliFinding.Hits, h => h.IsDrift && h.FilePath.EndsWith("BadSite.cs"));
        Assert.Contains(cliFinding.Hits, h => !h.IsDrift && h.FilePath.EndsWith("GoodSite.cs"));
    }

    [Fact]
    public void Analyze_ReportsMissingCanonicalAsDrift_WhenOnlyGoodVariantDefined()
    {
        // JSONL append rule: only GoodVariant defined; a file that
        // matches the candidate but lacks SemaphoreSlim is flagged.
        WriteFile("NoLockAppender.cs", """
            using System.IO;
            public class NoLockAppender {
              public void Append(string path) {
                using var s = new FileStream(path, FileMode.Append, FileAccess.Write);
                // FilePath ends with .jsonl
                var p = "/logs/example.jsonl";
              }
            }
            """);

        WriteFile("LockedAppender.cs", """
            using System.IO;
            using System.Threading;
            public class LockedAppender {
              private readonly SemaphoreSlim _lock = new(1, 1);
              public void Append(string path) {
                _lock.Wait();
                using var s = new FileStream(path, FileMode.Append, FileAccess.Write);
                var p = "/logs/example.jsonl";
              }
            }
            """);

        var svc = new CodePatternDriftAnalysisService(NullLogger<CodePatternDriftAnalysisService>.Instance);
        var report = svc.Analyze(_root);
        var finding = report.Findings.Single(f => f.RuleId == "jsonl-append-locked");

        Assert.Equal(1, finding.CanonicalSites);
        Assert.Equal(1, finding.DriftSites);
        Assert.Contains(finding.Hits, h => h.IsDrift && h.FilePath.EndsWith("NoLockAppender.cs") && h.Evidence == "missing-canonical");
    }

    [Fact]
    public void Analyze_ExcludesTestAndArtifactFolders()
    {
        // Even if a "bad" pattern lives in backend.Tests/, it is excluded.
        Directory.CreateDirectory(Path.Combine(_root, "backend.Tests"));
        File.WriteAllText(Path.Combine(_root, "backend.Tests", "FakeSite.cs"), """
            using System.Diagnostics;
            public class FakeSite {
              public void Run(string prompt) {
                var psi = new ProcessStartInfo { FileName = "claude" };
                psi.ArgumentList.Add("-p");
                psi.ArgumentList.Add(prompt);
              }
            }
            """);

        var svc = new CodePatternDriftAnalysisService(NullLogger<CodePatternDriftAnalysisService>.Instance);
        var report = svc.Analyze(_root);

        var cliFinding = report.Findings.Single(f => f.RuleId == "cli-one-shot-stdin");
        Assert.Equal(0, cliFinding.TotalSites);
        Assert.Equal(0, cliFinding.DriftSites);
    }

    // Machine-bound: scans the actual on-disk dev checkout and asserts it is
    // drift-free, so its result depends on the exact checkout state and OS path
    // handling rather than a hermetic fixture. Excluded from the CI gate via
    // `--filter Category!=MachineBound`; still runs on the dev machine.
    [Trait("Category", "MachineBound")]
    [Fact]
    public void Analyze_AgainstLiveDevCheckout_ReportsZeroCliDriftAfterFix()
    {
        // Smoke test against the actual dev repo to prove the post-fix
        // state is clean. Locates the repo by walking up from the test
        // binary location until AGENTS.md is found. Skipped if the marker
        // is not reachable (e.g. CI builds the test out-of-tree).
        var repo = LocateRepoRoot();
        if (repo is null)
        {
            _out.WriteLine("No AGENTS.md upstream; skipping live-repo smoke test");
            return;
        }

        var svc = new CodePatternDriftAnalysisService(NullLogger<CodePatternDriftAnalysisService>.Instance);
        var report = svc.Analyze(repo);
        var cli = report.Findings.Single(f => f.RuleId == "cli-one-shot-stdin");

        _out.WriteLine($"live-cli-one-shot-stdin: total={cli.TotalSites} canonical={cli.CanonicalSites} drift={cli.DriftSites}");
        foreach (var drift in cli.Hits.Where(h => h.IsDrift))
        {
            _out.WriteLine($"  drift: {drift.FilePath}:{drift.LineNumber} ({drift.Evidence})");
        }
        Assert.Equal(0, cli.DriftSites);
    }

    [Fact]
    public void Analyze_FlagsMoveJobToProgress_OutsideRunnerPickupPath()
    {
        // lane-write-3-progress-forbidden rule (added in response to the
        // 2026-05-11 race where the auto-review reissue path moved a job
        // straight into 3-progress while the runner picked another in
        // the same window). Inject the rule directly so the fixture
        // doesn't depend on docs/system/contracts/code-patterns.md being loaded.
        WriteFile("backend/Services/Runner/SomeOtherService.cs", """
            public class SomeOtherService {
              private readonly object _states = null!;
              public void Run(string jobId, string watchPath) {
                ((dynamic)_states).MoveJob(jobId, TaskStates.Progress, watchPath);
              }
            }
            """);

        WriteFile("backend/Services/Runner/CleanReissue.cs", """
            public class CleanReissue {
              private readonly object _states = null!;
              public void Run(string jobId, string watchPath) {
                ((dynamic)_states).MoveJob(jobId, TaskStates.Ready, watchPath);
              }
            }
            """);

        var rule = new CodePatternRule(
            Id: "lane-write-3-progress-forbidden",
            Title: "MoveJob to TaskStates.Progress is reserved for the runner pickup path",
            CanonicalDescription: "Only ProjectRunner.TickAsync may move a job into 3-progress.",
            FilePattern: @"backend/.*\.cs$",
            ExcludeFilePattern: @"Services[/\\]Runner[/\\]ProjectRunner\.cs",
            CandidateMarker: new System.Text.RegularExpressions.Regex(@"\.MoveJob\s*\("),
            BadVariant: new System.Text.RegularExpressions.Regex(
                @"\.MoveJob\s*\([^,]+,\s*(?:TaskStates\.Progress\b|""3-progress"")"),
            GoodVariant: null,
            SeverityIfBad: DriftSeverity.High);

        var svc = new CodePatternDriftAnalysisService(
            NullLogger<CodePatternDriftAnalysisService>.Instance,
            rules: new[] { rule });
        var report = svc.Analyze(_root);
        var finding = report.Findings.Single(f => f.RuleId == "lane-write-3-progress-forbidden");

        Assert.Equal(1, finding.DriftSites);
        Assert.Contains(finding.Hits, h => h.IsDrift && h.FilePath.EndsWith("SomeOtherService.cs"));
        Assert.DoesNotContain(finding.Hits, h => h.IsDrift && h.FilePath.EndsWith("CleanReissue.cs"));
        Assert.Equal(DriftSeverity.High, finding.OverallSeverity);
    }

    [Trait("Category", "MachineBound")]
    [Fact]
    public void Analyze_AgainstLiveDevCheckout_ReportsZeroLaneWriteDriftAfterFix()
    {
        // The post-fix invariant: ProjectRunner.TickAsync is the only
        // legitimate writer of 3-progress; every other call site routes
        // through 2-ready (with order 0). Live-repo smoke test mirrors
        // the cli-one-shot-stdin assertion above.
        var repo = LocateRepoRoot();
        if (repo is null)
        {
            _out.WriteLine("No AGENTS.md upstream; skipping live-repo smoke test");
            return;
        }

        var svc = new CodePatternDriftAnalysisService(NullLogger<CodePatternDriftAnalysisService>.Instance);
        var report = svc.Analyze(repo);
        var finding = report.Findings.Single(f => f.RuleId == "lane-write-3-progress-forbidden");

        _out.WriteLine($"live-lane-write-3-progress-forbidden: total={finding.TotalSites} canonical={finding.CanonicalSites} drift={finding.DriftSites}");
        foreach (var drift in finding.Hits.Where(h => h.IsDrift))
        {
            _out.WriteLine($"  drift: {drift.FilePath}:{drift.LineNumber} ({drift.Evidence})");
        }
        Assert.Equal(0, finding.DriftSites);
    }

    [Fact]
    public void Analyze_FlagsCardSourcingCommitsFromRepoHead_NotJobCommits()
    {
        // card-commit-source-not-repo-head rule (added 2026-06-01 after review
        // cards leaked the shared project's working-tree summary as if it were
        // the task's own commits — "main: 20 files"). A board surface that pulls
        // in GitSummaryService / gitSummary without the LANES_WITH_GIT
        // (3-progress-only) guard is the drift. Inject the rule directly so the
        // fixture does not depend on docs/system/contracts/code-patterns.md being loaded.
        WriteFile("frontend/src/app/features/board/components/leaky-card/leaky-card.component.ts", """
            export class LeakyCard {
              private readonly gitSummary = inject(GitSummaryService);
              readonly pill = computed(() => {
                const s = this.gitSummary.value().find(x => x.projectName === this.job().projectName);
                return s ? `${s.branch}: ${s.filesChanged} files` : null;
              });
            }
            """);

        WriteFile("frontend/src/app/features/board/components/clean-card/clean-card.component.ts", """
            export class CleanCard {
              private static readonly LANES_WITH_GIT = new Set(['3-progress']);
              private readonly gitSummary = inject(GitSummaryService);
              readonly pill = computed(() => {
                if (!CleanCard.LANES_WITH_GIT.has(this.job().state)) return null;
                return this.gitSummary.value().find(x => x.projectName === this.job().projectName) ?? null;
              });
            }
            """);

        var rule = new CodePatternRule(
            Id: "card-commit-source-not-repo-head",
            Title: "Card commit/file display sources from job.commits, not repo HEAD",
            CanonicalDescription: "Per-task commit/file displays read job.commits, not the shared GitSummaryService.",
            FilePattern: @"frontend/src/app/features/board/.*\.ts$",
            ExcludeFilePattern: @"\.spec\.ts$",
            CandidateMarker: new System.Text.RegularExpressions.Regex(@"GitSummaryService\b|\bgitSummary\b"),
            BadVariant: null,
            GoodVariant: new System.Text.RegularExpressions.Regex(@"LANES_WITH_GIT\b"),
            SeverityIfBad: DriftSeverity.High);

        var svc = new CodePatternDriftAnalysisService(
            NullLogger<CodePatternDriftAnalysisService>.Instance,
            rules: new[] { rule });
        var report = svc.Analyze(_root);
        var finding = report.Findings.Single(f => f.RuleId == "card-commit-source-not-repo-head");

        Assert.Equal(1, finding.CanonicalSites);
        Assert.Equal(1, finding.DriftSites);
        Assert.Contains(finding.Hits, h => h.IsDrift && h.FilePath.EndsWith("leaky-card.component.ts") && h.Evidence == "missing-canonical");
        Assert.Contains(finding.Hits, h => !h.IsDrift && h.FilePath.EndsWith("clean-card.component.ts"));
        Assert.Equal(DriftSeverity.High, finding.OverallSeverity);
    }

    [Trait("Category", "MachineBound")]
    [Fact]
    public void Analyze_AgainstLiveDevCheckout_ReportsZeroCommitSourceDriftAfterFix()
    {
        // Post-fix invariant: every board surface that touches GitSummaryService
        // gates it behind LANES_WITH_GIT. The docs/system/contracts/code-patterns.md rule is
        // merged into the default rule set by Analyze, so this also proves the
        // docs block parses. Skipped when the repo marker is not reachable.
        var repo = LocateRepoRoot();
        if (repo is null)
        {
            _out.WriteLine("No AGENTS.md upstream; skipping live-repo smoke test");
            return;
        }

        var svc = new CodePatternDriftAnalysisService(NullLogger<CodePatternDriftAnalysisService>.Instance);
        var report = svc.Analyze(repo);
        var finding = report.Findings.Single(f => f.RuleId == "card-commit-source-not-repo-head");

        _out.WriteLine($"live-card-commit-source-not-repo-head: total={finding.TotalSites} canonical={finding.CanonicalSites} drift={finding.DriftSites}");
        foreach (var drift in finding.Hits.Where(h => h.IsDrift))
        {
            _out.WriteLine($"  drift: {drift.FilePath}:{drift.LineNumber} ({drift.Evidence})");
        }
        Assert.Equal(0, finding.DriftSites);
    }

    [Fact]
    public void Analyze_FlagsNativeTitleTooltips_ButAllowsComponentTitleInputs()
    {
        WriteFile("frontend/src/app/features/demo/native-title.component.html", """
            <button type="button" title="Slow native tooltip">Run</button>
            <code [title]="tag.id">{{ tag.id }}</code>
            """);

        WriteFile("frontend/src/app/features/demo/generated-title.renderer.ts", """
            export function render(title: string) {
              const titleAttr = title ? ` title="${title}"` : '';
              return `<a href="#">x</a>`;
            }
            """);

        WriteFile("frontend/src/app/features/demo/clean.component.html", """
            <app-dialog title="Visible dialog title"></app-dialog>
            <app-section-header [title]="heading()"></app-section-header>
            <button type="button" [appTooltip]="'Instant app tooltip'">Run</button>
            """);

        var rule = new CodePatternRule(
            Id: "tooltip-canonical-directive",
            Title: "Tooltips use [appTooltip], not native title attributes",
            CanonicalDescription: "Native browser tooltip behaviour must go through [appTooltip].",
            FilePattern: @"frontend/src/app/.*\.(html|ts)$",
            ExcludeFilePattern: @"frontend/src/app/components/tooltip/",
            CandidateMarker: new System.Text.RegularExpressions.Regex(
                @"title=|\[title\]|\[attr\.title\]|appTip|titleAttr|selector:\s*['""][^'""]*tooltip|class\s+\w*Tooltip"),
            BadVariant: new System.Text.RegularExpressions.Regex(
                @"(?i)<(?!app-|ng-|mat-|cdk-)[a-z][\w-]*(?=[^>]*(?:\s(?:title|\[title\]|\[attr\.title\])\s*=))|` title=""|titleAttr\s*=|\[appTip\]|selector:\s*['""][^'""]*tooltip|class\s+\w*Tooltip"),
            GoodVariant: null,
            SeverityIfBad: DriftSeverity.Warn);

        var svc = new CodePatternDriftAnalysisService(
            NullLogger<CodePatternDriftAnalysisService>.Instance,
            rules: new[] { rule });
        var report = svc.Analyze(_root);
        var finding = report.Findings.Single(f => f.RuleId == "tooltip-canonical-directive");

        Assert.Equal(2, finding.DriftSites);
        Assert.Contains(finding.Hits, h => h.IsDrift && h.FilePath.EndsWith("native-title.component.html"));
        Assert.Contains(finding.Hits, h => h.IsDrift && h.FilePath.EndsWith("generated-title.renderer.ts"));
        Assert.DoesNotContain(finding.Hits, h => h.IsDrift && h.FilePath.EndsWith("clean.component.html"));
    }

    [Trait("Category", "MachineBound")]
    [Fact]
    public void Analyze_AgainstLiveDevCheckout_ReportsZeroTooltipDirectiveDrift()
    {
        var repo = LocateRepoRoot();
        if (repo is null)
        {
            _out.WriteLine("No AGENTS.md upstream; skipping live-repo smoke test");
            return;
        }

        var svc = new CodePatternDriftAnalysisService(NullLogger<CodePatternDriftAnalysisService>.Instance);
        var report = svc.Analyze(repo);
        var finding = report.Findings.Single(f => f.RuleId == "tooltip-canonical-directive");

        _out.WriteLine($"live-tooltip-canonical-directive: total={finding.TotalSites} canonical={finding.CanonicalSites} drift={finding.DriftSites}");
        foreach (var drift in finding.Hits.Where(h => h.IsDrift))
        {
            _out.WriteLine($"  drift: {drift.FilePath}:{drift.LineNumber} ({drift.Evidence})");
        }
        // Drift-Ratsche statt Nullforderung: die kanonische [appTooltip]-Direktive
        // existiert noch nicht (AGT-2156 baut sie und konvertiert die Bestandssites).
        // Baseline 2026-07-12: 15 bekannte Sites. Jeder NEUE Verstoss reisst die
        // Ratsche; AGT-2156 senkt sie auf 0 und stellt Assert.Equal(0, ...) wieder her.
        Assert.True(finding.DriftSites <= 15,
            $"Tooltip drift ratchet exceeded: {finding.DriftSites} sites (baseline 15). " +
            "New native title= tooltips are not allowed; use [appTooltip] (AGT-2156).");
    }

    private void WriteFile(string relativePath, string contents)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, contents);
    }

    private static string? LocateRepoRoot()
        => RepositoryRoot.TryFind();
}
