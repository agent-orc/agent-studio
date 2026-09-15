using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// End-to-end cover for the WEB-21 delivery (15.09.2026): a task captured
/// twelve screenshots and updated two files, the commit candidate gate warned
/// with twelve <c>binary-surprise</c> findings, nothing was committed, the work
/// stayed dirty in the worktree, and the card landed in 5e-escalated with an
/// empty parked reason.
///
/// <para>Three behaviours are pinned here against a real repository and a real
/// lane transition, because each of them is a wire rather than a decision:
/// requested evidence assets reach the commit unattended; a genuine binary
/// surprise still withholds AND says so on the parked card; and an operator can
/// commit what was withheld without a hard block becoming committable.</para>
/// </summary>
public sealed class CommitWithheldDeliveryTests : IDisposable
{
    private const string ProjectName = "web";
    private readonly string _tempDir;
    private readonly string _watchPath;
    private readonly string _repoRoot;

    public CommitWithheldDeliveryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "withheld-delivery-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_tempDir, "jobs");
        _repoRoot = Path.Combine(_tempDir, "repo");
        Directory.CreateDirectory(_tempDir);
        foreach (var state in TaskStates.All) Directory.CreateDirectory(Path.Combine(_watchPath, state));
        Directory.CreateDirectory(_repoRoot);

        RunGit(_repoRoot, "init", "-q", "-b", "main");
        RunGit(_repoRoot, "config", "user.email", "test@example.com");
        RunGit(_repoRoot, "config", "user.name", "test");
        File.WriteAllText(Path.Combine(_repoRoot, "README.md"), "seed\n");
        // Seed the directories the tasks write into. git's porcelain status
        // collapses a wholly untracked directory into a single entry, so a
        // fixture that invents brand-new trees would exercise a path shape the
        // real WEB-21 repository never had.
        foreach (var dir in new[]
                 {
                     "results/WEB-21", "results/WEB-23", "frontend/src/app",
                     "evidence", "tools", "docs", "deploy",
                 })
        {
            var full = Path.Combine(_repoRoot, dir.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(full);
            File.WriteAllText(Path.Combine(full, ".gitkeep"), "");
        }
        RunGit(_repoRoot, "add", "-A");
        RunGit(_repoRoot, "commit", "-q", "-m", "seed");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (Exception ex) { _ = ex; /* best-effort fixture cleanup */ }
    }

    // -- Acceptance 2: requested evidence assets commit without a manual step --

    [Fact]
    public async Task RequestedScreenshots_AreCommittedWithoutAnOperatorStep()
    {
        var run = SeedRunningJob("WEB-21");
        for (var i = 1; i <= 12; i++)
            WriteBinary($"results/WEB-21/shot-{i:00}.png", run);
        WriteText("frontend/src/app/board.ts", "export const board = true;\n", run);
        WriteText("frontend/src/app/board.html", "<p>board</p>\n", run);

        var deps = BuildDeps();
        var outcome = await deps.Transitions.MoveAsync("WEB-21", TaskStates.AutoReview, _watchPath);
        Assert.Equal(MoveJobStatus.Success, outcome.Status);

        // The whole delivery landed: twelve screenshots plus the two edits.
        var moved = ReadJob(TaskStates.AutoReview, "WEB-21");
        Assert.NotNull(moved?.Commit);
        var committed = CommittedPaths();
        Assert.Equal(14, committed.Length);
        Assert.Contains("results/WEB-21/shot-07.png", committed);

        // Nothing is waiting, so the card carries no withheld marker.
        Assert.Null(CommitWithholdingMarker.TryRead(
            Path.Combine(_watchPath, TaskStates.AutoReview, "WEB-21")));
        Assert.Empty(DirtyPaths());
    }

    [Fact]
    public async Task DeclaredAssetPaths_DecideWhichScreenshotsCommitUnattended()
    {
        var deps = BuildDeps();
        deps.Settings.SetEvidenceAssetPaths(ProjectName, ["evidence"]);

        var run = SeedRunningJob("WEB-22");
        WriteBinary("evidence/declared.png", run);

        var outcome = await deps.Transitions.MoveAsync("WEB-22", TaskStates.AutoReview, _watchPath);
        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        Assert.Contains("evidence/declared.png", CommittedPaths());

        // The project named where its evidence lives, so the platform default
        // no longer applies: a png under results/ is back to needing review.
        var second = SeedRunningJob("WEB-23");
        WriteBinary("results/WEB-23/undeclared.png", second);
        var outcome2 = await BuildDeps().Transitions.MoveAsync("WEB-23", TaskStates.AutoReview, _watchPath);
        Assert.Equal(MoveJobStatus.Success, outcome2.Status);
        Assert.Null(ReadJob(TaskStates.AutoReview, "WEB-23")?.Commit);
        Assert.Contains("results/WEB-23/undeclared.png", DirtyPaths());
    }

    // -- Acceptance 1: a withheld delivery says so on the parked card ---------

    [Fact]
    public async Task WithheldDelivery_ParksWithTheGateNamedTheCountAndTheFiles()
    {
        var run = SeedRunningJob("WEB-24");
        WriteBinary("tools/capture.bin", run);            // a real binary surprise
        WriteText("docs/report.md", "# report\n", run);   // clean, but in the same manifest

        var deps = BuildDeps();
        Assert.Equal(
            MoveJobStatus.Success,
            (await deps.Transitions.MoveAsync("WEB-24", TaskStates.AutoReview, _watchPath)).Status);

        // Nothing was committed and the work is still dirty: the WEB-21 symptom.
        Assert.Null(ReadJob(TaskStates.AutoReview, "WEB-24")?.Commit);
        Assert.Contains("tools/capture.bin", DirtyPaths());

        var reviewFolder = Path.Combine(_watchPath, TaskStates.AutoReview, "WEB-24");
        var report = CommitWithholdingMarker.TryRead(reviewFolder);
        Assert.NotNull(report);
        Assert.Equal(2, report!.WithheldCount);
        Assert.Contains(report.Withheld, w => w.Path == "tools/capture.bin" && w.Reason == "binary-surprise");
        Assert.Equal(_repoRootFull, Path.GetFullPath(report.RepositoryRoot));

        // The orchestrator's no-completion-signal escalation passes no reason.
        // Before the fix that produced an empty parked reason on a card that was
        // holding a finished delivery.
        Assert.Equal(
            MoveJobStatus.Success,
            (await BuildDeps().Transitions.MoveAsync("WEB-24", TaskStates.Escalated, _watchPath)).Status);

        var parked = ParkedBlockerMarker.TryRead(
            Path.Combine(_watchPath, TaskStates.Escalated, "WEB-24"));
        Assert.NotNull(parked);
        Assert.Equal(
            HumanReviewEscalationCategories.CommitCandidatesWithheld, parked!.BlockerType);
        Assert.Contains("commit candidate gate", parked.Reason, StringComparison.Ordinal);
        Assert.Contains("2 of 2 candidate files", parked.Reason, StringComparison.Ordinal);
        Assert.Equal(2, parked.WithheldCommitCandidates.Count);

        // And the board projection carries the same file list, so the card can
        // show which files and why without re-running git.
        var status = BuildDeps().Scanner.FindJob("WEB-24", _watchPath)?.ParkedBlocker;
        Assert.NotNull(status);
        Assert.Contains(status!.WithheldCommitCandidates!,
            c => c.Path == "docs/report.md"
                 && c.Reason == CommitWithholdingReasons.WithheldWithManifest);
    }

    // -- Acceptance 3: the operator action commits what was withheld ---------

    [Fact]
    public async Task OperatorAction_CommitsTheWithheldCandidatesAndClearsTheMarker()
    {
        var run = SeedRunningJob("WEB-25");
        WriteBinary("tools/capture.bin", run);
        WriteText("docs/report.md", "# report\n", run);

        var deps = BuildDeps();
        await deps.Transitions.MoveAsync("WEB-25", TaskStates.AutoReview, _watchPath);
        var folder = Path.Combine(_watchPath, TaskStates.AutoReview, "WEB-25");
        Assert.NotNull(CommitWithholdingMarker.TryRead(folder));

        var result = BuildDeps().Git.CommitWithheldCandidates("WEB-25", _watchPath);

        Assert.True(result.Success, result.Error);
        Assert.Empty(result.StillWithheld);
        Assert.Equal(["docs/report.md", "tools/capture.bin"], result.Committed.Order());
        Assert.Equal(["docs/report.md", "tools/capture.bin"], CommittedPaths().Order());
        Assert.Empty(DirtyPaths());
        // The card must stop claiming that work is waiting.
        Assert.Null(CommitWithholdingMarker.TryRead(folder));
    }

    [Fact]
    public async Task OperatorAction_CommitsOnlyTheSelectedSubset()
    {
        var run = SeedRunningJob("WEB-26");
        WriteBinary("tools/capture.bin", run);
        WriteText("docs/report.md", "# report\n", run);

        await BuildDeps().Transitions.MoveAsync("WEB-26", TaskStates.AutoReview, _watchPath);

        var result = BuildDeps().Git.CommitWithheldCandidates(
            "WEB-26", _watchPath, ["docs/report.md"], "docs: land the reviewed report");

        Assert.True(result.Success, result.Error);
        Assert.Equal(["docs/report.md"], CommittedPaths());
        // The binary nobody approved is still dirty and still on the card.
        Assert.Contains("tools/capture.bin", DirtyPaths());
        var report = CommitWithholdingMarker.TryRead(
            Path.Combine(_watchPath, TaskStates.AutoReview, "WEB-26"));
        Assert.NotNull(report);
        Assert.Contains(report!.Withheld, w => w.Path == "tools/capture.bin");
    }

    [Fact]
    public async Task OperatorAction_RefusesAPathOutsideTheRecordedManifest()
    {
        var run = SeedRunningJob("WEB-27");
        WriteBinary("tools/capture.bin", run);
        await BuildDeps().Transitions.MoveAsync("WEB-27", TaskStates.AutoReview, _watchPath);

        // A file that appeared after the manifest was recorded must not be able
        // to ride along on the operator's review of something else.
        File.WriteAllText(Path.Combine(_repoRoot, "sneaked-in.txt"), "not reviewed\n");

        var result = BuildDeps().Git.CommitWithheldCandidates(
            "WEB-27", _watchPath, ["tools/capture.bin", "sneaked-in.txt"]);

        Assert.False(result.Success);
        Assert.Contains("sneaked-in.txt", result.Error!, StringComparison.Ordinal);
        Assert.Equal("seed", HeadSubject());
        Assert.NotNull(CommitWithholdingMarker.TryRead(
            Path.Combine(_watchPath, TaskStates.AutoReview, "WEB-27")));
    }

    [Fact]
    public async Task OperatorAction_StillRefusesSecretMaterial()
    {
        var run = SeedRunningJob("WEB-28");
        WriteText("deploy/id_rsa",
            "-----BEGIN PRIVATE KEY-----\nvery-secret-body\n-----END PRIVATE KEY-----\n", run);

        await BuildDeps().Transitions.MoveAsync("WEB-28", TaskStates.AutoReview, _watchPath);
        var folder = Path.Combine(_watchPath, TaskStates.AutoReview, "WEB-28");
        Assert.True(CommitWithholdingMarker.TryRead(folder)?.Blocked);

        var result = BuildDeps().Git.CommitWithheldCandidates("WEB-28", _watchPath);

        // Explicit review resolves warnings. A hard block is not a warning, so
        // this action can never become a way around the gate.
        Assert.False(result.Success);
        Assert.Contains(result.Commit!.Gate!.Findings, f => f.Code == "private-key-material");
        Assert.DoesNotContain("very-secret-body", JsonSerializer.Serialize(result));
        Assert.Equal("seed", HeadSubject());
        // The failed attempt leaves the card still saying that work is waiting.
        Assert.NotNull(CommitWithholdingMarker.TryRead(folder));
    }

    [Fact]
    public void OperatorAction_WithoutARecordedManifestSaysSo()
    {
        SeedRunningJob("WEB-29");
        var result = BuildDeps().Git.CommitWithheldCandidates("WEB-29", _watchPath);

        Assert.False(result.Success);
        Assert.Contains("No withheld commit candidates", result.Error!, StringComparison.Ordinal);
    }

    // -- Fixture -----------------------------------------------------------

    private string _repoRootFull => Path.GetFullPath(_repoRoot);

    private DateTime SeedRunningJob(string slug)
    {
        var dir = Path.Combine(_watchPath, TaskStates.Progress, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug}\",\"state\":\"{TaskStates.Progress}\",\"order\":1,\"agent\":\"claude\"}}");

        // The auto-commit scope is attributed by mtime against the task's first
        // CLI event, so the run window has to exist before the files do.
        var startedAt = DateTime.UtcNow.AddMinutes(-5);
        var logsDir = Path.Combine(dir, "logs");
        Directory.CreateDirectory(logsDir);
        File.AppendAllText(
            Path.Combine(logsDir, "session-events.jsonl"),
            JsonSerializer.Serialize(new SessionEvent { Ts = startedAt, Kind = "start", Cli = "claude" })
                + Environment.NewLine,
            Encoding.UTF8);
        return startedAt;
    }

    private void WriteText(string relativePath, string content, DateTime runStart)
    {
        var full = Path.Combine(_repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        File.SetLastWriteTimeUtc(full, runStart.AddSeconds(30));
    }

    /// <summary>A PNG-shaped byte run: the NUL bytes in the header are exactly
    /// what git and the gate use to classify a candidate as binary.</summary>
    private void WriteBinary(string relativePath, DateTime runStart)
    {
        var full = Path.Combine(_repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D }
            .Concat(Enumerable.Repeat((byte)0x00, 256))
            .ToArray();
        File.WriteAllBytes(full, bytes);
        File.SetLastWriteTimeUtc(full, runStart.AddSeconds(30));
    }

    private string[] CommittedPaths()
        => RunGitCapture(_repoRoot, "show", "--name-only", "--pretty=format:", "HEAD")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();

    private string[] DirtyPaths()
        => RunGitCapture(_repoRoot, "status", "--porcelain", "--untracked-files=all")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line[3..].Trim())
            .ToArray();

    private string HeadSubject() => RunGitCapture(_repoRoot, "log", "-1", "--format=%s").Trim();

    private TaskInfo? ReadJob(string state, string slug)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        return Directory.Exists(dir) ? BuildDeps().Scanner.FindJob(slug, _watchPath) : null;
    }

    private Deps BuildDeps()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = ProjectName,
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _repoRoot,
                ["WatchPaths:0:RepositoryPath"] = _repoRoot,
                ["TaskRepository"] = _watchPath,
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var states = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        var mutations = new TaskMutationService(
            scanner,
            new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var git = new GitService(
            NullLogger<GitService>.Instance, scanner, config, prompts, projectSettings: settings);
        var sessions = new TaskSessionLog(scanner, NullLogger<TaskSessionLog>.Instance);
        var transitions = new TaskTransitionService(
            scanner, states, mutations, git, settings,
            NullLogger<TaskTransitionService>.Instance, sessions,
            timeline: new TimelineLog(NullLogger<TimelineLog>.Instance));
        return new Deps(scanner, transitions, git, settings);
    }

    private sealed record Deps(
        TaskScannerService Scanner,
        TaskTransitionService Transitions,
        GitService Git,
        ProjectSettingsService Settings);

    private static void RunGit(string cwd, params string[] args)
    {
        using var p = Process.Start(Psi(cwd, args))!;
        p.WaitForExit(15_000);
    }

    private static string RunGitCapture(string cwd, params string[] args)
    {
        using var p = Process.Start(Psi(cwd, args))!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(15_000);
        return output;
    }

    private static ProcessStartInfo Psi(string cwd, string[] args)
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
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        return psi;
    }
}
