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
            ComposeRenderGatePolicy.Scripts.Select(ComposeRenderGatePolicy.Command),
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

    // The render commands run under bash on every platform (Git Bash on
    // Windows), which reads backslashes as escapes.
    private static string ShellPath(string path) => path.Replace('\\', '/');
}
