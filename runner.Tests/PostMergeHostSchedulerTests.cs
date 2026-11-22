using System.Net;
using System.Net.Http.Json;
using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

public sealed class PostMergeHostSchedulerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "post-merge-host-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SchedulerReusesTheRunnerCredentialAndRegisteredClientIdentity()
    {
        var token = Guid.NewGuid().ToString("N");
        var options = new RunnerOptions
        {
            ServerUrl = "https://task-server.invalid/", PostMergeStateUrl = "https://studio.invalid/",
            RunnerId = "runner-id", RunnerName = "fixture", Hostname = "host", BackendName = "fixture",
            WorkDir = _root, BaseBranch = "main", AuthToken = token, ClientId = "configured-id",
        };
        using var http = PostMergeHostScheduler.CreateClient(options, "registered-client");
        Assert.Equal("https://studio.invalid/", http.BaseAddress!.ToString());
        Assert.Equal(token, http.DefaultRequestHeaders.Authorization!.Parameter);
        Assert.Equal("Bearer", http.DefaultRequestHeaders.Authorization.Scheme);
        Assert.Equal("registered-client", Assert.Single(http.DefaultRequestHeaders.GetValues("X-Client-Id")));
        Assert.Equal("runner-id", Assert.Single(http.DefaultRequestHeaders.GetValues("X-Principal-Consumer-Id")));
    }

    [Fact]
    public void MigratedHostUsesItsOwnDrainAndIgnoresTheRetiredLegacyDrain()
    {
        RunnerOptions Options(bool adapter) => new()
        {
            ServerUrl = "https://studio.invalid/", RunnerId = "runner-id", RunnerName = "fixture",
            Hostname = "host", BackendName = "fixture", WorkDir = _root, BaseBranch = "main",
            StateDir = Path.Combine(_root, "state"), HostAdapter = adapter,
        };
        var host = Options(true);
        var legacy = Options(false);
        CodingDrainGuard.RequestDrain(host.StateDir);
        Assert.True(PostMergeHostScheduler.IsDrained(legacy));
        Assert.False(PostMergeHostScheduler.IsDrained(host));
        HostRoleDrainControl.RequestDrain(host.StateDir, "coding");
        Assert.True(PostMergeHostScheduler.IsDrained(host));
        CodingDrainGuard.Clear(host.StateDir);
        Assert.False(PostMergeHostScheduler.IsDrained(legacy));
    }

    [Theory]
    [InlineData(false, 0, 0.1)]
    [InlineData(true, 1, 0.1)]
    [InlineData(true, 0, 2.0)]
    public async Task HostDiscoversTwoProjectsCreatesClonesAndReportsBusyWindows(bool busy, int occupiedSlots, double load)
    {
        Directory.CreateDirectory(_root);
        var targets = new List<PostMergeProjectTarget>();
        foreach (var (id, branch) in new[] { ("PROJ-001", "develop"), ("PROJ-002", "integration") })
        {
            var repository = Path.Combine(_root, id);
            await Git(_root, "init", "--quiet", "-b", branch, repository);
            await Git(repository, "config", "user.name", "Fixture");
            await Git(repository, "config", "user.email", "fixture@example.invalid");
            Directory.CreateDirectory(Path.Combine(repository, ".agent-studio"));
            File.WriteAllText(Path.Combine(repository, ".agent-studio", "project.yml"), """
                schemaVersion: 1
                stack: [custom]
                toolVersions:
                commands:
                  prepare: .agent-studio/prepare
                  build:
                    - "git rev-parse HEAD > tested.sha"
                  test:
                    - "git status --porcelain --untracked-files=no"
                  lint:
                testSuites:
                  - id: routine
                    category: routine
                    expectedDurationSeconds: 5
                    command: "git status --porcelain --untracked-files=no"
                cachePaths:
                capabilities: [git]
                """);
            var prepare = Path.Combine(repository, ".agent-studio", "prepare");
            File.WriteAllText(prepare, "#!/bin/sh\nexit 0\n");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(prepare, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await Git(repository, "add", ".");
            await Git(repository, "commit", "--quiet", "-m", "initial");
            // A later first-parent integration commit is the untested merge window.
            File.WriteAllText(Path.Combine(repository, "merged.txt"), "merged");
            await Git(repository, "add", ".");
            await Git(repository, "commit", "--quiet", "-m", "integrated");
            targets.Add(new(id, id, repository, branch));
        }
        var host = new FakeHost(targets) { HoldFirstRun = !busy };
        using var http = new HttpClient(host) { BaseAddress = new Uri("http://fake/") };
        var stateRoot = Path.Combine(_root, "state");
        var logs = new List<string>();
        var scheduler = new PostMergeHostScheduler(http, stateRoot, 1, [], new Clock(),
            () => new(load, occupiedSlots), logs.Add);
        var tick = scheduler.TickAsync(CancellationToken.None);
        if (!busy)
        {
            try
            {
                await Task.WhenAny(tick, host.FirstRunStarted.Task);
                Assert.True(host.FirstRunStarted.Task.IsCompleted, string.Join("\n", logs));
                var activeTarget = targets[0];
                var activeCheckout = Path.Combine(stateRoot, "post-merge", activeTarget.ProjectId);
                var pinned = (await Git(activeCheckout, "rev-parse", "FETCH_HEAD")).Trim();
                File.WriteAllText(Path.Combine(activeTarget.RepositoryUrl, "during-run.txt"), "develop moved during tests");
                await Git(activeTarget.RepositoryUrl, "add", ".");
                await Git(activeTarget.RepositoryUrl, "commit", "--quiet", "-m", "merge during active suite");
                await scheduler.ObserveOnceAsync(CancellationToken.None);
                Assert.True(host.Observations[targets[0].Project].Starved);
                Assert.NotEqual(pinned, host.Observations[targets[0].Project].TipSha);
                Assert.Equal(pinned, (await Git(activeCheckout, "rev-parse", "FETCH_HEAD")).Trim());
                Assert.True(host.Observations[targets[1].Project].Starved);
                await Git(activeTarget.RepositoryUrl, "reset", "--quiet", "--hard", pinned);
            }
            finally { host.ReleaseFirstRun.TrySetResult(); await tick; }
        }
        await tick;
        Assert.DoesNotContain(logs, line => line.Contains("IOException", StringComparison.Ordinal)
            || line.Contains("JsonException", StringComparison.Ordinal));
        Assert.Equal(2, host.Observations.Count);
        foreach (var target in targets)
        {
            Assert.True(Directory.Exists(Path.Combine(stateRoot, "post-merge", target.ProjectId, ".git")));
            if (busy)
            {
                Assert.True(host.Observations[target.Project].Starved);
                Assert.Contains("occupied slots", host.Observations[target.Project].AdmissionReason);
                Assert.Empty(host.Runs);
            }
            else
            {
                Assert.Contains(host.Runs, run => run.Project == target.Project && run.Kind == "window" && run.Status == "green");
                Assert.Contains(host.Runs, run => run.Project == target.Project && run.Kind == "nightly" && run.Status == "green");
            }
        }
        var before = host.Runs.Count;
        await scheduler.TickAsync(CancellationToken.None);
        Assert.Equal(before, host.Runs.Count); // unchanged windows and completed nightlies produce no new run
        // Host restart reconstructs nightly completion from authority, without a local schedule file.
        var restarted = new PostMergeHostScheduler(http, stateRoot, 1, [], new Clock(), () => new(0.1, 1), logs.Add);
        await restarted.TickAsync(CancellationToken.None);
        Assert.Equal(before, host.Runs.Count);
        if (!busy)
        {
            var target = targets[0];
            var manual = new PostMergeRunOptions(target.Project, Path.Combine(stateRoot, "post-merge", target.ProjectId),
                "http://fake/", target.Branch, "origin", "unused", "nightly", 6, TimeSpan.Zero, 1, [], false);
            await new PostMergeRunCommand(manual, TextWriter.Null, new Clock(), http, () => new(0.1, 0))
                .ExecuteAsync(CancellationToken.None);
            Assert.Equal("skipped", host.Runs[^1].Status);
            Assert.Equal("nightly", host.Runs[^1].Kind);
            var checkout = manual.Checkout;
            var previouslyTested = File.ReadAllText(Path.Combine(checkout, "tested.sha"));
            await Git(target.RepositoryUrl, "checkout", "--quiet", "-b", "registered-branch");
            File.WriteAllText(Path.Combine(target.RepositoryUrl, "new-merge.txt"), "next merge");
            await Git(target.RepositoryUrl, "add", ".");
            await Git(target.RepositoryUrl, "commit", "--quiet", "-m", "next integration");
            var relocated = Path.Combine(_root, "relocated-origin");
            await Git(_root, "clone", "--quiet", "--branch", "registered-branch", target.RepositoryUrl, relocated);
            targets[0] = target with { RepositoryUrl = relocated, Branch = "registered-branch" };
            File.WriteAllText(Path.Combine(checkout, "merged.txt"), "tracked output left by the previous suite");
            await scheduler.TickAsync(CancellationToken.None);
            Assert.NotEqual(previouslyTested, File.ReadAllText(Path.Combine(checkout, "tested.sha")));
            Assert.Equal(relocated, (await Git(checkout, "remote", "get-url", "origin")).Trim());
            Assert.Equal("merged", File.ReadAllText(Path.Combine(checkout, "merged.txt")));
            Assert.Equal("green", host.Runs[^1].Status);
            Assert.Equal("window", host.Runs[^1].Kind);
        }
    }

    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }

    private sealed class FakeHost(IReadOnlyList<PostMergeProjectTarget> targets) : HttpMessageHandler
    {
        public bool HoldFirstRun { get; init; }
        public TaskCompletionSource FirstRunStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstRun { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<PostMergeRunRecord> Runs { get; } = [];
        public Dictionary<string, PostMergeWindowObservation> Observations { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/runner/post-merge/projects") return Json(targets);
            var project = path.Split('/')[5];
            if (path.EndsWith("/escalation-check", StringComparison.Ordinal)) return new(HttpStatusCode.NoContent);
            if (path.EndsWith("/window", StringComparison.Ordinal))
            {
                Observations[project] = (await request.Content!.ReadFromJsonAsync<PostMergeWindowObservation>(ct))!;
                return new(HttpStatusCode.NoContent);
            }
            if (request.Method == HttpMethod.Get)
            {
                var green = Runs.LastOrDefault(run => run.Project == project && run.Status == "green");
                var last = Runs.LastOrDefault(run => run.Project == project);
                return Json(PostMergeProjectState.Empty(project) with { LastGreenSha = green?.TipSha,
                    ActiveRun = last?.Status == "running" ? last : null,
                    LastWindowTipSha = green?.TipSha, Runs = Runs.Where(run => run.Project == project).ToArray(),
                    WindowMinutes = 0 });
            }
            Runs.Add((await request.Content!.ReadFromJsonAsync<PostMergeRunRecord>(ct))!);
            if (HoldFirstRun && Runs.Count == 1)
            {
                FirstRunStarted.TrySetResult();
                await ReleaseFirstRun.Task.WaitAsync(ct);
            }
            return Json(PostMergeProjectState.Empty(project));
        }
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }

    private static async Task<string> Git(string directory, params string[] args)
    {
        var result = await ProcessRunner.RunAsync("git", args, directory,
            environment: new Dictionary<string, string?> { ["GIT_AUTHOR_DATE"] = "2026-11-21T00:00:00Z",
                ["GIT_COMMITTER_DATE"] = "2026-11-21T00:00:00Z" }, ct: CancellationToken.None);
        Assert.True(result.Success, result.StdErr);
        return result.StdOut;
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
