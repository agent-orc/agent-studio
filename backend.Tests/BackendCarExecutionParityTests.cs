using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentStudio.TestSupport;
using CodingAgentRunner.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Backend side of the CAR adoption parity gate. The fixture spawner records
/// the fully prepared production launch and replaces only the final executable
/// with the deterministic Node fixture replayer. The real
/// <see cref="GenericCliExecutionService"/>, CAR descriptor, stream pumps,
/// Studio callback bridge, persistence, rendering, usage capture and terminal
/// classifier remain in the path.
/// </summary>
[Trait("Category", "MachineBound")]
public sealed class BackendCarExecutionParityTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "backend-car-parity", Guid.NewGuid().ToString("N"));

    public static TheoryData<string> AllStreamFixtures()
    {
        var data = new TheoryData<string>();
        foreach (var path in CliCaptureFixtureLocator.AllRelativePaths(RepoRoot()))
        {
            data.Add(path);
        }
        return data;
    }

    [SkippableTheory]
    [MemberData(nameof(AllStreamFixtures))]
    public async Task Every_recorded_fixture_preserves_raw_streams_and_terminal_status_through_backend_car(
        string fixtureName)
    {
        RequireNode();
        var fixture = Fixture.Load(fixtureName);
        var run = await StartFixtureAsync(fixture);

        var final = await run.Finished.WaitAsync(TimeSpan.FromSeconds(20));
        var persisted = RunLogStore.ReadMerged(run.Service.GetOutputLogDir(run.JobKey));

        Assert.Equal(fixture.ExitCode, final.ExitCode);
        Assert.Equal(fixture.ExpectedStatus, final.Status);
        Assert.Equal(fixture.ExpectedOutcome, final.RunOutcome);
        Assert.Equal(
            Normalize(fixture.StdOut),
            Normalize(JoinStream(persisted, "stdout")));
        Assert.Equal(
            Normalize(fixture.StdErr),
            Normalize(JoinStream(persisted, "stderr")));

        Assert.Single(run.Events.OfType<CliRunEvent.RunStarted>());
        var ended = Assert.Single(run.Events.OfType<CliRunEvent.RunEnded>());
        var expectedTypedOutcome = final.Status switch
        {
            RunStatuses.Completed => CodingAgentRunner.Model.RunOutcome.Completed,
            RunStatuses.Stopped => CodingAgentRunner.Model.RunOutcome.Stopped,
            _ => CodingAgentRunner.Model.RunOutcome.Failed,
        };
        Assert.Equal(expectedTypedOutcome, ended.Outcome);
        Assert.Single(run.Output, line =>
            line.Stream == "system"
            && line.Text.StartsWith("[taskboard] Started ", StringComparison.Ordinal));
        Assert.Single(run.Output, line =>
            line.Stream == "system"
            && line.Text.Contains(" CLI exited:", StringComparison.Ordinal));
        Assert.DoesNotContain(run.Output, line =>
            line.Stream == "system"
            && line.Text.StartsWith("[runner] Started ", StringComparison.Ordinal));
        if (string.Equals(fixture.Form, "stream-json", StringComparison.OrdinalIgnoreCase))
        {
            Assert.DoesNotContain(run.Output, line =>
                line.Stream == "stdout"
                && line.Text.TrimStart().StartsWith('{'));
        }

        var preparedArgv = Assert.IsAssignableFrom<IReadOnlyList<string>>(run.Spawner.PreparedArgv);
        if (string.Equals(fixture.Cli, CliTypes.Codex, StringComparison.OrdinalIgnoreCase))
            AssertOption(preparedArgv, "--sandbox", "danger-full-access");
        else
            Assert.Contains("--dangerously-skip-permissions", preparedArgv);
    }

    [SkippableTheory]
    [InlineData("p1-happy-done.claude.fixture", 1542L, 911L, 48230L, 2010L, null)]
    [InlineData("p1-happy-done.codex.fixture", 22267L, 910L, 6528L, 0L, 128L)]
    public async Task TurnCompleted_subscriber_sees_parsed_usage_before_the_event(
        string fixtureName,
        long input,
        long output,
        long cacheRead,
        long cacheWrite,
        long? reasoningOutput)
    {
        RequireNode();
        var fixture = Fixture.Load(fixtureName);
        (ParsedTurnUsage Usage, DateTime ObservedAt, DateTime StartedAt)? usageAtEvent = null;
        var turnCompletedCount = 0;

        var run = await StartFixtureAsync(
            fixture,
            onRunEvent: (service, jobKey, evt) =>
            {
                if (evt is not CliRunEvent.TurnCompleted) return;
                Interlocked.Increment(ref turnCompletedCount);
                usageAtEvent = service.GetLastParsedTurnUsage(jobKey);
            });

        await run.Finished.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(1, Volatile.Read(ref turnCompletedCount));
        Assert.NotNull(usageAtEvent);
        Assert.Equal(input, usageAtEvent.Value.Usage.Input);
        Assert.Equal(output, usageAtEvent.Value.Usage.Output);
        Assert.Equal(cacheRead, usageAtEvent.Value.Usage.CacheRead);
        Assert.Equal(cacheWrite, usageAtEvent.Value.Usage.CacheWrite);
        Assert.Equal(reasoningOutput, usageAtEvent.Value.Usage.ReasoningOutput);
        Assert.InRange(
            usageAtEvent.Value.ObservedAt,
            usageAtEvent.Value.StartedAt,
            DateTime.UtcNow.AddSeconds(1));
    }

    [SkippableFact]
    public async Task Failed_codex_command_does_not_taint_the_next_noncommand_tool_event()
    {
        RequireNode();
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "failed-command-then-file-change.fixture");
        const string stdout = """
            {"type":"thread.started","thread_id":"019dee65-7a9b-7843-bfd9-06e555fff02b"}
            {"type":"turn.started"}
            {"type":"item.completed","item":{"id":"item_0","type":"command_execution","command":"false","aggregated_output":"failed","exit_code":1}}
            {"type":"item.completed","item":{"id":"item_1","type":"file_change","file_path":"backend/example.cs"}}
            {"type":"item.completed","item":{"id":"item_2","type":"agent_message","text":"Recovered and completed the change.\n\n[[TASK_DONE]]"}}
            {"type":"turn.completed","usage":{"input_tokens":10,"cached_input_tokens":0,"output_tokens":5,"reasoning_output_tokens":0}}
            """;
        await File.WriteAllTextAsync(
            path,
            "#! {\"scenario\":\"boundary\",\"cli\":\"codex\",\"form\":\"stream-json\",\"exitCode\":0}\n"
            + stdout
            + "\n");
        var fixture = new Fixture(
            path,
            CliTypes.Codex,
            "stream-json",
            0,
            stdout,
            string.Empty,
            RunStatuses.Completed,
            TerminalRunOutcomeKinds.Success);

        var run = await StartFixtureAsync(fixture);
        await run.Finished.WaitAsync(TimeSpan.FromSeconds(20));

        var tools = run.Events.OfType<CliRunEvent.ToolCompleted>().ToList();
        Assert.True(tools.Count >= 2, "expected command and file-change tool completion events");
        Assert.True(tools[0].IsError);
        Assert.False(tools[1].IsError);
    }

    [SkippableFact]
    public async Task Claude_car_launch_carries_permission_model_thinking_results_and_rules_file()
    {
        RequireNode();
        const string prompt = "Apply the fixture change and report the result.";
        var fixture = Fixture.Load("p1-happy-done.claude.fixture");
        var run = await StartFixtureAsync(fixture, new RunSettings
        {
            Prompt = prompt,
            Model = "claude-opus-4.8",
            ThinkingLevel = "xhigh",
            PermissionMode = CliPermissionModes.ReadOnly,
        });

        var final = await run.Finished.WaitAsync(TimeSpan.FromSeconds(20));
        var argv = Assert.IsAssignableFrom<IReadOnlyList<string>>(run.Spawner.PreparedArgv);
        var environment = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(
            run.Spawner.PreparedEnvironment);

        Assert.Equal("claude-opus-4-8", final.Model);
        Assert.Equal("xhigh", final.ThinkingLevel);
        AssertOption(argv, "--model", "claude-opus-4-8");
        AssertOption(argv, "--effort", "xhigh");
        AssertOption(argv, "--permission-mode", "plan");
        Assert.DoesNotContain("--dangerously-skip-permissions", argv);
        AssertOption(argv, "--append-system-prompt-file", run.RulesPath);
        Assert.DoesNotContain(prompt, argv);
        Assert.Equal(run.ResultsDirectory, environment["JOB_RESULTS_DIR"]);

        var rulesIndex = IndexOf(argv, "--append-system-prompt-file");
        Assert.Equal(argv.Count - 2, rulesIndex);
        Assert.Equal(1, argv.Count(arg => arg == "--append-system-prompt-file"));

        using var capture = await ReadCaptureAsync(run.CapturePath);
        Assert.Equal(prompt.Length, capture.RootElement.GetProperty("stdinChars").GetInt32());
    }

    [SkippableFact]
    public async Task Claude_fable_launch_keeps_the_studio_registry_thinking_level()
    {
        RequireNode();
        var fixture = Fixture.Load("p1-happy-done.claude.fixture");
        var run = await StartFixtureAsync(fixture, new RunSettings
        {
            Model = ModelIds.ClaudeFable51,
            ThinkingLevel = "max",
        });

        var final = await run.Finished.WaitAsync(TimeSpan.FromSeconds(20));
        var argv = Assert.IsAssignableFrom<IReadOnlyList<string>>(run.Spawner.PreparedArgv);

        Assert.Equal(ModelIds.ClaudeFable51, final.Model);
        Assert.Equal("max", final.ThinkingLevel);
        AssertOption(argv, "--model", ModelIds.ClaudeFable51);
        AssertOption(argv, "--effort", "max");
    }

    [SkippableFact]
    public async Task Claude_car_transports_a_200_kib_prompt_on_stdin_not_argv()
    {
        RequireNode();
        var prompt = new string('x', 200 * 1024);
        var fixture = Fixture.Load("p1-happy-done.claude.fixture");
        var run = await StartFixtureAsync(fixture, new RunSettings { Prompt = prompt });

        await run.Finished.WaitAsync(TimeSpan.FromSeconds(20));

        var argv = Assert.IsAssignableFrom<IReadOnlyList<string>>(run.Spawner.PreparedArgv);
        Assert.DoesNotContain(prompt, argv);
        Assert.All(argv, argument => Assert.True(argument.Length < 32_000));
        using var capture = await ReadCaptureAsync(run.CapturePath);
        Assert.Equal(prompt.Length, capture.RootElement.GetProperty("stdinChars").GetInt32());
    }

    [SkippableFact]
    public async Task Codex_car_launch_carries_permission_model_thinking_results_and_prefixed_stdin()
    {
        RequireNode();
        const string prompt = "Apply the fixture change and report the result.";
        var fixture = Fixture.Load("p1-happy-done.codex.fixture");
        var run = await StartFixtureAsync(fixture, new RunSettings
        {
            Prompt = prompt,
            Model = "gpt-5.5",
            ThinkingLevel = "xhigh",
            PermissionMode = CliPermissionModes.ReadOnly,
        });

        var final = await run.Finished.WaitAsync(TimeSpan.FromSeconds(20));
        var argv = Assert.IsAssignableFrom<IReadOnlyList<string>>(run.Spawner.PreparedArgv);
        var environment = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(
            run.Spawner.PreparedEnvironment);

        Assert.Equal("gpt-5.5", final.Model);
        Assert.Equal("xhigh", final.ThinkingLevel);
        Assert.Equal("exec", argv[0]);
        Assert.Contains("--experimental-json", argv);
        AssertOption(argv, "--sandbox", "read-only");
        AssertOption(argv, "-m", "gpt-5.5");
        AssertOption(argv, "-c", "model_reasoning_effort=\"xhigh\"");
        Assert.Equal("-", argv[^1]);
        Assert.DoesNotContain("--append-system-prompt-file", argv);
        Assert.Equal(run.ResultsDirectory, environment["JOB_RESULTS_DIR"]);

        using var capture = await ReadCaptureAsync(run.CapturePath);
        var stdinChars = capture.RootElement.GetProperty("stdinChars").GetInt32();
        Assert.True(stdinChars > prompt.Length, "Codex stdin must contain the Studio system prefix and prompt.");
    }

    [SkippableFact]
    public async Task User_stop_on_car_path_is_interrupted_and_reaps_the_fixture_process()
    {
        RequireNode();
        var fixture = Fixture.Load("p1-happy-done.claude.fixture");
        var run = await StartFixtureAsync(fixture, new RunSettings
        {
            FakeEnvironment = new Dictionary<string, string>
            {
                ["FAKE_CLI_DELAY_MS"] = "30000",
            },
        });

        Assert.True(run.Service.Stop(run.JobKey, RunStopReason.UserStop));
        var final = await run.Finished.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(RunStatuses.Stopped, final.Status);
        Assert.Equal(TerminalRunOutcomeKinds.Interrupted, final.RunOutcome);
        Assert.NotNull(run.Spawner.SpawnedProcess);
        Assert.True(
            run.Spawner.SpawnedProcess!.WaitForExit(10_000),
            "Stopping the CAR-owned run must not leave the fixture process alive.");
        var ended = Assert.Single(run.Events.OfType<CliRunEvent.RunEnded>());
        Assert.Equal(CodingAgentRunner.Model.RunOutcome.Stopped, ended.Outcome);
    }

    [SkippableFact]
    public async Task Host_adoption_failure_stops_and_forgets_the_car_owned_process()
    {
        RequireNode();
        Directory.CreateDirectory(Path.Combine(_root, ".runtime"));
        await File.WriteAllTextAsync(Path.Combine(_root, ".runtime", "cli-output"), "blocks host log directory");
        var rulesPath = Path.Combine(_root, "agent-rules.md");
        await File.WriteAllTextAsync(rulesPath, "Finish with one terminal sentinel.\n");
        var workingDirectory = Path.Combine(_root, "worktree");
        Directory.CreateDirectory(workingDirectory);
        var fixture = Fixture.Load("p1-happy-done.claude.fixture");
        var service = BuildService(CliTypes.Claude, rulesPath);
        service.SetCliPath("node");
        var spawner = new FixtureSpawner(
            fixture.Path,
            new Dictionary<string, string> { ["FAKE_CLI_DELAY_MS"] = "30000" });
        service.CarOptionsCustomizer = options => options with { Spawner = spawner };

        var (started, error) = await service.StartAsync(
            "adoption-failure",
            "adoption-failure",
            "Run the fixture.",
            workingDirectory,
            jobFolderPath: Path.Combine(_root, "task"),
            contextMode: CliContextModes.Shared,
            executionEngine: CliExecutionEngines.Car);

        Assert.Null(started);
        Assert.Contains("could not adopt", error, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(spawner.SpawnedProcess);
        Assert.True(
            spawner.SpawnedProcess!.WaitForExit(10_000),
            "a CAR process must not survive a host adoption failure");
        Assert.Null(service.GetExecution("adoption-failure"));
    }

    [SkippableFact]
    public async Task Car_start_failure_after_spawn_reaps_process_and_releases_clean_context()
    {
        RequireNode();
        Directory.CreateDirectory(_root);
        var rulesPath = Path.Combine(_root, "agent-rules.md");
        await File.WriteAllTextAsync(rulesPath, "Finish with one terminal sentinel.\n");
        var workingDirectory = Path.Combine(_root, "worktree");
        Directory.CreateDirectory(workingDirectory);
        var fixture = Fixture.Load("p1-happy-done.claude.fixture");
        var service = BuildService(CliTypes.Claude, rulesPath);
        service.SetCliPath("node");
        var fixtureSpawner = new FixtureSpawner(
            fixture.Path,
            new Dictionary<string, string> { ["FAKE_CLI_DELAY_MS"] = "30000" });
        service.CarOptionsCustomizer = options => options with { Spawner = fixtureSpawner };
        service.CarAfterSpawnForTest = _ => throw new IOException("fixture failure after process spawn");
        const string jobKey = "post-spawn-start-failure";

        var (started, error) = await service.StartAsync(
            jobKey,
            jobKey,
            "Run the fixture.",
            workingDirectory,
            jobFolderPath: Path.Combine(_root, "task"),
            contextMode: CliContextModes.Clean,
            executionEngine: CliExecutionEngines.Car);

        Assert.Null(started);
        Assert.NotNull(error);
        Assert.NotNull(fixtureSpawner.SpawnedProcess);
        Assert.True(
            fixtureSpawner.SpawnedProcess!.WaitForExit(10_000),
            "a process spawned before CAR start failure must be reaped");
        Assert.Null(service.GetExecution(jobKey));
        Assert.Null(service.GetPersistentCleanContextHome(jobKey));
    }

    [SkippableFact]
    public async Task Typed_stderr_diagnostic_stays_with_its_raw_line_before_later_stdout()
    {
        RequireNode();
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "stderr-diagnostic-order.fixture");
        const string warning = "plugin is not installed plugin=\"example-plugin\"";
        const string laterFrame = "{\"type\":\"turn.failed\",\"error\":{\"message\":\"fixture stopped\"}}";
        await File.WriteAllTextAsync(
            path,
            "#! {\"scenario\":\"diagnostic-order\",\"cli\":\"codex\",\"form\":\"stream-json\",\"exitCode\":1}\n"
            + $"!stderr {warning}\n"
            + "@delay 500\n"
            + laterFrame
            + "\n");
        var fixture = new Fixture(
            path,
            CliTypes.Codex,
            "stream-json",
            1,
            laterFrame,
            warning,
            RunStatuses.Failed,
            TerminalRunOutcomeKinds.Failed);
        var sequence = new ConcurrentQueue<string>();

        var run = await StartFixtureAsync(
            fixture,
            onRunEvent: (_, _, evt) =>
            {
                if (evt is CliRunEvent.Diagnostic diagnostic)
                    sequence.Enqueue($"diagnostic:{diagnostic.RawDetail}");
                else if (evt is CliRunEvent.TurnFailed)
                    sequence.Enqueue("event:turn-failed");
            },
            onOutput: (_, _, line) =>
            {
                if (line.Stream is "stdout" or "stderr")
                    sequence.Enqueue($"raw:{line.Stream}:{line.Text}");
            });

        await run.Finished.WaitAsync(TimeSpan.FromSeconds(20));

        var ordered = sequence.ToList();
        var rawStdErr = ordered.IndexOf($"raw:stderr:{warning}");
        var diagnostic = ordered.IndexOf($"diagnostic:{warning}");
        var laterTurnFailed = ordered.IndexOf("event:turn-failed");
        Assert.True(rawStdErr >= 0, "raw stderr was not observed");
        Assert.True(diagnostic > rawStdErr, "typed diagnostic must follow its raw source line");
        Assert.True(
            laterTurnFailed > diagnostic,
            $"typed stderr diagnostic must not wait for later stdout: {string.Join(" | ", ordered)}");
    }

    [SkippableTheory]
    [InlineData("p1-happy-done.claude.fixture")]
    [InlineData("p1-happy-done.codex.fixture")]
    public async Task Steer_continuation_uses_the_captured_session_in_the_car_resume_argv(string fixtureName)
    {
        RequireNode();
        var fixture = Fixture.Load(fixtureName);
        const string jobKey = "car-steer-resume";

        var first = await StartFixtureAsync(fixture, new RunSettings { JobKey = jobKey });
        await first.Finished.WaitAsync(TimeSpan.FromSeconds(20));
        var sessionId = first.Service.GetCapturedSessionId(jobKey);
        Assert.False(string.IsNullOrWhiteSpace(sessionId));

        const string followup = "Continue with the operator follow-up.";
        var second = await StartFixtureAsync(
            fixture,
            new RunSettings
            {
                JobKey = jobKey,
                Prompt = followup,
                SessionName = sessionId,
                ResumeSession = true,
            },
            first.Service);
        await second.Finished.WaitAsync(TimeSpan.FromSeconds(20));

        var argv = Assert.IsAssignableFrom<IReadOnlyList<string>>(second.Spawner.PreparedArgv);
        if (string.Equals(fixture.Cli, CliTypes.Claude, StringComparison.OrdinalIgnoreCase))
        {
            AssertOption(argv, "-r", sessionId!);
            Assert.DoesNotContain(followup, argv);
            using var capture = await ReadCaptureAsync(second.CapturePath);
            Assert.Equal(followup.Length, capture.RootElement.GetProperty("stdinChars").GetInt32());
        }
        else
        {
            var resumeIndex = IndexOf(argv, "resume");
            Assert.True(resumeIndex > 0, "Codex resume subcommand was not emitted.");
            Assert.Equal(sessionId, argv[resumeIndex + 1]);
            Assert.Equal("-", argv[^1]);

            using var capture = await ReadCaptureAsync(second.CapturePath);
            Assert.True(
                capture.RootElement.GetProperty("stdinChars").GetInt32() > followup.Length,
                "The resumed Codex turn must carry the Studio prefix and follow-up over stdin.");
        }
    }

    private async Task<StartedRun> StartFixtureAsync(
        Fixture fixture,
        RunSettings? settings = null,
        GenericCliExecutionService? existingService = null,
        Action<GenericCliExecutionService, string, CliRunEvent>? onRunEvent = null,
        Action<GenericCliExecutionService, string, CliOutputLine>? onOutput = null)
    {
        settings ??= new RunSettings();
        Directory.CreateDirectory(_root);
        var workingDirectory = Path.Combine(_root, "worktree");
        Directory.CreateDirectory(workingDirectory);
        var jobFolder = Path.Combine(_root, "task");
        var resultsDirectory = Path.Combine(jobFolder, "results");
        Directory.CreateDirectory(resultsDirectory);
        var rulesPath = Path.Combine(_root, "agent-rules.md");
        if (!File.Exists(rulesPath))
            await File.WriteAllTextAsync(rulesPath, "Use the repository rules and finish with one terminal sentinel.\n");

        var service = existingService ?? BuildService(fixture.Cli, rulesPath);
        service.SetCliPath("node");

        var capturePath = Path.Combine(_root, $"capture-{Guid.NewGuid():N}.json");
        var fakeEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["FAKE_CLI_CAPTURE"] = capturePath,
        };
        if (settings.FakeEnvironment != null)
        {
            foreach (var pair in settings.FakeEnvironment)
                fakeEnvironment[pair.Key] = pair.Value;
        }

        var spawner = new FixtureSpawner(fixture.Path, fakeEnvironment);
        service.CarOptionsCustomizer = options => options with
        {
            Spawner = spawner,
        };

        var jobKey = settings.JobKey ?? $"car-parity-{Guid.NewGuid():N}";
        var output = new ConcurrentQueue<CliOutputLine>();
        var events = new ConcurrentQueue<CliRunEvent>();
        var finished = new TaskCompletionSource<CliExecution>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.OnOutput += (id, line) =>
        {
            if (!string.Equals(id, jobKey, StringComparison.Ordinal)) return;
            output.Enqueue(line);
            onOutput?.Invoke(service, jobKey, line);
        };
        service.OnRunEvent += (id, evt) =>
        {
            if (!string.Equals(id, jobKey, StringComparison.Ordinal)) return;
            events.Enqueue(evt);
            onRunEvent?.Invoke(service, jobKey, evt);
        };
        service.OnFinished += (id, execution) =>
        {
            if (string.Equals(id, jobKey, StringComparison.Ordinal)) finished.TrySetResult(execution);
        };

        var model = settings.Model ?? (fixture.Cli == CliTypes.Codex ? "gpt-5.5" : "claude-sonnet-4-5");
        var (started, error) = await service.StartAsync(
            jobId: jobKey,
            jobKey: jobKey,
            prompt: settings.Prompt,
            workingDirectory: workingDirectory,
            sessionName: settings.SessionName,
            resumeSession: settings.ResumeSession,
            model: model,
            thinkingLevel: settings.ThinkingLevel,
            jobFolderPath: jobFolder,
            permissionMode: settings.PermissionMode,
            contextMode: CliContextModes.Shared,
            executionEngine: CliExecutionEngines.Car);

        Assert.Null(error);
        Assert.NotNull(started);
        return new StartedRun(
            service,
            spawner,
            jobKey,
            resultsDirectory,
            rulesPath,
            capturePath,
            output,
            events,
            finished.Task);
    }

    private GenericCliExecutionService BuildService(string cli, string rulesPath)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root,
                ["AgentRules:CorePath"] = rulesPath,
                ["CodexCli:Model"] = "gpt-5.5",
                ["CleanContext:Root"] = Path.Combine(_root, "clean-context"),
            })
            .Build();
        var modelRegistry = new CliModelRegistry();

        if (string.Equals(cli, CliTypes.Codex, StringComparison.OrdinalIgnoreCase))
        {
            return GenericCliExecutionService.ForCodex(
                NullLogger<GenericCliExecutionService>.Instance,
                configuration,
                new CodexModelDiscovery(NullLogger<CodexModelDiscovery>.Instance, configuration),
                new CliUsageParserRegistry([new CodexUsageParser()]),
                modelRegistry);
        }

        return GenericCliExecutionService.ForClaude(
            NullLogger<GenericCliExecutionService>.Instance,
            configuration,
            new CliUsageParserRegistry([new ClaudeUsageParser()]),
            modelRegistry);
    }

    private sealed class FixtureSpawner(
        string fixturePath,
        IReadOnlyDictionary<string, string> fakeEnvironment) : ICliProcessSpawner
    {
        public IReadOnlyList<string>? PreparedArgv { get; private set; }
        public IReadOnlyDictionary<string, string>? PreparedEnvironment { get; private set; }
        public Process? SpawnedProcess { get; private set; }

        public CliSpawn Spawn(ProcessStartInfo startInfo)
        {
            PreparedArgv = startInfo.ArgumentList.ToList();
            PreparedEnvironment = startInfo.Environment
                .Where(pair => pair.Value != null)
                .ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value!,
                    StringComparer.OrdinalIgnoreCase);

            foreach (var pair in fakeEnvironment)
                startInfo.Environment[pair.Key] = pair.Value;
            if (!startInfo.RedirectStandardInput)
                startInfo.Environment["FAKE_CLI_NO_STDIN"] = "1";

            startInfo.FileName = "node";
            startInfo.ArgumentList.Clear();
            startInfo.ArgumentList.Add(FakeCliPath());
            startInfo.ArgumentList.Add(fixturePath);

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.Start();
            SpawnedProcess = process;
            return new CliSpawn(
                process,
                startInfo.RedirectStandardInput ? process.StandardInput.BaseStream : Stream.Null,
                process.StandardOutput,
                process.StandardError);
        }
    }

    private sealed record StartedRun(
        GenericCliExecutionService Service,
        FixtureSpawner Spawner,
        string JobKey,
        string ResultsDirectory,
        string RulesPath,
        string CapturePath,
        ConcurrentQueue<CliOutputLine> Output,
        ConcurrentQueue<CliRunEvent> Events,
        Task<CliExecution> Finished);

    private sealed record RunSettings
    {
        public string Prompt { get; init; } = "Do the recorded fixture task.";
        public string? Model { get; init; }
        public string? ThinkingLevel { get; init; }
        public string? PermissionMode { get; init; }
        public string? SessionName { get; init; }
        public bool ResumeSession { get; init; }
        public string? JobKey { get; init; }
        public IReadOnlyDictionary<string, string>? FakeEnvironment { get; init; }
    }

    private sealed record Fixture(
        string Path,
        string Cli,
        string Form,
        int ExitCode,
        string StdOut,
        string StdErr,
        string ExpectedStatus,
        string ExpectedOutcome)
    {
        public static Fixture Load(string name)
        {
            var path = CliCaptureFixtureLocator.Resolve(RepoRoot(), name);
            Assert.True(File.Exists(path), $"fixture not found: {path}");

            JsonElement metadata = default;
            var sawMetadata = false;
            var stdout = new List<string>();
            var stderr = new List<string>();
            foreach (var line in File.ReadAllText(path).Replace("\r\n", "\n").Split('\n'))
            {
                if (line.Length == 0) continue;
                if (line.StartsWith("#!", StringComparison.Ordinal))
                {
                    metadata = JsonDocument.Parse(line[2..].Trim()).RootElement.Clone();
                    sawMetadata = true;
                    continue;
                }
                if (line.StartsWith('#')) continue;
                Assert.True(sawMetadata, $"fixture {name} does not start with metadata");
                if (line.StartsWith("@delay ", StringComparison.Ordinal)) continue;
                if (line.StartsWith("!stderr ", StringComparison.Ordinal))
                {
                    stderr.Add(line[8..]);
                    continue;
                }
                stdout.Add(line);
            }

            var (status, outcome) = ExpectedTerminal(name);
            return new Fixture(
                path,
                metadata.GetProperty("cli").GetString()!,
                metadata.GetProperty("form").GetString()!,
                metadata.TryGetProperty("exitCode", out var exitCode) ? exitCode.GetInt32() : 0,
                string.Join('\n', stdout),
                string.Join('\n', stderr),
                status,
                outcome);
        }

        private static (string Status, string Outcome) ExpectedTerminal(string name)
        {
            var fixtureName = System.IO.Path.GetFileName(name);
            if (fixtureName.StartsWith("p2-", StringComparison.Ordinal))
                return (RunStatuses.Completed, TerminalRunOutcomeKinds.NoOp);
            if (fixtureName.StartsWith("p3-", StringComparison.Ordinal))
                return (RunStatuses.Completed, TerminalRunOutcomeKinds.Blocked);
            if (fixtureName.StartsWith("p4-", StringComparison.Ordinal))
                return (RunStatuses.Completed, TerminalRunOutcomeKinds.NeedsInput);
            if (fixtureName.StartsWith("p5-", StringComparison.Ordinal))
                return (RunStatuses.Completed, TerminalRunOutcomeKinds.Success);
            if (fixtureName.StartsWith("p9-", StringComparison.Ordinal)
                || string.Equals(fixtureName, "p22-rate-limit.codex.fixture", StringComparison.Ordinal))
                return (RunStatuses.Failed, TerminalRunOutcomeKinds.Failed);
            return (RunStatuses.Completed, TerminalRunOutcomeKinds.Success);
        }
    }

    private static void AssertOption(IReadOnlyList<string> argv, string option, string value)
    {
        var index = IndexOf(argv, option);
        Assert.True(index >= 0, $"option '{option}' was not present in: {string.Join(' ', argv)}");
        Assert.True(index + 1 < argv.Count, $"option '{option}' has no value");
        Assert.Equal(value, argv[index + 1]);
    }

    private static int IndexOf(IReadOnlyList<string> values, string value)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (string.Equals(values[i], value, StringComparison.Ordinal)) return i;
        }
        return -1;
    }

    private static string JoinStream(IEnumerable<CliOutputLine> lines, string stream)
        => string.Join('\n', lines
            .Where(line => string.Equals(line.Stream, stream, StringComparison.OrdinalIgnoreCase))
            .Select(line => line.Text));

    private static string Normalize(string text)
        => text.Replace("\r\n", "\n").TrimEnd('\n');

    private static async Task<JsonDocument> ReadCaptureAsync(string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!File.Exists(path) && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.True(File.Exists(path), $"fake CLI capture was not written: {path}");
        return JsonDocument.Parse(await File.ReadAllTextAsync(path));
    }

    private static void RequireNode()
        => Skip.IfNot(NodeAvailable(), "node is not on PATH; backend CAR fixture replay requires Node.js.");

    private static bool NodeAvailable()
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
            if (probe == null || !probe.WaitForExit(8_000)) return false;
            return probe.ExitCode == 0;
        }
        catch
        {
            return false;
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

        throw new InvalidOperationException("agent-taskboard.sln was not found above the test source or output directory.");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // A just-killed fixture process can still be unwinding on Windows.
        }
    }
}
