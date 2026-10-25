using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Covers the wiki-grading maintenance run (AGT-2051) end-to-end over a real temp
/// git repo with a deterministic grader seam: enumerate -&gt; grade -&gt; write the
/// companion <c>grading</c> block, plus the run's progress counters, idempotent
/// skips, force re-grade, mid-run abort, and the Pulse critical-pages section that
/// surfaces C/D grades.
/// </summary>
public class WikiGradingServiceTests : IDisposable
{
    private readonly string _tempDir;

    public WikiGradingServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "wiki-grading-tests-" + Guid.NewGuid().ToString("N"));
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

    // ---- Verdict parsing (pure) ----

    [Fact]
    public void VerdictParser_ExtractsJsonFromFencedProse()
    {
        var reply = "Here is my verdict:\n```json\n{\"grade\":\"C\",\"assessment\":\"Weak page.\"," +
                    "\"outdated\":true,\"contradictory\":false,\"gaps\":true,\"notes\":[\"stale\"]}\n```\nDone.";
        var verdict = WikiGradeVerdictParser.TryParse(reply);
        Assert.NotNull(verdict);
        Assert.Equal("C", verdict!.Grade);
        Assert.Equal("Weak page.", verdict.Assessment);
        Assert.True(verdict.Outdated);
        Assert.False(verdict.Contradictory);
        Assert.True(verdict.Gaps);
        Assert.Contains("stale", verdict.Notes);
        Assert.True(verdict.Ok);
    }

    [Theory]
    [InlineData("no json here")]
    [InlineData("")]
    [InlineData(null)]
    public void VerdictParser_ReturnsNullWhenNoJsonObject(string? reply)
    {
        Assert.Null(WikiGradeVerdictParser.TryParse(reply));
    }

    // ---- Run: grades pages and writes companion grading blocks ----

    [Fact]
    public async Task Run_GradesAllPages_WritesGradingBlockAndCounts()
    {
        var repoRoot = SeedRepo(new()
        {
            ["concepts/alpha.md"] = "# Alpha\n\nSolid content that describes current behaviour.\n",
            ["concepts/beta.md"] = "# Beta\n\nAnother page.\n",
            ["guides/gamma.md"] = "# Gamma\n\nGuide content.\n",
        });

        var grader = new StubGrader(input => Verdict(GradeFor(input.RelPath)));
        var svc = BuildGrading(grader, ("Proj", repoRoot));

        var status = await svc.RunToCompletionAsync("Proj", Req());

        Assert.Equal(WikiGradingRunState.Completed, status.State);
        Assert.Equal(3, status.Total);
        Assert.Equal(3, status.Processed);
        Assert.Equal(3, status.Graded);
        Assert.Equal(0, status.Skipped);
        Assert.Equal(0, status.Failed);
        // alpha -> A, beta -> B, gamma -> C: one critical (C).
        Assert.Equal(1, status.Critical);

        // The companion carries the grading block with the run's model + fingerprint.
        var wikiDir = Path.Combine(repoRoot, "docs");
        var alpha = ReadGrading(Path.Combine(wikiDir, "concepts", "alpha.md.meta.json"));
        Assert.Equal("A", alpha.GetProperty("grade").GetString());
        Assert.Equal(TestModel, alpha.GetProperty("model").GetString());
        Assert.Equal("wiki-grading-run", alpha.GetProperty("method").GetString());
        Assert.False(string.IsNullOrWhiteSpace(alpha.GetProperty("sourceFingerprint").GetProperty("hash").GetString()));

        var gamma = ReadGrading(Path.Combine(wikiDir, "guides", "gamma.md.meta.json"));
        Assert.Equal("C", gamma.GetProperty("grade").GetString());
    }

    // ---- Idempotency: unchanged pages skipped on re-run, force re-grades ----

    [Fact]
    public async Task Run_IsIdempotent_SkipsUnchangedPages_ForceRegrades()
    {
        var repoRoot = SeedRepo(new()
        {
            ["concepts/alpha.md"] = "# Alpha\n\nContent.\n",
            ["concepts/beta.md"] = "# Beta\n\nContent.\n",
        });

        var grader = new StubGrader(input => Verdict("B"));
        var svc = BuildGrading(grader, ("Proj", repoRoot));

        var first = await svc.RunToCompletionAsync("Proj", Req());
        Assert.Equal(2, first.Graded);
        Assert.Equal(2, grader.Calls);

        // Second run, same model, nothing changed -> everything skipped, grader untouched.
        var second = await svc.RunToCompletionAsync("Proj", Req());
        Assert.Equal(0, second.Graded);
        Assert.Equal(2, second.Skipped);
        Assert.Equal(2, grader.Calls); // no new grader calls

        // Force -> re-grades all, even unchanged.
        var forced = await svc.RunToCompletionAsync("Proj", Req(force: true));
        Assert.Equal(2, forced.Graded);
        Assert.Equal(0, forced.Skipped);
        Assert.Equal(4, grader.Calls);
    }

    [Fact]
    public async Task Run_RegradesOnlyPagesWhoseContentChanged()
    {
        var repoRoot = SeedRepo(new()
        {
            ["a.md"] = "# A\n\nContent.\n",
            ["b.md"] = "# B\n\nContent.\n",
        });
        var grader = new StubGrader(input => Verdict("B"));
        var svc = BuildGrading(grader, ("Proj", repoRoot));

        await svc.RunToCompletionAsync("Proj", Req());
        Assert.Equal(2, grader.Calls);

        // Mutate only a.md; b.md fingerprint is unchanged.
        File.WriteAllText(Path.Combine(repoRoot, "docs", "a.md"), "# A\n\nContent changed materially.\n");

        var second = await svc.RunToCompletionAsync("Proj", Req());
        Assert.Equal(1, second.Graded);   // a.md re-graded
        Assert.Equal(1, second.Skipped);  // b.md skipped
        Assert.Equal(3, grader.Calls);
    }

    // ---- Limit caps the page count (probe path) ----

    [Fact]
    public async Task Run_Limit_CapsPageCount()
    {
        var repoRoot = SeedRepo(new()
        {
            ["a.md"] = "# A\n\nContent.\n",
            ["b.md"] = "# B\n\nContent.\n",
            ["c.md"] = "# C\n\nContent.\n",
            ["d.md"] = "# D\n\nContent.\n",
        });
        var grader = new StubGrader(input => Verdict("B"));
        var svc = BuildGrading(grader, ("Proj", repoRoot));

        var status = await svc.RunToCompletionAsync("Proj", Req(limit: 2));
        Assert.Equal(2, status.Total);
        Assert.Equal(2, status.Graded);
        Assert.Equal(2, grader.Calls);
    }

    // ---- Abort: cancels mid-run ----

    [Fact]
    public async Task Run_AbortMidRun_LeavesRunAbortedAndStopsGrading()
    {
        var repoRoot = SeedRepo(new()
        {
            ["a.md"] = "# A\n\nContent.\n",
            ["b.md"] = "# B\n\nContent.\n",
            ["c.md"] = "# C\n\nContent.\n",
        });

        using var cts = new CancellationTokenSource();
        // Cancel the run right after the first page is graded.
        var grader = new StubGrader(input =>
        {
            cts.Cancel();
            return Verdict("B");
        });
        var svc = BuildGrading(grader, ("Proj", repoRoot));

        var status = await svc.RunToCompletionAsync("Proj", Req(), cts.Token);

        Assert.Equal(WikiGradingRunState.Aborted, status.State);
        Assert.True(status.Graded < status.Total, "abort should stop before all pages are graded");
        Assert.Equal(1, grader.Calls);
    }

    // ---- A second Start while running is rejected as busy ----

    [Fact]
    public void Start_WhileRunning_IsRejectedAsBusy()
    {
        var repoRoot = SeedRepo(new() { ["a.md"] = "# A\n\nContent.\n" });
        var gate = new SemaphoreSlim(0, 1);
        var grader = new StubGrader(input => { gate.Wait(); return Verdict("B"); });
        var svc = BuildGrading(grader, ("Proj", repoRoot));

        var first = svc.Start("Proj", Req());
        Assert.True(first.Started);

        var second = svc.Start("Proj", Req());
        Assert.False(second.Started);
        Assert.NotNull(second.Error);
        Assert.NotNull(second.Status);

        gate.Release(); // let the background run finish so Dispose can clean up
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (svc.GetStatus("Proj")?.State == WikiGradingRunState.Running && DateTime.UtcNow < deadline)
            Thread.Sleep(20);
    }

    // ---- Pulse: critical section surfaces C/D grades worst-first ----

    [Fact]
    public async Task Pulse_CriticalSection_ListsBadlyGradedPagesWorstFirst()
    {
        var repoRoot = SeedRepo(new()
        {
            ["good.md"] = "# Good\n\nHealthy page.\n",
            ["weak.md"] = "# Weak\n\nWeak page.\n",
            ["poor.md"] = "# Poor\n\nPoor page.\n",
        });

        var grader = new StubGrader(input => Verdict(input.RelPath switch
        {
            "good.md" => "A",
            "weak.md" => "C",
            "poor.md" => "D",
            _ => "B",
        }));
        var docs = BuildDocsService(("Proj", repoRoot));
        var git = BuildGitService(("Proj", repoRoot));
        var svc = new WikiGradingService(docs, grader, new WikiCompanionStore(), NullLogger<WikiGradingService>.Instance);

        await svc.RunToCompletionAsync("Proj", Req());
        docs.InvalidateWikiTreeCache();

        var pulse = docs.GetWikiPulse("Proj", git);
        Assert.NotNull(pulse);
        Assert.True(pulse!.Critical.Available);
        Assert.Equal(2, pulse.Critical.Count);
        // Worst first: D (poor) before C (weak).
        Assert.Equal("poor.md", pulse.Critical.Items[0].RelPath);
        Assert.Equal("D", pulse.Critical.Items[0].Grade);
        Assert.Equal("weak.md", pulse.Critical.Items[1].RelPath);
        Assert.Equal("D", pulse.Critical.OverallGrade);
        Assert.DoesNotContain(pulse.Critical.Items, i => i.RelPath == "good.md");
    }

    [Fact]
    public void Pulse_CriticalSection_UngradedWikiIsHealthyWithHint()
    {
        var repoRoot = SeedRepo(new() { ["a.md"] = "# A\n\nContent.\n" });
        var docs = BuildDocsService(("Proj", repoRoot));
        var git = BuildGitService(("Proj", repoRoot));

        var pulse = docs.GetWikiPulse("Proj", git);
        Assert.NotNull(pulse);
        Assert.True(pulse!.Critical.Available);
        Assert.Equal(0, pulse.Critical.Count);
        Assert.False(string.IsNullOrWhiteSpace(pulse.Critical.Reason));
    }

    // ---- Heuristic grader spreads a real page set across grades ----

    [Fact]
    public async Task HeuristicGrader_ProducesCriticalGradeForStalePage()
    {
        var repoRoot = SeedRepo(new()
        {
            ["healthy.md"] = "# Healthy\n\n" + string.Join(' ', Enumerable.Repeat("clear stable documented behaviour", 30)) + "\n",
            ["stale.md"] = "# Stale\n\nThis is deprecated. TODO rewrite. TODO expand.\n",
        });
        var svc = BuildGrading(new HeuristicWikiPageGrader(), ("Proj", repoRoot));

        var status = await svc.RunToCompletionAsync("Proj", Req());
        Assert.Equal(2, status.Graded);
        Assert.True(status.Critical >= 1);

        var stale = ReadGrading(Path.Combine(repoRoot, "docs", "stale.md.meta.json"));
        Assert.Contains(stale.GetProperty("grade").GetString(), new[] { "C", "D" });
    }

    [Fact]
    public async Task Run_DoesNotReadDeliveredPageLink()
    {
        if (OperatingSystem.IsWindows()) return;
        var repoRoot = SeedRepo(new() { ["safe.md"] = "# Safe\n" });
        var outside = Path.Combine(_tempDir, "outside.md");
        File.WriteAllText(outside, "private content");
        File.CreateSymbolicLink(Path.Combine(repoRoot, "docs", "leak.md"), outside);
        var seen = new List<string>();
        var grader = new StubGrader(input =>
        {
            seen.Add(input.Content);
            return Verdict("B");
        });

        await BuildGrading(grader, ("Proj", repoRoot)).RunToCompletionAsync("Proj", Req());

        Assert.DoesNotContain("private content", seen);
        Assert.False(File.Exists(Path.Combine(repoRoot, "docs", "leak.md.meta.json")));
    }

    [Fact]
    public void CompanionWrites_RejectDeliveredLinks()
    {
        if (OperatingSystem.IsWindows()) return;
        var wikiDir = Directory.CreateDirectory(Path.Combine(_tempDir,
            "companion-links-" + Guid.NewGuid().ToString("N"), "docs")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(_tempDir,
            "outside-" + Guid.NewGuid().ToString("N"))).FullName;
        var outsideFile = Path.Combine(outside, "companion.json");
        File.WriteAllText(outsideFile, "private content");
        Directory.CreateSymbolicLink(Path.Combine(wikiDir, "linked"), outside);
        File.CreateSymbolicLink(Path.Combine(wikiDir, "page.md.meta.json"), outsideFile);
        var store = new WikiCompanionStore();
        var now = new DateTime(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc);

        Assert.Throws<IOException>(() => store.WriteCreationClassification(
            wikiDir, "linked/page.md", "Page", "# Page", null, now));
        Assert.Throws<IOException>(() => store.WriteGrading(
            wikiDir, "page.md", "Page", "# Page", Verdict("B"), Req(), "run-1", now));
        Assert.Equal("private content", File.ReadAllText(outsideFile));
        Assert.False(File.Exists(Path.Combine(outside, "page.md.meta.json")));
    }

    // ---- helpers ----

    private const string TestModel = "claude-sonnet-5";

    private static WikiGradingRunRequest Req(bool force = false, int limit = 0)
        => new("claude", TestModel, null, force, limit);

    private static WikiPageGradeVerdict Verdict(string grade) => new(
        Grade: grade,
        Assessment: $"Assessment for grade {grade}.",
        Outdated: grade is "C" or "D",
        Contradictory: false,
        Gaps: grade == "D",
        Notes: new[] { $"note-{grade}" },
        Ok: true,
        Error: null);

    private static string GradeFor(string relPath) => relPath switch
    {
        "concepts/alpha.md" => "A",
        "concepts/beta.md" => "B",
        "guides/gamma.md" => "C",
        _ => "B",
    };

    private static JsonElement ReadGrading(string companionPath)
    {
        Assert.True(File.Exists(companionPath), $"companion missing: {companionPath}");
        using var doc = JsonDocument.Parse(File.ReadAllText(companionPath));
        return doc.RootElement.GetProperty("grading").Clone();
    }

    private WikiGradingService BuildGrading(IWikiPageGrader grader, params (string Name, string RootPath)[] entries)
        => new(BuildDocsService(entries), grader, new WikiCompanionStore(), NullLogger<WikiGradingService>.Instance);

    /// <summary>Creates a git repo with a docs/ tree from a relPath -> content map.</summary>
    private string SeedRepo(Dictionary<string, string> pages)
    {
        var repoRoot = Path.Combine(_tempDir, "repo-" + Guid.NewGuid().ToString("N")[..8]);
        var docsDir = Path.Combine(repoRoot, "docs");
        Directory.CreateDirectory(docsDir);
        Directory.CreateDirectory(Path.Combine(repoRoot, "backend"));
        RunGit(repoRoot, "init -q -b main");
        RunGit(repoRoot, "config user.email test@example.com");
        RunGit(repoRoot, "config user.name test");
        foreach (var (rel, content) in pages)
        {
            var full = Path.Combine(docsDir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        File.WriteAllText(Path.Combine(repoRoot, "backend", "svc.cs"), "// v0\n");
        RunGit(repoRoot, "add -A");
        Commit(repoRoot, "2026-01-01T00:00:00", "seed docs");
        return repoRoot;
    }

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

    private static void Commit(string cwd, string isoDate, string message)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment["GIT_COMMITTER_DATE"] = isoDate;
        foreach (var a in new[] { "commit", "-q", $"--date={isoDate}", "-m", message })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit(15_000);
    }

    /// <summary>Deterministic grader seam; counts calls and maps input to a verdict.</summary>
    private sealed class StubGrader : IWikiPageGrader
    {
        private readonly Func<WikiPageGradeInput, WikiPageGradeVerdict> _fn;
        public int Calls;

        public StubGrader(Func<WikiPageGradeInput, WikiPageGradeVerdict> fn) => _fn = fn;

        public Task<WikiPageGradeVerdict> GradeAsync(
            WikiPageGradeInput input, WikiGradingRunRequest run, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            var verdict = _fn(input);
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(verdict);
        }
    }
}
