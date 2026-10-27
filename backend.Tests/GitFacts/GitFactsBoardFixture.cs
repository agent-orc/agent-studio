using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AgentStudio.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace AgentStudio.Tests;

/// <summary>
/// The Git facts S1 board (AGT-3321): 3,000 cards in one project with a real
/// code repository and a Git-tracked workspace, shaped like the 11 October
/// board plus growth margin. It carries every input the read census and the
/// budget tests need:
/// <list type="bullet">
///   <item>3,000 task folders (2,997 distinct ids): 2,050 archived, 210 in Human Review, 200
///   Completed, 40 Escalated, the rest in the open lanes. Archived, Completed
///   and Human Review cards carry delivery commits that exist on
///   <c>develop</c>.</item>
///   <item>Duplicate ids: <see cref="DuplicateIds"/> folders exist in both
///   <c>6-completed</c> and <c>7-archive</c>.</item>
///   <item>A code repository with <c>main</c>, <c>develop</c>, 200 task
///   branches packed into <c>packed-refs</c>, a linked worktree for the
///   in-progress card, a bare <c>origin</c>, a release tag and workflows for
///   the publishing derivation, and Wiki pages under <c>docs/</c>.</item>
///   <item>Named cards for the request families: an integrated Human Review
///   card (<see cref="IntegratedReview"/>) carried by a <c>merge(KEY)</c>
///   commit, an integrated escalated card with <c>orchestrator-follow-up.md</c>,
///   <c>results/decision.html</c> and <c>results/decision.json</c>
///   (<see cref="EscalatedWithFiles"/>), an escalated card without any of them
///   whose delivery is not integrated (<see cref="EscalatedBare"/>), and an
///   in-progress card with a worktree (<see cref="InProgress"/>).</item>
///   <item>Result and file histories: <c>status.md</c> versions committed to
///   the workspace repository with run trailers, run SHA ranges in
///   <c>logs/session-events.jsonl</c>, and a worktree run trailer commit.</item>
/// </list>
/// Commit dates and every timestamp come from <see cref="Clock"/>; nothing
/// reads the wall clock.
/// </summary>
public sealed class GitFactsBoardFixture : IDisposable
{
    public const string ProjectName = "facts";
    public const int DuplicateIds = 3;
    public const string IntegratedReview = "agt-9001";
    public const string EscalatedWithFiles = "agt-9002";
    public const string EscalatedBare = "agt-9003";
    public const string InProgress = "agt-9004";

    /// <summary>An Auto Review card whose delivery commit is on develop (Git facts S3, AGT-3323).</summary>
    public const string IntegratedAutoReview = "agt-9005";
    public const string WikiPage = "docs/guide/getting-started.md";

    /// <summary>Lane to folder count; with the duplicate folders the board has 3,000.</summary>
    public static readonly IReadOnlyList<(string Lane, int Count)> Shape =
    [
        (TaskStates.Archive, 2_050),
        (TaskStates.HumanReview, 210),
        (TaskStates.Completed, 200),
        (TaskStates.Backlog, 277),
        (TaskStates.Ready, 100),
        (TaskStates.AutoReview, 60),
        (TaskStates.Escalated, 40),
        (TaskStates.Progress, 30),
        (TaskStates.Preparation, 30),
    ];

    private const int DeliveryCommits = 120;
    private const int PackedTaskBranches = 200;
    private readonly TempWorkspace _root = new("git-facts-board");
    private readonly List<string> _deliveries = [];

    public GitFactsBoardFixture()
    {
        Workspace = _root.Directory("workspace");
        WatchPath = Path.Combine(Workspace, "projects", ProjectName);
        Code = _root.Directory("code");
        Origin = _root.Combine("origin.git");
        WorktreePath = _root.Combine("worktrees", InProgress);

        BuildCodeRepository();
        BuildBoard();
        BuildWorkspaceHistory();
        File.WriteAllText(Path.Combine(Workspace, "project-settings.json"),
            JsonSerializer.Serialize(new Dictionary<string, object>
            {
                [ProjectName] = new { integrationBranch = "develop" },
            }));
    }

    /// <summary>Clock of every fixture timestamp; hosts built on the fixture receive it.</summary>
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero));

    public string Workspace { get; }
    public string WatchPath { get; }
    public string Code { get; }
    public string Origin { get; }
    public string WorktreePath { get; }
    public int CardCount => Shape.Sum(lane => lane.Count) + DuplicateIds;
    public IReadOnlyList<string> DeliverySubjects => _deliveries;

    /// <summary>SHAs of the integrated review card's two delivery commits, oldest first.</summary>
    public string[] ReviewCommits { get; private set; } = [];

    /// <summary>The workspace commit of the first <c>status.md</c> version of the review card.</summary>
    public string FirstStatusVersion { get; private set; } = "";

    /// <summary>Configuration of a backend host over this board.</summary>
    public Dictionary<string, string?> HostSettings() => new()
    {
        ["TaskRepository"] = Workspace,
        ["WatchPaths:0:Name"] = ProjectName,
        ["WatchPaths:0:Path"] = WatchPath,
        ["WatchPaths:0:RootPath"] = Code,
        ["WatchPaths:0:RepositoryPath"] = Code,
        ["DeliveryChain:Guarded"] = "false",
        ["TaskIndexCache:SafetyTtlSeconds"] = "3600",
        ["GitStateIndex:SweepIntervalSeconds"] = "3600",
        ["GitStateIndex:MaxRetries"] = "0",
    };

    public string JobFolder(string lane, string id) => Path.Combine(WatchPath, lane, id);

    public string Query => "?watchPath=" + Uri.EscapeDataString(WatchPath);

    /// <summary>Commits one change to <c>develop</c> and pushes it, so every ref fingerprint moves.</summary>
    public string MoveDevelop(string label)
    {
        Clock.Advance(TimeSpan.FromMinutes(1));
        File.WriteAllText(Path.Combine(Code, "moves", label + ".txt"), label + "\n");
        var sha = Commit(Code, $"chore: move develop ({label})");
        Git(Code, "push", "-q", "origin", "develop");
        return sha;
    }

    private void BuildCodeRepository()
    {
        Git(_root.Path, "init", "-q", "-b", "main", Code);
        Git(Code, "config", "user.email", "facts@example.invalid");
        Git(Code, "config", "user.name", "Git Facts Fixture");
        Git(Code, "config", "commit.gpgsign", "false");
        Write(Code, "README.md", "# Git facts fixture\n");
        Write(Code, "src/Facts/Facts.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><PackageId>Facts.Package</PackageId></PropertyGroup></Project>");
        Write(Code, ".github/workflows/release.yml", """
            name: release
            on:
              push:
                tags:
                  - 'v*'
            jobs:
              publish:
                steps:
                  - run: dotnet pack -c Release
                  - run: dotnet nuget push out/*.nupkg --api-key ${{ secrets.NUGET }}
            """);
        Write(Code, "docs/index.md", "# Facts wiki\n\nStart at [getting started](guide/getting-started.md).\n");
        Write(Code, WikiPage, "# Getting started\n\nFirst version.\n");
        Write(Code, "docs/guide/operations.md", "# Operations\n\nRunbook.\n");
        Directory.CreateDirectory(Path.Combine(Code, "moves"));
        Write(Code, "moves/.keep", "");
        Commit(Code, "chore: seed repository");
        Git(Code, "tag", "v0.1.0");
        Git(Code, "checkout", "-q", "-b", "develop");

        for (var index = 1; index <= DeliveryCommits; index++)
        {
            Write(Code, $"deliveries/d{index:D3}.txt", $"delivery {index}\n");
            _deliveries.Add(Commit(Code, $"feat(AGT-{10_000 + index}): delivery {index}"));
        }
        Write(Code, WikiPage, "# Getting started\n\nSecond version.\n");
        Commit(Code, "docs: revise getting started");

        // Integrated review card: two commits on its task branch, the second
        // carrying the worktree run trailer, then the curated merge.
        Git(Code, "checkout", "-q", "-b", "task/" + IntegratedReview);
        Write(Code, "src/Facts/Review.cs", "namespace Facts; public sealed class Review { }\n");
        var first = Commit(Code, "feat(AGT-9001): add review model");
        Write(Code, "src/Facts/Review.cs", "namespace Facts; public sealed class Review { public int Id { get; init; } }\n");
        Write(Code, WikiPage, "# Getting started\n\nThird version, from the review card.\n");
        var second = Commit(Code, "feat(AGT-9001): add review id\n\n" + AgentStudio.Git.GitService.WorktreeRunCommitTrailer(IntegratedReview));
        ReviewCommits = [first, second];
        Git(Code, "checkout", "-q", "develop");
        Merge("task/" + IntegratedReview, "merge(AGT-9001): deliver review model");

        Git(Code, "checkout", "-q", "-b", "task/" + EscalatedWithFiles);
        Write(Code, "src/Facts/Escalation.cs", "namespace Facts; public sealed class Escalation { }\n");
        EscalatedCommit = Commit(Code, "feat(AGT-9002): add escalation");
        Git(Code, "checkout", "-q", "develop");
        Merge("task/" + EscalatedWithFiles, "merge(AGT-9002): deliver escalation");

        Git(Code, "checkout", "-q", "-b", "task/" + EscalatedBare);
        Write(Code, "src/Facts/Pending.cs", "namespace Facts; public sealed class Pending { }\n");
        PendingCommit = Commit(Code, "feat(AGT-9003): pending work");
        Git(Code, "checkout", "-q", "develop");

        Git(Code, "branch", "task/" + InProgress);
        for (var index = 1; index <= PackedTaskBranches; index++)
            Git(Code, "branch", $"task/agt-{20_000 + index}", _deliveries[index % _deliveries.Count]);

        Git(_root.Path, "init", "-q", "--bare", Origin);
        Git(Code, "remote", "add", "origin", Origin);
        Git(Code, "push", "-q", "origin", "main", "develop", "--tags");
        Git(Code, "fetch", "-q", "origin");
        Git(Code, "branch", "-q", "--set-upstream-to=origin/develop", "develop");
        Git(Code, "pack-refs", "--all");
        Directory.CreateDirectory(Path.GetDirectoryName(WorktreePath)!);
        Git(Code, "worktree", "add", "-q", WorktreePath, "task/" + InProgress);
        Write(WorktreePath, "src/Facts/InProgress.cs", "namespace Facts; public sealed class InProgress { }\n");
    }

    private readonly Dictionary<string, string> _plainCards = new(StringComparer.Ordinal);

    /// <summary>A card of <paramref name="lane"/> without a special role, for switch replays.</summary>
    public string PlainCard(string lane) => _plainCards[lane];

    public string EscalatedCommit { get; private set; } = "";
    public string PendingCommit { get; private set; } = "";

    private void BuildBoard()
    {
        var serial = 0;
        foreach (var (lane, count) in Shape)
        {
            for (var index = 0; index < count; index++)
            {
                serial++;
                var id = (lane, index) switch
                {
                    (TaskStates.HumanReview, 0) => IntegratedReview,
                    (TaskStates.Escalated, 0) => EscalatedWithFiles,
                    (TaskStates.Escalated, 1) => EscalatedBare,
                    (TaskStates.Progress, 0) => InProgress,
                    (TaskStates.AutoReview, 0) => IntegratedAutoReview,
                    (TaskStates.Archive, < DuplicateIds) => $"dup-{index + 1}",
                    _ => $"agt-{10_000 + serial}",
                };
                WriteCard(lane, id, order: index + 1, serial);
                if (id.StartsWith("agt-1", StringComparison.Ordinal)) _plainCards.TryAdd(lane, id);
            }
        }
        // The same ids again in Completed: a duplicate pair the projection must isolate.
        for (var index = 0; index < DuplicateIds; index++)
            WriteCard(TaskStates.Completed, $"dup-{index + 1}", order: 1_000 + index, serial: index);

        var follow = JobFolder(TaskStates.Escalated, EscalatedWithFiles);
        File.WriteAllText(Path.Combine(follow, "orchestrator-follow-up.md"),
            "# Follow-up\n\nThe reviewer asked for an operator decision.\n");
        Directory.CreateDirectory(Path.Combine(follow, "results"));
        File.WriteAllText(Path.Combine(follow, "results", "decision.html"), "<h1>Decision</h1>");
        File.WriteAllText(Path.Combine(follow, "results", "decision.json"), "{\"question\":\"Ship?\"}");
        // Every fifth other escalated card has a follow-up; the rest have none.
        foreach (var folder in Directory.EnumerateDirectories(Path.Combine(WatchPath, TaskStates.Escalated))
                     .Where(folder => !folder.EndsWith(EscalatedWithFiles, StringComparison.Ordinal)
                         && !folder.EndsWith(EscalatedBare, StringComparison.Ordinal))
                     .Where((_, index) => index % 5 == 0))
            File.WriteAllText(Path.Combine(folder, "orchestrator-follow-up.md"), "# Follow-up\n");

        var logs = Directory.CreateDirectory(Path.Combine(JobFolder(TaskStates.HumanReview, IntegratedReview), "logs"));
        File.WriteAllText(Path.Combine(logs.FullName, "session-events.jsonl"),
            JsonSerializer.Serialize(new
            {
                Ts = Stamp(),
                Kind = "start",
                HeadShaBefore = _deliveries[^1],
                HeadShaAfter = ReviewCommits[1],
            }) + "\n");
    }

    private void WriteCard(string lane, string id, int order, int serial)
    {
        var folder = Directory.CreateDirectory(JobFolder(lane, id)).FullName;
        var key = id.ToUpperInvariant();
        var delivered = lane is TaskStates.Archive or TaskStates.Completed or TaskStates.HumanReview
            || id == IntegratedAutoReview;
        object[] commits = id switch
        {
            IntegratedReview => ReviewCommits.Select((sha, index) => Card(sha, $"feat(AGT-9001): review {index + 1}",
                "src/Facts/Review.cs")).ToArray<object>(),
            EscalatedWithFiles => [Card(EscalatedCommit, "feat(AGT-9002): add escalation", "src/Facts/Escalation.cs")],
            EscalatedBare => [Card(PendingCommit, "feat(AGT-9003): pending work", "src/Facts/Pending.cs")],
            _ when delivered => [Card(_deliveries[serial % _deliveries.Count], $"feat({key}): delivery",
                $"deliveries/d{serial % _deliveries.Count + 1:D3}.txt")],
            _ => [],
        };
        var card = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["key"] = key,
            ["title"] = $"Git facts card {id}",
            ["state"] = lane,
            ["order"] = order,
            ["agent"] = "claude",
            ["cliType"] = "claude",
            ["ownerClientId"] = "local-default",
            ["mode"] = commits.Length > 0 ? "coding" : null,
            ["commits"] = commits,
            ["commit"] = commits.Length > 0 ? commits[^1] : null,
            ["createdAt"] = Stamp(),
        };
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(card));
        File.WriteAllText(Path.Combine(folder, "prompt.md"), $"# {key}\n\nFixture card.\n");
    }

    private Dictionary<string, object> Card(string sha, string message, string file) => new()
    {
        ["sha"] = sha,
        ["shortSha"] = sha[..8],
        ["message"] = message,
        ["filesChanged"] = 1,
        ["files"] = new[] { file },
        ["at"] = Stamp(),
        ["attribution"] = "automatic",
        ["confidence"] = 1,
    };

    /// <summary>
    /// The workspace is a Git repository like the production workspace: the
    /// review card's <c>status.md</c> has two committed versions with run
    /// trailers, so file history and historical reads have something to walk.
    /// </summary>
    private void BuildWorkspaceHistory()
    {
        Git(_root.Path, "init", "-q", "-b", "main", Workspace);
        Git(Workspace, "config", "user.email", "facts@example.invalid");
        Git(Workspace, "config", "user.name", "Git Facts Fixture");
        Git(Workspace, "config", "commit.gpgsign", "false");
        Git(Workspace, "add", "-A");
        Commit(Workspace, "chore(workspace): seed board");

        var review = JobFolder(TaskStates.HumanReview, IntegratedReview);
        Directory.CreateDirectory(Path.Combine(review, "results"));
        File.WriteAllText(Path.Combine(review, "results", "status.md"), "# Status\n\nRun 1: reissue.\n");
        File.WriteAllText(Path.Combine(review, "code-review.md"), "run 1 review\n");
        FirstStatusVersion = Commit(Workspace,
            "chore(workspace): record run artifacts for AGT-9001\n\nRun-Index: 1\nVerdict: reissue");
        File.WriteAllText(Path.Combine(review, "results", "status.md"), "# Status\n\nRun 2: accept.\n");
        File.WriteAllText(Path.Combine(review, "code-review.md"), "run 2 review\n");
        Commit(Workspace, "chore(workspace): record run artifacts for AGT-9001\n\nRun-Index: 2\nVerdict: accept");
    }

    private void Merge(string branch, string subject)
    {
        Clock.Advance(TimeSpan.FromMinutes(1));
        Git(Code, "merge", "-q", "--no-ff", "-m", subject, branch);
    }

    private string Commit(string repo, string message)
    {
        Clock.Advance(TimeSpan.FromMinutes(1));
        Git(repo, "add", "-A");
        Git(repo, "commit", "-q", "--allow-empty", "-m", message);
        return Git(repo, "rev-parse", "HEAD").Trim();
    }

    private static void Write(string repo, string relative, string content)
    {
        var path = Path.Combine(repo, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content.Replace("\r\n", "\n"));
    }

    private string Stamp() => Clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);

    public string Git(string cwd, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        var stamp = Stamp();
        start.Environment["GIT_AUTHOR_DATE"] = stamp;
        start.Environment["GIT_COMMITTER_DATE"] = stamp;
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {error.Result}");
        return output.Result;
    }

    public void Dispose() => _root.Dispose();
}
