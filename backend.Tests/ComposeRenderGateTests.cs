using AgentStudio.Pipeline;
using AgentStudio.Runner;
using AgentStudio.Shared;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2981: the promotion train was the first place that rendered the
/// Compose stack. AGT-2736 rewrote <c>docker-compose.yml</c>, passed its
/// Windows card gate, and broke release/20260927-101333Z on a scenario
/// overlay that still named removed services and secrets. The trigger and
/// requirement contract is pure; the gate contract runs the real gate runner
/// against stub render scripts.
/// </summary>
public sealed class ComposeRenderGatePolicyTests
{
    [Theory]
    [InlineData("docker-compose.yml")]
    [InlineData("deploy/compose/agent-host.env")]
    [InlineData("deploy/compose/secrets/README.md")]
    [InlineData("task-server/Dockerfile")]
    [InlineData("Dockerfile")]
    [InlineData("testsupport/scenario/runner.Dockerfile")]
    [InlineData("runner/agent-host.Dockerfile")]
    [InlineData("tools/Dockerfile.review")]
    [InlineData("scripts/compose-secret-bootstrap.sh")]
    [InlineData("scripts/scenario.sh")]
    [InlineData("scripts/scenario-remote-smoke.sh")]
    [InlineData("scripts/scenario.test.sh")]
    [InlineData("testsupport/scenario/docker-compose.scenario.yml")]
    [InlineData("./docker-compose.yml")]
    [InlineData("testsupport\\scenario\\docker-compose.scenario.yml")]
    public void Paths_that_can_change_the_rendered_stack_trigger_the_step(string path)
        => Assert.True(ComposeRenderGatePolicy.IsTrigger(path));

    [Theory]
    [InlineData("backend/Features/Pipeline/BuildTestGateRunner.cs")]
    [InlineData("docs/operations/testing/deployment-scenario.md")]
    [InlineData("scripts/release/release-scripts.test.sh")]
    [InlineData("scripts/update-stable.sh")]
    [InlineData("scripts/compose-notes.md")]
    [InlineData("deploy/linux-runner/agent-runner.service")]
    [InlineData("testsupport/PlatformGate.cs")]
    [InlineData("frontend/docker-compose.yml")]
    [InlineData("")]
    public void Unrelated_paths_do_not_trigger_the_step(string path)
        => Assert.False(ComposeRenderGatePolicy.IsTrigger(path));

    [Fact]
    public void An_unknown_diff_triggers_nothing()
        => Assert.Empty(ComposeRenderGatePolicy.Triggers(null));

    [Fact]
    public void The_host_verdict_names_the_routing_the_requirement_and_the_paths()
    {
        var verdict = ComposeRenderGatePolicy.HostVerdict(["docker-compose.yml"]);

        Assert.StartsWith("gate host cannot render Compose; route the gate to a Linux host", verdict);
        Assert.Contains("compose-render", verdict);
        Assert.Contains(CapabilityProtocol.ComposeRender, verdict);
        Assert.Contains("docker-compose.yml", verdict);
    }

    [Fact]
    public void A_repository_that_neither_carries_nor_touches_the_scripts_owes_no_render_step()
        => Assert.Empty(ComposeRenderGatePolicy.OwedScripts(
            [Path.Combine(Path.GetTempPath(), "no-such-checkout-" + Guid.NewGuid().ToString("N"))],
            ["Dockerfile"]));

    /// <summary>
    /// Review finding (code-quality, fail-open): a delivery that deletes the
    /// render scripts must not thereby drop the step. The deleted paths are in
    /// the diff, so the repository still declares the step and owes both.
    /// </summary>
    [Fact]
    public void Deleting_the_render_scripts_does_not_drop_the_render_commands()
        => Assert.Equal(
            ComposeRenderGatePolicy.Scripts.Select(ComposeRenderGatePolicy.Command),
            ComposeRenderGatePolicy.Commands(
                [Path.Combine(Path.GetTempPath(), "no-such-checkout-" + Guid.NewGuid().ToString("N"))],
                ["docker-compose.yml", "scripts/scenario.test.sh", "scripts/compose-smoke-version.test.sh"]));
}

public sealed class ComposeRenderGateContractTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "compose-render-gate-" + Guid.NewGuid().ToString("N"));

    private string Invocations => Path.Combine(_root, "render-invocations.txt");

    public ComposeRenderGateContractTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "scripts"));
        foreach (var script in ComposeRenderGatePolicy.Scripts)
        {
            // Stubs stand in for the real scripts; each records that the gate ran it.
            File.WriteAllText(
                Path.Combine(_root, script.Replace('/', Path.DirectorySeparatorChar)),
                $"#!/usr/bin/env bash\necho \"{script}\" >> \"{ShellPath(Invocations)}\"\n");
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
    }

    [Fact]
    public async Task A_diff_touching_docker_compose_yml_makes_the_gate_render_the_stack()
    {
        var probed = 0;

        var result = await RunGateAsync(["docker-compose.yml"], host: _ => { probed++; return Task.FromResult(true); });

        Assert.Equal(BuildTestGateVerdict.Ok, result.Verdict);
        Assert.Equal(1, probed);
        Assert.Equal(ComposeRenderGatePolicy.Scripts, File.ReadAllLines(Invocations));
        Assert.Contains(result.Processes, process => process.Command == "bash scripts/scenario.test.sh");
        Assert.Contains(result.Processes, process => process.Command == "bash scripts/compose-smoke-version.test.sh");
        Assert.Equal([CapabilityProtocol.ComposeRender], result.Requirements);
        Assert.Empty(result.UnmetRequirements);
        Assert.Contains(result.TestSelection!.Reasons,
            reason => reason == "compose-render: diff touches docker-compose.yml");
    }

    [Fact]
    public async Task A_host_without_the_Docker_CLI_returns_the_routing_verdict_instead_of_a_pass()
    {
        var result = await RunGateAsync(["docker-compose.yml"], host: _ => Task.FromResult(false));

        Assert.Equal(BuildTestGateVerdict.Fail, result.Verdict);
        Assert.False(PreDevelopBuildGate.IsGreen(result));
        Assert.Equal(BuildTestGateFailureKind.Environment, result.FailureKind);
        Assert.StartsWith(ComposeRenderGatePolicy.HostCannotRender, result.Reason);
        Assert.Equal([CapabilityProtocol.ComposeRender], result.UnmetRequirements);
        Assert.Empty(result.Processes);
        Assert.False(File.Exists(Invocations));
    }

    [Fact]
    public async Task A_diff_that_cannot_change_the_stack_neither_probes_nor_renders()
    {
        var result = await RunGateAsync(
            ["backend/Program.cs"],
            host: _ => throw new InvalidOperationException("the host must not be probed"));

        Assert.Equal(BuildTestGateVerdict.Ok, result.Verdict);
        Assert.False(File.Exists(Invocations));
        Assert.Empty(result.Requirements);
    }

    [Fact]
    public async Task A_reused_Remote_Review_verdict_that_rendered_the_stack_is_not_repeated_on_this_host()
    {
        var result = await RunGateAsync(
            ["docker-compose.yml"],
            host: _ => throw new InvalidOperationException("the host must not be probed"),
            covered: [CapabilityProtocol.ComposeRender]);

        Assert.Equal(BuildTestGateVerdict.Ok, result.Verdict);
        Assert.False(File.Exists(Invocations));
        Assert.Contains(result.TestSelection!.Reasons,
            reason => reason.Contains("covered by the reused Remote Review verdict", StringComparison.Ordinal));
    }

    [Fact]
    public void The_pre_develop_gate_applies_to_a_compose_only_merge_of_a_repository_with_render_scripts()
    {
        Assert.True(PreDevelopBuildGate.AppliesTo(null, ["docker-compose.yml"], _root));
        Assert.False(PreDevelopBuildGate.AppliesTo(null, ["docker-compose.yml"], Path.Combine(_root, "missing")));
        Assert.False(PreDevelopBuildGate.AppliesTo(null, ["docs/readme.md"], _root));
        // A merge that deletes a render script still owes the step (fail closed).
        Assert.True(PreDevelopBuildGate.AppliesTo(
            null, ["docker-compose.yml", "scripts/scenario.test.sh"], Path.Combine(_root, "missing")));
    }

    /// <summary>
    /// Review finding (code-quality, fail-open): a delivery that renames a
    /// render script shows only the new path in a name-only diff. The Studio
    /// checkout still carries the old one, so the step stays owed and the
    /// tested checkout fails closed on the missing script.
    /// </summary>
    [Fact]
    public async Task A_delivery_missing_a_render_script_fails_closed_instead_of_dropping_the_step()
    {
        var workspace = Path.Combine(_root, "renamed-delivery");
        Directory.CreateDirectory(Path.Combine(workspace, "scripts"));
        File.WriteAllText(Path.Combine(workspace, "scripts", "scenario.test.sh"), "#!/usr/bin/env bash\n");

        var scope = ComposeRenderGate.Plan(workspace, ["docker-compose.yml", "scripts/compose-render.test.sh"], [], _root);

        Assert.True(scope.Required);
        Assert.Equal(
            ComposeRenderGatePolicy.Scripts.Select(ComposeRenderGatePolicy.Command),
            scope.Commands.Select(command => command.Command));
        Assert.Equal(["scripts/compose-smoke-version.test.sh"], scope.MissingScripts);

        File.Delete(Path.Combine(_root, "scripts", "compose-smoke-version.test.sh"));
        var result = await RunGateAsync(
            ["docker-compose.yml"],
            host: _ => throw new InvalidOperationException("a missing script fails before the host probe"));

        Assert.Equal(BuildTestGateVerdict.Fail, result.Verdict);
        Assert.False(PreDevelopBuildGate.IsGreen(result));
        Assert.Equal(BuildTestGateFailureKind.Code, result.FailureKind);
        Assert.False(result.IsInfrastructureFailure);
        Assert.StartsWith(ComposeRenderGatePolicy.MissingScriptsPrefix, result.Reason);
        Assert.Contains("scripts/compose-smoke-version.test.sh", result.Reason);
        Assert.Empty(result.UnmetRequirements);
        Assert.Empty(result.Processes);
        Assert.False(File.Exists(Invocations));
    }

    [Fact]
    public void The_Remote_Review_plan_carries_the_render_steps_and_their_host_requirement()
    {
        var plan = ReviewLibraryStepPolicy.Seal(
            V1ReviewPlaneEndpoints.FallbackPlan(
                _root,
                new BuildProfile { BuildCmds = ["true"] },
                "refs/heads/develop",
                ["docker-compose.yml"]),
            new string('a', 40));

        var render = plan.Commands.Where(command => command.StepId.StartsWith("compose-render-", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(
            ComposeRenderGatePolicy.Scripts.Select(ComposeRenderGatePolicy.GuardedCommand),
            render.Select(command => command.Arguments[^1]));
        Assert.All(render, command =>
        {
            Assert.Equal("build-tests", command.Aspect);
            Assert.Equal(ReviewBaselineModes.ExitStatus, command.BaselineMode);
            Assert.Contains(CapabilityProtocol.ComposeRender, command.LibraryStep!.RequiredCapabilities);
            Assert.Contains(CapabilityProtocol.Node, command.LibraryStep.RequiredCapabilities);
        });

        var withoutDocker = ReviewLibraryStepPolicy.RequiredCapabilities(plan)
            .Where(key => key != CapabilityProtocol.ComposeRender)
            .ToHashSet(StringComparer.Ordinal);
        Assert.False(ReviewLibraryStepPolicy.Supports(plan, withoutDocker));
        Assert.True(ReviewLibraryStepPolicy.Supports(
            plan, ReviewLibraryStepPolicy.RequiredCapabilities(plan).ToHashSet(StringComparer.Ordinal)));
    }

    /// <summary>
    /// Review finding (code-quality, fail-open): the Remote Review plan keeps
    /// both render steps when the delivery deletes a script, and the frozen
    /// step exits 1 with the verdict (not bash's 127, which the remote planes
    /// read as an unavailable toolchain and retry).
    /// </summary>
    [Fact]
    public async Task The_Remote_Review_plan_keeps_a_render_step_whose_script_the_delivery_deleted()
    {
        var plan = V1ReviewPlaneEndpoints.FallbackPlan(
            Path.Combine(_root, "missing"),
            new BuildProfile { BuildCmds = ["true"] },
            "refs/heads/develop",
            ["docker-compose.yml", "scripts/compose-smoke-version.test.sh"]);
        var render = plan.Commands.Where(command => command.StepId.StartsWith("compose-render-", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(["compose-render-1", "compose-render-2"], render.Select(command => command.StepId));

        File.Delete(Path.Combine(_root, "scripts", "compose-smoke-version.test.sh"));
        var (exitCode, stderr) = await RunShellAsync(render[1].Arguments[^1]);

        Assert.Equal(1, exitCode);
        Assert.Contains(
            ComposeRenderGatePolicy.MissingScriptsPrefix + ": scripts/compose-smoke-version.test.sh", stderr);
    }

    [Fact]
    public void The_Remote_Review_plan_for_an_unrelated_diff_has_no_render_step_or_requirement()
    {
        var plan = ReviewLibraryStepPolicy.Seal(
            V1ReviewPlaneEndpoints.FallbackPlan(
                _root, new BuildProfile { BuildCmds = ["true"] }, "refs/heads/develop", ["backend/Program.cs"]),
            new string('a', 40));

        Assert.DoesNotContain(plan.Commands, command => command.StepId.StartsWith("compose-render-", StringComparison.Ordinal));
        Assert.DoesNotContain(CapabilityProtocol.ComposeRender, ReviewLibraryStepPolicy.RequiredCapabilities(plan));
    }

    [Fact]
    public void A_reused_verdict_covers_exactly_the_requirements_the_review_verified()
    {
        var review = new ReviewVerificationRecord
        {
            AttemptId = "rat_1",
            Outcome = "Pass",
            ResultSha = new string('b', 40),
            IntegrationRef = "develop",
            MergeBaseSha = new string('c', 40),
            IntegrationTipSha = new string('d', 40),
            TestedTreeSha = new string('e', 40),
            BuildTestGate = ReviewBuildTestGateClasses.Passed,
            VerifiedRequirements = [CapabilityProtocol.ComposeRender, CapabilityProtocol.DotNet],
        };

        var decision = IntegrationGateReusePolicy.Decide(new IntegrationGateReuseInput(
            true, review, "develop", new string('d', 40), true, false, new string('e', 40)));

        Assert.True(decision.Reused);
        Assert.Equal(review.VerifiedRequirements, decision.CoveredRequirements);
    }

    private Task<BuildTestGateResult> RunGateAsync(
        IReadOnlyList<string> changedFiles,
        Func<CancellationToken, Task<bool>> host,
        IReadOnlyList<string>? covered = null)
    {
        var runner = new BuildTestGateRunner(
            NullLogger<BuildTestGateRunner>.Instance,
            BuildTestMachineGateMode.BypassForHermeticTest,
            Path.Combine(_root, "cache"),
            composeRenderHost: host);
        return runner.RunAsync(
            new BuildTestGateRequest(_root, null, "test", RequireExactSubject: false)
            {
                RequiredTestLevel = TestExecutionLevels.BuildOnly,
                CoveredRequirements = covered ?? [],
            },
            changedFiles,
            new BuildProfile { BuildCmds = ["true"] },
            PostStepMode.Fail,
            TimeSpan.FromMinutes(2),
            CancellationToken.None);
    }

    private async Task<(int ExitCode, string StdErr)> RunShellAsync(string command)
    {
        var start = new System.Diagnostics.ProcessStartInfo("bash")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(command);
        using var process = System.Diagnostics.Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await stdout;
        return (process.ExitCode, await stderr);
    }

    // The render commands run under bash on every platform (Git Bash on
    // Windows), which reads backslashes as escapes.
    private static string ShellPath(string path) => path.Replace('\\', '/');
}
