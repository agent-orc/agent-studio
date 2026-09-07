using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class TestRunServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "test-runs-" + Guid.NewGuid().ToString("N"));

    public TestRunServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void CardEvidence_DistinguishesExactContainedEarlierAndMissingRuns()
    {
        var stack = BuildStack();
        var exactCommit = RevParse(stack.Repo, "HEAD");
        stack.Service.Create("Demo", Request(exactCommit, "completed", "passed"));

        Commit(stack.Repo, "included.txt", "included", "Card included by later run");
        var cardCommit = RevParse(stack.Repo, "HEAD");
        Commit(stack.Repo, "later.txt", "later", "Later test revision");
        var laterRunCommit = RevParse(stack.Repo, "HEAD");
        stack.Service.Create("Demo", Request(laterRunCommit, "completed", "passed"));

        Commit(stack.Repo, "after.txt", "after", "Card newer than every run");
        var newerCardCommit = RevParse(stack.Repo, "HEAD");
        var jobs = new[]
        {
            Task("exact", stack.Storage, exactCommit),
            Task("contained", stack.Storage, cardCommit),
            Task("before", stack.Storage, newerCardCommit),
            Task("missing", stack.Storage, new string('f', 40)),
        };

        var evidence = stack.Service.BuildLookup(jobs);

        Assert.Equal("perfect", evidence[jobs[0].TaskKey].MatchQuality);
        Assert.Equal("proven", evidence[jobs[0].TaskKey].EvidenceState);
        Assert.True(evidence[jobs[0].TaskKey].DiffContained);

        Assert.Equal("contains-diff", evidence[jobs[1].TaskKey].MatchQuality);
        Assert.Equal(1, evidence[jobs[1].TaskKey].Distance);
        Assert.Equal("after", evidence[jobs[1].TaskKey].Direction);
        Assert.Equal("proven", evidence[jobs[1].TaskKey].EvidenceState);

        Assert.Equal("does-not-contain-diff", evidence[jobs[2].TaskKey].MatchQuality);
        Assert.False(evidence[jobs[2].TaskKey].DiffContained);
        Assert.Equal("not-proven", evidence[jobs[2].TaskKey].EvidenceState);

        Assert.Null(evidence[jobs[3].TaskKey].RunId);
        Assert.Equal("unassigned", evidence[jobs[3].TaskKey].EvidenceState);
        Assert.Contains("No matching test run", evidence[jobs[3].TaskKey].Summary);
    }

    [Fact]
    public void CardEvidence_ReusesProjectionUntilRepositoryRefsChange()
    {
        var stack = BuildStack();
        var commit = RevParse(stack.Repo, "HEAD");
        stack.Service.Create("Demo", Request(commit, "completed", "passed"));
        var job = Task("cached", stack.Storage, commit);

        using (GitProcessTelemetry.BeginRequest("first", NullLogger.Instance, includeNested: true))
        {
            Assert.Equal("perfect", stack.Service.BuildLookup([job])[job.TaskKey].MatchQuality);
            Assert.True(GitProcessTelemetry.CurrentTally()!.Value.Spawns > 0);
        }

        using (GitProcessTelemetry.BeginRequest("unchanged", NullLogger.Instance, includeNested: true))
        {
            Assert.Equal("perfect", stack.Service.BuildLookup([job])[job.TaskKey].MatchQuality);
            Assert.Equal(0, GitProcessTelemetry.CurrentTally()!.Value.Spawns);
        }

        Commit(stack.Repo, "new-ref.txt", "new", "Move integration ref");

        using (GitProcessTelemetry.BeginRequest("changed-ref", NullLogger.Instance, includeNested: true))
        {
            Assert.Equal("perfect", stack.Service.BuildLookup([job])[job.TaskKey].MatchQuality);
            Assert.True(GitProcessTelemetry.CurrentTally()!.Value.Spawns > 0);
        }

        var movedCommit = RevParse(stack.Repo, "HEAD");
        var movedRun = stack.Service.Create("Demo", Request(movedCommit, "completed", "passed"))!;
        var movedJob = Task("cached", stack.Storage, movedCommit);

        using (GitProcessTelemetry.BeginRequest("new-run", NullLogger.Instance, includeNested: true))
        {
            var evidence = stack.Service.BuildLookup([movedJob])[movedJob.TaskKey];
            Assert.Equal(movedRun.Id, evidence.RunId);
            Assert.Equal("perfect", evidence.MatchQuality);
            Assert.True(GitProcessTelemetry.CurrentTally()!.Value.Spawns > 0);
        }
    }

    [Fact]
    public void Lifecycle_PersistsForwardTransitions_AndRejectsBackwardState()
    {
        var stack = BuildStack();
        var commit = RevParse(stack.Repo, "HEAD");
        var planned = stack.Service.Create("Demo", Request(commit, "planned", null))!;

        var running = stack.Service.Update("Demo", planned.Id, new UpdateTestRunRequest
        {
            State = "running",
            Host = "runner-01",
        });
        var completed = stack.Service.Update("Demo", planned.Id, new UpdateTestRunRequest
        {
            State = "completed",
            Result = "passed",
            DurationSeconds = 12.5,
            Host = "runner-01",
        });

        Assert.NotNull(running!.StartedAt);
        Assert.Equal("passed", completed!.Result);
        Assert.Equal(12.5, completed.DurationSeconds);
        Assert.NotNull(completed.CompletedAt);
        Assert.Equal("passed", Assert.Single(new TestRunStore(stack.Configuration).List(stack.Project.Id)).Result);
        Assert.Throws<TestRunValidationException>(() => stack.Service.Update("Demo", planned.Id, new UpdateTestRunRequest
        {
            State = "running",
        }));
    }

    [Fact]
    public async Task Api_CreatesReadsAndCompletesDurableRun()
    {
        var stack = BuildStack();
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["TaskRepository"] = stack.Configuration["TaskRepository"],
                    ["WatchPaths:0:Name"] = "Demo",
                    ["WatchPaths:0:Path"] = stack.Storage,
                    ["WatchPaths:0:RootPath"] = stack.Repo,
                    ["WatchPaths:0:RepositoryPath"] = stack.Repo,
                }));
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);
        var commit = RevParse(stack.Repo, "HEAD");

        var create = await client.PostAsJsonAsync("/api/projects/Demo/test-runs", Request(commit, "planned", null));

        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var planned = await create.Content.ReadFromJsonAsync<TestRunRecord>();
        Assert.NotNull(planned);
        var complete = await client.PutAsJsonAsync($"/api/projects/Demo/test-runs/{planned!.Id}", new UpdateTestRunRequest
        {
            State = "completed",
            Result = "passed",
            Host = "runner-api",
        });
        complete.EnsureSuccessStatusCode();
        var completed = await complete.Content.ReadFromJsonAsync<TestRunRecord>();
        Assert.Equal("passed", completed!.Result);
        Assert.NotNull(completed.DurationSeconds);

        var view = await client.GetFromJsonAsync<ProjectTestRunsResponse>("/api/projects/Demo/test-runs");
        Assert.Equal(planned.Id, Assert.Single(view!.Runs).Run.Id);
        Assert.Equal("runner-api", view.Runs[0].Run.Host);
    }

    [Fact]
    public void Lifecycle_NormalizesEnumsAndRejectsInvalidQueueOrder()
    {
        var stack = BuildStack();
        var commit = RevParse(stack.Repo, "HEAD");

        var completed = stack.Service.Create("Demo", Request(commit, "COMPLETED", "PASSED"));

        Assert.Equal(TestRunStates.Completed, completed!.State);
        Assert.Equal(TestRunResults.Passed, completed.Result);
        Assert.Equal(4, completed.DurationSeconds);
        var invalid = Request(commit, "planned", null) with { PlannedOrder = 0 };
        Assert.Throws<TestRunValidationException>(() => stack.Service.Create("Demo", invalid));
    }

    [Fact]
    public void CompletedCard_WaitsForPlannedExactRun_InsteadOfClaimingGreen()
    {
        var stack = BuildStack();
        var commit = RevParse(stack.Repo, "HEAD");
        var run = stack.Service.Create("Demo", Request(commit, "planned", null))!;
        var job = Task("done", stack.Storage, commit) with { State = TaskStates.Completed };

        var evidence = stack.Service.BuildLookup([job])[job.TaskKey];

        Assert.Equal(run.Id, evidence.RunId);
        Assert.Equal("pending", evidence.EvidenceState);
        Assert.True(evidence.AwaitingEvidence);
        Assert.StartsWith("Evidence pending", evidence.Summary);
    }

    [Fact]
    public void CardEvidence_BindsExactRemoteReviewBuildTestsGrade_WhenProjectRunIsAbsent()
    {
        var stack = BuildStack();
        var commit = RevParse(stack.Repo, "HEAD");
        var folder = Path.Combine(stack.Storage, TaskStates.Archive, "review-evidence");
        Directory.CreateDirectory(folder);
        File.WriteAllText(
            Path.Combine(folder, "remote-review-grade-review-42.md"),
            $"""
             ---
             type: remote-review-grade
             attemptId: "review-42"
             receivedAt: 2026-07-29T20:41:22Z
             outcome: "Pass"
             expectedResultSha: "{commit}"
             actualHead: "{commit}"
             ---

             ## Aspect verdicts

             | Aspect | Status | Classification | Summary |
             | --- | --- | --- | --- |
             | build-tests | pass | CommandPassed | Review command passed. |
             """);
        var job = Task("review-evidence", stack.Storage, commit) with
        {
            State = TaskStates.Archive,
            FolderPath = folder,
        };

        var evidence = stack.Service.BuildLookup([job])[job.TaskKey];

        Assert.Equal("proven", evidence.EvidenceState);
        Assert.Equal("perfect", evidence.MatchQuality);
        Assert.False(evidence.AwaitingEvidence);
        Assert.Equal($"Review build-tests Pass at {commit[..8]}", evidence.Summary);
        var source = Assert.Single(evidence.Sources);
        Assert.Equal("review-build-tests", source.Kind);
        Assert.Equal("review-42", source.Id);
        Assert.Equal(commit, source.Commit);
        Assert.Equal("passed", source.Result);
        Assert.Equal("All build-tests verdicts passed.", source.Reason);
        Assert.Equal("remote-review-grade-review-42.md", source.ReportRef);
    }

    [Fact]
    public void CardEvidence_KeepsPassingReviewBuildTestsIndependentFromBlockedAspect()
    {
        var stack = BuildStack();
        var commit = RevParse(stack.Repo, "HEAD");
        var folder = Path.Combine(stack.Storage, TaskStates.HumanReview, "agt-2689-review-evidence");
        Directory.CreateDirectory(folder);
        File.WriteAllText(
            Path.Combine(folder, "remote-review-grade-review_ad5cca8e3178425fb9ba9cabe329d50e.md"),
            $"""
             ---
             type: remote-review-grade
             attemptId: "review_ad5cca8e3178425fb9ba9cabe329d50e"
             receivedAt: 2026-08-31T12:00:00Z
             outcome: "ProductFailure"
             expectedResultSha: "{commit}"
             actualHead: "{commit}"
             ---

             ## Aspect verdicts

             | Aspect | Status | Classification | Summary |
             | --- | --- | --- | --- |
             | build-tests | pass | CommandPassed | Review command 'verify-1' passed. |
             | build-tests | pass | CommandPassed | Review command 'verify-2' passed. |
             | requirement-fit | pass | RemoteAspectVerdict | Requirements match the implementation. |
             | code-quality | pass | RemoteAspectVerdict | Code quality passed. |
             | tests-and-evidence | pass | RemoteAspectVerdict | Tests and evidence passed. |
             | documentation-impact | block | RemoteAspectVerdict | Public API and state-file contract changed without corresponding load-bearing doc updates. |

             ## Command evidence

             | Phase | Workspace | Step | Location | Host / executor | Command | Exit | Budget | Output | Errors |
             | --- | --- | --- | --- | --- | --- | ---: | --- | --- | --- |
             | verification | candidate | verify-1 | local | review / executor | `dotnet build` | 0 | verify: 100/1000 ms | stdout | stderr |
             | verification | candidate | verify-2 | local | review / executor | `dotnet test` | 0 | verify: 528000/7200000 ms | stdout | stderr |
             """);
        var job = Task("agt-2689-review-evidence", stack.Storage, commit) with
        {
            State = TaskStates.HumanReview,
            FolderPath = folder,
        };

        var evidence = stack.Service.BuildLookup([job])[job.TaskKey];

        Assert.Equal("proven", evidence.EvidenceState);
        Assert.Equal($"Review build-tests Pass at {commit[..8]} (verify-1, verify-2)", evidence.Summary);
        Assert.Collection(
            evidence.Sources,
            buildTests =>
            {
                Assert.Equal("review-build-tests", buildTests.Kind);
                Assert.Equal("passed", buildTests.Result);
                Assert.Equal("verify-1 and verify-2 passed.", buildTests.Reason);
                Assert.Equal("remote-review-grade-review_ad5cca8e3178425fb9ba9cabe329d50e.md", buildTests.ReportRef);
            },
            aspects =>
            {
                Assert.Equal("review-aspects", aspects.Kind);
                Assert.Equal("blocked", aspects.Result);
                Assert.Equal("Review blocked by documentation-impact", aspects.Summary);
                Assert.Equal(
                    "documentation-impact blocked: Public API and state-file contract changed without corresponding load-bearing doc updates.",
                    aspects.Reason);
                Assert.Equal("remote-review-grade-review_ad5cca8e3178425fb9ba9cabe329d50e.md", aspects.ReportRef);
            });
    }

    [Fact]
    public void CardEvidence_ExplainsWhichReviewBuildTestsVerdictIsMissing()
    {
        var stack = BuildStack();
        var commit = RevParse(stack.Repo, "HEAD");
        var folder = Path.Combine(stack.Storage, TaskStates.HumanReview, "missing-review-build-tests");
        Directory.CreateDirectory(folder);
        File.WriteAllText(
            Path.Combine(folder, "remote-review-grade-review-missing.md"),
            $"""
             ---
             type: remote-review-grade
             attemptId: "review-missing"
             receivedAt: 2026-08-31T12:00:00Z
             outcome: "ProductFailure"
             expectedResultSha: "{commit}"
             actualHead: "{commit}"
             ---

             ## Aspect verdicts

             | Aspect | Status | Classification | Summary |
             | --- | --- | --- | --- |
             | documentation-impact | block | RemoteAspectVerdict | Required documentation was not updated. |

             ## Command evidence

             | Phase | Workspace | Step | Location | Host / executor | Command | Exit | Budget | Output | Errors |
             | --- | --- | --- | --- | --- | --- | ---: | --- | --- | --- |
             | verification | candidate | verify-1 | local | review / executor | `dotnet build` | 0 | verify: 100/1000 ms | stdout | stderr |
             """);
        var job = Task("missing-review-build-tests", stack.Storage, commit) with
        {
            State = TaskStates.HumanReview,
            FolderPath = folder,
        };

        var evidence = stack.Service.BuildLookup([job])[job.TaskKey];

        Assert.Equal("not-proven", evidence.EvidenceState);
        var buildTests = Assert.Single(evidence.Sources, source => source.Kind == "review-build-tests");
        Assert.Equal("not-proven", buildTests.Result);
        Assert.Equal("Build-tests verdict is missing for verify-1.", buildTests.Reason);
    }

    [Fact]
    public void CardEvidence_BindsExactBuildGateLog_WhenProjectRunIsAbsent()
    {
        var stack = BuildStack();
        var commit = RevParse(stack.Repo, "HEAD");
        var folder = Path.Combine(stack.Storage, TaskStates.HumanReview, "gate-evidence");
        var postSteps = Path.Combine(folder, "post-steps");
        Directory.CreateDirectory(postSteps);
        File.WriteAllText(
            Path.Combine(postSteps, "build-test-gate-1.log"),
            $"verdict=Ok exit=0 signal=n/a durationMs=1500\n"
            + "gateId=post-build-test-gate failureKind=None failureFingerprint=n/a\n"
            + "gateRunId=gate-42 startedAtUtc=2026-07-29T20:39:00Z completedAtUtc=2026-07-29T20:40:00Z\n"
            + $"repository=demo expectedSha={commit} testedSha={commit}\n"
            + "reason=All selected commands passed.\n");
        var job = Task("gate-evidence", stack.Storage, commit) with { FolderPath = folder };

        var evidence = stack.Service.BuildLookup([job])[job.TaskKey];

        Assert.Equal("proven", evidence.EvidenceState);
        Assert.Equal($"Build/test gate green at {commit[..8]}", evidence.Summary);
        var source = Assert.Single(evidence.Sources);
        Assert.Equal("build-test-gate", source.Kind);
        Assert.Equal("gate-42", source.Id);
        Assert.Equal("All selected commands passed.", source.Reason);
        Assert.Equal("post-steps/build-test-gate-1.log", source.ReportRef);
    }

    [Theory]
    [InlineData("NotApplicable", "no verify commands derivable", "not-applicable", "not-applicable", "No build/test defined")]
    [InlineData("Skipped", "no verify commands derivable", "not-applicable", "not-applicable", "No build/test defined")]
    [InlineData("Skipped", "pipeline interrupted before command execution", "not-proven", "not-proven", "Build/test gate skipped at")]
    public void CardEvidence_DistinguishesNotApplicableFromSkippedBuildGate(
        string verdict,
        string reason,
        string expectedEvidenceState,
        string expectedSourceResult,
        string expectedSummary)
    {
        var stack = BuildStack();
        var commit = RevParse(stack.Repo, "HEAD");
        var folder = Path.Combine(stack.Storage, TaskStates.HumanReview, $"gate-{verdict}");
        var postSteps = Path.Combine(folder, "post-steps");
        Directory.CreateDirectory(postSteps);
        File.WriteAllText(
            Path.Combine(postSteps, "build-test-gate-1.log"),
            $"verdict={verdict} exit=n/a signal=n/a durationMs=0\n"
            + "gateId=post-build-test-gate failureKind=None failureFingerprint=n/a\n"
            + "gateRunId=gate-42 startedAtUtc=2026-08-08T10:00:00Z completedAtUtc=2026-08-08T10:00:01Z\n"
            + $"repository=demo expectedSha={commit} testedSha={commit}\n"
            + $"reason={reason}\n");
        var job = Task($"gate-{verdict}", stack.Storage, commit) with { FolderPath = folder };

        var evidence = stack.Service.BuildLookup([job])[job.TaskKey];

        Assert.Equal(expectedEvidenceState, evidence.EvidenceState);
        Assert.StartsWith(expectedSummary, evidence.Summary, StringComparison.Ordinal);
        var source = Assert.Single(evidence.Sources);
        Assert.Equal(expectedSourceResult, source.Result);
    }

    [Fact]
    public void CardEvidence_DoesNotReuseTaskScopedGradeForDifferentCommit()
    {
        var stack = BuildStack();
        var oldCommit = RevParse(stack.Repo, "HEAD");
        Commit(stack.Repo, "new.txt", "new", "New card revision");
        var currentCommit = RevParse(stack.Repo, "HEAD");
        var folder = Path.Combine(stack.Storage, TaskStates.Archive, "stale-review-evidence");
        Directory.CreateDirectory(folder);
        File.WriteAllText(
            Path.Combine(folder, "remote-review-grade-review-old.md"),
            $"""
             ---
             type: remote-review-grade
             attemptId: "review-old"
             receivedAt: 2026-07-29T20:41:22Z
             outcome: "Pass"
             expectedResultSha: "{oldCommit}"
             actualHead: "{oldCommit}"
             ---

             ## Aspect verdicts

             | Aspect | Status | Classification | Summary |
             | --- | --- | --- | --- |
             | build-tests | pass | CommandPassed | Review command passed. |
             """);
        var job = Task("stale-review-evidence", stack.Storage, currentCommit) with
        {
            State = TaskStates.Archive,
            FolderPath = folder,
        };

        var evidence = stack.Service.BuildLookup([job])[job.TaskKey];

        Assert.Equal("unassigned", evidence.EvidenceState);
        Assert.False(evidence.AwaitingEvidence);
        Assert.Empty(evidence.Sources);
        Assert.Equal("No test evidence assigned", evidence.Summary);
    }

    [Fact]
    public void CardEvidence_RecognizesDiffInCuratedRecut_WhenOriginalCommitIsNotAnAncestor()
    {
        var stack = BuildStack();
        var olderRunCommit = RevParse(stack.Repo, "HEAD");
        stack.Service.Create("Demo", Request(olderRunCommit, "completed", "passed"));
        Git(stack.Repo, "checkout", "-q", "-b", "task/rewritten");
        Commit(stack.Repo, "rewritten.txt", "task version", "Original task commit");
        var taskCommit = RevParse(stack.Repo, "HEAD");

        Git(stack.Repo, "checkout", "-q", "develop");
        Commit(stack.Repo, "rewritten.txt", "integrated version", "merge-recut(DEM-9): integrate rewritten task");
        Commit(stack.Repo, "later.txt", "later", "Later test revision");
        var runCommit = RevParse(stack.Repo, "HEAD");
        var containingRun = stack.Service.Create("Demo", Request(runCommit, "completed", "passed"))!;
        var job = Task("rewritten", stack.Storage, taskCommit, DateTime.UnixEpoch);

        var evidence = stack.Service.BuildLookup([job])[job.TaskKey];

        Assert.Equal(containingRun.Id, evidence.RunId);
        Assert.Equal("contains-diff", evidence.MatchQuality);
        Assert.Equal("proven", evidence.EvidenceState);
        Assert.Equal(1, evidence.Distance);
        Assert.True(evidence.DiffContained);
    }

    [Fact]
    public void CardEvidence_DoesNotReuseGreenRecutThatPredatesCurrentCardCommit()
    {
        var stack = BuildStack();
        Commit(stack.Repo, "integrated-v1.txt", "v1", "merge-recut(DEM-9): integrate first revision");
        var oldRunCommit = RevParse(stack.Repo, "HEAD");
        stack.Service.Create("Demo", Request(oldRunCommit, "completed", "passed"));

        Git(stack.Repo, "checkout", "-q", "-b", "task/current-revision");
        Commit(stack.Repo, "current-v2.txt", "v2", "Current card revision");
        var currentCardCommit = RevParse(stack.Repo, "HEAD");
        var job = Task("rewritten", stack.Storage, currentCardCommit, DateTime.UtcNow.AddMinutes(1));

        var evidence = stack.Service.BuildLookup([job])[job.TaskKey];

        Assert.NotNull(evidence.RunId);
        Assert.Equal("does-not-contain-diff", evidence.MatchQuality);
        Assert.Equal("not-proven", evidence.EvidenceState);
        Assert.False(evidence.DiffContained);
        Assert.Contains("before, diff not included", evidence.Summary);
    }

    [Fact]
    public void Deployment_DefaultsToLatestGreenRun_AndReportsDistanceToHead()
    {
        var stack = BuildStack();
        var testedCommit = RevParse(stack.Repo, "HEAD");
        var run = stack.Service.Create("Demo", Request(testedCommit, "completed", "passed"))!;
        Commit(stack.Repo, "untested.txt", "new", "Untested Head change");
        var head = RevParse(stack.Repo, "HEAD");

        var evidence = stack.Service.LastGreenForDeployment("Demo", head);

        Assert.NotNull(evidence);
        Assert.Equal(run.Id, evidence!.Id);
        Assert.Equal(testedCommit, evidence.Commit);
        Assert.Equal(1, evidence.DistanceToHead);
        Assert.Equal("head-ahead", evidence.HeadDirection);
    }

    private Stack BuildStack()
    {
        var workspace = Path.Combine(_root, Guid.NewGuid().ToString("N"), "workspace");
        var storage = Path.Combine(workspace, "projects", "demo", "tasks");
        var repo = Path.Combine(_root, Guid.NewGuid().ToString("N"), "repo");
        Directory.CreateDirectory(storage);
        Directory.CreateDirectory(repo);
        Git(repo, "init", "-b", "develop");
        Git(repo, "config", "user.email", "test@example.com");
        Git(repo, "config", "user.name", "Test User");
        Git(repo, "config", "commit.gpgsign", "false");
        Commit(repo, "initial.txt", "initial", "Initial");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = workspace,
            ["WatchPaths:0:Name"] = "Demo",
            ["WatchPaths:0:Path"] = storage,
            ["WatchPaths:0:RootPath"] = repo,
            ["WatchPaths:0:RepositoryPath"] = repo,
        }).Build();
        var registry = new ProjectRegistry(configuration, NullLogger<ProjectRegistry>.Instance);
        var project = registry.EnsureProjectForStorage(storage, "Demo", DefaultWorkspace.Id);
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, configuration);
        var scanner = new TaskScannerService(configuration, NullLogger<TaskScannerService>.Instance, summary);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, configuration);
        var store = new TestRunStore(configuration);
        return new Stack(configuration, storage, repo, project, new TestRunService(store, registry, scanner, git));
    }

    private static CreateTestRunRequest Request(string commit, string state, string? result) => new()
    {
        Trigger = "manual",
        Commit = commit,
        Branch = "develop",
        Scope = new TestRunScope { Level = "project", TestSet = "all" },
        State = state,
        Result = result,
        DurationSeconds = string.Equals(state, "completed", StringComparison.OrdinalIgnoreCase) ? 4 : null,
        Host = state == "planned" ? null : "runner-01",
    };

    private static TaskInfo Task(string id, string storage, string commit, DateTime? committedAt = null) => new()
    {
        Id = id,
        TaskKey = "PROJ-001::" + id,
        Key = "DEM-" + id.Length,
        Title = id,
        State = TaskStates.HumanReview,
        WatchPath = storage,
        ProjectName = "Demo",
        Commit = new TaskCommitInfo { Sha = commit, ShortSha = commit[..7], Message = id, At = committedAt ?? default },
        Commits = [new TaskCommitInfo { Sha = commit, ShortSha = commit[..7], Message = id, At = committedAt ?? default }],
    };

    private static void Commit(string repo, string file, string content, string message)
    {
        File.WriteAllText(Path.Combine(repo, file), content);
        Git(repo, "add", "--", file);
        Git(repo, "commit", "-m", message);
    }

    private static string RevParse(string repo, string value) => Git(repo, "rev-parse", value).Trim();

    private static string Git(string cwd, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(15_000), "git command timed out");
        Assert.Equal(0, process.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(error), error);
        return output;
    }

    private sealed record Stack(
        IConfiguration Configuration,
        string Storage,
        string Repo,
        ProjectRecord Project,
        TestRunService Service);
}
