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

    [Fact]
    public async Task ExcludedHostDoesNotPollAndAnUnreadableRepositoryLogsOnce()
    {
        var project = new PostMergeProjectTarget("PROJ-001", "one", Path.Combine(_root, "unavailable"), "develop");
        var host = new FakeHost([project]);
        using var http = new HttpClient(host) { BaseAddress = new Uri("http://fake/") };
        var logs = new List<string>();
        var clock = new Clock();
        var state = Path.Combine(_root, "state");
        PostMergeHostScheduler Scheduler(bool enabled) => new(http, state, () => 1, [], clock, () => new(0.1, 0),
            logs.Add, new RunnerOptions
            {
                ServerUrl = "http://fake/", RunnerId = "runner-id", RunnerName = "fixture",
                Hostname = "host", BackendName = "fixture", WorkDir = _root, BaseBranch = "main",
                StateDir = state, PostMergeEnabled = enabled,
            });
        await Scheduler(false).TickAsync(default);
        Assert.Equal(0, host.Requests);
        Assert.Empty(logs);

        var enabled = Scheduler(true);
        for (var poll = 0; poll < 7; poll++)
        {
            await enabled.TickAsync(default);
            clock.UtcNow += TimeSpan.FromMinutes(1);
        }
        Assert.Equal(7, host.Requests); // Discovery only. Neither a Git checkout nor a server claim was attempted.
        Assert.Contains("repository access unavailable", Assert.Single(logs), StringComparison.Ordinal);
        Assert.False(Directory.Exists(state));
    }

    [Theory]
    [InlineData(false, 0, 0.1, false, false)]
    [InlineData(true, 1, 0.1, false, false)]
    [InlineData(true, 0, 2.0, false, false)]
    [InlineData(true, 1, 0.1, true, true)]
    [InlineData(true, 0, 2.0, true, true)]
    [InlineData(true, 1, 0.1, true, false)]
    [InlineData(false, 0, 0.1, true, true)]
    public async Task SchedulerUsesOnlyGrantedProjectsWithCapacity(bool busy, int occupiedSlots, double load,
        bool gateAvailableAtClaim, bool gateAvailableAtPlacement)
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
        var host = new FakeHost(targets)
        {
            GateConnected = gateAvailableAtPlacement,
            DenyPrimaryClaim = busy && !gateAvailableAtClaim,
            PrimaryPlacementOnly = busy && gateAvailableAtClaim,
            HoldFirstRun = !busy && !gateAvailableAtPlacement,
        };
        using var peerHttp = new HttpClient(host, disposeHandler: false) { BaseAddress = new Uri("http://fake/") };
        peerHttp.DefaultRequestHeaders.Add("X-Client-Id", "peer");
        var peerRoot = Path.Combine(_root, "peer-state");
        var peer = new PostMergeHostScheduler(peerHttp, peerRoot, () => 1, [], new Clock(),
            () => new(0.1, 0), _ => { });
        using var http = new HttpClient(host) { BaseAddress = new Uri("http://fake/") };
        var stateRoot = Path.Combine(_root, "state");
        var logs = new List<string>();
        var options = new RunnerOptions
        {
            ServerUrl = "http://fake/", RunnerId = "first", RunnerName = "fixture",
            Hostname = "first-host", BackendName = "fixture", WorkDir = _root, BaseBranch = "main",
            StateDir = stateRoot, HostAdapter = true,
        };
        var scheduler = new PostMergeHostScheduler(http, stateRoot, () => 1, [], new Clock(),
            () => new(load, occupiedSlots), logs.Add, options);
        if (!host.DenyPrimaryClaim)
        {
            var tick = scheduler.TickAsync(default);
            try
            {
                if (host.HoldFirstRun)
                {
                    Assert.Same(host.FirstRunStarted.Task, await Task.WhenAny(tick, host.FirstRunStarted.Task));
                    await peer.TickAsync(default);
                    Assert.False(Directory.Exists(peerRoot));
                    Assert.Single(host.Runs);
                }
            }
            finally
            {
                host.ReleaseFirstRun.TrySetResult();
                await tick;
            }
        }
        else
        {
            await scheduler.TickAsync(CancellationToken.None);
            Assert.Empty(host.Runs);
            Assert.Empty(host.Observations);
            Assert.False(Directory.Exists(stateRoot));
            return;
        }
        Assert.DoesNotContain(logs, line => line.Contains("IOException", StringComparison.Ordinal)
            || line.Contains("JsonException", StringComparison.Ordinal));
        Assert.Equal(2, host.Observations.Count);
        Assert.All(targets, target => Assert.True(Directory.Exists(Path.Combine(stateRoot, "post-merge", target.ProjectId, ".git"))));
        if (gateAvailableAtClaim)
        {
            Assert.Empty(host.Runs);
            Assert.Equal(4, host.Placements); // A due window and nightly for each project.
            Assert.All(targets, target =>
                Assert.False(File.Exists(Path.Combine(stateRoot, "post-merge", target.ProjectId, "tested.sha"))));
            Assert.All(host.Observations.Values, observation => Assert.Equal(!gateAvailableAtPlacement, observation.Starved));
            return;
        }
        Assert.All(targets, target =>
        {
            Assert.Contains(host.Runs, run => run.Project == target.Project && run.Kind == "window" && run.Status == "green");
            Assert.Contains(host.Runs, run => run.Project == target.Project && run.Kind == "nightly" && run.Status == "green");
        });
        var before = host.Runs.Count;
        await scheduler.TickAsync(CancellationToken.None);
        Assert.Equal(before, host.Runs.Count);
        var restarted = new PostMergeHostScheduler(http, stateRoot, () => 1, [], new Clock(), () => new(0.1, 0), logs.Add);
        await restarted.TickAsync(CancellationToken.None);
        Assert.Equal(before, host.Runs.Count);
        var target = targets[0];
        var manual = new PostMergeRunOptions(target.Project, Path.Combine(stateRoot, "post-merge", target.ProjectId),
            "http://fake/", target.Branch, "origin", "unused", "nightly", 6, TimeSpan.Zero, 1, [], false);
        host.AllowManual = true;
        await new PostMergeRunCommand(manual, TextWriter.Null, new Clock(), http, () => new(0.1, 0))
            .ExecuteAsync(CancellationToken.None);
        host.AllowManual = false;
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
        await Git(relocated, "config", "user.name", "Fixture");
        await Git(relocated, "config", "user.email", "fixture@example.invalid");
        File.WriteAllText(Path.Combine(relocated, "takeover.txt"), "another integration");
        await Git(relocated, "add", ".");
        await Git(relocated, "commit", "--quiet", "-m", "takeover");
        HostRoleDrainControl.RequestDrain(stateRoot, "coding");
        var requests = host.Requests;
        await scheduler.TickAsync(default);
        Assert.Equal(requests, host.Requests);
        await peer.TickAsync(default);
        Assert.True(File.Exists(Path.Combine(peerRoot, "post-merge", target.ProjectId, "tested.sha")));
        Assert.DoesNotContain(logs, message => message.Contains("receipt retry", StringComparison.Ordinal));
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = Now;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class FakeHost(IReadOnlyList<PostMergeProjectTarget> targets) : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _grants = [];
        public int Requests { get; private set; }
        public bool HoldFirstRun { get; init; }
        public TaskCompletionSource FirstRunStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstRun { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool GateConnected { get; init; }
        public bool DenyPrimaryClaim { get; init; }
        public bool PrimaryPlacementOnly { get; init; }
        public int Placements { get; private set; }
        public bool AllowManual { get; set; }
        public List<PostMergeRunRecord> Runs { get; } = [];
        public Dictionary<string, PostMergeWindowObservation> Observations { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/runner/post-merge/projects")
                return Json(request.Headers.Contains("X-Client-Id") ? targets.Take(1).ToArray() : targets);
            var project = path.Split('/')[5];
            if (path.EndsWith("/claim", StringComparison.Ordinal))
            {
                var primary = !request.Headers.Contains("X-Client-Id");
                if (_grants.ContainsKey(project) || primary && DenyPrimaryClaim) return new(HttpStatusCode.NoContent);
                var id = Guid.NewGuid().ToString("N");
                _grants.Add(project, id);
                return Json(new PostMergeGrant(id, Now.AddMinutes(3)) { PlacementOnly = primary && PrimaryPlacementOnly });
            }
            var ownsGrant = request.Headers.TryGetValues(PostMergeClaimProtocol.GrantHeader, out var headers)
                && _grants.GetValueOrDefault(project) == headers.Single();
            if (!ownsGrant && !AllowManual) return new(HttpStatusCode.Conflict);
            if (path.EndsWith("/grant", StringComparison.Ordinal))
            {
                if (request.RequestUri.Query == "?release=true") _grants.Remove(project);
                return new(HttpStatusCode.NoContent);
            }
            if (path.EndsWith("/placement", StringComparison.Ordinal))
            {
                Placements++;
                return Json(new PostMergePlacement(GateConnected ? "gate" : "run"));
            }
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
                    WindowMinutes = GateConnected ? 60 : 0, WindowMaxMerges = 1 });
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
            environment: new Dictionary<string, string?> { ["GIT_AUTHOR_DATE"] = "2026-11-22T02:59:00Z",
                ["GIT_COMMITTER_DATE"] = "2026-11-22T02:59:00Z" }, ct: CancellationToken.None);
        Assert.True(result.Success, result.StdErr);
        return result.StdOut;
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
