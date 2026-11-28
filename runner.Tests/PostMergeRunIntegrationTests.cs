using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentRunner;
using AgentStudio.Pipeline;
using Microsoft.Extensions.Time.Testing;
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LintReceiptRecordsTapDiagnosticsAndOnlyConfirmsObservedRepeats(bool reproduced)
    {
        const string lint = "bash scripts/npm-frontend.sh run lint";
        const string tap = """
            TAP version 13
            # Subtest: rotates (false)
            not ok 1 - rotates (false)
              ---
              duration_ms: 1.2
              error: 'listen EADDRINUSE: address already in use'
              ...
            not ok 2 - pending # TODO later
            not ok 3 - renewal
              ---
              error: |-
                test timed out after 60000ms
                not ok 99 - quoted test output
              ...
            """;
        var initial = tap + "\n#" + new string('x', 3000);
        var receipt = await LintReceiptAsync(lint, initial, 1, reproduced ? initial : "lint setup failed");
        Assert.Equal("red", receipt.Status);
        var verdict = Assert.Single(receipt.Verdicts);
        Assert.True(verdict.Retried);
        Assert.Contains(verdict.SuiteId!, receipt.SuiteSelection!.SelectedSuites);
        Assert.Equal(lint, receipt.SuiteSelection.SuiteCommands[verdict.SuiteId!]);
        Assert.DoesNotContain("EADDRINUSE", verdict.OutputTail);
        if (!reproduced) Assert.Equal("lint setup failed", receipt.Failure!.LogExcerpt);
        Assert.Equal("listen EADDRINUSE: address already in use", verdict.FailureMessages!["rotates (false)"]);
        Assert.Equal("test timed out after 60000ms", verdict.FailureMessages["renewal"]);
        Assert.Equal(["rotates (false)", "renewal"], receipt.Failure!.FailingTests);
        Assert.Equal(receipt.Failure.FailingTests, receipt.TestVerdicts!.Select(test => test.Test));
        Assert.All(receipt.TestVerdicts!, test =>
        {
            Assert.Equal(["initial", "repeat"], test.Attempts.Select(attempt => attempt.Phase));
            Assert.Equal(["fail", reproduced ? "fail" : "unknown"], test.Attempts.Select(attempt => attempt.Result));
            Assert.All(test.Attempts, attempt => Assert.Equal(receipt.TipSha, attempt.Sha));
        });
    }

    [Fact]
    public async Task LintReceiptNamesTheRetryFailureAndKeepsEachMeasuredAttempt()
    {
        const string initial = """
            not ok 1 - recovered
              ---
              error: 'first attempt error'
              ...
            ok 2 - switched
            """;
        const string repeat = """
            ok 1 - recovered
            not ok 2 - switched
              ---
              error: 'second attempt error'
              ...
            not ok 3 - new failure
              ---
              error: 'only seen on repeat'
              ...
            """;
        var receipt = await LintReceiptAsync("lint", initial, 1, repeat);
        var verdict = Assert.Single(receipt.Verdicts);
        Assert.Equal(["switched", "new failure"], verdict.FailedTests);
        Assert.Equal(verdict.FailedTests, receipt.Failure!.FailingTests);
        Assert.Equal(["recovered"], verdict.FlakyTests);
        Assert.Equal("first attempt error", verdict.FailureMessages!["recovered"]);
        Assert.Equal("second attempt error", verdict.FailureMessages["switched"]);
        Assert.Equal("only seen on repeat", verdict.FailureMessages["new failure"]);
        Assert.Contains("only seen on repeat", receipt.Failure.LogExcerpt);
        var attempts = receipt.TestVerdicts!.ToDictionary(test => test.Test,
            test => test.Attempts.Select(attempt => attempt.Result).ToArray());
        Assert.Equal(["fail", "pass"], attempts["recovered"]);
        Assert.Equal(["pass", "fail"], attempts["switched"]);
        Assert.Equal(["unknown", "fail"], attempts["new failure"]);
    }

    [Fact]
    public async Task LintReceiptKeepsNewRetryFailuresWithinTheFocusedRunLimit()
    {
        var initial = string.Join("\n", Enumerable.Range(1, 64).Select(n => $"not ok {n} - initial {n}"));
        var repeat = string.Join("\n", Enumerable.Range(1, 64).Select(n => $"not ok {n} - retry {n}"));
        var receipt = await LintReceiptAsync("lint", initial, 1, repeat);
        Assert.Equal(64, Assert.Single(receipt.FocusedRuns!).Tests);
        Assert.Equal(Enumerable.Range(1, 64).Select(n => $"retry {n}"), receipt.Failure!.FailingTests);
        Assert.All(receipt.TestVerdicts!, test =>
            Assert.Equal(["unknown", "fail"], test.Attempts.Select(attempt => attempt.Result)));
    }

    [Theory]
    [InlineData("ok 1 - original # SKIP disabled")]
    [InlineData("not ok 1 - original # TODO later")]
    [InlineData("ok 1 - unrelated")]
    [InlineData("ok 1 - original")]
    public async Task SuccessfulLintRetryRequiresAnObservedPassToClearTheFailure(string repeat)
    {
        var receipt = await LintReceiptAsync("lint", "not ok 1 - original", 0, repeat);
        var passed = repeat == "ok 1 - original";
        var verdict = Assert.Single(receipt.Verdicts);
        Assert.Equal(passed ? "green" : "red", receipt.Status);
        Assert.Equal(passed ? [] : new[] { "original" }, verdict.FailedTests);
        Assert.Equal(passed ? new[] { "original" } : [], verdict.FlakyTests);
        var test = Assert.Single(receipt.TestVerdicts!);
        Assert.Equal(["fail", passed ? "pass" : "unknown"], test.Attempts.Select(attempt => attempt.Result));
    }

    [Theory]
    [InlineData("ok 1 - renewal # SKIP disabled", 0)]
    [InlineData("ok 1 - renewal # TODO later", 0)]
    [InlineData("not ok 1 - renewal", 1)]
    public async Task SameNamedSiblingCannotConfirmOrClearAnUnidentifiedFailure(string firstRetryPoint, int exit)
    {
        var receipt = await LintReceiptAsync("lint", "ok 1 - renewal\nnot ok 2 - renewal", exit,
            firstRetryPoint + "\nok 2 - renewal");
        Assert.Equal("red", receipt.Status);
        Assert.Empty(Assert.Single(receipt.Verdicts).FlakyTests!);
        var tests = receipt.TestVerdicts!;
        Assert.Equal(exit == 0 ? 1 : 2, tests.Count);
        Assert.All(tests, test =>
        {
            Assert.Contains("renewal", test.Test);
            Assert.Contains(test.Attempts, attempt => attempt.Result == "unknown");
            Assert.Contains(test.Attempts, attempt => attempt.Result == "fail");
        });
    }

    [Fact]
    public async Task GeneratedNamesakesCanFailAtTheSameSourceLocationOnDifferentAttempts()
    {
        const string diagnostic = "\n  ---\n  location: 'test/renew.mjs:4:1'\n  ...";
        var receipt = await LintReceiptAsync("lint", "not ok 1 - renewal" + diagnostic + "\nok 2 - renewal",
            1, "ok 1 - renewal\nnot ok 2 - renewal" + diagnostic);
        Assert.Equal(2, receipt.Failure!.FailingTests.Count);
        Assert.All(receipt.TestVerdicts!, test => Assert.Contains(test.Attempts, attempt => attempt.Result == "unknown"));
    }

    [Fact]
    public async Task SameNamedFailuresKeepTheirLocationAndErrorAcrossReorderedRetries()
    {
        static string Failure(int number, string file) => $"""
            not ok {number} - renewal
              ---
              location: 'test/{file}.mjs:1:4'
              error: '{file} error'
              ...
            """;
        var initial = string.Join("\n", Failure(1, "a"), Failure(2, "b"));
        var repeat = string.Join("\n", Failure(1, "c"), Failure(2, "a"));
        var receipt = await LintReceiptAsync("lint", initial, 1, repeat);
        var verdict = Assert.Single(receipt.Verdicts);
        Assert.Equal(3, receipt.Failure!.FailingTests.Count);
        foreach (var (file, first, retry) in new[] { ("a", "fail", "fail"), ("b", "fail", "unknown"), ("c", "unknown", "fail") })
        {
            var test = Assert.Single(receipt.TestVerdicts!, test => test.Test.Contains($"test/{file}.mjs:1:4"));
            Assert.Equal([first, retry], test.Attempts.Select(attempt => attempt.Result));
            Assert.Equal($"{file} error", verdict.FailureMessages![test.Test]);
        }
    }

    [Theory]
    [InlineData("ok 2 - renewal")]
    [InlineData("TAP version 13\n1..0\nTAP version 13\nok 1 - renewal")]
    public async Task APassInAnotherPositionCannotClearAPartialTapFailure(string repeat)
    {
        var receipt = await LintReceiptAsync("lint", "TAP version 13\nnot ok 1 - renewal", 0,
            repeat.StartsWith("TAP", StringComparison.Ordinal) ? repeat : "TAP version 13\n" + repeat);
        Assert.Equal("red", receipt.Status);
        var test = Assert.Single(receipt.TestVerdicts!);
        Assert.Equal(["fail", "unknown"], test.Attempts.Select(attempt => attempt.Result));
    }

    [Fact]
    public async Task PassingChildInAnotherSuiteCannotClearAMissingChild()
    {
        var receipt = await LintReceiptAsync("lint", "# Subtest: suite A\n    not ok 1 - renewal", 0,
            "# Subtest: suite B\n    ok 1 - renewal");
        Assert.Equal("red", receipt.Status);
        var test = Assert.Single(receipt.TestVerdicts!);
        Assert.Contains("suite A", test.Test);
        Assert.Equal(["fail", "unknown"], test.Attempts.Select(attempt => attempt.Result));
    }

    [Fact]
    public async Task NestedSummaryDoesNotConfirmDifferentFailingChildren()
    {
        static string Tap(string first, string second) => $"""
            TAP version 13
            # Subtest: outer
                # Subtest: inner
                    {first} 1 - first
                    {second} 2 - second
                    1..2
                not ok 1 - inner
                  ---
                  error: '1 subtest failed'
                  ...
                1..1
            not ok 1 - outer
            """;

        var receipt = await LintReceiptAsync("lint", Tap("not ok", "ok"), 1, Tap("ok", "not ok"));
        Assert.Equal("red", receipt.Status);
        Assert.Equal(["outer > inner > second"], receipt.Failure!.FailingTests);
        Assert.Equal(["outer > inner > first"], Assert.Single(receipt.Verdicts).FlakyTests);
        var attempts = receipt.TestVerdicts!.ToDictionary(test => test.Test,
            test => test.Attempts.Select(attempt => attempt.Result).ToArray());
        Assert.Equal(2, attempts.Count);
        Assert.Equal(["fail", "pass"], attempts["outer > inner > first"]);
        Assert.Equal(["pass", "fail"], attempts["outer > inner > second"]);
    }

    [Fact]
    public async Task SummaryOnlyRepeatLeavesMissingChildUnresolved()
    {
        const string summary = """
            not ok 1 - suite
              ---
              failureType: 'subtestsFailed'
              error: '1 subtest failed'
              ...
            """;
        var initial = "# Subtest: suite\n    not ok 1 - child\n    1..1\n" + summary;
        var receipt = await LintReceiptAsync("lint", initial, 1, summary);
        Assert.Equal(["suite > child"], receipt.Failure!.FailingTests);
        var test = Assert.Single(receipt.TestVerdicts!);
        Assert.Equal(["fail", "unknown"], test.Attempts.Select(attempt => attempt.Result));
    }

    [Theory]
    [InlineData("failureType: 'hookFailed'")]
    [InlineData("duration_ms: 1.2")]
    public async Task SuiteHookFailureKeepsItsOwnEvidenceAlongsideAFailingChild(string metadata)
    {
        var tap = $"""
            # Subtest: suite
                not ok 1 - child
                1..1
            not ok 1 - suite
              ---
              {metadata}
              error: 'after hook failed'
              ...
            """;
        var receipt = await LintReceiptAsync("lint", tap, 1, tap);
        Assert.Equal(["suite > child", "suite"], receipt.Failure!.FailingTests);
        Assert.Equal("after hook failed", Assert.Single(receipt.Verdicts).FailureMessages!["suite"]);
        Assert.All(receipt.TestVerdicts!, test =>
            Assert.Equal(["fail", "fail"], test.Attempts.Select(attempt => attempt.Result)));
    }

    private async Task<PostMergeRunRecord> LintReceiptAsync(string lint, string initial, int retryExit, string repeat)
    {
        var fixture = DurableFixture(lint);
        using var http = fixture.Http;
        var calls = new List<string>();
        Task<(int, bool, string)> Execute(string command, CancellationToken _)
        {
            calls.Add(command);
            return Task.FromResult(calls.Count == 1 ? (1, false, initial) : (retryExit, false, repeat));
        }
        await DurableWorker(http, calls, executor: Execute).ExecuteJournalAsync(fixture.Journal, default);
        Assert.Equal([lint, lint], calls);
        return Assert.Single(new PostMergeRunStore(fixture.StoreRoot).Read("PROJ-002").Runs);
    }

    [Fact]
    public async Task RestartAfterCommandReconstructsLedgerWithoutRepeatingFinishedWork()
    {
        var fixture = DurableFixture();
        using var http = fixture.Http;
        var executed = new List<string>();
        var first = DurableWorker(http, executed, afterCheckpoint: _ => throw new InjectedRestart());
        await Assert.ThrowsAsync<InjectedRestart>(() => first.ExecuteJournalAsync(fixture.Journal, CancellationToken.None));
        Assert.Equal(["build"], executed);
        var checkpoint = first.ReadJournal();
        Assert.Single(checkpoint.Commands);
        Assert.Single(checkpoint.Checkpoint!.Verdicts);
        Assert.Equal(0, await DurableWorker(http, executed).ExecuteJournalAsync(null, CancellationToken.None));
        Assert.Equal(["build", "suite"], executed);
        var state = new PostMergeRunStore(fixture.StoreRoot).Read("PROJ-002");
        Assert.Null(state.ActiveRun);
        var receipt = Assert.Single(state.Runs);
        Assert.Equal("green", receipt.Status);
        Assert.Equal(2, receipt.Verdicts.Count);
        Assert.Equal(checkpoint.Checkpoint.Verdicts[0].DurationMs, receipt.Verdicts[0].DurationMs);
    }

    [Fact]
    public async Task RestartAfterRecoveredSuiteRetainsPerTestAttemptsAndFocusedDurations()
    {
        var fixture = DurableFixture();
        using var http = fixture.Http;
        var executed = new List<string>();
        Task<(int, bool, string)> Execute(string command, CancellationToken _)
        {
            executed.Add(command);
            return Task.FromResult(command == "suite" && executed.Count == 2
                ? (1, false, "  Failed Product.Tests.One [1 ms]") : (0, false, ""));
        }
        var worker = DurableWorker(http, executed,
            afterCheckpoint: _ => executed.Count == 3 ? throw new InjectedRestart() : Task.CompletedTask,
            executor: Execute);
        await Assert.ThrowsAsync<InjectedRestart>(() => worker.ExecuteJournalAsync(fixture.Journal, CancellationToken.None));
        var saved = worker.ReadJournal().Checkpoint!;
        Assert.Single(saved.TestVerdicts!);
        Assert.Single(saved.FocusedRuns!);
        await DurableWorker(http, executed).ExecuteJournalAsync(null, CancellationToken.None);
        Assert.Equal(["build", "suite", "suite"], executed);
        var terminal = Assert.Single(new PostMergeRunStore(fixture.StoreRoot).Read("PROJ-002").Runs);
        Assert.Equal("green", terminal.Status);
        Assert.Equal(JsonSerializer.Serialize(saved.TestVerdicts), JsonSerializer.Serialize(terminal.TestVerdicts));
        Assert.Equal(JsonSerializer.Serialize(saved.FocusedRuns), JsonSerializer.Serialize(terminal.FocusedRuns));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparedReceiptSurvivesRestartAndApiOutageBeyondThreeRetries(bool outage)
    {
        var fixture = DurableFixture();
        using var http = fixture.Http;
        var executed = new List<string>();
        fixture.Transport.RejectTerminal = outage;
        var first = DurableWorker(http, executed, afterTerminal: outage ? null : _ => throw new InjectedRestart());
        if (outage)
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => first.ExecuteJournalAsync(fixture.Journal, CancellationToken.None));
            Assert.Equal(3, fixture.Transport.TerminalAttempts);
        }
        else await Assert.ThrowsAsync<InjectedRestart>(() => first.ExecuteJournalAsync(fixture.Journal, CancellationToken.None));
        var terminal = first.ReadJournal().Terminal!;
        Assert.NotNull(terminal);
        Assert.NotNull(new PostMergeRunStore(fixture.StoreRoot).Read("PROJ-002").ActiveRun);
        fixture.Transport.RejectTerminal = false;
        fixture.Transport.Now = Now.AddHours(1); // Well beyond the renewable lease, below the fallback window.
        Assert.Equal(0, await DurableWorker(http, executed).ExecuteJournalAsync(null, CancellationToken.None));
        Assert.Equal(["build", "suite"], executed);
        var state = new PostMergeRunStore(fixture.StoreRoot).Read("PROJ-002");
        Assert.Null(state.ActiveRun);
        Assert.Equal(JsonSerializer.Serialize(terminal), JsonSerializer.Serialize(Assert.Single(state.Runs)));
        Assert.False(File.Exists(Path.Combine(_root, "durable", ".post-merge-journal.json")));
    }

    [Fact]
    public async Task LostTerminalAcknowledgementReplaysIdempotentlyBeforeAnyNewWork()
    {
        var fixture = DurableFixture();
        using var http = fixture.Http;
        var executed = new List<string>();
        fixture.Transport.LoseAcknowledgement = true;
        var worker = DurableWorker(http, executed);
        await Assert.ThrowsAsync<HttpRequestException>(() => worker.ExecuteJournalAsync(fixture.Journal, CancellationToken.None));
        Assert.Null(new PostMergeRunStore(fixture.StoreRoot).Read("PROJ-002").ActiveRun);
        fixture.Transport.LoseAcknowledgement = false;
        await DurableWorker(http, executed).ExecuteJournalAsync(null, CancellationToken.None);
        Assert.Equal(["build", "suite"], executed);
        Assert.Single(new PostMergeRunStore(fixture.StoreRoot).Read("PROJ-002").Runs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidOrInFlightCheckpointSettlesInfrastructureAndRetainsFinishedEvidence(bool inFlight)
    {
        var fixture = DurableFixture();
        using var http = fixture.Http;
        var executed = new List<string>();
        var worker = DurableWorker(http, executed, afterCheckpoint: _ => throw new InjectedRestart());
        await Assert.ThrowsAsync<InjectedRestart>(() => worker.ExecuteJournalAsync(fixture.Journal, CancellationToken.None));
        if (inFlight)
        {
            var path = Path.Combine(_root, "durable", ".post-merge-journal.json");
            var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            document["inFlight"] = true;
            File.WriteAllText(path, document.ToJsonString());
        }
        else File.WriteAllText(Path.Combine(_root, "durable", "build-output"), "changed after checkpoint");
        Assert.Equal(1, await DurableWorker(http, executed).ExecuteJournalAsync(null, CancellationToken.None));
        Assert.Equal(["build"], executed);
        var state = new PostMergeRunStore(fixture.StoreRoot).Read("PROJ-002");
        Assert.Null(state.ActiveRun);
        Assert.Equal("inconclusive", state.DevelopState);
        var terminal = Assert.Single(state.Runs);
        Assert.Single(terminal.Verdicts);
        Assert.Equal(0, terminal.Verdicts[0].ExitCode);
        Assert.Null(state.LastGreenSha);
        Assert.Null(state.LastWindowTipSha);
    }

    [Fact]
    public async Task UndeliveredOutboxBlocksExecutionWhileApiIsStillUnavailable()
    {
        var fixture = DurableFixture();
        using var http = fixture.Http;
        var executed = new List<string>();
        fixture.Transport.RejectTerminal = true;
        var first = DurableWorker(http, executed);
        await Assert.ThrowsAsync<HttpRequestException>(() => first.ExecuteJournalAsync(fixture.Journal, CancellationToken.None));
        await Assert.ThrowsAsync<HttpRequestException>(() => DurableWorker(http, executed).ExecuteAsync(CancellationToken.None));
        Assert.Equal(["build", "suite"], executed);
        Assert.NotNull(first.ReadJournal().Terminal);
        Assert.NotNull(new PostMergeRunStore(fixture.StoreRoot).Read("PROJ-002").ActiveRun);
    }

    [Fact]
    public async Task RenewalFailureCancelsCommandAndRecoverySettlesItsInterruption()
    {
        var fixture = DurableFixture();
        using var http = fixture.Http;
        var executed = new List<string>();
        var clock = new FakeTimeProvider(Now);
        var begun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<(int, bool, string)> Command(string command, CancellationToken ct)
        {
            executed.Add(command);
            begun.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return (0, false, "");
        }
        fixture.Transport.RejectRenewal = true;
        var worker = DurableWorker(http, executed, executor: Command, clock: clock);
        var execution = worker.ExecuteJournalAsync(fixture.Journal, CancellationToken.None);
        await begun.Task;
        clock.Advance(TimeSpan.FromSeconds(30));
        await Assert.ThrowsAsync<HttpRequestException>(() => execution);
        Assert.True(worker.ReadJournal().InFlight);
        Assert.NotNull(new PostMergeRunStore(fixture.StoreRoot).Read("PROJ-002").ActiveRun);
        fixture.Transport.RejectRenewal = false;
        await DurableWorker(http, executed).ExecuteJournalAsync(null, CancellationToken.None);
        var settled = new PostMergeRunStore(fixture.StoreRoot).Read("PROJ-002");
        Assert.Null(settled.ActiveRun);
        Assert.Equal("inconclusive", Assert.Single(settled.Runs).Status);
        Assert.Equal(["build"], executed);
    }

    [Fact]
    public async Task LostRecoveryAcknowledgementAfterLeaseExpiryGetsFreshCustodyBeforeCommands()
    {
        var fixture = DurableFixture();
        using var http = fixture.Http;
        var executed = new List<string>();
        var recovery = Guid.NewGuid().ToString("N");
        fixture.Journal.RecoveryId = recovery;
        var store = new PostMergeRunStore(fixture.StoreRoot);
        store.RenewCustody("PROJ-002", fixture.Journal.Started.Id, fixture.Journal.Owner, 1, Now, true, recovery);
        fixture.Transport.Now = Now.AddMinutes(10);
        var worker = DurableWorker(http, executed, clock: new FakeTimeProvider(fixture.Transport.Now));
        fixture.Journal.WorkspaceDigest = worker.WorkspaceDigest();
        File.WriteAllText(Path.Combine(_root, "durable", ".post-merge-journal.json"),
            JsonSerializer.Serialize(fixture.Journal, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        await worker.ExecuteJournalAsync(null, CancellationToken.None);
        var custody = new PostMergeRunStore(fixture.StoreRoot).ReadCustody("PROJ-002")!;
        Assert.Equal(3, custody.Fence);
        Assert.True(custody.ExpiresAtUtc > fixture.Transport.Now);
        Assert.Equal(["build", "suite"], executed);
        Assert.Null(store.Read("PROJ-002").ActiveRun);
    }

    private PostMergeRunCommand DurableWorker(HttpClient http, List<string> executed,
        Func<CancellationToken, Task>? afterCheckpoint = null, Func<CancellationToken, Task>? afterTerminal = null,
        Func<string, CancellationToken, Task<(int ExitCode, bool TimedOut, string Output)>>? executor = null,
        TimeProvider? clock = null)
        => new(new("demo", Path.Combine(_root, "durable"), "http://test/", "develop", "origin", "unused",
            "window", 1, TimeSpan.Zero, 1, [], false), TextWriter.Null, clock ?? new FixedClock(), http, () => new(0, 0))
        {
            CommandExecutor = executor ?? ((command, _) => { executed.Add(command); return Task.FromResult((0, false, "")); }),
            AfterCheckpoint = afterCheckpoint, AfterTerminalPrepared = afterTerminal,
            ReceiptDelay = (_, _) => Task.CompletedTask,
        };

    private (HttpClient Http, DurableTransport Transport, PostMergeHostJournal Journal, string StoreRoot) DurableFixture(string? lint = null)
    {
        var checkout = Path.Combine(_root, "durable");
        Directory.CreateDirectory(checkout);
        var definition = new ProjectExecutionDefinition(1, [], new Dictionary<string, string>(),
            new("", ["build"], ["suite"], lint is null ? [] : [lint]), [new("routine", "routine", 1, "suite")], [], [],
            new Dictionary<string, string>(), null, null, null, null, null);
        var run = new PostMergeRunRecord("durable-run", "demo", "window", new string('a', 40), null,
            Now, Now, 1, lint is null ? ["build", "suite"] : [lint], [], "running", Now, null)
            { SuiteSelection = new(["routine"], [], []) };
        var owner = Guid.NewGuid().ToString("N");
        var storeRoot = Path.Combine(_root, "store");
        var store = new PostMergeRunStore(storeRoot);
        store.Record("PROJ-002", run, owner, 0, Now);
        var journal = new PostMergeHostJournal(run, PostMergeProjectState.Empty("demo"), definition, 1, owner) { Fence = 1 };
        var transport = new DurableTransport(storeRoot);
        return (new HttpClient(transport) { BaseAddress = new Uri("http://test/") }, transport, journal, storeRoot);
    }

    private sealed class InjectedRestart : Exception;

    // Reconstruct the authority for every request, as a new service graph would after restart.
    private sealed class DurableTransport(string storeRoot) : HttpMessageHandler
    {
        public bool RejectTerminal { get; set; }
        public bool RejectRenewal { get; set; }
        public bool LoseAcknowledgement { get; set; }
        public int TerminalAttempts { get; private set; }
        public DateTimeOffset Now { get; set; } = PostMergeRunIntegrationTests.Now;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var store = new PostMergeRunStore(storeRoot);
            var run = (await request.Content!.ReadFromJsonAsync<PostMergeRunRecord>(ct))!;
            if (request.Headers.TryGetValues("X-Post-Merge-Operation", out var operations))
            {
                var recover = operations.Single() == "recover";
                if (RejectRenewal && !recover) return new(HttpStatusCode.ServiceUnavailable);
                var result = store.RenewCustody("PROJ-002", run.Id,
                    request.Headers.GetValues("X-Post-Merge-Owner").Single(),
                    long.Parse(request.Headers.GetValues("X-Post-Merge-Fence").Single()), Now, recover,
                    request.Headers.TryGetValues("X-Post-Merge-Recovery-Id", out var ids) ? ids.Single() : null);
                return result is null ? new(HttpStatusCode.Conflict) : Json(result);
            }
            TerminalAttempts++;
            if (RejectTerminal) return new(HttpStatusCode.ServiceUnavailable);
            var state = store.Record("PROJ-002", run, request.Headers.GetValues("X-Post-Merge-Owner").Single(),
                long.Parse(request.Headers.GetValues("X-Post-Merge-Fence").Single()), Now);
            if (LoseAcknowledgement) throw new HttpRequestException("receipt accepted; acknowledgement lost");
            return state is null ? new(HttpStatusCode.Conflict) : Json(state);
        }
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
