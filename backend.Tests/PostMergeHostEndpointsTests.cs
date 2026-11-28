using System.Net.Http.Json;
using AgentStudio.Pipeline;
using AgentStudio.Clients;
using AgentStudio.Projects;
using AgentStudio.Registry;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class PostMergeHostEndpointsTests : IDisposable
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 22, 3, 0, 0, TimeSpan.Zero));
    private readonly string _root = Path.Combine(Path.GetTempPath(), "post-merge-endpoints-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task DiscoveryUsesRegisteredBranchesExcludesStrictAndExposesStarvedWindow()
    {
        using var factory = new TestHostFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root, ["Security:Profile"] = "local", ["AllowedHosts"] = "*",
            }));
            builder.ConfigureServices(services =>
            {
                services.Replace(ServiceDescriptor.Singleton(new PostMergeRunStore(Path.Combine(_root, "post-merge"))));
                services.Replace(ServiceDescriptor.Singleton<TimeProvider>(_clock));
            });
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);
        var registry = factory.Services.GetRequiredService<ProjectRegistry>();
        var settings = factory.Services.GetRequiredService<ProjectSettingsService>();
        var registered = new List<ProjectRecord>();
        // AGT-3965: fast and balanced integrate first and need T3 runs; strict does not.
        foreach (var (name, branch, strategy) in new[] { ("one", "develop", TestingStrategies.Fast),
                     ("two", "trunk", TestingStrategies.Balanced), ("strict", "main", TestingStrategies.Strict) })
        {
            var path = Path.Combine(_root, name);
            Directory.CreateDirectory(path);
            var project = registry.EnsureProjectForStorage(path, name, DefaultWorkspace.Id, _clock);
            registry.Update(project.Id, new UpdateProjectRequest { RepositoryUrl = $"https://example.invalid/{name}.git" },
                factory.Services.GetRequiredService<WorkspaceRegistry>());
            settings.SetIntegrationBranch(name, branch);
            settings.SetTestingStrategy(name, strategy);
            registered.Add(project);
        }
        var targets = await client.GetFromJsonAsync<PostMergeProjectTarget[]>("/api/runner/post-merge/projects");
        Assert.Equal(["one", "two"], targets!.Select(target => target.Project).Order().ToArray());
        Assert.Equal("trunk", targets.Single(target => target.Project == "two").Branch);
        Assert.Equal("https://example.invalid/one.git", targets.Single(target => target.Project == "one").RepositoryUrl);
        var now = _clock.GetUtcNow();
        var observation = new PostMergeWindowObservation(new string('a', 40), now.AddMinutes(-31), now, 30, "1 occupied slots");
        using var response = await client.PostAsJsonAsync("/api/runner/post-merge/projects/one/window", observation);
        response.EnsureSuccessStatusCode();
        var store = factory.Services.GetRequiredService<PostMergeRunStore>();
        var one = registered.Single(project => project.DisplayName == "one");
        foreach (var (id, at, names) in new[]
                 { ("expired", now.AddDays(-15), new[] { "Runner.Old" }),
                   ("first", now.AddDays(-2), new[] { "Runner.Frequent", "Runner.Rare" }),
                   ("second", now.AddDays(-1), new[] { "Runner.Frequent" }) })
        {
            var run = new PostMergeRunRecord(id, "one", "nightly", observation.TipSha, null,
                null, null, 0, ["dotnet test"], [], "running", at, null,
                StartLoadPerCore: 0.4, StartOccupiedSlots: 6);
            Assert.NotNull(store.Record(one.Id, run));
            Assert.NotNull(store.Record(one.Id, run with { Status = "green", FinishedAtUtc = at,
                Verdicts = [new("dotnet test", 0, false, FlakyTests: names, Retried: true)] }));
        }
        // Admission clears the pending observation, so restore it before checking its projection.
        store.ObserveWindow(one.Id, observation);
        var receiptState = await client.GetFromJsonAsync<PostMergeProjectState>("/api/runner/post-merge/projects/one");
        Assert.Equal(1, receiptState!.Runs[^1].FlakyCount);
        Assert.Equal(6, receiptState.Runs[^1].StartOccupiedSlots);
        Assert.Equal(0.4, receiptState.Runs[^1].StartLoadPerCore);
        var states = await client.GetFromJsonAsync<DevelopStateSummary[]>("/api/develop-states");
        var pending = states!.Single(state => state.Project == "one").PendingWindow;
        var summary = states.Single(state => state.Project == "one");
        Assert.Equal(1, summary.FlakyCount);
        Assert.Equal(["Runner.Frequent", "Runner.Rare"], summary.MostFlakyTests14Days!.Select(test => test.Test));
        Assert.Equal([2, 1], summary.MostFlakyTests14Days.Select(test => test.Runs));
        Assert.NotNull(pending);
        Assert.True(pending.Starved);
        Assert.Equal(observation.TipSha, pending.TipSha);
        Assert.Null(states.Single(state => state.Project == "strict").PendingWindow);
    }

    [Fact]
    public async Task Rt3ReceiptFieldsRoundTripAndOlderReceiptsStayValid()
    {
        // RT-3 (AGT-3988): per-test verdicts, focused-run durations, attribution
        // evidence and the envelope fingerprint pass the run route unchanged.
        using var factory = new TestHostFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root, ["Security:Profile"] = "local", ["AllowedHosts"] = "*",
            }));
            builder.ConfigureServices(services =>
            {
                services.Replace(ServiceDescriptor.Singleton(new PostMergeRunStore(Path.Combine(_root, "post-merge"))));
                services.Replace(ServiceDescriptor.Singleton<TimeProvider>(_clock));
            });
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);
        var registry = factory.Services.GetRequiredService<ProjectRegistry>();
        var path = Path.Combine(_root, "one");
        Directory.CreateDirectory(path);
        registry.EnsureProjectForStorage(path, "one", DefaultWorkspace.Id, _clock);
        factory.Services.GetRequiredService<ProjectSettingsService>().SetTestingStrategy("one", TestingStrategies.Fast);
        var now = _clock.GetUtcNow();
        var tip = new string('a', 40);
        var green = new string('c', 40);
        var point = new string('d', 40);
        var envelope = new PostMergeEnvelopeFingerprint("0123456789abcdef", "fedcba9876543210", "worker",
            "300000 100000", 3, "10.0.100", "runner-routine=1", 0.4, 1.25);
        var running = new PostMergeRunRecord("rt3", "one", "window", tip, green, now, now, 2,
            ["dotnet test runner.Tests/AgentRunner.Tests.csproj --no-build"], [], "running", now, null,
            StartLoadPerCore: 0.4, StartOccupiedSlots: 0) { Envelope = envelope with { PeakLoadPerCore = null } };
        (await client.PostAsJsonAsync("/api/runner/post-merge/projects/one/runs", running)).EnsureSuccessStatusCode();
        var test = new PostMergeTestVerdict("Runner.Tests.FeatureTests.Works", "runner-routine", "routine",
            [new(PostMergeRunPhases.Initial, PostMergeTestResults.Fail, null, tip),
             new(PostMergeRunPhases.Repeat, PostMergeTestResults.Fail, 812, tip),
             new(PostMergeRunPhases.Control, PostMergeTestResults.Pass, 790, green),
             new(PostMergeRunPhases.Bisect, PostMergeTestResults.Fail, 801, point)],
            PostMergeClassificationHints.Regression)
        {
            TestFile = "runner.Tests/FeatureTests.cs", CoveringFolders = ["runner/Services"], CoveringFallback = false,
        };
        var failure = new PostMergeFailureEvidence("fingerprint", [test.Test], "Failed Runner.Tests.FeatureTests.Works",
            ["runner/Feature.cs"], [point], point, true, false)
        {
            Attribution = new(green, ["pass", "pass"], [new(point, ["fail", "fail"], "fail") { ParentResults = ["pass", "pass"] }],
                PostMergeClassificationHints.Regression) { BuildCommand = "dotnet build runner.Tests/AgentRunner.Tests.csproj --no-restore" },
        };
        var diagnosticSecret = "sk-" + "proj-" + new string('x', 80);
        var terminal = running with
        {
            Status = "red", FinishedAtUtc = now.AddMinutes(9), Failure = failure, Envelope = envelope,
            Verdicts = [new(running.Commands[0], 1, false, "Failed Runner.Tests.FeatureTests.Works",
                [test.Test], [], true) { DurationMs = 61_000,
                    FailureMessages = new Dictionary<string, string> { [test.Test] = "Expected healthy. " + diagnosticSecret } }],
            TestVerdicts = [test],
            FocusedRuns = [new(PostMergeRunPhases.Repeat, tip, 1, 1, false, 812),
                new(PostMergeRunPhases.ControlBuild, green, 0, 0, false, 14_000),
                new(PostMergeRunPhases.Control, green, 1, 0, false, 790)],
        };
        var invalid = terminal with { TestVerdicts = [test with { ClassificationHint = "guess" }] };
        using (var rejected = await client.PostAsJsonAsync("/api/runner/post-merge/projects/one/runs", invalid))
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, rejected.StatusCode);
        using (var rejected = await client.PostAsJsonAsync("/api/runner/post-merge/projects/one/runs",
                   terminal with { FocusedRuns = [new(PostMergeRunPhases.Bisect, "not-a-sha", 1, 0, false, -1)] }))
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, rejected.StatusCode);
        using (var rejected = await client.PostAsJsonAsync("/api/runner/post-merge/projects/one/runs",
                   terminal with { Verdicts = [terminal.Verdicts[0] with
                       { FailureMessages = new Dictionary<string, string> { [test.Test] = new string('x', 2001) } }] }))
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, rejected.StatusCode);
        (await client.PostAsJsonAsync("/api/runner/post-merge/projects/one/runs", terminal)).EnsureSuccessStatusCode();

        var state = await client.GetFromJsonAsync<PostMergeProjectState>("/api/runner/post-merge/projects/one");
        var stored = state!.Runs.Single(run => run.Id == "rt3");
        Assert.Equal(envelope, stored.Envelope);
        Assert.Equal(61_000, stored.Verdicts[0].DurationMs);
        Assert.StartsWith("Expected healthy.", stored.Verdicts[0].FailureMessages![test.Test]);
        Assert.DoesNotContain(diagnosticSecret, stored.Verdicts[0].FailureMessages![test.Test]);
        var verdict = Assert.Single(stored.TestVerdicts!);
        Assert.Equal(test.Attempts, verdict.Attempts);
        Assert.Equal(["pass", "pass"], Assert.Single(stored.Failure!.Attribution!.Points).ParentResults);
        Assert.Equal(["runner/Services"], verdict.CoveringFolders);
        Assert.False(verdict.CoveringFallback);
        Assert.Equal("runner.Tests/FeatureTests.cs", verdict.TestFile);
        Assert.Equal(PostMergeClassificationHints.Regression, verdict.ClassificationHint);
        Assert.Equal(terminal.FocusedRuns, stored.FocusedRuns);
        var attribution = stored.Failure!.Attribution!;
        Assert.Equal(["pass", "pass"], attribution.ControlResults);
        Assert.Equal("fail", Assert.Single(attribution.Points).Verdict);
        Assert.Equal(failure.Attribution.BuildCommand, attribution.BuildCommand);

        // A receipt without any RT-3 field, as an older runner posts it, is still accepted.
        var old = new string('b', 40);
        const string started = "2026-11-22T03:30:00+00:00";
        foreach (var json in new[]
                 {
                     $$"""{"id":"old","project":"one","kind":"window","tipSha":"{{old}}","mergeCount":1,"commands":["dotnet test"],"verdicts":[],"status":"running","startedAtUtc":"{{started}}"}""",
                     $$"""{"id":"old","project":"one","kind":"window","tipSha":"{{old}}","mergeCount":1,"commands":["dotnet test"],"verdicts":[{"command":"dotnet test","exitCode":0,"timedOut":false}],"status":"green","startedAtUtc":"{{started}}","finishedAtUtc":"2026-11-22T03:40:00+00:00"}""",
                 })
        {
            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            using var response = await client.PostAsync("/api/runner/post-merge/projects/one/runs", content);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        }
        var after = await client.GetFromJsonAsync<PostMergeProjectState>("/api/runner/post-merge/projects/one");
        var legacy = after!.Runs.Single(run => run.Id == "old");
        Assert.Equal("green", legacy.Status);
        Assert.Null(legacy.TestVerdicts);
        Assert.Null(legacy.Envelope);
        Assert.Null(legacy.Verdicts[0].DurationMs);
    }

    [Fact]
    public async Task TerminalCoverageConflictsLeaveTheClaimAvailableForAnExactLegacyReceipt()
    {
        using var factory = new TestHostFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            { ["TaskRepository"] = _root, ["Security:Profile"] = "local", ["AllowedHosts"] = "*" }));
            builder.ConfigureServices(services =>
            {
                TestHostFactory.RemoveBackgroundHostedServices(services);
                services.Replace(ServiceDescriptor.Singleton(new PostMergeRunStore(Path.Combine(_root, "post-merge"))));
                services.Replace(ServiceDescriptor.Singleton<TimeProvider>(_clock));
            });
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", DefaultClientIdentity.Id);
        var path = Path.Combine(_root, "one");
        Directory.CreateDirectory(path);
        var project = factory.Services.GetRequiredService<ProjectRegistry>()
            .EnsureProjectForStorage(path, "one", DefaultWorkspace.Id, _clock);
        var store = factory.Services.GetRequiredService<PostMergeRunStore>();
        const string route = "/api/runner/post-merge/projects/one/runs";
        var running = new PostMergeRunRecord("coverage", "one", "window", new string('a', 40), null,
            null, null, 1, ["build", "test"], [], "running", _clock.GetUtcNow(), null);
        using (var started = await client.PostAsJsonAsync(route, running)) started.EnsureSuccessStatusCode();
        var terminal = running with { Status = "green", FinishedAtUtc = _clock.GetUtcNow(),
            Verdicts = [new("build", 0, false), new("test", 0, false)] };
        foreach (var bad in new[]
                 {
                     terminal with { Commands = ["build"], Verdicts = [terminal.Verdicts[0]] },
                     terminal with { Verdicts = [terminal.Verdicts[0], terminal.Verdicts[0]] },
                     terminal with { Verdicts = [terminal.Verdicts[0], new("other", 0, false)] },
                     terminal with { Verdicts = [terminal.Verdicts[0]] },
                 })
        {
            using var response = await client.PostAsJsonAsync(route, bad);
            Assert.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);
            Assert.Null(store.Read(project.Id).LastGreenSha);
            Assert.Equal(running.Id, store.Read(project.Id).ActiveRun!.Id);
        }
        foreach (var bad in new[]
                 {
                     running with { Commands = ["build", "build"] },
                     running with { Commands = [" "] },
                     terminal with { Verdicts = [new(" ", 0, false)] },
                     running with { Commands = Enumerable.Range(0, 65).Select(index => "command-" + index).ToArray() },
                 })
        {
            using var response = await client.PostAsJsonAsync(route, bad);
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        }
        var malformed = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(terminal))!;
        malformed["Verdicts"] = new System.Text.Json.Nodes.JsonArray((System.Text.Json.Nodes.JsonNode?)null);
        using (var content = new StringContent(malformed.ToJsonString(), System.Text.Encoding.UTF8, "application/json"))
        using (var response = await client.PostAsync(route, content))
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        using (var accepted = await client.PostAsJsonAsync(route, terminal)) accepted.EnsureSuccessStatusCode();
        using (var replay = await client.PostAsJsonAsync(route, terminal)) replay.EnsureSuccessStatusCode();
        Assert.Equal(terminal.TipSha, store.Read(project.Id).LastGreenSha);
        Assert.Single(store.Read(project.Id).Runs);
        using var conflict = await client.PostAsJsonAsync(route, terminal with { Reason = "changed receipt" });
        Assert.Equal(System.Net.HttpStatusCode.Conflict, conflict.StatusCode);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
