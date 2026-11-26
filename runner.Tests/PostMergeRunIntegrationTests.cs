using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

public sealed class PostMergeRunIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "post-merge-integration-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task NightlyRunsFetchedShaEvenWhenOriginAdvancesAfterClaim()
    {
        Directory.CreateDirectory(_root);
        var origin = Path.Combine(_root, "origin.git");
        var seed = Path.Combine(_root, "seed");
        var checkout = Path.Combine(_root, "checkout");
        await Git(_root, "init", "--quiet", "--bare", origin);
        await Git(_root, "init", "--quiet", "-b", "develop", seed);
        await Git(seed, "config", "user.name", "Test");
        await Git(seed, "config", "user.email", "test@example.invalid");
        Directory.CreateDirectory(Path.Combine(seed, ".agent-studio"));
        File.WriteAllText(Path.Combine(seed, ".agent-studio", "project.yml"), """
            schemaVersion: 1
            stack: [custom]
            toolVersions:
            commands:
              prepare: .agent-studio/prepare
              build:
                - "true"
              test:
                - "git rev-parse HEAD > tested.sha"
              lint:
            testSuites:
              - id: routine
                category: routine
                expectedDurationSeconds: 5
                command: "git rev-parse HEAD > tested.sha"
            cachePaths:
            capabilities: [git]
            """);
        var prepare = Path.Combine(seed, ".agent-studio", "prepare");
        File.WriteAllText(prepare, "#!/bin/sh\ntrue\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(prepare, UnixFileMode.UserRead | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute);
        await Git(seed, "add", ".");
        await Git(seed, "commit", "--quiet", "-m", "first");
        await Git(seed, "push", "--quiet", origin, "develop");
        var first = (await Git(seed, "rev-parse", "HEAD")).Trim();
        await Git(_root, "clone", "--quiet", "--branch", "develop", origin, checkout);

        var received = new List<PostMergeRunRecord>();
        var escalationChecks = 0;
        string? lastGreenForGet = null;
        using var http = new HttpClient(new Handler(async request =>
        {
            if (request.Method == HttpMethod.Get)
                return Json(PostMergeProjectState.Empty("PROJ-002") with { LastGreenSha = lastGreenForGet });
            if (request.RequestUri?.AbsolutePath.EndsWith("/escalation-check", StringComparison.Ordinal) == true)
            {
                escalationChecks++;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (request.RequestUri?.AbsolutePath.EndsWith("/window", StringComparison.Ordinal) == true)
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            var run = JsonSerializer.Deserialize<PostMergeRunRecord>(
                await request.Content!.ReadAsStringAsync(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            received.Add(run);
            if (run.Status == "running")
            {
                File.WriteAllText(Path.Combine(seed, "after.txt"), "after fetch\n");
                await Git(seed, "add", ".");
                await Git(seed, "commit", "--quiet", "-m", "second");
                await Git(seed, "push", "--quiet", origin, "develop");
            }
            return Json(PostMergeProjectState.Empty("PROJ-002"));
        })) { BaseAddress = new Uri("http://test/") };
        var options = new PostMergeRunOptions("Agent Studio", checkout, "http://test/",
            "develop", "origin", "unused", "nightly", 6, TimeSpan.Zero, 1, [], false);
        var output = new StringWriter();
        var exit = await new PostMergeRunCommand(options, output, new FixedClock(), http, () => new(0.1, 0))
            .ExecuteAsync(CancellationToken.None);

        Assert.True(exit == 0, output.ToString());
        Assert.Equal(["running", "green"], received.Select(run => run.Status));
        Assert.All(received, run => Assert.Equal(first, run.TipSha));
        Assert.Equal(first, File.ReadAllText(Path.Combine(checkout, "tested.sha")).Trim());
        var second = (await Git(seed, "rev-parse", "HEAD")).Trim();
        Assert.NotEqual(first, second);

        lastGreenForGet = second;
        Assert.Equal(0, await new PostMergeRunCommand(options with { Mode = "window" }, output, new FixedClock(), http,
            () => new(0.1, 0)).ExecuteAsync(CancellationToken.None));
        Assert.Equal("skipped", received[^1].Status);
        Assert.Equal(2, escalationChecks);
        Assert.Equal(second, received[^1].TipSha);
        Assert.Equal(first, File.ReadAllText(Path.Combine(checkout, "tested.sha")).Trim());
    }

    [Theory]
    [Trait("Category", "MachineBound")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DocsOnlyWindowRepeatsRunnerFailureAndNeverBlamesTheDocsMerge(bool recovers)
    {
        Directory.CreateDirectory(_root);
        var seed = Path.Combine(_root, "seed");
        var checkout = Path.Combine(_root, "checkout");
        await Git(_root, "init", "--quiet", "-b", "develop", seed);
        await Git(seed, "config", "user.name", "Test");
        await Git(seed, "config", "user.email", "test@example.invalid");
        Directory.CreateDirectory(Path.Combine(seed, ".agent-studio"));
        Directory.CreateDirectory(Path.Combine(seed, "runner.Tests"));
        File.WriteAllText(Path.Combine(seed, "runner.Tests", "Runner.Tests.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(seed, ".agent-studio", "project.yml"), """
            schemaVersion: 1
            stack: [custom]
            toolVersions:
            commands:
              prepare: .agent-studio/prepare
              build:
                - "true"
              test:
                - "sh suite.sh dotnet test runner.Tests/Runner.Tests.csproj --no-build"
              lint:
            testSuites:
              - id: runner-routine
                category: routine
                expectedDurationSeconds: 5
                command: "sh suite.sh dotnet test runner.Tests/Runner.Tests.csproj --no-build"
            cachePaths:
            capabilities: [git]
            """);
        var prepare = Path.Combine(seed, ".agent-studio", "prepare");
        File.WriteAllText(prepare, "#!/bin/sh\ntrue\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(prepare, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        // Exercise real command dispatch while keeping the fixture independent of an SDK invocation.
        File.WriteAllText(Path.Combine(seed, "suite.sh"),
            "#!/bin/sh\ncase \"$*\" in *\"dotnet build\"*) exit 0;; esac\necho attempt >> attempts\n"
            + (recovers ? "if [ -f repeated ]; then exit 0; fi\n" : "")
            + "touch repeated\necho '  Failed Runner.Tests.NetworkRemoval(listCommand: \"exit 1\") [1 ms]'\nexit 1\n");
        await Git(seed, "add", ".");
        await Git(seed, "commit", "--quiet", "-m", "base");
        var green = (await Git(seed, "rev-parse", "HEAD")).Trim();
        Directory.CreateDirectory(Path.Combine(seed, "docs"));
        File.WriteAllText(Path.Combine(seed, "docs", "guide.md"), "Documentation only");
        await Git(seed, "add", ".");
        await Git(seed, "commit", "--quiet", "-m", "Integrate docs-only");
        await Git(_root, "clone", "--quiet", "--branch", "develop", seed, checkout);
        var receipts = new List<PostMergeRunRecord>();
        using var http = new HttpClient(new Handler(async request =>
        {
            if (request.Method == HttpMethod.Get)
                return Json(PostMergeProjectState.Empty("PROJ-002") with
                    { LastGreenSha = green, LastWindowTipSha = green });
            if (request.RequestUri!.AbsolutePath.EndsWith("/runs", StringComparison.Ordinal))
                receipts.Add((await request.Content!.ReadFromJsonAsync<PostMergeRunRecord>())!);
            return new HttpResponseMessage(HttpStatusCode.OK);
        })) { BaseAddress = new Uri("http://test/") };
        var options = new PostMergeRunOptions("demo", checkout, "http://test/", "develop", "origin", "unused",
            "window", 1, TimeSpan.Zero, 8, [], false);
        var output = new StringWriter();
        var exit = await new PostMergeRunCommand(options, output, new FixedClock(), http, () => new(0.4, 6))
            .ExecuteAsync(CancellationToken.None);
        Assert.True(exit == (recovers ? 0 : 1), output.ToString());
        // RT-6 repeats a persistent failure once in the reference envelope before
        // RT-3's two last-green controls and their test-project-only build.
        Assert.Equal(recovers ? 2 : 5, File.ReadAllLines(Path.Combine(checkout, "attempts")).Length);
        var terminal = receipts[^1];
        Assert.Equal(recovers ? 0 : 1, terminal.FocusedRuns!.Count(run => run.Phase == PostMergeRunPhases.Reference));
        Assert.Equal(recovers ? "green" : "red", terminal.Status);
        Assert.Equal(recovers ? 1 : 0, terminal.FlakyCount);
        Assert.Equal(0.4, terminal.StartLoadPerCore);
        Assert.Equal(6, terminal.StartOccupiedSlots);
        if (!recovers)
        {
            Assert.NotNull(terminal.Failure);
            Assert.Empty(terminal.Failure.SuspectMerges);
            Assert.Null(terminal.Failure.AttributedMerge);
            Assert.True(terminal.Failure.NoRelevantMerges);
            Assert.Equal(PostMergeClassificationHints.Environment, terminal.Failure.Attribution!.Classification);
            Assert.Equal(["fail", "fail"], terminal.Failure.Attribution.ControlResults);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "MachineBound")]
    [Trait("Category", "ReviewFlaky")]
    public async Task Declared_quarantine_runs_and_reports_the_failure_without_blocking_post_merge(bool fromStore)
    {
        Directory.CreateDirectory(_root);
        var seed = Path.Combine(_root, "seed");
        var checkout = Path.Combine(_root, "checkout");
        await Git(_root, "init", "--quiet", "-b", "develop", seed);
        await Git(seed, "config", "user.name", "Test");
        await Git(seed, "config", "user.email", "test@example.invalid");
        Directory.CreateDirectory(Path.Combine(seed, ".agent-studio"));
        File.WriteAllText(Path.Combine(seed, ".agent-studio", "project.yml"), """
            schemaVersion: 1
            stack: [custom]
            toolVersions:
            commands:
              prepare: .agent-studio/prepare
              build:
                - "true"
              test:
                - "sh suite.sh"
              lint:
            testSuites:
              - id: routine
                category: routine
                expectedDurationSeconds: 5
                command: "sh suite.sh"
            cachePaths:
            capabilities: [git]
            """);
        var prepare = Path.Combine(seed, ".agent-studio", "prepare");
        File.WriteAllText(prepare, "#!/bin/sh\ntrue\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(prepare, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(Path.Combine(seed, "suite.sh"), "echo '  Failed Product.Tests.Isolated [1 ms]'\nexit 1\n");
        var definition = ProjectDefinitionReader.ReadWorkspace(seed);
        Assert.True(definition.IsValid, string.Join("; ", definition.Issues.Select(issue => issue.Message)));
        if (!fromStore)
            File.WriteAllText(Path.Combine(seed, TestQuarantineFile.RelativePath),
                """{"tests":[{"test":"Product.Tests.Isolated","card":"repair","expiresOn":"2026-10-28"}]}""");
        await Git(seed, "add", ".");
        await Git(seed, "commit", "--quiet", "-m", "base");
        await Git(_root, "clone", "--quiet", "--branch", "develop", seed, checkout);
        var receipts = new List<PostMergeRunRecord>();
        using var http = new HttpClient(new Handler(async request =>
        {
            if (request.Method == HttpMethod.Get)
                return Json(PostMergeProjectState.Empty("PROJ-002") with { Quarantines = fromStore
                    ? [new("quarantine-one", "Product.Tests.Isolated", "repair", DateOnly.MaxValue, null,
                        "confirmed twice", "held", "system:post-merge", Now, DateTimeOffset.MaxValue,
                        "fingerprint", [], ["backend/Features/Pipeline/PostMerge"])] : [] });
            if (request.RequestUri!.AbsolutePath.EndsWith("/runs", StringComparison.Ordinal))
                receipts.Add((await request.Content!.ReadFromJsonAsync<PostMergeRunRecord>())!);
            return new HttpResponseMessage(HttpStatusCode.OK);
        })) { BaseAddress = new Uri("http://test/") };
        var options = new PostMergeRunOptions("demo", checkout, "http://test/", "develop", "origin", "unused",
            "nightly", 1, TimeSpan.Zero, 8, [], false);
        var output = new StringWriter();
        var exit = await new PostMergeRunCommand(options, output, new FixedClock(), http, () => new(0, 0))
            .ExecuteAsync(CancellationToken.None);
        Assert.True(exit == 0, output.ToString());
        var run = receipts[^1];
        Assert.Equal("green", run.Status);
        var test = Assert.Single(run.TestVerdicts!);
        Assert.Equal("Product.Tests.Isolated", test.Test);
        Assert.True(test.Quarantined);
        Assert.Contains(run.Verdicts, verdict => verdict.OutputTail?.Contains("Product.Tests.Isolated", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(run.Verdicts, verdict => verdict.Retried);
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    {
        Content = System.Net.Http.Json.JsonContent.Create(value),
    };

    private static async Task<string> Git(string root, params string[] args)
    {
        var result = await ProcessRunner.RunAsync("git", args, root, ct: CancellationToken.None);
        Assert.True(result.Success, result.StdErr);
        return result.StdOut;
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
