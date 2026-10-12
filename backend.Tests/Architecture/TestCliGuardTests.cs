using System.Diagnostics;
using AgentStudio.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests.Architecture;

/// <summary>
/// Holds the test-host CLI guard in place (AGT-3087).
///
/// On agent-runner-01 this suite started the real npm-installed <c>claude</c>
/// about 90 times a minute: every service built without <c>ClaudeCli:Path</c>
/// falls back to the bare name, and the dominant caller was the three-attempt
/// Result finalization behind every fake run that reaches review. The calls
/// only failed because the host login had expired, and each one left a
/// transcript folder under <c>~/.claude/projects</c>. <see cref="TestCliGuard"/>
/// puts a stub first on the <c>PATH</c>; these tests prove the bare name lands
/// on it, that <c>--version</c> keeps answering, and that no transcript carries
/// this host's suite root.
///
/// Other tests hit the guard in parallel, so every assertion filters on its own
/// working directory or a unique marker argument, never on a global count.
/// </summary>
public sealed class TestCliGuardTests
{
    private static readonly TimeSpan ProcessBound = TimeSpan.FromMinutes(2);

    [Fact]
    public async Task Bare_claude_runs_the_guard_stub()
    {
        Assert.True(TestCliGuard.IsInstalled, "TestTempRoot.Redirect() did not install the CLI guard.");
        using var workspace = new TempWorkspace("cli-guard-probe");
        var marker = "--guard-probe-" + Guid.NewGuid().ToString("N");

        var resolved = GenericCliExecutionService.ResolveExecutable("claude");
        if (OperatingSystem.IsWindows())
        {
            Assert.EndsWith(@"cli-guard\claude.cmd", resolved, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(Path.GetFullPath(TestTempRoot.SuiteRoot), Path.GetFullPath(resolved), StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            var onPath = Executables.FindOnPath("claude");
            Assert.NotNull(onPath);
            Assert.StartsWith(Path.GetFullPath(TestTempRoot.SuiteRoot), Path.GetFullPath(onPath), StringComparison.Ordinal);
        }

        var run = await RunAsync(resolved, workspace.Path, stdin: "a prompt the guard must drain", "-p", marker);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains(TestCliGuard.RefusalMessage("claude"), run.Stderr, StringComparison.Ordinal);
        var logged = Assert.Single(TestCliGuard.ReadInvocations(), invocation => invocation.Argv.Contains(marker));
        Assert.Equal("claude", logged.Cli);
        Assert.True(logged.RanIn(workspace.Path), $"Guard logged cwd '{logged.WorkingDirectory}', expected '{workspace.Path}'.");
        Assert.Contains("-p", logged.Argv);
    }

    [Fact]
    public async Task Version_probe_succeeds_without_logging()
    {
        using var workspace = new TempWorkspace("cli-guard-version");

        var run = await RunAsync(
            GenericCliExecutionService.ResolveExecutable("claude"),
            workspace.Path,
            stdin: null,
            "--version");

        Assert.Equal(0, run.ExitCode);
        var line = run.Stdout.Trim();
        Assert.Equal(TestCliGuard.VersionLine, line);
        Assert.DoesNotContain(TestCliGuard.ReadInvocations(), invocation => invocation.RanIn(workspace.Path));

        var version = SemanticCliVersion.FromCliOutput(line);
        Assert.All(
            ModelMetadataRegistry.ForVendor("anthropic").Where(model => model.MinimumCliVersion is not null),
            model => Assert.True(
                SemanticCliVersion.IsAtLeast(version, model.MinimumCliVersion),
                $"Guard version '{version}' is below {model.Id}'s minimum {model.MinimumCliVersion}; "
                + "model admission would reject or disable it in every test."));
    }

    [Fact]
    public async Task Summary_finalization_without_a_configured_path_hits_the_guard()
    {
        using var workspace = new TempWorkspace("cli-guard-summary");
        var service = new SummaryGenerationService(
            NullLogger<SummaryGenerationService>.Instance,
            UnconfiguredClaude(workspace));

        var outcome = await service.FinalizeAsync(await TaskIn(workspace));

        Assert.Equal(TaskSummaryStatus.Degraded, outcome.Status);
        Assert.Equal(3, outcome.Attempt);
        var calls = TestCliGuard.ReadInvocations()
            .Where(invocation => invocation.RanIn(workspace.Path))
            .ToList();
        Assert.Equal(3, calls.Count);
        Assert.All(calls, call =>
        {
            Assert.Equal("claude", call.Cli);
            Assert.Contains("-p", call.Argv);
            Assert.Contains("--output-format json", call.Arguments, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task ClaudeOneShot_without_a_configured_path_skips_the_npm_repair()
    {
        using var workspace = new TempWorkspace("cli-guard-oneshot");
        var logger = new CapturingLogger<ClaudeOneShot>();
        var oneShot = NewOneShot(new ConfigurationBuilder().Build(), logger);

        var result = await oneShot.RunAsync(OneShotRequest(workspace));

        Assert.DoesNotContain(logger.Messages, message => message.Contains("claude --version failed pre-OneShot", StringComparison.Ordinal));
        Assert.False(result.Ok);
        Assert.Single(TestCliGuard.ReadInvocations(), invocation => invocation.RanIn(workspace.Path));
    }

    [Fact]
    public async Task Explicit_cli_path_bypasses_the_guard()
    {
        using var workspace = new TempWorkspace("cli-guard-explicit");
        const string reply = "explicit stub reply";
        var stub = await StubCli.WriteAsync(workspace.Directory("bin"), "claude-explicit", reply);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ClaudeCli:Path"] = stub })
            .Build();
        var oneShot = NewOneShot(configuration, new CapturingLogger<ClaudeOneShot>());

        var result = await oneShot.RunAsync(OneShotRequest(workspace));

        Assert.True(result.Ok, $"Explicit stub did not run: exit {result.ExitCode}, stderr '{result.Stderr}'.");
        Assert.Contains(reply, result.ParsedText, StringComparison.Ordinal);
        Assert.DoesNotContain(TestCliGuard.ReadInvocations(), invocation => invocation.RanIn(workspace.Path));
    }

    /// <summary>
    /// The in-suite tripwire. The real CLI names its transcript folder after the
    /// encoded cwd, and every test cwd lives below the suite root
    /// <c>ats-&lt;token&gt;</c>, so a folder carrying that name means some call in
    /// this process got past the guard. It runs one guarded finalization of its
    /// own first, so it never passes vacuously when it happens to run early.
    /// </summary>
    [Fact]
    public async Task No_claude_transcript_carries_the_suite_root_token()
    {
        using var workspace = new TempWorkspace("cli-guard-tripwire");
        var configuration = new ConfigurationBuilder()
            .AddConfiguration(UnconfiguredClaude(workspace))
            .AddInMemoryCollection(new Dictionary<string, string?> { ["SummaryGeneration:FinalizationMaxAttempts"] = "1" })
            .Build();
        var service = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, configuration);
        var outcome = await service.FinalizeAsync(await TaskIn(workspace));
        Assert.Equal(TaskSummaryStatus.Degraded, outcome.Status);
        Assert.Single(TestCliGuard.ReadInvocations(), invocation => invocation.RanIn(workspace.Path));

        var token = Path.GetFileName(Path.TrimEndingDirectorySeparator(TestTempRoot.SuiteRoot));
        Assert.True(TempRootLengthPolicy.IsSuiteRootName(token), $"Unexpected suite root name '{token}'.");
        var home = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrWhiteSpace(home)) return;
        var projects = Path.Combine(home, ".claude", "projects");
        if (!Directory.Exists(projects)) return;

        var leaked = Directory.EnumerateDirectories(projects)
            .Select(Path.GetFileName)
            .Where(name => name is not null && name.Contains(token, StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.True(
            leaked.Count == 0,
            $"{leaked.Count} Claude transcript folder(s) under '{projects}' carry this test host's suite root "
            + $"'{token}', so a test started the real CLI past the guard:\n  " + string.Join("\n  ", leaked.Take(25)));
    }

    private static IConfiguration UnconfiguredClaude(TempWorkspace workspace)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PromptTemplates:RuntimePath"] = Path.Combine(Directory.GetCurrentDirectory(), "prompts", "runtime"),
                // Keeps the prompt service off the user-level override directory.
                ["TaskRepository"] = workspace.Path,
            })
            .Build();

    private static async Task<TaskInfo> TaskIn(TempWorkspace workspace)
    {
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Directory("logs"), "cli-output.log"),
            "[assistant/final] Fake run completed.\n[assistant/final] [[TASK_DONE]]\n");
        return new TaskInfo
        {
            Id = "cli-guard-" + Guid.NewGuid().ToString("N")[..8],
            TaskKey = "GUARD-" + Guid.NewGuid().ToString("N")[..8],
            Title = "CLI guard probe",
            TaskType = "feature",
            Mode = TaskModes.Coding,
            State = TaskStates.Progress,
            FolderPath = workspace.Path,
            WatchPath = workspace.Path,
            ProjectName = "cli-guard",
        };
    }

    private static ClaudeOneShot NewOneShot(IConfiguration configuration, ILogger<ClaudeOneShot> logger)
        => new(configuration, logger, new CliUsageParserRegistry([new ClaudeUsageParser()]), new CliModelRegistry());

    private static CliOneShotRequest OneShotRequest(TempWorkspace workspace)
        => new(CliTypes.Claude, ModelFamilyResolver.Resolve(ModelFamilies.ClaudeHaiku), "Say hello.")
        {
            WorkingDirectory = workspace.Path,
            Timeout = ProcessBound,
            RecordUsage = false,
        };

    private static async Task<ProcessRun> RunAsync(string fileName, string workingDirectory, string? stdin, params string[] args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = stdin is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) startInfo.ArgumentList.Add(arg);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Process.Start returned null for '{fileName}'.");
        if (stdin is not null)
        {
            await process.StandardInput.WriteLineAsync(stdin);
            process.StandardInput.Close();
        }
        using var bound = new CancellationTokenSource(ProcessBound);
        var stdout = process.StandardOutput.ReadToEndAsync(bound.Token);
        var stderr = process.StandardError.ReadToEndAsync(bound.Token);
        await process.WaitForExitAsync(bound.Token);
        return new ProcessRun(process.ExitCode, await stdout, await stderr);
    }

    private sealed record ProcessRun(int ExitCode, string Stdout, string Stderr);

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages
        {
            get { lock (_messages) return _messages.ToList(); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_messages) _messages.Add(formatter(state, exception));
        }
    }
}
