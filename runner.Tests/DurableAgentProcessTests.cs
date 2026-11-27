using System.Diagnostics;
using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class DurableAgentProcessTests
{
    private static readonly string[] CredentialNames =
    [
        "CLAUDE_CODE_OAUTH_TOKEN", "ANTHROPIC_API_KEY", "OPENAI_API_KEY", "CODEX_API_KEY",
        "GEMINI_API_KEY", "GOOGLE_API_KEY", "GH_TOKEN", "GITHUB_TOKEN",
        "AWS_ACCESS_KEY_ID", "AWS_SECRET_ACCESS_KEY", "AWS_SESSION_TOKEN", "AWS_PROFILE",
        "AZURE_CLIENT_SECRET", "GCP_ACCESS_TOKEN", "GOOGLE_APPLICATION_CREDENTIALS",
        "SSH_AUTH_SOCK", "SSH_AGENT_PID", "KUBECONFIG", "DOCKER_AUTH_CONFIG",
        "RUNNER_AUTH_TOKEN", "CONNECTOR_SESSION_SECRET", "UNRELATED_FUTURE_SECRET",
    ];

    private static Dictionary<string, string?> FixtureEnvironment()
    {
        var environment = CredentialNames.ToDictionary(name => name,
            _ => (string?)("fixture-" + Guid.NewGuid().ToString("N")), StringComparer.Ordinal);
        foreach (var name in new[] { "PATH", "HOME", "TMPDIR", "NUGET_PACKAGES",
                     "NPM_CONFIG_CACHE", "PLAYWRIGHT_BROWSERS_PATH", "DOTNET_ROOT", "XDG_CACHE_HOME" })
            environment[name] = "/fixture/" + name;
        return environment;
    }

    [Theory]
    [InlineData("claude", "CLAUDE_CODE_OAUTH_TOKEN")]
    [InlineData("codex", "CODEX_API_KEY")]
    [InlineData(null, null)]
    [InlineData("unsupported", null)]
    public void Coding_worker_keeps_only_runtime_and_selected_auth(string? cli, string? authName)
    {
        var environment = FixtureEnvironment();
        var expectedAuth = authName is null ? null : environment[authName];
        using var candidate = ProviderAuthEnvironment.UseCandidateClaudeToken(
            environment["CLAUDE_CODE_OAUTH_TOKEN"]!);
        DurableAgentProcess.ApplyWorkerEnvironment(environment, cli);
        foreach (var name in CredentialNames)
            if (name == authName) Assert.Equal(expectedAuth, environment[name]);
            else Assert.False(environment.ContainsKey(name), $"Unexpected variable: {name}");
        Assert.Equal("/fixture/PATH", environment["PATH"]);
        Assert.Equal("/fixture/HOME", environment["HOME"]);
        Assert.Equal("/fixture/NUGET_PACKAGES", environment["NUGET_PACKAGES"]);
        Assert.Equal("/fixture/NPM_CONFIG_CACHE", environment["NPM_CONFIG_CACHE"]);
        Assert.Equal("/fixture/PLAYWRIGHT_BROWSERS_PATH", environment["PLAYWRIGHT_BROWSERS_PATH"]);
        Assert.True(WorkerBuildServerHygiene.IsFenced(environment));
    }

    [Fact]
    public void Claude_api_key_is_the_fallback_when_no_oauth_token_is_supplied()
    {
        var environment = FixtureEnvironment();
        environment.Remove("CLAUDE_CODE_OAUTH_TOKEN");
        var expected = environment["ANTHROPIC_API_KEY"];
        DurableAgentProcess.ApplyWorkerEnvironment(environment, "claude");
        Assert.Equal(expected, environment["ANTHROPIC_API_KEY"]);
        Assert.False(environment.ContainsKey("OPENAI_API_KEY"));
    }

    [Fact]
    public void Scoped_candidate_wins_only_for_claude_workers()
    {
        var candidate = "fixture-" + Guid.NewGuid().ToString("N");
        using var scope = ProviderAuthEnvironment.UseCandidateClaudeToken(candidate);
        var claude = FixtureEnvironment();
        DurableAgentProcess.ApplyWorkerEnvironment(claude, "claude");
        Assert.Equal(candidate, claude["CLAUDE_CODE_OAUTH_TOKEN"]);
        Assert.False(claude.ContainsKey("ANTHROPIC_API_KEY"));
        var codex = FixtureEnvironment();
        DurableAgentProcess.ApplyWorkerEnvironment(codex, "codex");
        Assert.False(codex.ContainsKey("CLAUDE_CODE_OAUTH_TOKEN"));
    }

    [Theory]
    [InlineData("claude", "CLAUDE_CONFIG_DIR", "CODEX_HOME")]
    [InlineData("codex", "CODEX_HOME", "CLAUDE_CONFIG_DIR")]
    public void Only_selected_provider_config_home_survives(string cli, string selected, string unrelated)
    {
        var environment = FixtureEnvironment();
        environment[selected] = "/fixture/selected";
        environment[unrelated] = "/fixture/unrelated";
        DurableAgentProcess.ApplyWorkerEnvironment(environment, cli);
        Assert.Equal("/fixture/selected", environment[selected]);
        Assert.False(environment.ContainsKey(unrelated));
    }

    [Theory]
    [InlineData("claude", "CLAUDE_CODE_OAUTH_TOKEN", false)]
    [InlineData("claude", "ANTHROPIC_API_KEY", false)]
    [InlineData("codex", "CODEX_API_KEY", false)]
    [InlineData("codex", "OPENAI_API_KEY", false)]
    [InlineData("claude", "CLAUDE_CODE_OAUTH_TOKEN", true)]
    [InlineData("claude", "ANTHROPIC_API_KEY", true)]
    [InlineData("codex", "CODEX_API_KEY", true)]
    [InlineData("codex", "OPENAI_API_KEY", true)]
    [Trait("Category", "MachineBound")]
    [Trait("Category", "ReviewFlaky")]
    public async Task Spawned_process_observes_the_same_auth_boundary(string cli, string selected, bool car)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var runtime = new FixtureRuntime();
        var environment = FixtureEnvironment();
        runtime.ApplyTo(environment);
        if (selected == "ANTHROPIC_API_KEY") environment.Remove("CLAUDE_CODE_OAUTH_TOKEN");
        if (selected == "OPENAI_API_KEY") environment.Remove("CODEX_API_KEY");
        var expected = environment[selected];
        var start = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.Environment.Clear();
        foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
        start.ArgumentList.Add("-c");
        // Emit names only. The expected value is a generated fixture, never host auth.
        start.ArgumentList.Add(string.Join("\n", CredentialNames.Select(name =>
                $"[ \"${{{name}+x}}\" = x ] && printf '%s\\n' '{name}'"))
            + $"\n[ \"${selected}\" = '{expected}' ] && worker-tool");
        Process process;
        if (car)
            process = new CarWorkerExecution.CodingWorkerSpawner(cli).Spawn(start).Process;
        else
        {
            DurableAgentProcess.ApplyWorkerEnvironment(start.Environment, cli);
            process = Process.Start(start)!;
        }
        using (process)
        {
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var output = await process.StandardOutput.ReadToEndAsync(deadline.Token);
                await process.WaitForExitAsync(deadline.Token);
                Assert.Equal(0, process.ExitCode);
                Assert.Equal(selected, output.Trim());
                foreach (var cache in runtime.CachePaths)
                    Assert.Equal("cache-ok", File.ReadAllText(Path.Combine(cache, "probe")));
            }
            finally
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            }
        }
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    public void Serialized_preparation_environment_cannot_reintroduce_credentials(string cli)
    {
        var options = new RunnerOptions
        {
            ServerUrl = "http://localhost", RunnerId = "fixture", RunnerName = "fixture",
            Hostname = "fixture", BackendName = "fixture", WorkDir = Path.GetTempPath(),
            BaseBranch = "main", CliType = cli, CodexCliBin = "codex", ClaudeCliBin = "claude",
        };
        var environment = FixtureEnvironment().ToDictionary(pair => pair.Key, pair => pair.Value!);
        var spec = DurableAgentProcess.BuildSpec(options, Path.GetTempPath(), "fixture", Path.GetTempPath(),
            environment: environment);
        Assert.NotNull(spec.Environment);
        foreach (var name in CredentialNames) Assert.False(spec.Environment.ContainsKey(name), name);
        Assert.Equal(environment["NUGET_PACKAGES"], spec.Environment["NUGET_PACKAGES"]);
    }

    [Fact]
    public void Pinned_car_windows_spawn_contract_preserves_pipes_and_tree_stop()
    {
        var method = CarWorkerExecution.CodingWorkerSpawner.WindowsSpawnMethod;
        Assert.Equal(new[]
        {
            typeof(string), typeof(IReadOnlyList<string>), typeof(string),
            typeof(IReadOnlyDictionary<string, string>), typeof(bool), typeof(Microsoft.Extensions.Logging.ILogger),
        }, method.GetParameters().Select(parameter => parameter.ParameterType).ToArray());
        foreach (var (name, type) in new[]
        {
            ("Process", typeof(Process)), ("Stdin", typeof(FileStream)),
            ("Stdout", typeof(FileStream)), ("Stderr", typeof(FileStream)), ("KillTree", typeof(Action)),
        })
            Assert.Equal(type, method.ReturnType.GetProperty(name)?.PropertyType);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    public void Missing_environment_auth_keeps_native_login_available_without_inventing_keys(string cli)
    {
        var environment = new Dictionary<string, string?> { ["HOME"] = "/fixture/home" };
        DurableAgentProcess.ApplyWorkerEnvironment(environment, cli);
        foreach (var name in CredentialNames) Assert.False(environment.ContainsKey(name), name);
        Assert.Equal("/fixture/home", environment["HOME"]);
    }

    private sealed class FixtureRuntime : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "worker-environment-" + Guid.NewGuid().ToString("N"));
        private readonly string[] _cacheNames = ["NUGET_PACKAGES", "NPM_CONFIG_CACHE", "PLAYWRIGHT_BROWSERS_PATH"];
        internal string[] CachePaths => _cacheNames.Select(name => Path.Combine(_root, name)).ToArray();

        internal FixtureRuntime()
        {
            Directory.CreateDirectory(Path.Combine(_root, "bin"));
            foreach (var cache in CachePaths) Directory.CreateDirectory(cache);
            var tool = Path.Combine(_root, "bin", "worker-tool");
            File.WriteAllText(tool, """
                #!/bin/sh
                for cache in "$NUGET_PACKAGES" "$NPM_CONFIG_CACHE" "$PLAYWRIGHT_BROWSERS_PATH"; do
                    printf 'cache-ok' > "$cache/probe" || exit 11
                done
                """);
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        internal void ApplyTo(IDictionary<string, string?> environment)
        {
            environment["PATH"] = Path.Combine(_root, "bin");
            environment["HOME"] = _root;
            environment["TMPDIR"] = _root;
            foreach (var name in _cacheNames) environment[name] = Path.Combine(_root, name);
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Terminal_result_wins_when_worker_exits_between_discovery_checks()
    {
        var terminal = new DetachedJobResult(
            0,
            "before-restart\n[[TASK_DONE]]\n",
            "",
            false,
            DateTime.UtcNow);
        var resultReads = 0;

        var observation = DurableAgentProcess.InspectForReattach(
            () => ++resultReads == 1 ? null : terminal,
            () => (false, "process has exited"));

        Assert.False(observation.IsLive);
        Assert.Same(terminal, observation.Result);
        Assert.Equal("durable result ready", observation.Detail);
        Assert.Equal(2, resultReads);
    }
}
