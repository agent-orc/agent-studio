using System.Diagnostics;
using System.Text.Json;

using AgentStudio.Git;
using AgentStudio.Pipeline;
using AgentStudio.Registry;
using AgentStudio.Shared;
using AgentStudio.Tasks;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2871: the reconciliation pass that unsticks the Human Review cards whose
/// verdict mixes delivery generations. Every test drives real git, because the
/// content rule IS a git answer (<c>git merge-tree --write-tree</c>) and the
/// point of the pass is that the answer becomes durable card state which the
/// board projection can then read without spawning git per commit.
/// </summary>
[Trait("Category", "MachineBound")]
public sealed class IntegrationGenerationReconcilerTests : IDisposable
{
    private const string Project = "Fixture";
    private const string Salvage = "wip(runner): salvage before teardown - outcome Done";

    private readonly string _root;
    private readonly string _watchPath;
    private readonly string _repo;

    public IntegrationGenerationReconcilerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "integration-generation-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_root, "project-store");
        _repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_root);
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));

        Git(_root, "init", "-q", "-b", "develop", _repo);
        Git(_repo, "config", "user.email", "test@example.com");
        Git(_repo, "config", "user.name", "Integration Generation Test");
        File.WriteAllText(Path.Combine(_repo, "seed.txt"), "seed\n");
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-q", "-m", "seed");
    }

    [Fact]
    public void RunOnce_SalvageCommitWhoseContentIsAlreadyInDevelop_IsRecordedAndTheCardFlipsToIntegrated()
    {
        var stack = Build();
        // First round: a salvage commit that set feature.txt to its final
        // content on a branch that was never merged.
        Git(_repo, "checkout", "-q", "-b", "task/first-round", "develop");
        File.WriteAllText(Path.Combine(_repo, "feature.txt"), "final content\n");
        var salvage = CommitAll(Salvage);

        // develop received the same feature.txt content out of band, then the
        // generation that was actually reviewed and merged.
        Git(_repo, "checkout", "-q", "develop");
        File.WriteAllText(Path.Combine(_repo, "feature.txt"), "final content\n");
        CommitAll("feat: same content through another card");
        File.WriteAllText(Path.Combine(_repo, "other.txt"), "delivered\n");
        var merged = CommitAll("feat: final generation");

        var folder = SeedCard(
            "content-equal",
            [
                CommitJson(salvage, Salvage, ["feature.txt"]),
                CommitJson(merged, "feat: final generation", ["other.txt"]),
            ]);

        // The old salvage commit is not an ancestor, so before reconciliation
        // the card reads as an incomplete delivery.
        Assert.Equal(IntegrationStatuses.Partial, Status(stack, "content-equal").Status);

        var report = stack.Reconciler.RunOnce();

        Assert.Equal(1, report.RepairedTasks);
        Assert.Equal(1, report.RepairedCommits);
        var row = Assert.Single(report.Rows);
        Assert.Null(row.Error);
        Assert.Contains(
            $"{salvage[..7]} {CommitIntegrationEvidence.ContentEqual}",
            row.Decisions);

        // The verdict is durable card state now, and history is intact.
        var persisted = PersistedCommits(folder);
        Assert.Equal([salvage, merged], persisted.Select(commit => commit.Sha));
        Assert.Equal(
            CommitIntegrationEvidence.ContentEqual,
            persisted.Single(commit => commit.Sha == salvage).IntegrationEvidence);
        Assert.Null(persisted.Single(commit => commit.Sha == salvage).SupersededByAttempt);

        stack.Scanner.InvalidateCache();
        stack.Integration.InvalidateCache();
        var after = Status(stack, "content-equal");
        Assert.Equal(IntegrationStatuses.Integrated, after.Status);
        Assert.Contains("integrated by content", after.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void RunOnce_SupersededEarlierGeneration_RecordsTheReplacementWithoutDroppingHistory()
    {
        var stack = Build();
        // Generation 1: a salvage commit that was never merged.
        Git(_repo, "checkout", "-q", "-b", "task/first-round", "develop");
        File.WriteAllText(Path.Combine(_repo, "feature.txt"), "first round\n");
        var salvage = CommitAll(Salvage);

        // Generation 2 delivered twice: one commit whose content reached
        // develop through another card, and the commit that was merged.
        Git(_repo, "checkout", "-q", "-b", "task/second-round", "develop");
        File.WriteAllText(Path.Combine(_repo, "gone.txt"), "shared content\n");
        var stale = CommitAll("feat: content develop received elsewhere");

        Git(_repo, "checkout", "-q", "develop");
        File.WriteAllText(Path.Combine(_repo, "gone.txt"), "shared content\n");
        CommitAll("feat: same content through another card");
        File.WriteAllText(Path.Combine(_repo, "other.txt"), "delivered\n");
        var merged = CommitAll("feat: second generation");

        var folder = SeedCard(
            "superseded",
            [
                CommitJson(salvage, Salvage, ["feature.txt"], attempt: "run-1"),
                CommitJson(stale, "feat: content develop received elsewhere", ["gone.txt"], attempt: "run-2"),
                CommitJson(merged, "feat: second generation", ["other.txt"], attempt: "run-2"),
            ]);

        Assert.Equal(IntegrationStatuses.Partial, Status(stack, "superseded").Status);

        var report = stack.Reconciler.RunOnce();

        Assert.Equal(2, report.RepairedCommits);
        var persisted = PersistedCommits(folder);
        Assert.Equal([salvage, stale, merged], persisted.Select(commit => commit.Sha));
        var historical = persisted.Single(commit => commit.Sha == salvage);
        Assert.Equal(CommitIntegrationEvidence.GenerationSuperseded, historical.IntegrationEvidence);
        Assert.Equal("run-2", historical.SupersededByAttempt);
        Assert.Equal(Salvage, historical.Message);
        var byContent = persisted.Single(commit => commit.Sha == stale);
        Assert.Equal(CommitIntegrationEvidence.ContentEqual, byContent.IntegrationEvidence);
        Assert.Null(byContent.SupersededByAttempt);

        stack.Scanner.InvalidateCache();
        stack.Integration.InvalidateCache();
        Assert.Equal(IntegrationStatuses.Integrated, Status(stack, "superseded").Status);
    }

    [Fact]
    public void RunOnce_MissingCommitOfTheCurrentGeneration_ChangesNothing()
    {
        var stack = Build();
        // Both commits belong to the same run attempt and the missing one's
        // content is nowhere in develop: the delivery really is incomplete and
        // must keep blocking acceptance.
        Git(_repo, "checkout", "-q", "-b", "task/incomplete", "develop");
        File.WriteAllText(Path.Combine(_repo, "missing.txt"), "never merged\n");
        var missing = CommitAll("feat: work that never landed");

        Git(_repo, "checkout", "-q", "develop");
        File.WriteAllText(Path.Combine(_repo, "landed.txt"), "landed\n");
        var landed = CommitAll("feat: work that landed");

        var folder = SeedCard(
            "incomplete",
            [
                CommitJson(missing, "feat: work that never landed", ["missing.txt"], attempt: "run-7"),
                CommitJson(landed, "feat: work that landed", ["landed.txt"], attempt: "run-7"),
            ]);

        var report = stack.Reconciler.RunOnce();

        Assert.Equal(0, report.RepairedCommits);
        Assert.All(
            PersistedCommits(folder),
            commit => Assert.Null(commit.IntegrationEvidence));
        stack.Scanner.InvalidateCache();
        stack.Integration.InvalidateCache();
        var after = Status(stack, "incomplete");
        Assert.Equal(IntegrationStatuses.Partial, after.Status);
        Assert.Contains(missing[..7], after.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void RunOnce_UnchangedFacts_AreNotProbedAgain()
    {
        var stack = Build();
        Git(_repo, "checkout", "-q", "-b", "task/repeat", "develop");
        File.WriteAllText(Path.Combine(_repo, "missing.txt"), "never merged\n");
        var missing = CommitAll("feat: work that never landed");
        Git(_repo, "checkout", "-q", "develop");
        File.WriteAllText(Path.Combine(_repo, "landed.txt"), "landed\n");
        var landed = CommitAll("feat: work that landed");
        SeedCard(
            "repeat",
            [
                CommitJson(missing, "feat: work that never landed", ["missing.txt"], attempt: "run-7"),
                CommitJson(landed, "feat: work that landed", ["landed.txt"], attempt: "run-7"),
            ]);

        var first = stack.Reconciler.RunOnce();
        var second = stack.Reconciler.RunOnce();

        Assert.Equal(1, first.ContentProbes);
        Assert.Equal(0, second.ContentProbes);
    }

    [Fact]
    public void IsContentContainedInBranch_IsTrueOnlyWhenTheMergeAddsNothing()
    {
        var stack = Build();
        Git(_repo, "checkout", "-q", "-b", "task/probe", "develop");
        File.WriteAllText(Path.Combine(_repo, "feature.txt"), "final content\n");
        var sameContent = CommitAll("feat: same content as develop will get");
        File.WriteAllText(Path.Combine(_repo, "extra.txt"), "only here\n");
        var extraContent = CommitAll("feat: content develop never got");

        Git(_repo, "checkout", "-q", "develop");
        File.WriteAllText(Path.Combine(_repo, "feature.txt"), "final content\n");
        CommitAll("feat: same content through another card");

        Assert.True(stack.Git.IsContentContainedInBranch(_repo, sameContent, "develop"));
        Assert.False(stack.Git.IsContentContainedInBranch(_repo, extraContent, "develop"));
    }

    private TaskIntegrationStatus Status(Stack stack, string id)
    {
        var job = stack.Scanner.FindJob(id, _watchPath);
        Assert.NotNull(job);
        return stack.Integration.BuildLookup([job!])[job!.TaskKey];
    }

    private static List<TaskCommitInfo> PersistedCommits(string folder)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "task.json")));
        return document.RootElement.GetProperty("commits")
            .EnumerateArray()
            .Select(element => JsonSerializer.Deserialize<TaskCommitInfo>(
                element.GetRawText(),
                TaskJsonFile.ReadOpts)!)
            .ToList();
    }

    private string SeedCard(string id, IReadOnlyList<object> commits)
    {
        var folder = Path.Combine(_watchPath, TaskStates.HumanReview, id);
        Directory.CreateDirectory(folder);
        var card = new
        {
            id,
            key = "AGT-" + id,
            title = id,
            state = TaskStates.HumanReview,
            order = 1,
            agent = "codex",
            cliType = "codex",
            mode = TaskModes.Coding,
            projectName = Project,
            ownerClientId = DefaultClientIdentity.Id,
            commit = commits[^1],
            commits,
        };
        File.WriteAllText(
            Path.Combine(folder, "task.json"),
            JsonSerializer.Serialize(
                card,
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        File.WriteAllText(Path.Combine(folder, "prompt.md"), $"Implement {id}.\n");
        return folder;
    }

    private static object CommitJson(
        string sha,
        string message,
        IReadOnlyList<string> files,
        string? attempt = null)
        => new
        {
            sha,
            shortSha = sha[..7],
            message,
            filesChanged = files.Count,
            files,
            at = DateTimeOffset.UtcNow,
            attribution = "automatic",
            confidence = 1,
            runAttemptId = attempt,
        };

    private Stack Build()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = Project,
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _repo,
                ["WatchPaths:0:RepositoryPath"] = _repo,
                ["TaskRepository"] = _root,
            }).Build();
        var scanner = new TaskScannerService(
            configuration,
            NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, configuration));
        var git = new GitService(NullLogger<GitService>.Instance, scanner, configuration);
        var settings = new ProjectSettingsService(
            NullLogger<ProjectSettingsService>.Instance,
            configuration);
        settings.SetIntegrationBranch(Project, "develop");
        var mutations = new TaskMutationService(
            scanner,
            new ClientIdentityStore(configuration, NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(configuration, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance);
        var integration = new TaskIntegrationStatusService(
            git,
            settings,
            new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance),
            NullLogger<TaskIntegrationStatusService>.Instance);
        var reconciler = new IntegrationGenerationReconciler(
            scanner,
            integration,
            mutations,
            git,
            settings,
            NullLogger<IntegrationGenerationReconciler>.Instance);
        return new Stack(scanner, git, integration, reconciler);
    }

    private string CommitAll(string message)
    {
        Git(_repo, "add", "-A");
        Git(_repo, "commit", "-q", "-m", message);
        return Git(_repo, "rev-parse", "HEAD");
    }

    private static string Git(string cwd, params string[] args)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {stderr}");
        return stdout.Trim();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) { SilentCatch.Note(ex, "Reconciler test cleanup is best-effort."); }
    }

    private sealed record Stack(
        TaskScannerService Scanner,
        GitService Git,
        TaskIntegrationStatusService Integration,
        IntegrationGenerationReconciler Reconciler);
}
