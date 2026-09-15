using System.Text.Json;
using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// T0b (CAR migration plan §3 T0b / §7 AP3) — the claim's execution spec decides
/// the CLI invocation, and <c>RUNNER_CLI_*</c> is what fills the gaps it leaves.
/// These tests pin both directions plus the persistence contract of the detached
/// worker's <c>spec.json</c>: a worker started before T0b must keep running after
/// a daemon upgrade, which is what makes a hot runner deploy legal
/// (<c>KillMode=process</c> leaves live workers behind).
/// </summary>
public sealed class RunSpecInvocationTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "runner-run-spec-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Claim_without_a_spec_keeps_the_configured_binary_and_args()
    {
        var invocation = AgentCliProcess.Resolve(Options("claude", "-p --verbose"), runSpec: null);

        Assert.Equal("claude", invocation.FileName);
        Assert.Equal(["-p", "--verbose"], invocation.Arguments);
        Assert.Equal(AgentCliProcess.ClaudeCli, invocation.CliType);
        Assert.Null(invocation.Model);
        Assert.Null(invocation.ThinkingLevel);
        Assert.False(invocation.SpecApplied);
    }

    [Fact]
    public void Card_model_and_thinking_level_are_appended_to_the_configured_claude_args()
    {
        var invocation = AgentCliProcess.Resolve(
            Options("claude", "-p"),
            new RunSpecDto("claude", "claude-opus-4-8", "max", "yolo", "shared"));

        Assert.Equal("claude", invocation.FileName);
        Assert.Equal(["-p", "--model", "claude-opus-4-8", "--effort", "max"], invocation.Arguments);
        Assert.True(invocation.SpecApplied);
        Assert.Null(invocation.Note);
    }

    [Fact]
    public void Model_without_a_thinking_level_selects_only_the_model()
    {
        var invocation = AgentCliProcess.Resolve(
            Options("claude", "-p"),
            new RunSpecDto("claude", "claude-sonnet-4-6"));

        Assert.Equal(["-p", "--model", "claude-sonnet-4-6"], invocation.Arguments);
        Assert.True(invocation.SpecApplied);
    }

    [Fact]
    public void Card_routed_to_codex_uses_the_codex_binary_and_its_minimal_headless_form()
    {
        // RUNNER_CLI_ARGS describes claude ("-p"); it would be wrong for codex,
        // so the resolved CLI brings its own base form and reads the prompt from
        // stdin via the "-" positional, exactly like the local codex path.
        var invocation = AgentCliProcess.Resolve(
            Options("claude", "-p"),
            new RunSpecDto("codex", "gpt-5.6-codex", "high"));

        Assert.Equal("codex", invocation.FileName);
        Assert.Equal(
            ["exec", "--experimental-json", "-m", "gpt-5.6-codex", "-c", "model_reasoning_effort=\"high\"", "-"],
            invocation.Arguments);
        Assert.Equal(AgentCliProcess.CodexCli, invocation.CliType);
    }

    [Fact]
    public void Codex_primary_host_routes_a_claude_card_to_its_claude_binary()
    {
        var options = Options(
            "codex",
            "exec --experimental-json",
            claudeCliBin: "/usr/local/bin/claude-card-cli");

        var invocation = AgentCliProcess.Resolve(
            options,
            new RunSpecDto("claude", "claude-opus-5", "high"));

        Assert.Equal("/usr/local/bin/claude-card-cli", invocation.FileName);
        Assert.Equal(AgentCliProcess.ClaudeCli, invocation.CliType);
        Assert.Equal(
            ["-p", "--model", "claude-opus-5", "--effort", "high"],
            invocation.Arguments);
        Assert.Equal("claude-opus-5", invocation.Model);
        Assert.Null(invocation.Note);
    }

    [Fact]
    public void A_cli_this_host_has_no_binary_for_keeps_the_configured_one_and_says_so()
    {
        // The host runs codex only. A claude card must not silently spawn a
        // binary the operator never configured. The claude model and effort
        // selectors must not cross-apply to codex; the host's configured args
        // and default model win, and the mismatch is stated instead.
        var options = Options(
            "codex",
            "exec --experimental-json",
            claudeCliBin: string.Empty);
        var invocation = AgentCliProcess.Resolve(
            options,
            new RunSpecDto("claude", "claude-opus-5", "max"));

        Assert.Equal("codex", invocation.FileName);
        Assert.Equal(AgentCliProcess.CodexCli, invocation.CliType);
        Assert.Equal(["exec", "--experimental-json", "-"], invocation.Arguments);
        Assert.DoesNotContain("-m", invocation.Arguments);
        Assert.DoesNotContain("--model", invocation.Arguments);
        Assert.DoesNotContain("-c", invocation.Arguments);
        Assert.DoesNotContain("--effort", invocation.Arguments);
        Assert.DoesNotContain("claude-opus-5", invocation.Arguments);
        Assert.Null(invocation.Model);
        Assert.Null(invocation.ThinkingLevel);
        Assert.Equal("card-cli-fallback(model-pins-dropped)", invocation.Source);
        Assert.NotNull(invocation.Note);
        Assert.Contains("claude", invocation.Note!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_cli_id_never_overrides_the_configured_binary()
    {
        var invocation = AgentCliProcess.Resolve(
            Options("claude", "-p"),
            new RunSpecDto("kloude", "claude-opus-4-8"));

        Assert.Equal("claude", invocation.FileName);
        Assert.Equal(["-p", "--model", "claude-opus-4-8"], invocation.Arguments);
        Assert.Null(AgentCliProcess.NormalizeCliType("kloude"));
    }

    [Fact]
    public void Resume_args_stay_the_base_form_and_still_carry_the_cards_model()
    {
        // The bounded same-session resume replaces the base args with
        // RUNNER_CLI_RESUME_ARGS. That template carries the resume handshake
        // only, so the card's selection has to survive the second attempt.
        var invocation = AgentCliProcess.Resolve(
            Options("claude", "-p"),
            new RunSpecDto("claude", "claude-opus-4-8", "high"),
            argsOverride: ["-p", "-r", "session-42"]);

        Assert.Equal(
            ["-p", "-r", "session-42", "--model", "claude-opus-4-8", "--effort", "high"],
            invocation.Arguments);
    }

    [Fact]
    public void Worker_spec_carries_the_run_spec_through_the_persisted_file()
    {
        Directory.CreateDirectory(_root);
        var specPath = Path.Combine(_root, "spec.json");
        var built = DurableAgentProcess.BuildSpec(
            Options("claude", "-p"),
            _root,
            "do the thing",
            Path.Combine(_root, "results"),
            argsOverride: null,
            runSpec: new RunSpecDto("claude", "claude-opus-4-8", "max", "yolo", "shared"),
            cleanContextKey: "AGT-SPEC-1");
        File.WriteAllText(specPath, JsonSerializer.Serialize(built, Json));

        var reloaded = DurableAgentProcess.ReadSpec(specPath);

        Assert.Equal("claude", reloaded.FileName);
        Assert.Equal(["-p", "--model", "claude-opus-4-8", "--effort", "max"], reloaded.Arguments);
        Assert.Equal("claude", reloaded.CliType);
        Assert.Equal("claude-opus-4-8", reloaded.Model);
        Assert.Equal("max", reloaded.ThinkingLevel);
        // Transported as evidence only: the runner does not build flags from
        // these two yet (permission injection and clean context are T1).
        Assert.Equal("yolo", reloaded.PermissionMode);
        Assert.Equal("shared", reloaded.ContextMode);
        Assert.Equal("AGT-SPEC-1", reloaded.CleanContextKey);
        Assert.DoesNotContain("--dangerously-skip-permissions", reloaded.Arguments);
    }

    [Fact]
    public void A_worker_spec_written_before_the_run_spec_existed_still_loads_and_runs()
    {
        Directory.CreateDirectory(_root);
        var specPath = Path.Combine(_root, "legacy-spec.json");
        File.WriteAllText(specPath, """
            {
              "fileName": "claude",
              "arguments": ["-p"],
              "workingDirectory": "/srv/work/AGT-1",
              "prompt": "do the thing",
              "resultsDirectory": "/srv/work/AGT-1/results",
              "timeoutSeconds": 3600
            }
            """);

        var reloaded = DurableAgentProcess.ReadSpec(specPath);

        Assert.Equal("claude", reloaded.FileName);
        Assert.Equal(["-p"], reloaded.Arguments);
        Assert.Equal(3600, reloaded.TimeoutSeconds);
        Assert.Null(reloaded.CliType);
        Assert.Null(reloaded.Model);
        Assert.Null(reloaded.ThinkingLevel);
        Assert.Null(reloaded.PermissionMode);
        Assert.Null(reloaded.ContextMode);
    }

    [Fact]
    public void The_worker_spec_carries_the_preparation_cache_binding_into_the_coding_run()
    {
        // TE-52: the agent itself runs this repository's build, test and lint
        // commands. The cache locations repository preparation restored into
        // therefore have to reach the worker, and a resumed attempt of the same
        // run must read the same binding back from the first attempt's spec.
        var workerDirectory = Path.Combine(_root, "worker");
        Directory.CreateDirectory(workerDirectory);
        var specPath = Path.Combine(workerDirectory, "spec.json");
        var binding = new Dictionary<string, string>
        {
            ["NUGET_PACKAGES"] = "/srv/cache/.runs/abc/nuget",
            ["NPM_CONFIG_CACHE"] = "/srv/cache/.runs/abc/npm",
        };
        var built = DurableAgentProcess.BuildSpec(
            Options("claude", "-p"),
            _root,
            "do the thing",
            Path.Combine(_root, "results"),
            environment: binding);
        File.WriteAllText(specPath, JsonSerializer.Serialize(built, Json));

        var reloaded = DurableAgentProcess.ReadSpec(specPath);

        Assert.Equal(binding, reloaded.Environment);
        Assert.Equal(binding, DurableAgentProcess.TryReadEnvironment(workerDirectory));
    }

    [Fact]
    public void A_worker_spec_written_before_the_cache_binding_existed_still_loads()
    {
        Directory.CreateDirectory(_root);
        var specPath = Path.Combine(_root, "pre-binding-spec.json");
        File.WriteAllText(specPath, """
            {
              "fileName": "claude",
              "arguments": ["-p"],
              "workingDirectory": "/srv/work/AGT-1",
              "prompt": "do the thing",
              "resultsDirectory": "/srv/work/AGT-1/results",
              "timeoutSeconds": 3600
            }
            """);

        var reloaded = DurableAgentProcess.ReadSpec(specPath);

        Assert.Null(reloaded.Environment);
        Assert.Null(DurableAgentProcess.TryReadEnvironment(Path.Combine(_root, "missing-worker")));
    }

    [Fact]
    public void The_run_spec_with_prompt_enrichment_survives_the_persisted_daemon_slot()
    {
        var stateRoot = Path.Combine(_root, "state");
        var store = new RunnerStateStore(stateRoot);
        var lease = Lease("AGT-SPEC-SLOT");
        const string enrichment = "## Prompt enrichment\n\n- Apply the project style guide.";
        var spec = new RunSpecDto(
            "codex",
            "gpt-5.6-codex",
            "high",
            "yolo",
            "shared",
            enrichment);

        store.Create(
            lease.TaskKey,
            lease,
            Path.Combine(_root, "worktree"),
            runSpec: spec);
        var reloaded = Assert.Single(new RunnerStateStore(stateRoot).LoadAll());

        Assert.Equal(spec, reloaded.RunSpec);
        Assert.Equal(enrichment, reloaded.RunSpec?.ModeFraming);
    }

    [Fact]
    public void A_daemon_slot_written_before_the_run_spec_existed_loads_without_one()
    {
        var stateRoot = Path.Combine(_root, "legacy-state");
        var store = new RunnerStateStore(stateRoot);
        var lease = Lease("AGT-LEGACY-SLOT");
        store.Create(lease.TaskKey, lease, Path.Combine(_root, "worktree"));

        var reloaded = Assert.Single(new RunnerStateStore(stateRoot).LoadAll());

        Assert.Null(reloaded.RunSpec);
        Assert.Equal("AGT-LEGACY-SLOT", reloaded.TaskKey);
    }

    private static RunnerOptions Options(
        string cliBin,
        string cliArgs,
        string? claudeCliBin = null) => new()
    {
        ServerUrl = "http://localhost",
        RunnerId = "runner-run-spec-test",
        RunnerName = "runner-run-spec-test",
        Hostname = "test-host",
        BackendName = "test",
        WorkDir = Path.GetTempPath(),
        BaseBranch = "main",
        CliBin = cliBin,
        ClaudeCliBin = claudeCliBin ?? "claude",
        CliArgs = cliArgs,
        TtlSeconds = 120,
        HeartbeatSeconds = 30,
        RunTimeoutSeconds = 3600,
        HostMaxParallelism = 1,
        PollSeconds = 1,
    };

    private static RunLeaseInfoDto Lease(string taskKey) => new(
        taskKey,
        "runner-run-spec-test",
        "runner-run-spec-test",
        "test-host",
        Environment.ProcessId,
        "test",
        $"lease-{taskKey}",
        1,
        DateTime.UtcNow,
        DateTime.UtcNow.AddMinutes(2));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }
}
