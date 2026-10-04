using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;
using AgentStudio.TestSupport;

namespace AgentStudio.Tests;

/// <summary>
/// End-to-end probe for the wiki-grading maintenance run (AGT-2051) over a
/// handful of REAL docs pages from this repository. It always runs the real
/// <see cref="WikiGradingService"/> (enumerate -&gt; grade -&gt; write companion ->
/// compose Pulse) and asserts the mechanics; when <c>WIKI_GRADING_PROBE_OUT</c>
/// is set it also exports the written companions plus a markdown summary + a JSON
/// snapshot to that directory as review evidence.
///
/// <para>The probe uses the deterministic <see cref="HeuristicWikiPageGrader"/>
/// rather than the live model rail: the dev backend is offline by default and a
/// live model pass is billable, so the probe proves the full plumbing without
/// spending tokens. The production run wires <see cref="CliWikiPageGrader"/>
/// through the same one-shot CLI rail the drift post-step uses.</para>
/// </summary>
public class WikiGradingProbe : IDisposable
{
    private readonly string _tempDir;

    public WikiGradingProbe()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "wiki-grading-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_tempDir, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            }
            Directory.Delete(_tempDir, recursive: true);
        }
        catch { /* best-effort */ }
    }

    // The real pages the probe grades (relative to docs/). Chosen to span
    // categories and lengths.
    private static readonly string[] ProbePages =
    [
        "start/README.md",
        "concepts/wiki-pulse-dashboard.md",
        "concepts/wiki-grading-run.md",
        "system/domains/cli.md",
        "start/wiki-document-classification.md",
    ];

    [Fact]
    public async Task Probe_GradesRealDocsPages_AndComposesPulse()
    {
        var docsRoot = Path.Combine(LocateRepoRoot(), "docs");

        // Copy the real pages into an isolated temp git repo so the probe never
        // mutates the working tree, while grading genuine page content.
        var repoRoot = Path.Combine(_tempDir, "probe-repo");
        var repoDocs = Path.Combine(repoRoot, "docs");
        Directory.CreateDirectory(repoDocs);
        Directory.CreateDirectory(Path.Combine(repoRoot, "backend"));
        var copied = new List<string>();
        foreach (var rel in ProbePages)
        {
            var src = Path.Combine(docsRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(src)) continue;
            var dst = Path.Combine(repoDocs, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(src, dst, overwrite: true);
            copied.Add(rel);
        }
        Assert.True(copied.Count >= 3, $"probe needs >= 3 real pages, found {copied.Count}");

        File.WriteAllText(Path.Combine(repoRoot, "backend", "svc.cs"), "// v0\n");
        RunGit(repoRoot, "init -q -b main");
        RunGit(repoRoot, "config user.email probe@example.com");
        RunGit(repoRoot, "config user.name probe");
        RunGit(repoRoot, "add -A");
        RunGit(repoRoot, "commit -q -m seed");

        var docs = BuildDocsService(("Probe", repoRoot));
        var git = BuildGitService(("Probe", repoRoot));
        var svc = new WikiGradingService(docs, new HeuristicWikiPageGrader(), new WikiCompanionStore(),
            NullLogger<WikiGradingService>.Instance);

        var req = new WikiGradingRunRequest("claude", "claude-sonnet-5", null, Force: false, Limit: 0);
        var status = await svc.RunToCompletionAsync("Probe", req);

        // --- Mechanics assertions ---
        Assert.Equal(WikiGradingRunState.Completed, status.State);
        Assert.Equal(copied.Count, status.Total);
        Assert.Equal(copied.Count, status.Graded);
        Assert.Equal(0, status.Failed);

        // Every graded page now carries a companion grading block.
        foreach (var rel in copied)
        {
            var companion = Path.Combine(repoDocs, rel.Replace('/', Path.DirectorySeparatorChar) + ".meta.json");
            Assert.True(File.Exists(companion), $"missing companion for {rel}");
            using var doc = JsonDocument.Parse(File.ReadAllText(companion));
            var grading = doc.RootElement.GetProperty("grading");
            Assert.Contains(grading.GetProperty("grade").GetString(), new[] { "A", "B", "C", "D" });
            Assert.Equal("claude-sonnet-5", grading.GetProperty("model").GetString());
        }

        // Pulse now composes a critical section from the companion grades.
        docs.InvalidateWikiTreeCache();
        var pulse = docs.GetWikiPulse("Probe", git);
        Assert.NotNull(pulse);
        Assert.True(pulse!.Critical.Available);

        // Idempotent re-run: same model, unchanged content -> everything skipped.
        var second = await svc.RunToCompletionAsync("Probe", req);
        Assert.Equal(copied.Count, second.Skipped);
        Assert.Equal(0, second.Graded);

        ExportEvidenceIfRequested(repoDocs, copied, status, pulse);
    }

    private static void ExportEvidenceIfRequested(
        string repoDocs, List<string> pages, WikiGradingRunStatus status, WikiPulse pulse)
    {
        var outDir = Environment.GetEnvironmentVariable("WIKI_GRADING_PROBE_OUT");
        if (string.IsNullOrWhiteSpace(outDir)) return;
        Directory.CreateDirectory(outDir);

        var companionDir = Path.Combine(outDir, "companions");
        Directory.CreateDirectory(companionDir);

        var rows = new List<(string Page, string Grade, string Outdated, string Gaps, string Assessment)>();
        foreach (var rel in pages)
        {
            var companion = Path.Combine(repoDocs, rel.Replace('/', Path.DirectorySeparatorChar) + ".meta.json");
            if (!File.Exists(companion)) continue;
            // Flatten the companion into a per-page evidence file.
            var safe = rel.Replace('/', '_') + ".meta.json";
            File.Copy(companion, Path.Combine(companionDir, safe), overwrite: true);

            using var doc = JsonDocument.Parse(File.ReadAllText(companion));
            var g = doc.RootElement.GetProperty("grading");
            rows.Add((
                rel,
                g.GetProperty("grade").GetString() ?? "?",
                g.TryGetProperty("outdated", out var o) ? o.ToString() : "-",
                g.TryGetProperty("gaps", out var gp) ? gp.ToString() : "-",
                g.GetProperty("assessment").GetString() ?? ""));
        }

        var sb = new StringBuilder();
        sb.AppendLine("# Wiki grading probe - real pages (AGT-2051)");
        sb.AppendLine();
        sb.AppendLine($"- Run id: `{status.RunId}`");
        sb.AppendLine($"- Grader: `HeuristicWikiPageGrader` (deterministic offline fallback; the live run uses the one-shot CLI rail)");
        sb.AppendLine($"- Model recorded: `{status.Model}`  |  CLI: `{status.CliType}`");
        sb.AppendLine($"- Pages graded: {status.Graded}/{status.Total}  |  skipped: {status.Skipped}  |  failed: {status.Failed}  |  critical (C/D): {status.Critical}");
        sb.AppendLine();
        sb.AppendLine("## Per-page grades (written into each page's `.meta.json` companion)");
        sb.AppendLine();
        sb.AppendLine("| Page | Grade | Outdated | Gaps | Assessment |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var r in rows)
            sb.AppendLine($"| `{r.Page}` | {r.Grade} | {r.Outdated} | {r.Gaps} | {r.Assessment} |");
        sb.AppendLine();
        sb.AppendLine("## Pulse critical section (composed from the companion grades)");
        sb.AppendLine();
        if (pulse.Critical.Count == 0)
        {
            sb.AppendLine($"_No critical pages among the probe set._ Reason: {pulse.Critical.Reason}");
        }
        else
        {
            sb.AppendLine($"Overall: **{pulse.Critical.OverallGrade}**, {pulse.Critical.Count} critical page(s), worst first:");
            sb.AppendLine();
            sb.AppendLine("| Page | Grade | Assessment | Report |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var i in pulse.Critical.Items)
                sb.AppendLine($"| `{i.RelPath}` | {i.Grade} | {i.Assessment} | `{i.ReportPath}` |");
        }
        sb.AppendLine();
        sb.AppendLine("## Idempotency");
        sb.AppendLine();
        sb.AppendLine("A second run with the same model over unchanged content skipped every page "
            + "(graded 0), proving the fingerprint-based idempotent skip.");

        File.WriteAllText(Path.Combine(outDir, "wiki-grading-probe--real.md"), sb.ToString());
        File.WriteAllText(Path.Combine(outDir, "wiki-grading-probe-status--real.json"),
            JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string LocateRepoRoot()
        => RepositoryRoot.Find();

    private ProjectDocsService BuildDocsService(params (string Name, string RootPath)[] entries)
        => new(BuildScanner(entries),
               new ProjectRegistry(BuildConfig(entries), NullLogger<ProjectRegistry>.Instance),
               NullLogger<ProjectDocsService>.Instance);

    private GitService BuildGitService(params (string Name, string RootPath)[] entries)
        => new(NullLogger<GitService>.Instance, BuildScanner(entries), BuildConfig(entries));

    private static IConfiguration BuildConfig((string Name, string RootPath)[] entries)
    {
        var dict = new Dictionary<string, string?>();
        for (var i = 0; i < entries.Length; i++)
        {
            dict[$"WatchPaths:{i}:Name"] = entries[i].Name;
            dict[$"WatchPaths:{i}:RootPath"] = entries[i].RootPath;
            dict[$"WatchPaths:{i}:Path"] = Path.Combine(entries[i].RootPath, ".orchestrator", "jobs");
        }
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    private static TaskScannerService BuildScanner((string Name, string RootPath)[] entries)
    {
        var config = BuildConfig(entries);
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        return new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
    }

    private static void RunGit(string cwd, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit(15_000);
    }
}
