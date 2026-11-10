using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using AgentStudio.Git;
using AgentStudio.Projects;
using AgentStudio.Tasks;

using Xunit;

namespace AgentStudio.Tests;

public sealed class SupersededCommitSweepTests
{
    [Theory]
    [InlineData("AGT-2533: Deliver work\n\nAgent-Studio-Salvage: teardown", true)]
    [InlineData("wip(runner): salvage before teardown - outcome Done", true)]
    [InlineData("AGT-2533: explain salvage recovery", false)]
    public void IsRunnerFence_RecognizesTrailerAndLegacySubjectOnly(string message, bool expected)
    {
        Assert.Equal(expected, SupersededCommitSweepPolicy.IsRunnerFence(
            Commit("1111111", message, "round-1", ["backend/a.cs"])));
    }

    [Fact]
    public void Evaluate_MissingFenceCoveredByLaterIntegratedGeneration_IsSuperseded()
    {
        var fence = Commit(
            "1111111",
            "wip(runner): salvage before teardown - outcome Done",
            "round-1",
            ["backend/a.cs", "frontend/a.ts"]);
        var replacement = Commit(
            "2222222",
            "feat(AGT-2533): replacement delivery",
            "round-2",
            ["backend/a.cs", "frontend/a.ts", "backend/regression.cs"]);

        var decision = SupersededCommitSweepPolicy.Evaluate(
            [fence, replacement],
            sha => sha == replacement.Sha);

        var marked = Assert.Single(decision.Replacements);
        Assert.Equal(fence.Sha, marked.SupersededSha);
        Assert.Equal(replacement.Sha, marked.ReplacementSha);
        Assert.Equal("round-2", marked.ReplacementAttempt);
        Assert.Equal(100, marked.CoveragePercent);
        Assert.Empty(marked.MissingFiles);
        Assert.Empty(decision.Ambiguous);
    }

    [Fact]
    public void Evaluate_LaterCommitWithNarrowerFileBreadth_RemainsUntouchedAndListed()
    {
        var fence = Commit(
            "1111111",
            "wip(runner): salvage before teardown - outcome Done",
            "round-1",
            ["backend/a.cs", "frontend/a.ts"]);
        var replacement = Commit(
            "2222222",
            "fix(AGT-2533): narrow follow-up",
            "round-2",
            ["backend/a.cs"]);

        var decision = SupersededCommitSweepPolicy.Evaluate(
            [fence, replacement],
            sha => sha == replacement.Sha);

        Assert.Empty(decision.Replacements);
        Assert.Contains("full changed-file breadth", Assert.Single(decision.Ambiguous).Reason);
    }

    [Fact]
    public void Evaluate_NonFenceMissingCommit_IsNeverBulkSuperseded()
    {
        var missing = Commit(
            "1111111",
            "feat(AGT-2533): real partial work",
            "round-1",
            ["backend/a.cs"]);
        var integrated = Commit(
            "2222222",
            "fix(AGT-2533): another commit",
            "round-2",
            ["backend/a.cs"]);

        var decision = SupersededCommitSweepPolicy.Evaluate(
            [missing, integrated],
            sha => sha == integrated.Sha);

        Assert.Empty(decision.Replacements);
        Assert.Empty(decision.Ambiguous);
    }

    [Fact]
    public void Evaluate_MissingFenceWithoutLaterIntegratedCommit_IsListedAndUntouched()
    {
        var fence = Commit(
            "1111111",
            "wip(runner): salvage before teardown - outcome Done",
            "round-1",
            ["backend/a.cs"]);
        var laterMissing = Commit(
            "2222222",
            "feat(AGT-2533): replacement delivery",
            "round-2",
            ["backend/a.cs"]);

        var decision = SupersededCommitSweepPolicy.Evaluate(
            [fence, laterMissing],
            _ => false);

        Assert.Empty(decision.Replacements);
        var ambiguity = Assert.Single(decision.Ambiguous);
        Assert.Contains("No later integrated commit", ambiguity.Reason);
        Assert.Empty(ambiguity.LaterIntegratedShas);
    }

    [Fact]
    public void Evaluate_Agt2533BreadthShape_AllowsThreeRemovedPathsAtNinetyPercentOverlap()
    {
        var shared = Enumerable.Range(1, 40).Select(index => $"shared/{index}.cs").ToList();
        var fence = Commit(
            "15c38f8",
            "wip(runner): salvage before teardown - outcome Done",
            "round-1",
            [.. shared, "old/view.html", "old/view.scss", "old/view.ts"]);
        var replacement = Commit(
            "225b0ed",
            "feat(workbench): add stable keys and card references",
            "round-2",
            [.. shared, "new/panel.html", "new/panel.ts"]);

        var decision = SupersededCommitSweepPolicy.Evaluate(
            [fence, replacement],
            sha => sha == replacement.Sha);

        var marked = Assert.Single(decision.Replacements);
        Assert.Equal(93.0, marked.CoveragePercent);
        Assert.Equal(3, marked.MissingFiles.Count);
        Assert.Empty(decision.Ambiguous);
    }

    [Fact]
    public void Evaluate_LaterIntegratedFenceWithChangedFiles_IsAValidReplacement()
    {
        var fence = Commit(
            "1111111",
            "wip(runner): salvage before teardown - outcome Done",
            "round-1",
            ["backend/a.cs", "frontend/a.ts"]);
        var replacement = Commit(
            "2222222",
            "wip(runner): salvage before teardown - outcome Done",
            "round-2",
            ["backend/a.cs", "frontend/a.ts"]);

        var decision = SupersededCommitSweepPolicy.Evaluate(
            [fence, replacement],
            sha => sha == replacement.Sha);

        Assert.Single(decision.Replacements);
        Assert.Empty(decision.Ambiguous);
    }

    [Fact]
    public void RunOnce_RepairsDeliveredCard_WritesAuditReport_AndDoesNotRepeat()
    {
        var root = Path.Combine(Path.GetTempPath(), "superseded-sweep-" + Guid.NewGuid().ToString("N"));
        var repo = Path.Combine(root, "repo");
        var jobs = Path.Combine(root, "task-store", TaskStates.Completed);
        var job = Path.Combine(jobs, "agt-2533");
        var reportPath = Path.Combine(root, "task-store", ".metadata", "migrations", "report.json");
        Directory.CreateDirectory(repo);
        Directory.CreateDirectory(job);
        try
        {
            Git(repo, "init", "-q", "-b", "main");
            Git(repo, "config", "user.email", "test@example.com");
            Git(repo, "config", "user.name", "test");
            File.WriteAllText(Path.Combine(repo, "README.md"), "seed");
            Git(repo, "add", "README.md");
            Git(repo, "commit", "-q", "-m", "seed");
            Git(repo, "checkout", "-q", "-b", "develop");
            Git(repo, "checkout", "-q", "-b", "task/conflict-round");
            File.WriteAllText(Path.Combine(repo, "delivery.cs"), "complete");
            Git(repo, "add", "delivery.cs");
            Git(repo, "commit", "-q", "-m", "wip(runner): salvage before teardown - outcome Done");
            var fenceSha = Git(repo, "rev-parse", "HEAD").Trim();
            Git(repo, "checkout", "-q", "develop");
            Git(repo, "commit", "-q", "--allow-empty", "-m", "chore: advance develop");
            File.WriteAllText(Path.Combine(repo, "delivery.cs"), "complete");
            Git(repo, "add", "delivery.cs");
            Git(repo, "commit", "-q", "-m", "feat(AGT-2533): replacement delivery");
            var replacementSha = Git(repo, "rev-parse", "HEAD").Trim();

            File.WriteAllText(Path.Combine(job, "task.json"), JsonSerializer.Serialize(new
            {
                id = "agt-2533",
                key = "AGT-2533",
                title = "Superseded delivery fixture",
                state = TaskStates.Completed,
                agent = "codex",
                createdAt = "2026-08-09T08:00:00Z",
                commits = new[]
                {
                    new
                    {
                        sha = fenceSha,
                        shortSha = fenceSha[..7],
                        message = "wip(runner): salvage before teardown - outcome Done",
                        filesChanged = 1,
                        files = new[] { "delivery.cs" },
                        at = "2026-08-09T10:00:00Z",
                        runAttemptId = "round-1",
                    },
                    new
                    {
                        sha = replacementSha,
                        shortSha = replacementSha[..7],
                        message = "feat(AGT-2533): replacement delivery",
                        filesChanged = 1,
                        files = new[] { "delivery.cs" },
                        at = "2026-08-09T11:00:00Z",
                        runAttemptId = "round-2",
                    },
                },
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
            File.WriteAllText(Path.Combine(job, "prompt.md"), "fixture");

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["TaskRepository"] = Path.Combine(root, "task-store"),
                    ["WatchPaths:0:Name"] = "Fixture",
                    ["WatchPaths:0:Path"] = Path.Combine(root, "task-store"),
                    ["WatchPaths:0:RootPath"] = repo,
                    ["WatchPaths:0:RepositoryPath"] = repo,
                }).Build();
            var scanner = new TaskScannerService(
                configuration,
                NullLogger<TaskScannerService>.Instance,
                new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, configuration));
            var git = new GitService(NullLogger<GitService>.Instance, scanner, configuration);
            var settings = new ProjectSettingsService(
                NullLogger<ProjectSettingsService>.Instance,
                configuration);
            settings.SetIntegrationBranch("Fixture", "develop");
            var mutations = new TaskMutationService(
                scanner,
                new ClientIdentityStore(configuration, NullLogger<ClientIdentityStore>.Instance),
                new AgentStudio.Registry.ProjectRegistry(
                    configuration,
                    NullLogger<AgentStudio.Registry.ProjectRegistry>.Instance),
                new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
                NullLogger<TaskMutationService>.Instance,
                git: git);
            var sweep = new SupersededCommitSweep(
                scanner,
                mutations,
                git,
                settings,
                reportPath,
                NullLogger<SupersededCommitSweep>.Instance);

            var first = sweep.RunOnce();
            var second = sweep.RunOnce();

            Assert.Equal(1, first.RepairedTasks);
            Assert.Equal(1, first.RepairedCommits);
            Assert.Equal(0, first.UnresolvedTasks);
            Assert.True(File.Exists(reportPath));
            Assert.True(second.AlreadyCompleted);
            var persisted = scanner.FindJob("agt-2533", Path.Combine(root, "task-store"));
            Assert.NotNull(persisted);
            Assert.Equal("round-2", persisted!.Commits[0].SupersededByAttempt);
            Assert.Null(persisted.Commits[1].SupersededByAttempt);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static TaskCommitInfo Commit(
        string shortSha,
        string message,
        string runAttemptId,
        IReadOnlyList<string> files)
        => new()
        {
            Sha = shortSha.PadRight(40, '0'),
            ShortSha = shortSha,
            Message = message,
            FilesChanged = files.Count,
            Files = files.ToList(),
            At = DateTime.UtcNow,
            RunAttemptId = runAttemptId,
        };

    private static string Git(string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}");
        return output;
    }
}
