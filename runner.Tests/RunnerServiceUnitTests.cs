using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using AgentStudio.TestSupport;
using Xunit;

namespace AgentRunner.Tests;

public sealed class RunnerServiceUnitTests
{
    [Theory]
    [InlineData("deploy/systemd/agent-host.service")]
    [InlineData("scripts/remote-runner-onboard.sh")]
    public void Installed_units_preserve_workers_and_bound_restart_loops(string relativePath)
    {
        var content = File.ReadAllText(Path.Combine(RepoRoot(), relativePath));

        Assert.Contains("KillSignal=SIGTERM", content);
        Assert.Contains("KillMode=process", content);
        Assert.Contains("StartLimitIntervalSec=300", content);
        Assert.Contains("StartLimitBurst=5", content);
        Assert.Contains("RestartSec=10s", content);
    }

    /// <summary>
    /// PrivateTmp=true tears the unit's private /tmp mount away on every
    /// restart, including from underneath a detached worker that KillMode=process
    /// deliberately left running (MSB1025 / SocketException (99) / NuGet mkdtemp
    /// ENOENT). A worker surviving the restart is worthless if its next build or
    /// test step cannot use /tmp.
    /// </summary>
    [Theory]
    [InlineData("deploy/systemd/agent-host.service")]
    [InlineData("scripts/remote-runner-onboard.sh")]
    [InlineData("deploy/agent-host/systemd/10-agent-runner-hardening.conf")]
    public void Installed_units_never_privatize_tmp_out_from_under_a_surviving_worker(
        string relativePath)
    {
        var lines = File.ReadAllLines(Path.Combine(RepoRoot(), relativePath))
            .Select(line => line.Trim())
            .ToArray();

        Assert.Contains("PrivateTmp=false", lines);
        Assert.DoesNotContain("PrivateTmp=true", lines);
    }

    [Fact]
    public void Review_unit_refuses_direct_manual_stop_through_a_review_only_drop_in()
    {
        var guardPath = Path.Combine(
            RepoRoot(),
            "deploy",
            "agent-host",
            "systemd",
            "20-agent-runner-review-restart-guard.conf");
        var guard = File.ReadAllLines(guardPath)
            .Select(line => line.Trim())
            .ToArray();
        var staticCodingUnit = File.ReadAllText(
            Path.Combine(RepoRoot(), "deploy", "systemd", "agent-host.service"));
        var migration = File.ReadAllText(
            Path.Combine(RepoRoot(), "scripts", "harden-agent-runner-host.sh"));
        var onboarding = File.ReadAllText(
            Path.Combine(RepoRoot(), "scripts", "remote-runner-onboard.sh"));

        Assert.Contains("[Unit]", guard);
        Assert.Contains("RefuseManualStop=true", guard);
        Assert.Contains("[Service]", guard);
        Assert.Contains("Restart=on-failure", guard);
        Assert.DoesNotContain("RefuseManualStop", staticCodingUnit, StringComparison.Ordinal);
        Assert.Contains("restart_guard_source", migration, StringComparison.Ordinal);
        Assert.Contains(
            "/etc/systemd/system/agent-runner-review.service.d/20-agent-runner-review-restart-guard.conf",
            migration,
            StringComparison.Ordinal);
        Assert.Contains("--property=RefuseManualStop --value", migration, StringComparison.Ordinal);
        Assert.Contains("--property=Restart --value", migration, StringComparison.Ordinal);
        Assert.Contains(
            "/etc/systemd/system/${service_name}.service.d/20-agent-runner-review-restart-guard.conf",
            onboarding,
            StringComparison.Ordinal);
        Assert.Contains(
            "Review service %s did not adopt RefuseManualStop=true",
            onboarding,
            StringComparison.Ordinal);
        Assert.Contains(
            "Review service %s did not adopt Restart=on-failure",
            onboarding,
            StringComparison.Ordinal);
        Assert.Contains(
            "--restart-guard --hold-admission --role review",
            onboarding,
            StringComparison.Ordinal);
        Assert.True(
            onboarding.IndexOf("--restart-guard --hold-admission", StringComparison.Ordinal)
            < onboarding.IndexOf(
                "systemctl kill --kill-whom=main --signal=SIGTERM",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Agent_host_unit_uses_the_atomic_current_release_path()
    {
        var content = File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "systemd", "agent-host.service"));

        Assert.Contains("ExecStart=/opt/agent-host/current/agent-host --poll", content);
        Assert.DoesNotContain("ExecStart=/opt/agent-host/agent-host --poll", content);
        Assert.Contains("Alias=agent-runner.service", content);
    }

    [Theory]
    [InlineData("deploy/systemd/agent-host.service")]
    [InlineData("scripts/remote-runner-onboard.sh")]
    public void Agent_host_units_load_the_shared_provider_auth_file_after_the_role_environment(
        string relativePath)
    {
        var content = File.ReadAllText(Path.Combine(RepoRoot(), relativePath));
        var roleEnvironment = content.IndexOf("EnvironmentFile=/etc/agent-runner/runner.env", StringComparison.Ordinal);
        if (relativePath.EndsWith("remote-runner-onboard.sh", StringComparison.Ordinal))
            roleEnvironment = content.IndexOf("EnvironmentFile=$env_file", StringComparison.Ordinal);
        var providerEnvironment = content.IndexOf(
            "EnvironmentFile=/etc/agent-runner/provider-auth.env",
            StringComparison.Ordinal);
        if (relativePath.EndsWith("remote-runner-onboard.sh", StringComparison.Ordinal))
            providerEnvironment = content.IndexOf("EnvironmentFile=$provider_auth_file", StringComparison.Ordinal);

        Assert.True(roleEnvironment >= 0);
        Assert.True(providerEnvironment > roleEnvironment);
        if (relativePath.EndsWith("remote-runner-onboard.sh", StringComparison.Ordinal))
            Assert.Contains("/proc/${main_pid}/environ", content);
    }

    [Fact]
    public void Static_and_managed_units_load_the_shared_provider_auth_file_last()
    {
        var staticUnit = File.ReadAllText(
            Path.Combine(RepoRoot(), "deploy", "systemd", "agent-host.service"));
        var onboarding = File.ReadAllText(
            Path.Combine(RepoRoot(), "scripts", "remote-runner-onboard.sh"));

        Assert.Contains(
            "EnvironmentFile=/etc/agent-runner/runner.env\n" +
            "# Shared, root-owned provider credentials.",
            staticUnit.ReplaceLineEndings("\n"));
        Assert.Contains(
            "EnvironmentFile=/etc/agent-runner/provider-auth.env",
            staticUnit);
        Assert.True(
            staticUnit.IndexOf(
                "EnvironmentFile=/etc/agent-runner/provider-auth.env",
                StringComparison.Ordinal) >
            staticUnit.IndexOf(
                "EnvironmentFile=/etc/agent-runner/runner.env",
                StringComparison.Ordinal));
        Assert.Contains(
            "EnvironmentFile=$env_file\n" +
            "# One shared provider credential file",
            onboarding.ReplaceLineEndings("\n"));
        Assert.Contains(
            "provider_auth_metadata=\"$(sudo stat -c '%U:%G:%a' \"$provider_auth_file\")\"",
            onboarding);
        Assert.True(
            onboarding.IndexOf(
                "EnvironmentFile=$provider_auth_file",
                StringComparison.Ordinal) >
            onboarding.IndexOf("EnvironmentFile=$env_file", StringComparison.Ordinal));
        Assert.Contains("root:agent:640", onboarding);
        Assert.DoesNotContain("provider_auth_tmp", onboarding);
        Assert.DoesNotContain("claude auth login", onboarding);
    }

    [Fact]
    public void Onboarding_installs_immutable_tool_releases_and_switches_current_atomically()
    {
        var content = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "remote-runner-onboard.sh"));

        Assert.Contains("releases_root=\"$tool_root/releases\"", content);
        Assert.Contains("dotnet tool install --tool-path \"$stage_root\"", content);
        Assert.Contains("ln -sfnT \"$release_root\" \"$tool_root/candidate\"", content);
        Assert.Contains("ln -sfnT \"$candidate_release\" \"$tool_root/current\"", content);
        Assert.Contains("ExecStart=$agent_host_root/current/$runner_command --poll", content);
        Assert.DoesNotContain("dotnet tool update --global", content);

        var stopped = content.IndexOf("((daemon_stopped == 1))", StringComparison.Ordinal);
        var reviewPromotion = content.IndexOf(
            "ln -sfnT \"$candidate_release\" \"$tool_root/current\"",
            stopped,
            StringComparison.Ordinal);
        var reviewStart = content.IndexOf(
            "sudo systemctl start \"$service_name\"",
            reviewPromotion,
            StringComparison.Ordinal);
        var codingPromotion = content.LastIndexOf(
            "ln -sfnT \"$candidate_release\" \"$tool_root/current\"",
            StringComparison.Ordinal);
        var codingRestart = content.IndexOf(
            "sudo systemctl restart \"$service_name\"",
            codingPromotion,
            StringComparison.Ordinal);

        Assert.True(stopped >= 0);
        Assert.True(reviewPromotion > stopped);
        Assert.True(reviewStart > reviewPromotion);
        Assert.True(codingPromotion > reviewStart);
        Assert.True(codingRestart > codingPromotion);
    }

    [Fact]
    public void Manual_runner_publish_uses_the_validated_root_promotion_boundary()
    {
        var content = File.ReadAllText(
            Path.Combine(RepoRoot(), "docs", "operations", "setup", "linux-runner-host.md"));

        Assert.Contains("dotnet publish runner/AgentRunner.csproj -c Release -o \"$staging_root\"", content);
        Assert.Contains("sudo /usr/local/sbin/agent-runner-deploy", content);
        Assert.DoesNotContain(
            "dotnet publish runner/AgentRunner.csproj -c Release -o /opt/agent-host",
            content);
        Assert.DoesNotContain(
            "sudo ln -sfnT \"$release_root\" /opt/agent-host/current",
            content);
    }

    [Fact]
    public void Onboarding_creates_the_legacy_publish_path_symlink()
    {
        var content = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "remote-runner-onboard.sh"));

        Assert.Contains("agent_host_root=\"/opt/agent-host\"", content);
        Assert.Contains("legacy_root=\"/opt/agent-runner\"", content);
        Assert.Contains("ln -sfnT \"$agent_host_root\" \"$legacy_root\"", content);
    }

    [Fact]
    public void Runner_project_publishes_the_agent_host_binary()
    {
        var content = File.ReadAllText(Path.Combine(RepoRoot(), "runner", "AgentRunner.csproj"));

        Assert.Contains("<AssemblyName>agent-host</AssemblyName>", content);
    }

    [Fact]
    public void Onboarding_places_restart_state_on_persistent_runner_storage()
    {
        var content = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "remote-runner-onboard.sh"));

        Assert.Contains("service_root=\"/var/lib/agent-runner\"", content);
        Assert.Contains("RUNNER_STATE_DIR=%s/state", content);
    }

    [Fact]
    public void Agent_runner_sudoers_enumerates_the_complete_bounded_config_allowlist()
    {
        var content = File.ReadAllText(
            Path.Combine(RepoRoot(), "deploy", "agent-host", "sudoers.d", "agent-runner"));

        Assert.DoesNotContain("NOPASSWD:ALL", content);
        Assert.DoesNotContain("NOPASSWD: ALL", content);
        Assert.DoesNotContain("*", content);
        Assert.Contains("/usr/local/sbin/agent-runner-deploy \"\"", content);
        Assert.Contains("/usr/local/sbin/agent-runner-deploy restart-review", content);
        Assert.Contains("/usr/bin/systemctl restart agent-runner.service", content);
        Assert.DoesNotContain("systemctl restart agent-runner-review.service", content);
        Assert.DoesNotContain("agent-runner-deploy --force", content);
        Assert.Equal(
            12,
            CountOccurrences(
                content,
                "/usr/local/sbin/agent-runner-deploy config "));
        Assert.DoesNotContain("RUNNER_MAX_PARALLELISM 0", content);
        Assert.DoesNotContain("RUNNER_MAX_PARALLELISM 7", content);
    }

    [Fact]
    public void Agent_runner_config_helper_owns_atomic_update_restart_audit_and_process_proof()
    {
        var helper = File.ReadAllText(
            Path.Combine(RepoRoot(), "deploy", "agent-host", "agent-runner-deploy"));
        var migration = File.ReadAllText(
            Path.Combine(RepoRoot(), "scripts", "harden-agent-runner-host.sh"));

        Assert.Contains("source \"$config_policy\"", helper);
        Assert.Contains("mv -fT -- \"$candidate_file\" \"$config_env_file\"", helper);
        Assert.Contains("restart_unit_and_wait_for_new_main_pid", helper);
        Assert.Contains("wait_for_unit_inactive", helper);
        Assert.Contains("drained and stopped", helper);
        Assert.DoesNotContain("drained and replaced", helper);
        Assert.Contains(
            "systemctl kill --kill-whom=main --signal=SIGTERM \"$unit\"",
            helper);
        Assert.Contains("systemctl start \"$unit\"", helper);
        Assert.DoesNotContain("systemctl restart agent-runner-review.service", helper);
        Assert.Contains("readonly agent_host_binary=\"$current_link/agent-host\"", helper);
        Assert.Contains("1:restart-review)", helper);
        Assert.Contains("2:restart-review)", helper);
        Assert.Contains("/proc/$main_pid/environ", helper);
        Assert.Contains("result=$result", helper);
        Assert.Contains("rollback_config", helper);
        Assert.Contains("EnvironmentFiles", helper);
        Assert.Contains("previous_main_pid", helper);
        Assert.Contains("--property=ActiveState --property=MainPID", helper);
        Assert.Contains("new-main-pid-timeout", helper);
        Assert.DoesNotContain("pgrep", helper);
        Assert.Contains("installed_policy", migration);
        Assert.Contains("visudo -c", migration);
        Assert.Contains("for privileged_group in sudo docker", migration);
    }

    [SkippableFact]
    [Trait("Category", "MachineBound")]
    [Trait("Category", "ReviewFlaky")]
    [Trait(PlatformGate.TraitName, PlatformGate.Linux)]
    public void Agent_runner_config_restart_uses_new_main_pid_and_role_environment_file_precedence()
    {
        PlatformGate.LinuxOnly("the config proof reads /proc/<pid>/environ");

        var result = RunShellScript(
            "runner.Tests/Fixtures/agent-runner-deploy-restart-race.sh",
            Path.Combine(RepoRoot(), "deploy", "agent-host", "agent-runner-deploy"));

        Assert.True(result.ExitCode == 0, result.StandardError);
        Assert.Contains("first-post-restart-state=active old-main-pid", result.StandardOutput);
        Assert.Contains("legacy-single-read-result=process-environment-mismatch", result.StandardOutput);
        Assert.Contains("detached-worker-alive-after-restart=true", result.StandardOutput);
        Assert.Contains("main-unit-default-RUNNER_MAX_PARALLELISM=2", result.StandardOutput);
        Assert.Contains("role-EnvironmentFile-RUNNER_MAX_PARALLELISM=6", result.StandardOutput);
        Assert.Contains("effective-RUNNER_MAX_PARALLELISM=6", result.StandardOutput);
        Assert.Contains("review-control=signal-main", result.StandardOutput);
        Assert.Contains("inactive-review-control=start", result.StandardOutput);
    }

    [Fact]
    public void Agent_runner_release_helper_validates_and_boots_before_the_atomic_flip()
    {
        var helper = File.ReadAllText(
            Path.Combine(RepoRoot(), "deploy", "agent-host", "agent-runner-deploy"));
        var closureValidation = helper.IndexOf(
            "\"$deps_closure_validator\" \"$staging_root/agent-host.deps.json\"",
            StringComparison.Ordinal);
        var smokeCheck = helper.IndexOf("./agent-host --version", StringComparison.Ordinal);
        var reviewGuard = helper.LastIndexOf(
            "assert_review_restart_is_safe",
            StringComparison.Ordinal);
        var currentFlip = helper.IndexOf(
            "ln -sfnT \"$release_root\" \"$current_link\"",
            StringComparison.Ordinal);

        Assert.True(closureValidation >= 0);
        Assert.True(smokeCheck > closureValidation);
        Assert.True(reviewGuard > smokeCheck);
        Assert.True(reviewGuard < currentFlip);
        Assert.Contains(
            "assert_review_restart_is_safe \"$staging_root/agent-host\"",
            helper,
            StringComparison.Ordinal);
        Assert.True(currentFlip > smokeCheck);
        Assert.Contains("runuser -u \"$service_user\"", helper);
        Assert.Contains("timeout --signal=TERM", helper);
        Assert.Contains("DOTNET_ROOT=\"$dotnet_root\"", helper);
        Assert.Contains("systemctl show --property=NRestarts", helper);
        Assert.Contains("previous release:", helper);
        Assert.Contains("rollback command:", helper);
    }

    [Fact]
    public void Review_guard_uses_the_candidate_and_the_active_process_state_directory()
    {
        PlatformGate.LinuxOnly("the restart guard resolves /proc/<pid>/environ");

        var result = RunShellScript(
            "runner.Tests/Fixtures/agent-runner-deploy-review-guard.sh",
            Path.Combine(RepoRoot(), "deploy", "agent-host", "agent-runner-deploy"));

        Assert.True(result.ExitCode == 0, result.StandardError);
        Assert.Contains("candidate-used=true", result.StandardOutput);
        Assert.Contains("legacy-current-used=false", result.StandardOutput);
        Assert.Contains("candidate-guard-args=--restart-guard --hold-admission", result.StandardOutput);
        Assert.Contains("live-state-dir=", result.StandardOutput);
        Assert.Contains("inactive-loaded-state-dir=", result.StandardOutput);
    }

    [Fact]
    public void Host_hardening_installs_the_root_owned_dependency_validator_without_expanding_sudoers()
    {
        var migration = File.ReadAllText(
            Path.Combine(RepoRoot(), "scripts", "harden-agent-runner-host.sh"));
        var sudoers = File.ReadAllText(
            Path.Combine(RepoRoot(), "deploy", "agent-host", "sudoers.d", "agent-runner"));

        Assert.Contains("agent-runner-deps-closure.py", migration);
        Assert.Contains(
            "install -o root -g root -m 0755 \"$deps_validator_source\" \"$installed_deps_validator\"",
            migration);
        Assert.DoesNotContain("agent-runner-deps-closure", sudoers);
    }

    [SkippableTheory]
    [InlineData("coding", "1", "agent-runner.service", "/etc/agent-runner/runner-coding.env")]
    [InlineData("coding", "6", "agent-runner.service", "/etc/agent-runner/runner-coding.env")]
    [InlineData("review", "1", "agent-runner-review.service", "/etc/agent-runner/runner-review.env")]
    [InlineData("review", "6", "agent-runner-review.service", "/etc/agent-runner/runner-review.env")]
    public void Agent_runner_config_policy_accepts_only_each_role_boundary(
        string role,
        string value,
        string expectedUnit,
        string expectedPrimaryEnvironment)
    {
        PlatformGate.RequiresPosixShell();

        var result = RunConfigPolicy(role, "RUNNER_MAX_PARALLELISM", value);

        Assert.True(result.ExitCode == 0, result.StandardError);
        Assert.Contains($"unit={expectedUnit}", result.StandardOutput);
        Assert.Contains($"primary_env={expectedPrimaryEnvironment}", result.StandardOutput);
        Assert.Contains($"value={value}", result.StandardOutput);
    }

    [SkippableTheory]
    [InlineData("review", "RUNNER_MAX_PARALLELISM", "0", "must be an integer from 1 through 6")]
    [InlineData("review", "RUNNER_MAX_PARALLELISM", "7", "must be an integer from 1 through 6")]
    [InlineData("review", "RUNNER_MAX_PARALLELISM", "4x", "must be an integer from 1 through 6")]
    [InlineData("review", "RUNNER_POLL_SECONDS", "4", "variable is not allowlisted")]
    [InlineData("gate", "RUNNER_MAX_PARALLELISM", "4", "role must be")]
    public void Agent_runner_config_policy_rejects_outside_the_allowlist(
        string role,
        string variable,
        string value,
        string expectedError)
    {
        PlatformGate.RequiresPosixShell();

        var result = RunConfigPolicy(role, variable, value);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(expectedError, result.StandardError);
    }

    [SkippableTheory]
    [InlineData("coding", "12", "CPUQuota=1200%\nCPUWeight=100\nIOWeight=100\n")]
    [InlineData("review", "12", "CPUQuota=400%\nCPUWeight=30\nIOWeight=30\n")]
    [InlineData("review", "2", "CPUQuota=100%\nCPUWeight=30\nIOWeight=30\n")]
    public void Agent_host_generates_role_quotas_from_host_cpu_count(
        string role,
        string cpuCount,
        string expected)
    {
        PlatformGate.RequiresPosixShell();

        var profile = Path.Combine(Path.GetTempPath(), $"missing-agent-host-profile-{Guid.NewGuid():N}");
        var result = RunResourceGovernance(
            "--role", role,
            "--cpu-count", cpuCount,
            "--profile", profile);

        AssertScriptSucceeded(result);
        Assert.Equal(expected, result.StandardOutput.ReplaceLineEndings("\n"));
    }

    [SkippableFact]
    public void Agent_host_profile_is_the_only_explicit_resource_override()
    {
        PlatformGate.RequiresPosixShell();

        var root = CreateTemporaryDirectory();
        try
        {
            var profile = Path.Combine(root, "profile.conf");
            File.WriteAllText(
                profile,
                """
                CODING_CPU_QUOTA=600%
                CODING_CPU_WEIGHT=200
                CODING_IO_WEIGHT=150
                CODING_MEMORY_MAX=16G
                """);

            var result = RunResourceGovernance(
                "--role", "coding",
                "--cpu-count", "12",
                "--profile", profile);

            AssertScriptSucceeded(result);
            Assert.Equal(
                "CPUQuota=600%\nCPUWeight=200\nIOWeight=150\nMemoryMax=16G\n",
                result.StandardOutput.ReplaceLineEndings("\n"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [SkippableFact]
    public void Agent_host_adopts_and_replaces_legacy_resource_drop_in()
    {
        PlatformGate.RequiresPosixShell();

        var root = CreateTemporaryDirectory();
        try
        {
            var profile = Path.Combine(root, "agent-host", "profile.conf");
            var dropInDirectory = Path.Combine(root, "agent-runner-review.service.d");
            Directory.CreateDirectory(dropInDirectory);
            var limits = Path.Combine(dropInDirectory, "10-limits.conf");
            File.WriteAllText(
                limits,
                """
                [Service]
                CPUQuota=200%
                CPUWeight=20
                IOWeight=20
                MemoryMax=8G
                """);
            var overrideLimits = Path.Combine(dropInDirectory, "90-local.conf");
            File.WriteAllText(
                overrideLimits,
                """
                [Service]
                CPUQuota=400%
                CPUWeight=30
                IOWeight=30
                RestartSec=20s
                """);

            var result = RunResourceGovernance(
                "--role", "review",
                "--cpu-count", "24",
                "--profile", profile,
                "--drop-in-dir", dropInDirectory,
                "--migrate-drop-ins");

            AssertScriptSucceeded(result);
            Assert.Equal(
                "CPUQuota=400%\nCPUWeight=30\nIOWeight=30\nMemoryMax=8G\n",
                result.StandardOutput.ReplaceLineEndings("\n"));
            Assert.Contains("REVIEW_CPU_QUOTA=400%", File.ReadAllText(profile));
            Assert.Contains("REVIEW_MEMORY_MAX=8G", File.ReadAllText(profile));
            Assert.False(File.Exists(limits));
            Assert.DoesNotContain("CPUQuota", File.ReadAllText(overrideLimits));
            Assert.Contains("RestartSec=20s", File.ReadAllText(overrideLimits));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Onboarding_embeds_generated_policy_in_the_managed_main_unit()
    {
        var content = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "remote-runner-onboard.sh"));

        Assert.Contains("--migrate-drop-ins", content);
        Assert.Contains("resource_policy=", content);
        Assert.Contains("$resource_policy", content);
        Assert.Contains("/etc/systemd/system/${service_name}.service", content);
    }

    /// <summary>
    /// Runs the agent-host resource policy script through a real POSIX shell.
    ///
    /// Two Windows-specific details, both handled centrally in
    /// <see cref="PosixShell"/> rather than here: the interpreter must be a full
    /// path (Git's bash is usually not on the PATH of the test host process),
    /// and every path argument must be MSYS-style, because the script enforces
    /// <c>[[ "$profile" == /* ]]</c> and a literal <c>C:\...</c> fails that guard
    /// with exit 2. The Windows form stays in the caller so its own file
    /// assertions keep working - both spellings address the same file.
    /// </summary>
    private static (int ExitCode, string StandardOutput, string StandardError) RunResourceGovernance(
        params string[] arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = PosixShell.RequirePath(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(
            PosixShell.ToShellPath(Path.Combine(RepoRoot(), "scripts", "agent-host-resource-governance.sh")));
        foreach (var argument in arguments)
            start.ArgumentList.Add(Path.IsPathRooted(argument) ? PosixShell.ToShellPath(argument) : argument);

        using var process = Process.Start(start)!;
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, standardOutput, standardError);
    }

    private static (int ExitCode, string StandardOutput, string StandardError) RunConfigPolicy(
        params string[] arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = PosixShell.RequirePath(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(
            PosixShell.ToShellPath(
                Path.Combine(RepoRoot(), "deploy", "agent-host", "agent-runner-config-policy")));
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start)!;
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, standardOutput, standardError);
    }

    private static (int ExitCode, string StandardOutput, string StandardError) RunShellScript(
        string relativePath,
        params string[] arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = PosixShell.RequirePath(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(PosixShell.ToShellPath(Path.Combine(RepoRoot(), relativePath)));
        foreach (var argument in arguments)
            start.ArgumentList.Add(Path.IsPathRooted(argument) ? PosixShell.ToShellPath(argument) : argument);

        using var process = Process.Start(start)!;
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, standardOutput, standardError);
    }

    /// <summary>
    /// A bare exit-code assertion hides the script's own diagnosis; the script
    /// reports every rejection on stderr, so surface it in the failure message.
    /// </summary>
    private static void AssertScriptSucceeded(
        (int ExitCode, string StandardOutput, string StandardError) result)
        => Assert.True(
            result.ExitCode == 0,
            $"agent-host-resource-governance.sh exited {result.ExitCode}: {result.StandardError.Trim()}");

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-host-resource-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static int CountOccurrences(string content, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = content.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }

    private static string RepoRoot([CallerFilePath] string sourceFile = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFile)!);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"Repository root was not found above {sourceFile}.");
    }
}
