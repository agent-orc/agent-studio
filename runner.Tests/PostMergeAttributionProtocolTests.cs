using System.Net;
using System.Net.Http.Json;
using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// RT-3 (AGT-3988, RT-D9 B): the attribution protocol on a real fixture
/// repository with four first-parent merges after the last green SHA. The suite
/// script stands in for the test host: it logs partial builds and decides each
/// focused run from the checked-out tree or a run counter.
/// </summary>
public sealed class PostMergeAttributionProtocolTests : IDisposable
{
    private const string Test = "Runner.Tests.FeatureTests.Works";
    private const string PartialBuild = "sh suite.sh dotnet build runner.Tests/Runner.Tests.csproj --no-restore";
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "post-merge-protocol-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RegressionInMergeTwoOfFourIsAttributedToMergeTwoWithTwoRunsPerPoint()
    {
        var fixture = await Fixture("if grep -q broken runner/Feature.cs; then fail; fi");
        var (exit, terminal, output) = await Run(fixture);

        Assert.True(exit == 1, output);
        var failure = terminal.Failure!;
        Assert.Equal(fixture.Merges[1], failure.AttributedMerge);
        Assert.Equal("AGT-2", failure.AttributedTaskId);
        Assert.Equal(fixture.Merges, failure.SuspectMerges);
        // File overlap decides coupling: merges three and four do not touch Feature.cs.
        Assert.False(failure.LaterMergeDependsOnSuspect);
        Assert.True(failure.RevertAppliesCleanly);
        Assert.Equal(["runner/Feature.cs"], failure.FailingPaths);
        var attribution = failure.Attribution!;
        Assert.Equal(PostMergeClassificationHints.Regression, attribution.Classification);
        Assert.Equal(fixture.Green, attribution.LastGreenSha);
        Assert.Equal(["pass", "pass"], attribution.ControlResults);
        Assert.Equal([(fixture.Merges[0], "pass"), (fixture.Merges[1], "fail")],
            attribution.Points.Select(point => (point.Sha, point.Verdict)));
        Assert.All(attribution.Points, point => Assert.Equal(2, point.Results.Count));
        Assert.Equal(PartialBuild, attribution.BuildCommand);

        var runs = terminal.FocusedRuns!;
        Assert.Equal(1, runs.Count(run => run.Phase == PostMergeRunPhases.Repeat));
        Assert.Equal(1, runs.Count(run => run.Phase == PostMergeRunPhases.ControlBuild));
        Assert.Equal(2, runs.Count(run => run.Phase == PostMergeRunPhases.Control));
        Assert.Equal(2, runs.Count(run => run.Phase == PostMergeRunPhases.BisectBuild));
        Assert.Equal(4, runs.Count(run => run.Phase == PostMergeRunPhases.Bisect));
        Assert.All(runs, run => Assert.True(run.DurationMs >= 0));
        Assert.Equal(2, runs.Count(run => run.Phase == PostMergeRunPhases.Bisect && run.Sha == fixture.Merges[1]));

        var verdict = Assert.Single(terminal.TestVerdicts!);
        Assert.Equal(Test, verdict.Test);
        Assert.Equal("runner-routine", verdict.SuiteId);
        Assert.Equal(ProjectTestSuiteCategories.Routine, verdict.SuiteCategory);
        Assert.Equal(PostMergeClassificationHints.Regression, verdict.ClassificationHint);
        Assert.Equal(["initial:fail", "repeat:fail", "control:pass", "control:pass",
                "bisect:fail", "bisect:fail", "bisect:pass", "bisect:pass"],
            verdict.Attempts.Select(attempt => attempt.Phase + ":" + attempt.Result));
        Assert.Null(verdict.Attempts[0].DurationMs);
        Assert.All(verdict.Attempts.Skip(1), attempt => Assert.NotNull(attempt.DurationMs));
        Assert.Equal("runner.Tests/FeatureTests.cs", verdict.TestFile);
        Assert.Equal(["runner/Services"], verdict.CoveringFolders);
        Assert.False(verdict.CoveringFallback);

        var envelope = terminal.Envelope!;
        Assert.Equal(16, envelope.AllowlistDigest.Length);
        Assert.Equal(16, envelope.DeclaredEnvironmentDigest.Length);
        Assert.Equal("runner-routine=1", envelope.ShardPlan);
        Assert.Equal(0.4, envelope.StartLoadPerCore);
        Assert.True(envelope.PeakLoadPerCore >= envelope.StartLoadPerCore);
        Assert.True(envelope.ProcessorCount > 0);
        Assert.Contains(envelope.CgroupKind, new[] { "worker", "daemon", "process", "none" });
        Assert.All(terminal.Verdicts, item => Assert.NotNull(item.DurationMs));
    }

    [Fact]
    public async Task BisectLogShowsOnlyTheTestProjectBuild()
    {
        var fixture = await Fixture("if grep -q broken runner/Feature.cs; then fail; fi");
        var (_, _, output) = await Run(fixture);
        var commands = output.Split('\n').Where(line => line.StartsWith("post-merge ", StringComparison.Ordinal))
            .Select(line => line["post-merge ".Length..].TrimEnd('\r')).ToArray();
        // Prepare and the three build commands run once, for the tip; never at a bisect point.
        Assert.Single(commands, command => command == ".agent-studio/prepare");
        Assert.Single(commands, command => command == "echo solution-build");
        Assert.Single(commands, command => command == "echo release-check");
        Assert.Single(commands, command => command == "echo frontend-build");
        Assert.Equal(3, commands.Count(command => command == PartialBuild));
        var firstPartial = Array.IndexOf(commands, PartialBuild);
        Assert.DoesNotContain(commands.Skip(firstPartial), command => command.StartsWith("echo ", StringComparison.Ordinal)
            || command == ".agent-studio/prepare");
        Assert.Equal(3, File.ReadAllLines(Path.Combine(fixture.Checkout, "build.log")).Length);
    }

    [Fact]
    public async Task FlakyResultAtAPointIsMixedAndAttributesNoMerge()
    {
        // Runs one and two fail at the tip, the control passes twice, and the first
        // bisect point fails and then passes.
        var fixture = await Fixture("case \"$n\" in 1|2|5) fail;; esac");
        var (exit, terminal, output) = await Run(fixture);

        Assert.True(exit == 1, output);
        var failure = terminal.Failure!;
        Assert.Null(failure.AttributedMerge);
        Assert.False(failure.RevertAppliesCleanly);
        var attribution = failure.Attribution!;
        Assert.Equal(PostMergeClassificationHints.Flaky, attribution.Classification);
        var point = Assert.Single(attribution.Points);
        Assert.Equal(fixture.Merges[1], point.Sha);
        Assert.Equal("mixed", point.Verdict);
        Assert.Equal(["fail", "pass"], point.Results);
        Assert.Contains("mixed", attribution.StopReason);
        Assert.Equal(2, terminal.FocusedRuns!.Count(run => run.Phase == PostMergeRunPhases.Bisect));
        Assert.Equal(PostMergeClassificationHints.Flaky, Assert.Single(terminal.TestVerdicts!).ClassificationHint);
    }

    [Fact]
    public async Task FailureAtTheLastGreenShaAttributesNoMerge()
    {
        var fixture = await Fixture("fail");
        var (exit, terminal, output) = await Run(fixture);

        Assert.True(exit == 1, output);
        var failure = terminal.Failure!;
        Assert.Null(failure.AttributedMerge);
        Assert.Null(failure.AttributedTaskId);
        Assert.False(failure.LaterMergeDependsOnSuspect);
        var attribution = failure.Attribution!;
        Assert.Equal(PostMergeClassificationHints.Environment, attribution.Classification);
        Assert.Equal(["fail", "fail"], attribution.ControlResults);
        Assert.Empty(attribution.Points);
        Assert.DoesNotContain(terminal.FocusedRuns!, run => run.Phase is PostMergeRunPhases.Bisect
            or PostMergeRunPhases.BisectBuild);
        Assert.Equal(PostMergeClassificationHints.Environment, Assert.Single(terminal.TestVerdicts!).ClassificationHint);
        // Without an attribution the hold falls back to every candidate's paths, as before.
        Assert.Equal(4, failure.FailingPaths.Count);
    }

    private sealed record FixtureRepository(string Checkout, string Green, IReadOnlyList<string> Merges);

    private async Task<FixtureRepository> Fixture(string scenario)
    {
        Directory.CreateDirectory(_root);
        var seed = Path.Combine(_root, "seed");
        var checkout = Path.Combine(_root, "checkout");
        await Git(_root, "init", "--quiet", "-b", "develop", seed);
        await Git(seed, "config", "user.name", "Test");
        await Git(seed, "config", "user.email", "test@example.invalid");
        const string suite = "sh suite.sh dotnet test runner.Tests/Runner.Tests.csproj --no-build";
        Write(seed, ".agent-studio/project.yml", $$"""
            schemaVersion: 1
            stack: [custom]
            toolVersions:
            commands:
              prepare: .agent-studio/prepare
              build:
                - "echo solution-build"
                - "echo release-check"
                - "echo frontend-build"
              test:
                - "{{suite}}"
              lint:
            testSuites:
              - id: runner-routine
                category: routine
                expectedDurationSeconds: 5
                command: "{{suite}}"
            cachePaths:
            capabilities: [git]
            """);
        Write(seed, ".agent-studio/prepare", "#!/bin/sh\ntrue\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(Path.Combine(seed, ".agent-studio", "prepare"),
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Write(seed, "suite.sh", "#!/bin/sh\n"
            + "case \"$*\" in *\"dotnet build\"*) echo \"$*\" >> build.log; exit 0;; esac\n"
            + "n=$(cat .runs 2>/dev/null || echo 0); n=$((n+1)); echo \"$n\" > .runs\n"
            + $"fail() {{ echo '  Failed {Test} [1 ms]'; exit 1; }}\n"
            + scenario + "\nexit 0\n");
        Write(seed, "runner/Runner.csproj", "<Project />");
        Write(seed, "runner.Tests/Runner.Tests.csproj",
            "<Project><ItemGroup><ProjectReference Include=\"..\\runner\\Runner.csproj\" /></ItemGroup></Project>");
        Write(seed, "runner/Feature.cs", "namespace Runner; public static class Feature { public const string State = \"ok\"; }\n");
        Write(seed, "runner/Services/FeatureService.cs", "namespace Runner.Services; public sealed class FeatureService { }\n");
        Write(seed, "runner.Tests/FeatureTests.cs",
            "namespace Runner.Tests; public sealed class FeatureTests { public void Works() => _ = new Runner.Services.FeatureService(); }\n");
        await Git(seed, "add", ".");
        await Git(seed, "commit", "--quiet", "-m", "base");
        var green = (await Git(seed, "rev-parse", "HEAD")).Trim();
        var merges = new List<string>();
        foreach (var (number, path, content) in new[]
                 {
                     (1, "runner/One.cs", "namespace Runner; public static class One { }\n"),
                     (2, "runner/Feature.cs", "namespace Runner; public static class Feature { public const string State = \"broken\"; }\n"),
                     (3, "runner/Three.cs", "namespace Runner; public static class Three { }\n"),
                     (4, "runner/Four.cs", "namespace Runner; public static class Four { }\n"),
                 })
        {
            Write(seed, path, content);
            await Git(seed, "add", ".");
            await Git(seed, "commit", "--quiet", "-m", $"Integrate AGT-{number}");
            merges.Add((await Git(seed, "rev-parse", "HEAD")).Trim());
        }
        await Git(_root, "clone", "--quiet", "--branch", "develop", seed, checkout);
        return new(checkout, green, merges);
    }

    private static async Task<(int Exit, PostMergeRunRecord Terminal, string Output)> Run(FixtureRepository fixture)
    {
        var receipts = new List<PostMergeRunRecord>();
        using var http = new HttpClient(new Handler(async request =>
        {
            if (request.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(PostMergeProjectState.Empty("demo") with
                        { LastGreenSha = fixture.Green, LastWindowTipSha = fixture.Green }),
                };
            if (request.RequestUri!.AbsolutePath.EndsWith("/runs", StringComparison.Ordinal))
                receipts.Add((await request.Content!.ReadFromJsonAsync<PostMergeRunRecord>())!);
            return new HttpResponseMessage(HttpStatusCode.OK);
        })) { BaseAddress = new Uri("http://test/") };
        var options = new PostMergeRunOptions("demo", fixture.Checkout, "http://test/", "develop", "origin", "unused",
            "window", 1, TimeSpan.Zero, 8, [], false);
        var output = new StringWriter();
        var exit = await new PostMergeRunCommand(options, output, new FixedClock(), http, () => new(0.4, 0))
            .ExecuteAsync(CancellationToken.None);
        Assert.Equal("running", receipts[0].Status);
        Assert.NotNull(receipts[0].Envelope);
        return (exit, receipts[^1], output.ToString());
    }

    private static void Write(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

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
