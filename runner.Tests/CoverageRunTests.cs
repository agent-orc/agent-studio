using System.Net;
using System.Text.Json;
using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TestSupport;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// AGT-3074: review gates run the .NET suites without a coverage collector
/// (test-acceleration Dossier, lever B11), so a scheduled run on the runner
/// host measures the <c>release</c>-category suites instead. These tests pin
/// which suites it selects, the order and priority it runs them in, when it
/// yields to the host, and what it hands to Quality Studio.
/// </summary>
public sealed class CoverageRunTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "coverage-run-tests", Guid.NewGuid().ToString("N"));

    public CoverageRunTests() => Directory.CreateDirectory(_root);

    public void Dispose() => ResilientDirectory.TryDelete(_root);

    [Fact]
    public void Repository_definition_selects_the_release_coverage_suites_in_declaration_order()
    {
        var definition = RepositoryDefinition();

        var suites = CoverageRunPlanner.SelectReleaseSuites(definition);

        Assert.Equal(
            ["backend-coverage", "runner-coverage", "task-server-coverage", "frontend-coverage"],
            suites.Select(suite => suite.Id));
        Assert.All(suites.Where(suite => suite.Id != "frontend-coverage"), suite =>
        {
            Assert.Contains("--collect \"Code Coverage;Format=Cobertura\"", suite.Command, StringComparison.Ordinal);
            Assert.Contains("--results-directory coverage/dotnet/", suite.Command, StringComparison.Ordinal);
        });
        Assert.Equal("npm --prefix frontend run test:release", suites.Single(suite => suite.Id == "frontend-coverage").Command);
    }

    [Fact]
    public void Review_gate_commands_stay_without_a_coverage_collector()
    {
        var definition = RepositoryDefinition();

        Assert.DoesNotContain(definition.Commands.Test, command => command.Contains("--collect", StringComparison.Ordinal));
        Assert.DoesNotContain(
            definition.TestSuites.Where(suite => suite.Category != CoverageRunPlanner.ReleaseCategory),
            suite => suite.Command.Contains("--collect", StringComparison.Ordinal) || suite.Command.Contains("test:release", StringComparison.Ordinal));
    }

    [Fact]
    public void Plan_runs_prepare_then_builds_then_release_suites_one_at_a_time_under_nice()
    {
        var definition = Definition(
            prepare: ".agent-studio/prepare",
            build: ["dotnet build app.sln", "npm run build"],
            ("unit", "routine", 60, "dotnet test unit"),
            ("unit-coverage", "release", 900, "dotnet test unit --collect cov"),
            ("e2e", "machine-bound", 60, "npm run e2e"),
            ("web-coverage", "release", 60, "npm run test:release"));

        var plan = CoverageRunPlanner.Build(definition);

        Assert.Equal(["prepare", "build-1", "build-2", "unit-coverage", "web-coverage"], plan.Steps.Select(step => step.Id));
        Assert.Equal(["prepare", "build", "build", "suite", "suite"], plan.Steps.Select(step => step.Kind));
        Assert.All(plan.Steps, step => Assert.Equal(["nice", "-n", "19", "sh", "-c", step.Command], step.Arguments));
        Assert.Equal(TimeSpan.FromSeconds(1800), plan.Steps[3].Timeout);
        Assert.Equal(CoverageRunPlanner.MinimumSuiteTimeout, plan.Steps[4].Timeout);
        Assert.Equal(CoverageRunPlanner.SetupTimeout, plan.Steps[0].Timeout);
        Assert.Equal("nice -n 19 sh -c 'dotnet test unit --collect cov'", plan.Steps[3].Display);
        Assert.Equal("1", plan.Environment["CI"]);
    }

    [Fact]
    public void Display_quotes_single_quotes_for_a_copyable_dry_run()
    {
        var step = new CoverageRunStep("s", CoverageRunPlanner.SuiteKind, "echo 'a b'", TimeSpan.FromMinutes(1));

        Assert.Equal("nice -n 19 sh -c 'echo '\\''a b'\\'''", step.Display);
    }

    public static TheoryData<double?, int, double, int, CoverageRunVerdict> HostMatrix => new()
    {
        { 0.1, 0, 0.5, 0, CoverageRunVerdict.Run },
        { 0.5, 0, 0.5, 0, CoverageRunVerdict.Run },
        { 0.51, 0, 0.5, 0, CoverageRunVerdict.SkipBusy },
        { 0.1, 1, 0.5, 0, CoverageRunVerdict.SkipBusy },
        { 0.1, 1, 0.5, 1, CoverageRunVerdict.Run },
        { 0.1, 2, 0.5, 1, CoverageRunVerdict.SkipBusy },
        { null, 0, 0.5, 0, CoverageRunVerdict.Run },
        { null, 1, 0.5, 0, CoverageRunVerdict.SkipBusy },
        { 2.2, 0, 3.0, 0, CoverageRunVerdict.Run },
    };

    [Theory]
    [MemberData(nameof(HostMatrix))]
    public void Host_admission_yields_to_claimed_slots_and_load(
        double? loadPerCore, int activeSlots, double maxLoad, int maxSlots, CoverageRunVerdict expected)
    {
        var decision = CoverageRunAdmission.ForHost(new(loadPerCore, activeSlots), maxLoad, maxSlots);

        Assert.Equal(expected, decision.Verdict);
        Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
    }

    [Theory]
    [InlineData("abc", null, false, CoverageRunVerdict.Run)]
    [InlineData("abc", "def", false, CoverageRunVerdict.Run)]
    [InlineData("abc", "abc", false, CoverageRunVerdict.SkipUnchanged)]
    [InlineData("ABC", "abc", false, CoverageRunVerdict.SkipUnchanged)]
    [InlineData("abc", "abc", true, CoverageRunVerdict.Run)]
    public void Commit_admission_measures_each_tip_once_unless_forced(
        string commit, string? last, bool force, CoverageRunVerdict expected)
        => Assert.Equal(expected, CoverageRunAdmission.ForCommit(commit, last, force).Verdict);

    [Fact]
    public void Options_read_the_environment_file_and_let_flags_win()
    {
        var options = CoverageRunOptions.Parse(
            ["--branch", "main", "--state-dir", "/s/one", "--state-dir", "/s/two", "--dry-run"],
            new Dictionary<string, string?>
            {
                ["COVERAGE_RUN_CHECKOUT"] = _root,
                ["COVERAGE_RUN_BRANCH"] = "develop",
                ["COVERAGE_RUN_STATE_DIRS"] = "/ignored",
                ["COVERAGE_RUN_MAX_LOAD_PER_CORE"] = "0.75",
                ["QUALITY_STUDIO_BASE_URL"] = "http://qs.local",
                ["QUALITY_STUDIO_REPO_ID"] = "repo",
            });

        Assert.Equal(_root, options.Checkout);
        Assert.Equal("main", options.Branch);
        Assert.Equal("origin", options.Remote);
        Assert.Equal(["/s/one", "/s/two"], options.StateDirectories);
        Assert.Equal(0.75, options.MaxLoadPerCore);
        Assert.Equal(0, options.MaxActiveSlots);
        Assert.Equal(CoverageRunOptions.DefaultHistoryPath, options.HistoryPath);
        Assert.True(options.DryRun);
        Assert.False(options.Force);
        Assert.True(options.QualityStudioConfigured);
    }

    [Fact]
    public void Options_default_state_directories_to_the_runner_state_dir()
    {
        var options = CoverageRunOptions.Parse([], new Dictionary<string, string?>
        {
            ["COVERAGE_RUN_CHECKOUT"] = _root,
            ["RUNNER_STATE_DIR"] = "/var/lib/agent-runner/state",
        });

        Assert.Equal(["/var/lib/agent-runner/state"], options.StateDirectories);
        Assert.Equal(CoverageRunOptions.DefaultBranch, options.Branch);
        Assert.False(options.QualityStudioConfigured);
    }

    [Theory]
    [InlineData("")]
    [InlineData("--checkout")]
    [InlineData("--unknown")]
    [InlineData("--checkout /a --checkout /b")]
    [InlineData("--checkout /a --max-load-per-core -1")]
    public void Options_reject_missing_checkout_and_malformed_flags(string args)
        => Assert.Throws<ArgumentException>(() => CoverageRunOptions.Parse(
            args.Split(' ', StringSplitOptions.RemoveEmptyEntries), new Dictionary<string, string?>()));

    [Fact]
    public void Host_probe_counts_coding_review_and_gate_claims()
    {
        var state = Path.Combine(_root, "state");
        Directory.CreateDirectory(Path.Combine(state, "reviews"));
        File.WriteAllText(Path.Combine(state, "AGT-1.slot.json"), "{}");
        File.WriteAllText(Path.Combine(state, "reviews", "attempt.review-slot.json"), "{}");
        File.WriteAllText(Path.Combine(state, "gate-claim.json"), "{}");
        File.WriteAllText(Path.Combine(state, "unrelated.json"), "{}");

        var load = CoverageRunCommand.ProbeHost([state, Path.Combine(_root, "missing")]);

        Assert.Equal(3, load.ActiveSlots);
    }

    [Theory]
    [InlineData("0-11", 12)]
    [InlineData("0", 1)]
    [InlineData("0-3,6,8-9\n", 7)]
    [InlineData("", null)]
    [InlineData("3-1", null)]
    [InlineData("a-b", null)]
    public void Online_cpu_list_counts_host_cores(string cpuList, int? expected)
        => Assert.Equal(expected, CoverageRunCommand.OnlineCpuCount(cpuList));

    [Fact]
    public async Task Dry_run_prints_the_commands_and_executes_nothing()
    {
        var repository = RepositoryRoot.Find();
        var history = Path.Combine(_root, "history.jsonl");
        var output = new StringWriter();
        var options = Options(repository, history, dryRun: true) with
        {
            QualityStudioBaseUrl = "http://127.0.0.1:5127",
            QualityStudioRepositoryId = "agent-studio-coverage",
        };

        var exit = await new CoverageRunCommand(options, output, new SteppingClock(), () => new(0.1, 0))
            .ExecuteAsync(CancellationToken.None);

        var text = output.ToString();
        Assert.Equal(0, exit);
        Assert.Contains("host: would run", text, StringComparison.Ordinal);
        Assert.Contains($"git -C {repository} fetch --quiet origin develop", text, StringComparison.Ordinal);
        Assert.Contains("[prepare] nice -n 19 sh -c '.agent-studio/prepare'", text, StringComparison.Ordinal);
        foreach (var suite in CoverageRunPlanner.SelectReleaseSuites(RepositoryDefinition()))
            Assert.Contains($"[{suite.Id}] nice -n 19 sh -c '{suite.Command}'", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[backend-routine]", text, StringComparison.Ordinal);
        Assert.Contains("POST http://127.0.0.1:5127/api/repos/agent-studio-coverage/sensors/coverage/scan", text, StringComparison.Ordinal);
        Assert.False(File.Exists(history));
    }

    [Fact]
    public void Timer_schedules_nightly_retries_and_the_unit_runs_the_command_at_low_priority()
    {
        var systemd = Path.Combine(RepositoryRoot.Find(), "deploy", "systemd");
        var timer = File.ReadAllLines(Path.Combine(systemd, "agent-coverage-run.timer"));
        var service = File.ReadAllLines(Path.Combine(systemd, "agent-coverage-run.service"));

        Assert.Contains("OnCalendar=*-*-* 01,03,05:15:00", timer);
        Assert.Contains("Persistent=true", timer);
        Assert.Contains("Unit=agent-coverage-run.service", timer);
        Assert.Contains("Type=oneshot", service);
        Assert.Contains("ExecStart=/opt/agent-host/current/agent-host coverage-run", service);
        Assert.Contains("Nice=19", service);
        Assert.Contains("IOSchedulingClass=idle", service);
        Assert.Contains("EnvironmentFile=/etc/agent-runner/coverage-run.env", service);
        Assert.DoesNotContain(service, line => line.StartsWith("EnvironmentFile=", StringComparison.Ordinal) && line.Contains("runner.env", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task Run_measures_the_fetched_tip_and_asks_Quality_Studio_to_ingest_once()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "The run uses nice, sh and setsid.");
        var (checkout, tip) = await CreateCheckoutAsync();
        // A report from an earlier commit must not survive into this measurement.
        Directory.CreateDirectory(Path.Combine(checkout, "coverage", "dotnet", "stale"));
        File.WriteAllText(Path.Combine(checkout, "coverage", "dotnet", "stale", "old.cobertura.xml"), "<coverage/>");
        var history = Path.Combine(_root, "history.jsonl");
        var qualityStudio = new RecordingQualityStudio();
        var options = Options(checkout, history) with
        {
            QualityStudioBaseUrl = "http://qs.test/root",
            QualityStudioRepositoryId = "agent studio",
            QualityStudioApiToken = "token-" + Guid.NewGuid().ToString("N"),
        };
        var output = new StringWriter();

        var exit = await new CoverageRunCommand(options, output, new SteppingClock(), () => new(0.1, 0), qualityStudio)
            .ExecuteAsync(CancellationToken.None);

        Assert.True(exit == 0, output.ToString());
        Assert.Equal(tip, (await Git(checkout, "rev-parse", "HEAD")).Trim());
        Assert.False(File.Exists(Path.Combine(checkout, "coverage", "dotnet", "stale", "old.cobertura.xml")));
        Assert.Equal(2, CoverageRunCommand.CountReports(checkout));
        Assert.Equal("suite-a,suite-b", File.ReadAllText(Path.Combine(checkout, "order.txt")).Trim());
        // Even a definition-declared value never reaches test code from the measured commit.
        Assert.Equal("unset", File.ReadAllText(Path.Combine(checkout, "token.txt")).Trim());
        var scan = Assert.Single(qualityStudio.Requests);
        Assert.Equal("http://qs.test/root/api/repos/agent%20studio/sensors/coverage/scan", scan.Uri);
        Assert.Equal($"Bearer {options.QualityStudioApiToken}", scan.Authorization);

        var record = ReadHistory(history).Single();
        Assert.Equal(CoverageRunRecord.Complete, record.Status);
        Assert.Equal(tip, record.Commit);
        Assert.Equal("ingested", record.Scan);
        Assert.Equal(["prepare", "build-1", "suite-a", "suite-b"], record.Steps.Select(step => step.Id));
        Assert.True(record.DurationSeconds > 0);
        Assert.Contains($"coverage-run status=complete commit={tip} branch=develop", output.ToString(), StringComparison.Ordinal);
        var summary = JsonSerializer.Deserialize<CoverageRunRecord>(
            File.ReadAllText(Path.Combine(checkout, "coverage", "coverage-run.json")), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(tip, summary!.Commit);

        var again = await new CoverageRunCommand(options, new StringWriter(), new SteppingClock(), () => new(0.1, 0), qualityStudio)
            .ExecuteAsync(CancellationToken.None);

        Assert.Equal(0, again);
        Assert.Single(qualityStudio.Requests);
        Assert.Equal("skipped-unchanged", ReadHistory(history).Last().Status);
    }

    [SkippableFact]
    public async Task Busy_host_skips_without_touching_the_checkout()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "The fixture uses a POSIX shell.");
        var (checkout, _) = await CreateCheckoutAsync();
        var before = (await Git(checkout, "rev-parse", "HEAD")).Trim();
        var history = Path.Combine(_root, "history.jsonl");
        var output = new StringWriter();

        var exit = await new CoverageRunCommand(Options(checkout, history), output, new SteppingClock(), () => new(0.2, 1))
            .ExecuteAsync(CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(before, (await Git(checkout, "rev-parse", "HEAD")).Trim());
        Assert.Equal("skipped-busy", ReadHistory(history).Single().Status);
        Assert.Contains("reason=\"1 active slot(s), limit 0\"", output.ToString(), StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Red_build_stops_before_the_suites_and_does_not_count_as_measured()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "The fixture uses a POSIX shell.");
        var (checkout, tip) = await CreateCheckoutAsync(build: "exit 3");
        var history = Path.Combine(_root, "history.jsonl");
        var qualityStudio = new RecordingQualityStudio();
        var options = Options(checkout, history) with { QualityStudioBaseUrl = "http://qs.test", QualityStudioRepositoryId = "r" };

        var exit = await new CoverageRunCommand(options, new StringWriter(), new SteppingClock(), () => new(0.1, 0), qualityStudio)
            .ExecuteAsync(CancellationToken.None);

        Assert.Equal(1, exit);
        var record = ReadHistory(history).Single();
        Assert.Equal("setup-failed", record.Status);
        Assert.Equal(["prepare", "build-1"], record.Steps.Select(step => step.Id));
        Assert.Equal(3, record.Steps[^1].ExitCode);
        Assert.Empty(qualityStudio.Requests);
        Assert.Null(new CoverageRunCommand(options, new StringWriter(), new SteppingClock()).LastMeasuredCommit());
        Assert.False(File.Exists(Path.Combine(checkout, "order.txt")), $"suites ran for {tip}");
    }

    private CoverageRunOptions Options(string checkout, string history, bool dryRun = false) => new(
        checkout, "develop", "origin", history, [], 0.5, 0, null, null, null, null, dryRun, Force: false);

    private static ProjectExecutionDefinition RepositoryDefinition()
    {
        var read = ProjectDefinitionReader.ReadWorkspace(RepositoryRoot.Find());
        Assert.True(read.IsValid, string.Join("; ", read.Issues.Select(issue => $"{issue.Path}: {issue.Message}")));
        return read.Definition!;
    }

    private static ProjectExecutionDefinition Definition(
        string prepare,
        IReadOnlyList<string> build,
        params (string Id, string Category, int Seconds, string Command)[] suites)
        => new(1, ["custom"], new Dictionary<string, string>(),
            new ProjectCommandSet(prepare, build, [], []),
            [.. suites.Select(suite => new ProjectTestSuite(suite.Id, suite.Category, suite.Seconds, suite.Command))],
            [], [], new Dictionary<string, string> { ["CI"] = "1" }, null, null, null, null, null);

    /// <summary>
    /// An origin whose develop tip carries a project.yml with shell steps that
    /// write a Cobertura and an LCOV report, and a clone of it as the
    /// coverage checkout, left one commit behind the tip.
    /// </summary>
    private async Task<(string Checkout, string Tip)> CreateCheckoutAsync(string build = "true")
    {
        var origin = Path.Combine(_root, "origin.git");
        var seed = Path.Combine(_root, "seed");
        var checkout = Path.Combine(_root, "checkout");
        await Git(_root, "init", "--quiet", "--bare", origin);
        await Git(_root, "init", "--quiet", "-b", "develop", seed);
        await Git(seed, "config", "user.email", "test@example.invalid");
        await Git(seed, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(seed, ".gitignore"), "/coverage/\n/order.txt\n/token.txt\n");
        await Git(seed, "add", ".");
        await Git(seed, "commit", "--quiet", "-m", "base");
        await Git(seed, "push", "--quiet", origin, "develop");
        await Git(_root, "clone", "--quiet", "--branch", "develop", origin, checkout);

        Directory.CreateDirectory(Path.Combine(seed, ".agent-studio"));
        File.WriteAllText(Path.Combine(seed, ".agent-studio", "project.yml"), $$"""
            schemaVersion: 1
            stack: [custom]
            toolVersions:
            commands:
              prepare: .agent-studio/prepare
              build:
                - {{build}}
              test:
                - true
              lint:
            testSuites:
              - id: routine
                category: routine
                expectedDurationSeconds: 5
                command: echo routine >> order.txt
              - id: suite-a
                category: release
                expectedDurationSeconds: 5
                command: mkdir -p coverage/dotnet/a/run && echo '<coverage/>' > coverage/dotnet/a/run/a.cobertura.xml && printf suite-a >> order.txt
              - id: suite-b
                category: release
                expectedDurationSeconds: 5
                command: mkdir -p coverage/frontend && echo 'end_of_record' > coverage/frontend/lcov.info && printf ,suite-b >> order.txt
            cachePaths:
            capabilities: [linux]
            environment:
              QUALITY_STUDIO_CLIENT_ID: leaked
            """);
        File.WriteAllText(Path.Combine(seed, ".agent-studio", "prepare"), "#!/bin/sh\necho ${QUALITY_STUDIO_CLIENT_ID:-unset} > token.txt\n");
        await Git(seed, "add", ".");
        await Git(seed, "update-index", "--chmod=+x", ".agent-studio/prepare");
        await Git(seed, "commit", "--quiet", "-m", "tip");
        await Git(seed, "push", "--quiet", origin, "develop");
        return (checkout, (await Git(seed, "rev-parse", "HEAD")).Trim());
    }

    private static async Task<string> Git(string workingDirectory, params string[] arguments)
    {
        var result = await ProcessRunner.RunAsync("git", arguments, workingDirectory, ct: CancellationToken.None);
        Assert.True(result.Success, $"git {string.Join(' ', arguments)} failed: {result.StdErr}");
        return result.StdOut;
    }

    private static List<CoverageRunRecord> ReadHistory(string path)
        => [.. File.ReadAllLines(path).Select(line =>
            JsonSerializer.Deserialize<CoverageRunRecord>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)];

    /// <summary>Advances one minute per reading so durations are positive and deterministic.</summary>
    private sealed class SteppingClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 12, 1, 15, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now = _now.AddMinutes(1);
    }

    private sealed class RecordingQualityStudio : HttpMessageHandler
    {
        public List<(string Uri, string? Authorization)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString()));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"available":true,"unavailableReason":null,"findings":[]}"""),
            });
        }
    }
}
