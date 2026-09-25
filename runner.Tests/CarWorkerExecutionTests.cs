using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentRunner;
using AgentStudio.TestSupport;
using AgentStudio.TaskServer.Contracts;
using CodingAgentRunner.Abstractions;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// T1 (AGT-2370) — the CAR execution engine inside the worker, driven against
/// the recorded parity fixtures (<c>testdata/cli-fixtures/streams</c>) through
/// the injected <see cref="ICliProcessSpawner"/> seam. The spawner swaps only the
/// launch (node + fake-cli.mjs replaying the fixture); everything else is the
/// production path: descriptor argv, permission injection, clean-context env,
/// stdin prompt transport, bounded tails, typed event trace, timeout.
///
/// <para>The core claim is <b>byte parity</b>: for every recorded fixture, the
/// stdout/stderr/exit-code triple the CAR path hands to classification equals
/// the recording — and therefore every verdict the level-1 suite
/// (<see cref="ParityFixtureTests"/>) pins holds on the CAR path too. The named
/// pins (P1, P5 incl. the plaintext no-sentinel protection) are asserted
/// explicitly on top.</para>
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class CarWorkerExecutionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "car-worker-tests", Guid.NewGuid().ToString("N"));

    public static TheoryData<string> AllStreamFixtures()
    {
        var data = new TheoryData<string>();
        foreach (var file in CliCaptureFixtureLocator.AllRelativePaths(RepoRoot()))
            data.Add(file);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllStreamFixtures))]
    public async Task Every_fixture_reaches_classification_byte_identical_through_the_car_engine(string fixtureName)
    {
        if (NodeMissing()) return;
        var fixture = Fixture.Load(fixtureName);
        var run = await RunFixtureAsync(fixture);

        Assert.False(run.TimedOut);
        Assert.False(run.LaunchFailed);
        Assert.Equal(fixture.ExitCode, run.Result.ExitCode);
        Assert.Equal(Normalize(fixture.StdOut), Normalize(run.Result.StdOut));
        Assert.Equal(Normalize(fixture.StdErr), Normalize(run.Result.StdErr));

        // Byte parity implies verdict parity; assert it end to end anyway so a
        // future change to the classification stack cannot decouple the two.
        var recorded = SentinelScanner.Scan(fixture.StdOut);
        var driven = SentinelScanner.Scan(run.Result.StdOut);
        Assert.Equal(recorded.Kind, driven.Kind);
        Assert.Equal(recorded.TargetState, driven.TargetState);
        Assert.Equal(
            Classify(fixture.StdOut, fixture.StdErr, fixture.ExitCode).Outcome,
            Classify(run.Result.StdOut, run.Result.StdErr, run.Result.ExitCode).Outcome);

        var novelty = run.Shipped
            .Where(line => line.Stream == "system"
                           && line.Text.StartsWith(
                               ProtocolNoveltyTelemetry.MarkerPrefix,
                               StringComparison.Ordinal))
            .ToList();
        if (string.Equals(fixture.Scenario, "P23", StringComparison.Ordinal))
            Assert.Single(novelty);
        else
            Assert.Empty(novelty);
    }

    [Fact]
    public async Task P1_done_still_reads_done_and_P5_plaintext_still_refuses_auto_review_on_the_car_engine()
    {
        if (NodeMissing()) return;

        var done = await RunFixtureAsync(Fixture.Load("p1-happy-done.claude.fixture"));
        Assert.Equal(RunOutcomeKind.Done, SentinelScanner.Scan(done.Result.StdOut).Kind);
        Assert.Equal("4-auto-review", SentinelScanner.Scan(done.Result.StdOut).TargetState);

        // The P5 pin this migration must not silently move: a substantial reply
        // without a terminal sentinel in PLAINTEXT stays inconclusive and goes to
        // human review - the form change to stream-json must not promote it.
        var plain = await RunFixtureAsync(Fixture.Load("p5-no-sentinel.plaintext.fixture"));
        Assert.Equal(RunOutcomeKind.Unknown, SentinelScanner.Scan(plain.Result.StdOut).Kind);
        Assert.Equal("5-human-review", SentinelScanner.Scan(plain.Result.StdOut).TargetState);
        var decision = Classify(plain.Result.StdOut, plain.Result.StdErr, plain.Result.ExitCode);
        Assert.Equal(ExecutionOutcomeKind.ProtocolInconclusive, decision.Outcome);
        Assert.Equal(ExecutionRecoveryAction.AskForHumanInput, decision.RecoveryAction);
    }

    [Fact]
    public async Task Permission_and_clean_context_jumps_are_injected_and_the_prompt_travels_on_stdin()
    {
        if (NodeMissing()) return;
        var fixture = Fixture.Load("p1-happy-done.claude.fixture");
        const string prompt = "Refactor the parser and end with the sentinel.";
        var run = await RunFixtureAsync(fixture, prompt: prompt);

        // T1 jump 1 - permission injection: no permissionMode on the spec means
        // YOLO, where the legacy remote spawn injected nothing at all.
        Assert.NotNull(run.SpawnedArgv);
        Assert.Contains("--dangerously-skip-permissions", run.SpawnedArgv!);
        Assert.Contains("--output-format", run.SpawnedArgv!);
        Assert.Contains("stream-json", run.SpawnedArgv!);

        // T1 jump 2 - clean context: the CLI gets a task-stable config home
        // from the same store used by local execution. CAR receives it as a
        // shared home and must not compose a second process-temp directory.
        Assert.True(run.SpawnedEnvironment!.TryGetValue("CLAUDE_CONFIG_DIR", out var configDir));
        Assert.False(string.IsNullOrWhiteSpace(configDir));
        Assert.StartsWith(
            Path.Combine(_root, "clean-context"),
            configDir!,
            StringComparison.Ordinal);

        // CAR-A keeps the remote prompt transport: stdin, never the argv.
        Assert.DoesNotContain(prompt, run.SpawnedArgv!);
        Assert.NotNull(run.Capture);
        Assert.Equal(prompt.Length, run.Capture!.RootElement.GetProperty("stdinChars").GetInt32());

        // The results contract every remote agent relies on.
        Assert.Equal(
            run.ResultsDirectory,
            run.SpawnedEnvironment!["JOB_RESULTS_DIR"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Clean_context_admits_the_process_environment_claude_token_only_when_provisioned(
        bool tokenProvisioned)
    {
        if (NodeMissing()) return;
        const string dummyToken = "dummy-claude-setup-token-for-unit-test";
        using var environment = new EnvironmentVariableScope(
            ProviderAuthEnvironment.ClaudeCodeOAuthToken,
            tokenProvisioned ? dummyToken : null);

        var run = await RunFixtureAsync(Fixture.Load("p1-happy-done.claude.fixture"));

        Assert.NotNull(run.SpawnedEnvironment);
        if (tokenProvisioned)
        {
            Assert.True(
                string.Equals(
                    dummyToken,
                    run.SpawnedEnvironment![ProviderAuthEnvironment.ClaudeCodeOAuthToken],
                    StringComparison.Ordinal),
                "The spawned Claude process did not receive the arranged setup token.");
        }
        else
        {
            Assert.DoesNotContain(
                ProviderAuthEnvironment.ClaudeCodeOAuthToken,
                run.SpawnedEnvironment!.Keys);
        }
    }

    [Fact]
    public async Task Same_task_reuses_its_remote_clean_home_while_other_tasks_stay_isolated()
    {
        if (NodeMissing()) return;
        var fixture = Fixture.Load("p1-happy-done.codex.fixture");

        var first = await RunFixtureAsync(fixture, cleanContextKey: "AGT-2525");
        var continued = await RunFixtureAsync(fixture, cleanContextKey: "AGT-2525");
        var otherTask = await RunFixtureAsync(fixture, cleanContextKey: "AGT-2526");

        var firstHome = first.SpawnedEnvironment!["CODEX_HOME"];
        Assert.Equal(firstHome, continued.SpawnedEnvironment!["CODEX_HOME"]);
        Assert.NotEqual(firstHome, otherTask.SpawnedEnvironment!["CODEX_HOME"]);
        Assert.Contains(continued.Shipped, line => line.Text.Contains("clean-context reused", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Read_only_permission_and_shared_context_from_the_spec_reach_the_argv_and_environment()
    {
        if (NodeMissing()) return;
        var fixture = Fixture.Load("p1-happy-done.claude.fixture");
        var run = await RunFixtureAsync(
            fixture,
            permissionMode: "read-only",
            contextMode: "shared");

        Assert.Contains("--permission-mode", run.SpawnedArgv!);
        Assert.Contains("plan", run.SpawnedArgv!);
        Assert.DoesNotContain("--dangerously-skip-permissions", run.SpawnedArgv!);
        Assert.False(run.SpawnedEnvironment!.ContainsKey("CLAUDE_CONFIG_DIR"));
    }

    [Fact]
    public async Task Codex_spec_uses_the_codex_descriptor_with_full_access_sandbox()
    {
        if (NodeMissing()) return;
        var fixture = Fixture.Load("p1-happy-done.codex.fixture");
        var run = await RunFixtureAsync(fixture);

        Assert.Equal("exec", run.SpawnedArgv![0]);
        Assert.Contains("--experimental-json", run.SpawnedArgv!);
        Assert.Contains("--sandbox", run.SpawnedArgv!);
        Assert.Contains("danger-full-access", run.SpawnedArgv!);
        Assert.Equal("-", run.SpawnedArgv![^1]); // prompt on stdin
        Assert.Equal(fixture.ExitCode, run.Result.ExitCode);
    }

    [Fact]
    public async Task Car_worker_starts_without_CultureNotFoundException_under_invariant_globalization()
    {
        if (NodeMissing()) return;
        var modeProbe = Assert.Throws<CultureNotFoundException>(
            () => CultureInfo.GetCultureInfo("en-US"));
        Assert.Contains("invariant culture", modeProbe.Message, StringComparison.OrdinalIgnoreCase);

        var exception = await Record.ExceptionAsync(async () =>
        {
            var fixture = Fixture.Load("p1-happy-done.codex.fixture");
            var run = await RunFixtureAsync(fixture);
            Assert.Equal(fixture.ExitCode, run.Result.ExitCode);
        });

        Assert.False(
            exception is CultureNotFoundException,
            $"CAR attempted to construct a named culture in invariant mode: {exception}");
        Assert.Null(exception);
    }

    [Fact]
    public async Task Timeout_produces_the_legacy_result_shape_and_no_surviving_cli_process()
    {
        if (NodeMissing()) return;
        var fixture = Fixture.Load("p1-happy-done.claude.fixture");
        var run = await RunFixtureAsync(
            fixture,
            timeoutSeconds: 2,
            extraEnvironment: new Dictionary<string, string> { ["FAKE_CLI_DELAY_MS"] = "30000" });

        Assert.True(run.TimedOut);
        Assert.Equal(124, run.Result.ExitCode);
        Assert.Equal(string.Empty, run.Result.StdOut);
        Assert.Equal("Runner timeout", run.Result.StdErr);
        Assert.Contains(run.Shipped, line => line.Text.Contains("exceeded 2s timeout"));

        Assert.NotNull(run.SpawnedProcess);
        Assert.True(
            run.SpawnedProcess!.WaitForExit(10_000),
            "the fake CLI process must be killed with the timeout, not orphaned");
    }

    [Fact]
    public async Task Launch_failure_is_preserved_as_an_explicit_pre_agent_fact()
    {
        var workerDirectory = Path.Combine(_root, "launch-failure");
        var workingDirectory = Path.Combine(workerDirectory, "worktree");
        var resultsDirectory = Path.Combine(workerDirectory, "results");
        Directory.CreateDirectory(workingDirectory);
        Directory.CreateDirectory(resultsDirectory);
        var shipped = new List<(string Stream, string Text)>();
        var spec = new DetachedJobSpec(
            FileName: "missing-cli",
            Arguments: [],
            WorkingDirectory: workingDirectory,
            Prompt: "prompt",
            ResultsDirectory: resultsDirectory,
            TimeoutSeconds: 30,
            CliType: "claude",
            RunId: "car-launch-failure");

        var (result, timedOut, launchFailed) = await CarWorkerExecution.RunAsync(
            spec,
            workerDirectory,
            (stream, text) => shipped.Add((stream, text)),
            options => options with
            {
                ClaudePath = Path.Combine(_root, "missing-cli"),
            },
            cleanContextRoot: Path.Combine(_root, "clean-context"));

        Assert.Equal(125, result.ExitCode);
        Assert.False(timedOut);
        Assert.True(
            launchFailed,
            $"Expected a launch failure. stdout={result.StdOut}; stderr={result.StdErr}; " +
            $"events={string.Join(" | ", shipped.Select(line => $"{line.Stream}:{line.Text}"))}");
        Assert.Contains(shipped, line =>
            line.Text.Contains("failed to start claude", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task The_typed_event_trace_is_written_and_ends_with_RunEnded()
    {
        if (NodeMissing()) return;
        var fixture = Fixture.Load("p1-happy-done.claude.fixture");
        var run = await RunFixtureAsync(fixture);

        var eventsPath = Path.Combine(run.WorkerDirectory, "events.jsonl");
        Assert.True(File.Exists(eventsPath));
        var lines = File.ReadAllLines(eventsPath).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        Assert.NotEmpty(lines);
        using var last = JsonDocument.Parse(lines[^1]);
        Assert.Equal("RunEnded", last.RootElement.GetProperty("type").GetString());
        Assert.Contains(lines, line => line.Contains("\"type\":\"TurnCompleted\""));
    }

    [Theory]
    [InlineData("claude/2.1.202/p23-unknown-frame.claude.fixture", "future.protocol.frame")]
    [InlineData("codex/0.144.1/p23-unknown-frame.codex.fixture", "item.completed/future_widget")]
    public async Task Unknown_frames_emit_scrubbed_typed_drift_telemetry_without_changing_the_outcome(
        string fixtureName,
        string expectedFrameType)
    {
        if (NodeMissing()) return;
        var run = await RunFixtureAsync(Fixture.Load(fixtureName));

        var markerLine = Assert.Single(run.Shipped, line =>
            line.Stream == "system"
            && line.Text.StartsWith(ProtocolNoveltyTelemetry.MarkerPrefix, StringComparison.Ordinal));
        Assert.True(ProtocolNoveltyTelemetry.TryParseMarker(markerLine.Text, out var telemetry));
        Assert.Equal(expectedFrameType, telemetry.FrameType);
        Assert.Equal(1, telemetry.Occurrence);
        Assert.Equal(1, telemetry.TotalUnknownFrames);
        Assert.DoesNotContain("must-not-leave-worker", markerLine.Text, StringComparison.Ordinal);
        Assert.Equal(
            ExecutionOutcomeKind.SuccessfulCompletion,
            Classify(run.Result.StdOut, run.Result.StdErr, run.Result.ExitCode).Outcome);

        Assert.Equal(
            LifecycleEventKinds.ProtocolUnknownFrame,
            TaskServerClient.ClassifyV1Event(
                new CliOutputLine(DateTime.UtcNow, markerLine.Stream, markerLine.Text)));
    }

    // ── harness ─────────────────────────────────────────────────────────

    private sealed record FixtureRun(
        ProcessResult Result,
        bool TimedOut,
        bool LaunchFailed,
        string WorkerDirectory,
        string ResultsDirectory,
        IReadOnlyList<(string Stream, string Text)> ShippedRaw,
        IReadOnlyList<string>? SpawnedArgv,
        IReadOnlyDictionary<string, string>? SpawnedEnvironment,
        Process? SpawnedProcess,
        JsonDocument? Capture)
    {
        public IReadOnlyList<(string Stream, string Text)> Shipped => ShippedRaw;
    }

    private async Task<FixtureRun> RunFixtureAsync(
        Fixture fixture,
        string prompt = "do the recorded thing",
        string? permissionMode = null,
        string? contextMode = null,
        int timeoutSeconds = 120,
        IReadOnlyDictionary<string, string>? extraEnvironment = null,
        string? cleanContextKey = null)
    {
        var workerDirectory = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        var workingDirectory = Path.Combine(workerDirectory, "worktree");
        var resultsDirectory = Path.Combine(workerDirectory, "results");
        Directory.CreateDirectory(workingDirectory);
        Directory.CreateDirectory(resultsDirectory);
        var capturePath = Path.Combine(workerDirectory, "capture.json");

        var spec = new DetachedJobSpec(
            FileName: "unused-by-the-car-engine",
            Arguments: [],
            WorkingDirectory: workingDirectory,
            Prompt: prompt,
            ResultsDirectory: resultsDirectory,
            TimeoutSeconds: timeoutSeconds,
            CliType: fixture.Cli,
            PermissionMode: permissionMode,
            ContextMode: contextMode,
            RunId: $"car-parity-{Guid.NewGuid():N}",
            CleanContextKey: cleanContextKey);

        var spawner = new FixtureSpawner(fixture.Path);
        var shipped = new List<(string, string)>();
        var environment = new Dictionary<string, string> { ["FAKE_CLI_CAPTURE"] = capturePath };
        if (extraEnvironment is not null)
            foreach (var kv in extraEnvironment) environment[kv.Key] = kv.Value;

        var (result, timedOut, launchFailed) = await CarWorkerExecution.RunAsync(
            spec,
            workerDirectory,
            (stream, text) => { lock (shipped) shipped.Add((stream, text)); },
            options => options with
            {
                // node --version satisfies the claude pre-spawn health probe; the
                // spawner then replaces the launch with the fixture replay.
                ClaudePath = "node",
                CodexPath = "node",
                Spawner = spawner,
                EnvironmentOverrides = environment,
            },
            cleanContextRoot: Path.Combine(_root, "clean-context"));

        JsonDocument? capture = null;
        if (File.Exists(capturePath))
            capture = JsonDocument.Parse(await File.ReadAllTextAsync(capturePath));

        return new FixtureRun(
            result,
            timedOut,
            launchFailed,
            workerDirectory,
            resultsDirectory,
            shipped,
            spawner.Argv,
            spawner.Environment,
            spawner.Spawned,
            capture);
    }

    /// <summary>
    /// Records the fully prepared production launch (argv, environment) and then
    /// starts <c>node fake-cli.mjs &lt;fixture&gt;</c> in its place - the
    /// injection seam the migration plan calls V1.
    /// </summary>
    private sealed class FixtureSpawner(string fixturePath) : ICliProcessSpawner
    {
        public IReadOnlyList<string>? Argv { get; private set; }
        public IReadOnlyDictionary<string, string>? Environment { get; private set; }
        public Process? Spawned { get; private set; }

        public CliSpawn Spawn(ProcessStartInfo startInfo)
        {
            Argv = startInfo.ArgumentList.ToList();
            Environment = startInfo.Environment
                .Where(kv => kv.Value is not null)
                .ToDictionary(kv => kv.Key, kv => kv.Value!, StringComparer.OrdinalIgnoreCase);

            startInfo.FileName = "node";
            startInfo.ArgumentList.Clear();
            startInfo.ArgumentList.Add(FakeCliPath());
            startInfo.ArgumentList.Add(fixturePath);

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.Start();
            Spawned = process;
            return new CliSpawn(
                process,
                startInfo.RedirectStandardInput ? process.StandardInput.BaseStream : Stream.Null,
                process.StandardOutput,
                process.StandardError);
        }
    }

    private static ExecutionOutcomeDecision Classify(string stdOut, string stdErr, int exitCode)
    {
        var provider = ProviderOutputEvidenceExtractor.Extract(stdOut);
        return ExecutionOutcomeAdapter.Classify(new ExecutionRawFacts(
            "attempt-car-parity",
            ExecutionAttemptKind.Coding,
            provider.TerminalEvent,
            provider.FinalAssistantOutput,
            stdOut,
            stdErr,
            exitCode,
            Signal: null,
            SessionState: string.IsNullOrWhiteSpace(provider.SessionId)
                ? ExecutionSessionState.Unsupported
                : ExecutionSessionState.Active,
            SessionId: provider.SessionId,
            DurableOutputState: DurableOutputState.LocalOnly,
            DurableOutputReference: "/srv/agent-host/worktrees/AGT-2370"));
    }

    /// <summary>Trailing-newline-insensitive comparison: the bounded buffer re-adds one per line.</summary>
    private static string Normalize(string text) => text.Replace("\r\n", "\n").TrimEnd('\n');

    private static bool NodeMissing()
    {
        try
        {
            using var probe = Process.Start(new ProcessStartInfo
            {
                FileName = "node",
                ArgumentList = { "--version" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (probe is null) return true;
            probe.WaitForExit(8000);
            return probe.ExitCode != 0;
        }
        catch
        {
            return true;
        }
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _original;

        public EnvironmentVariableScope(string name, string? value)
        {
            _name = name;
            _original = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _original);
    }

    private sealed record Fixture(
        string Path,
        string Scenario,
        string Cli,
        string Form,
        int ExitCode,
        string StdOut,
        string StdErr)
    {
        public static Fixture Load(string name)
        {
            var path = CliCaptureFixtureLocator.Resolve(RepoRoot(), name);
            Assert.True(File.Exists(path), $"fixture not found: {path}");

            JsonElement meta = default;
            var seenMeta = false;
            var stdout = new List<string>();
            var stderr = new List<string>();
            foreach (var line in File.ReadAllText(path).Replace("\r\n", "\n").Split('\n'))
            {
                if (line.Length == 0) continue;
                if (line.StartsWith("#!", StringComparison.Ordinal))
                {
                    meta = JsonDocument.Parse(line[2..].Trim()).RootElement.Clone();
                    seenMeta = true;
                    continue;
                }
                if (line.StartsWith('#')) continue;
                Assert.True(seenMeta, $"fixture {name} does not open with a '#!' metadata line");
                if (line.StartsWith("@delay ", StringComparison.Ordinal)) continue;
                if (line.StartsWith("!stderr ", StringComparison.Ordinal)) { stderr.Add(line[8..]); continue; }
                stdout.Add(line);
            }

            return new Fixture(
                path,
                meta.GetProperty("scenario").GetString()!,
                meta.GetProperty("cli").GetString()!,
                meta.GetProperty("form").GetString()!,
                meta.TryGetProperty("exitCode", out var exit) ? exit.GetInt32() : 0,
                string.Join('\n', stdout),
                string.Join('\n', stderr));
        }
    }

    private static string FakeCliPath()
        => Path.Combine(RepoRoot(), "testdata", "cli-fixtures", "fake-cli.mjs");

    private static string RepoRoot([CallerFilePath] string sourceFile = "")
    {
        foreach (var start in new[] { Path.GetDirectoryName(sourceFile), AppContext.BaseDirectory })
        {
            var current = start;
            while (!string.IsNullOrEmpty(current))
            {
                if (File.Exists(Path.Combine(current, "agent-taskboard.sln"))) return current;
                current = Path.GetDirectoryName(current);
            }
        }

        throw new InvalidOperationException(
            "agent-taskboard.sln not found above the CAR parity test source file or the test base directory.");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* a killed fake CLI may still be unwinding */ }
    }
}
