using System.Diagnostics;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

public sealed class ProjectIntegrationViewServiceTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "integration-view-" + Guid.NewGuid().ToString("N"));

    public ProjectIntegrationViewServiceTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); } catch { }
    }

    [Fact]
    public void Build_UsesOriginDevelop_ThenExposesQueuePublisherAndPromotion()
    {
        var remote = Path.Combine(_temp, "remote.git");
        var repo = Path.Combine(_temp, "repo");
        var tasks = Path.Combine(_temp, "tasks");
        Directory.CreateDirectory(repo);
        RunGit(_temp, "init", "-q", "--bare", remote);
        RunGit(repo, "init", "-q", "-b", "main");
        RunGit(repo, "config", "user.email", "test@example.com");
        RunGit(repo, "config", "user.name", "publisher");
        File.WriteAllText(Path.Combine(repo, "README.md"), "seed\n");
        RunGit(repo, "add", "-A");
        RunGit(repo, "commit", "-q", "-m", "seed");
        RunGit(repo, "remote", "add", "origin", remote);
        RunGit(repo, "push", "-q", "-u", "origin", "main");
        RunGit(repo, "checkout", "-q", "-b", "develop");
        RunGit(repo, "push", "-q", "-u", "origin", "develop");

        RunGit(repo, "checkout", "-q", "main");
        File.WriteAllText(Path.Combine(repo, "release-only.txt"), "already on main\n");
        RunGit(repo, "add", "-A");
        RunGit(repo, "commit", "-q", "-m", "fix: release-only hotfix");
        RunGit(repo, "push", "-q", "origin", "main");
        RunGit(repo, "checkout", "-q", "develop");
        File.WriteAllText(Path.Combine(repo, "feature.txt"), "integrated work\n");
        RunGit(repo, "add", "-A");
        RunGit(repo, "commit", "-q", "-m", "merge(AGT-1): curated feature");
        var localMerge = GitOut(repo, "rev-parse", "HEAD");
        WriteTask(tasks, "done", "AGT-1", "Curated feature", localMerge);
        WriteTask(tasks, "docs", "AGT-2", "Read-only note", null);
        WriteTask(tasks, "review", "AGT-3", "Not accepted yet", null, TaskStates.HumanReview);

        var service = BuildService(repo, tasks);

        // A local commit does not count. Integration truth is origin/develop.
        var beforePush = service.Build("Demo");
        Assert.Equal("origin/develop", beforePush.IntegrationRef);
        Assert.Equal(IntegrationQueueStates.Waiting,
            Assert.Single(beforePush.Queue, item => item.TaskKey == "AGT-1").Status);
        Assert.Equal(IntegrationQueueStates.Skipped,
            Assert.Single(beforePush.Queue, item => item.TaskKey == "AGT-2").Status);
        Assert.DoesNotContain(beforePush.Queue, item => item.TaskKey == "AGT-3");
        Assert.Empty(beforePush.PublisherMerges);

        RunGit(repo, "push", "-q", "origin", "develop");

        var afterPush = service.Build("Demo");
        var merged = Assert.Single(afterPush.Queue, item => item.TaskKey == "AGT-1");
        Assert.Equal(IntegrationQueueStates.Merged, merged.Status);
        Assert.Equal(localMerge, merged.MergeSha);
        Assert.Equal("AGT-1", Assert.Single(afterPush.PublisherMerges).TaskKey);
        Assert.Equal("AGT-1", Assert.Single(afterPush.Promotion.Tasks).TaskKey);
        Assert.Contains(afterPush.Promotion.Files, file => file.Path == "feature.txt");
        Assert.DoesNotContain(afterPush.Promotion.Files, file => file.Path == "release-only.txt");
        Assert.Equal(afterPush.Promotion.Files.Count, afterPush.Promotion.FilesChanged);
    }

    [Fact]
    public void Build_TerminalizesArchivedLegacyAndFileConflicts_WithOriginalEvidence()
    {
        var remote = Path.Combine(_temp, "terminal-remote.git");
        var repo = Path.Combine(_temp, "terminal-repo");
        var tasks = Path.Combine(_temp, "terminal-tasks");
        Directory.CreateDirectory(repo);
        RunGit(_temp, "init", "-q", "--bare", remote);
        RunGit(repo, "init", "-q", "-b", "main");
        RunGit(repo, "config", "user.email", "test@example.com");
        RunGit(repo, "config", "user.name", "publisher");
        File.WriteAllText(Path.Combine(repo, "README.md"), "seed\n");
        RunGit(repo, "add", "-A");
        RunGit(repo, "commit", "-q", "-m", "seed");
        RunGit(repo, "remote", "add", "origin", remote);
        RunGit(repo, "push", "-q", "-u", "origin", "main");
        RunGit(repo, "checkout", "-q", "-b", "develop");
        RunGit(repo, "push", "-q", "-u", "origin", "develop");

        const string missingSha = "ffffffffffffffffffffffffffffffffffffffff";
        WriteTask(tasks, "legacy", "AGT-LEGACY", "Legacy subject", missingSha, TaskStates.Archive);
        WriteTask(tasks, "files", "AGT-FILES", "Archived file conflict", missingSha, TaskStates.Archive);
        WriteConflict(Path.Combine(tasks, TaskStates.Archive, "legacy"), "Conflicted: legacy.cs");
        WriteConflict(Path.Combine(tasks, TaskStates.Archive, "files"), "Conflicted: current.cs");
        WriteLegacyReviewSubject(Path.Combine(tasks, TaskStates.Archive, "legacy"), "AGT-LEGACY", missingSha);

        var view = BuildService(repo, tasks).Build("Demo");

        var legacy = Assert.Single(view.Queue, item => item.TaskKey == "AGT-LEGACY");
        Assert.Equal(IntegrationQueueStates.LegacyUnverifiable, legacy.Status);
        Assert.Contains("Conflicted: legacy.cs", legacy.Reason);
        var superseded = Assert.Single(view.Queue, item => item.TaskKey == "AGT-FILES");
        Assert.Equal(IntegrationQueueStates.Superseded, superseded.Status);
        Assert.Contains("Conflicted: current.cs", superseded.Reason);
        Assert.DoesNotContain(view.Queue, item => item.Status == IntegrationQueueStates.Conflict);
    }

    [Fact]
    public async Task Endpoint_ReturnsTheGitDerivedIntegrationProjection()
    {
        var remote = Path.Combine(_temp, "endpoint-remote.git");
        var repo = Path.Combine(_temp, "endpoint-repo");
        var tasks = Path.Combine(_temp, "endpoint-tasks");
        Directory.CreateDirectory(repo);
        RunGit(_temp, "init", "-q", "--bare", remote);
        RunGit(repo, "init", "-q", "-b", "main");
        RunGit(repo, "config", "user.email", "test@example.com");
        RunGit(repo, "config", "user.name", "publisher");
        File.WriteAllText(Path.Combine(repo, "README.md"), "seed\n");
        RunGit(repo, "add", "-A");
        RunGit(repo, "commit", "-q", "-m", "seed");
        RunGit(repo, "remote", "add", "origin", remote);
        RunGit(repo, "push", "-q", "-u", "origin", "main");
        RunGit(repo, "checkout", "-q", "-b", "develop");
        File.WriteAllText(Path.Combine(repo, "endpoint.txt"), "integrated\n");
        RunGit(repo, "add", "-A");
        RunGit(repo, "commit", "-q", "-m", "merge(AGT-9): endpoint projection");
        var mergeSha = GitOut(repo, "rev-parse", "HEAD");
        RunGit(repo, "push", "-q", "-u", "origin", "develop");
        WriteTask(tasks, "endpoint", "AGT-9", "Endpoint projection", mergeSha);

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TaskRepository"] = Path.Combine(_temp, "host-state"),
                    ["Logging:BackendFile:LogDirectory"] = Path.Combine(_temp, "host-logs"),
                    ["WatchPaths:0:Name"] = "Endpoint Demo",
                    ["WatchPaths:0:RootPath"] = repo,
                    ["WatchPaths:0:RepositoryPath"] = repo,
                    ["WatchPaths:0:Path"] = tasks,
                }));
            // This fixture verifies a read-only endpoint against a fully built
            // host. Background workers are outside its subject and can rewrite
            // completed-card metadata or run Git concurrently while the request
            // is being asserted, which is especially visible with Windows Git's
            // higher spawn cost and exclusive file locking.
            builder.ConfigureTestServices(services => services.RemoveAll<IHostedService>());
        });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/git/integration?project=Endpoint%20Demo");

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ProjectIntegrationView>();
        Assert.NotNull(body);
        Assert.Equal("origin/develop", body!.IntegrationRef);
        Assert.Equal(mergeSha, Assert.Single(body.Queue).MergeSha);
        Assert.Equal("AGT-9", Assert.Single(body.PublisherMerges).TaskKey);
    }

    [Fact]
    public void ClassificationPolicy_SeparatesActionableAndArchivedConflictOutcomes()
    {
        var activeConflict = IntegrationQueueClassificationPolicy.Decide(Facts(
            archived: false,
            legacy: false,
            IntegrationStatuses.ConflictSkipped,
            "Conflict in app.ts."));
        var legacyArchive = IntegrationQueueClassificationPolicy.Decide(Facts(
            archived: true,
            legacy: true,
            IntegrationStatuses.ConflictSkipped,
            "Review subject for 'AGT-1' has no RunAttemptId and cannot be accepted."));
        var archivedConflict = IntegrationQueueClassificationPolicy.Decide(Facts(
            archived: true,
            legacy: false,
            IntegrationStatuses.ConflictSkipped,
            "Conflict in app.ts."));

        Assert.Equal(IntegrationQueueStates.Conflict, activeConflict.Status);
        Assert.Equal("Conflict in app.ts.", activeConflict.Reason);
        Assert.Equal(IntegrationQueueStates.LegacyUnverifiable, legacyArchive.Status);
        Assert.Contains("predates RunAttempt authority", legacyArchive.Reason);
        Assert.Contains("Original integration outcome", legacyArchive.Reason);
        Assert.Equal(IntegrationQueueStates.Superseded, archivedConflict.Status);
        Assert.Contains("archived card", archivedConflict.Reason);
        Assert.Contains("Recover still-required work through a new card", archivedConflict.Reason);
    }

    [Fact]
    public void ClassificationPolicy_PreservesIntegratedAndNonConflictOutcomes()
    {
        const string proof = "0123456789012345678901234567890123456789";
        var merged = IntegrationQueueClassificationPolicy.Decide(new(
            IsIntegrated: true,
            IntegrationProof: proof,
            IsArchived: true,
            HasLegacyUnfencedReviewSubject: true,
            IntegrationStatus: IntegrationStatuses.ConflictSkipped,
            IntegrationDetail: "stale conflict",
            IntegrationRef: "origin/develop"));
        var skipped = IntegrationQueueClassificationPolicy.Decide(Facts(
            archived: true,
            legacy: true,
            IntegrationStatuses.NoBranch,
            "No delivery ref."));
        var waiting = IntegrationQueueClassificationPolicy.Decide(Facts(
            archived: false,
            legacy: false,
            IntegrationStatuses.Pending,
            null));

        Assert.Equal(IntegrationQueueStates.Merged, merged.Status);
        Assert.Equal(proof, merged.MergeSha);
        Assert.Null(merged.Reason);
        Assert.Equal(IntegrationQueueStates.Skipped, skipped.Status);
        Assert.Equal("No delivery ref.", skipped.Reason);
        Assert.Equal(IntegrationQueueStates.Waiting, waiting.Status);
        Assert.Equal("Accepted change is not present in origin/develop.", waiting.Reason);
    }

    private ProjectIntegrationViewService BuildService(string repo, string tasks)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "Demo",
            ["WatchPaths:0:RootPath"] = repo,
            ["WatchPaths:0:RepositoryPath"] = repo,
            ["WatchPaths:0:Path"] = tasks,
        }).Build();
        var scanner = new TaskScannerService(
            config,
            NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var pipeline = new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance);
        var taskIntegration = new TaskIntegrationStatusService(
            git, settings, pipeline, NullLogger<TaskIntegrationStatusService>.Instance);
        return new ProjectIntegrationViewService(git, scanner, settings, taskIntegration);
    }

    private static IntegrationQueueClassificationFacts Facts(
        bool archived,
        bool legacy,
        string integrationStatus,
        string? detail)
        => new(
            IsIntegrated: false,
            IntegrationProof: null,
            IsArchived: archived,
            HasLegacyUnfencedReviewSubject: legacy,
            IntegrationStatus: integrationStatus,
            IntegrationDetail: detail,
            IntegrationRef: "origin/develop");

    private static void WriteConflict(string folder, string summary)
    {
        File.WriteAllText(Path.Combine(folder, PipelineExecutionLog.FileName),
            $$"""
            {
              "pipelineId": "standard",
              "pipelineVersion": 1,
              "project": "Demo",
              "jobId": "fixture",
              "startedAt": "2026-07-22T10:00:00Z",
              "attempt": 1,
              "steps": [
                {
                  "stepId": "post-merge-into-develop",
                  "status": 3,
                  "verdict": "conflict",
                  "verdictSummary": "{{summary}}"
                }
              ]
            }
            """);
    }

    private static void WriteLegacyReviewSubject(string folder, string key, string sha)
    {
        var path = ReviewSubjectStore.PathFor(folder);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            $$"""
            {
              "version": 1,
              "taskKey": "{{key}}",
              "runAttemptId": "",
              "project": "Demo",
              "repository": "fixture",
              "resultSha": "{{sha}}",
              "attemptChainId": "legacy-chain",
              "completedAtUtc": "2026-07-22T10:00:00Z"
            }
            """);
    }

    private static void WriteTask(
        string tasks,
        string id,
        string key,
        string title,
        string? sha,
        string state = TaskStates.Completed)
    {
        var folder = Path.Combine(tasks, state, id);
        Directory.CreateDirectory(folder);
        var commits = sha == null
            ? "[]"
            : $"[{{\"sha\":\"{sha}\",\"message\":\"work\",\"authorEmail\":\"x@y\",\"at\":\"2026-07-22T10:00:00Z\",\"fileCount\":1}}]";
        File.WriteAllText(Path.Combine(folder, "task.json"),
            $"{{\"id\":\"{id}\",\"key\":\"{key}\",\"title\":\"{title}\",\"state\":\"{state}\",\"order\":1,\"agent\":\"codex\",\"enteredLaneAt\":\"2026-07-22T10:00:00Z\",\"commits\":{commits}}}");
    }

    private static string GitOut(string cwd, params string[] args)
    {
        var (output, error, code) = Run(cwd, args);
        if (code != 0) throw new InvalidOperationException(error);
        return output.Trim();
    }

    private static void RunGit(string cwd, params string[] args)
    {
        var (_, error, code) = Run(cwd, args);
        if (code != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {error}");
    }

    private static (string Output, string Error, int Code) Run(string cwd, string[] args)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit(15_000);
        return (output, error, process.ExitCode);
    }
}
