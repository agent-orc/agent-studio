using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

public sealed class BuildProfileGateTests
{
    [Fact]
    public void NullProfile_AllowsPickup_LegacyBehaviour()
    {
        var d = BuildProfileGate.Evaluate(null);
        Assert.True(d.AllowsPickup);
    }

    [Fact]
    public void DeclaredButUnvalidated_BlocksPickup()
    {
        var d = BuildProfileGate.Evaluate(new BuildProfile { Status = BuildProfileStatuses.Declared });
        Assert.False(d.AllowsPickup);
    }

    [Fact]
    public void NullStatusOnProfile_TreatedAsDeclared_BlocksPickup()
    {
        // A hand-written profile with a missing/unknown status must NOT be treated
        // as pipeline-ready; it normalizes to declared and stays blocked.
        var d = BuildProfileGate.Evaluate(new BuildProfile { Status = "" });
        Assert.False(d.AllowsPickup);
    }

    [Fact]
    public void Validating_BlocksPickup()
    {
        var d = BuildProfileGate.Evaluate(new BuildProfile { Status = BuildProfileStatuses.Validating });
        Assert.False(d.AllowsPickup);
    }

    [Fact]
    public void ValidationFailed_BlocksPickup_AndCarriesReason()
    {
        var d = BuildProfileGate.Evaluate(new BuildProfile
        {
            Status = BuildProfileStatuses.ValidationFailed,
            LastValidationError = "build exited 1",
        });
        Assert.False(d.AllowsPickup);
        Assert.Contains("build exited 1", d.Reason);
    }

    [Fact]
    public void PipelineReady_AllowsPickup()
    {
        var d = BuildProfileGate.Evaluate(new BuildProfile { Status = BuildProfileStatuses.PipelineReady });
        Assert.True(d.AllowsPickup);
    }

    [Theory]
    [InlineData(3, true)]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public void RevalidationPending_UsesBoundedGraceRuns(int remaining, bool expectedPickup)
    {
        var d = BuildProfileGate.Evaluate(new BuildProfile
        {
            Status = BuildProfileStatuses.PipelineReady,
            RevalidationPending = true,
            RevalidationGraceRunsRemaining = remaining,
        });

        Assert.Equal(expectedPickup, d.AllowsPickup);
        Assert.Contains("revalidation pending", d.Reason);
    }
}

public sealed class BuildProfileDryRunPlannerTests
{
    [Fact]
    public void Plan_Null_IsEmpty()
    {
        Assert.Empty(BuildProfileDryRunPlanner.Plan(null));
    }

    [Fact]
    public void Plan_InstallFirstThenBuildsInOrder()
    {
        var plan = BuildProfileDryRunPlanner.Plan(new BuildProfile
        {
            InstallCmd = "npm ci",
            BuildCmds = ["npm run build", "dotnet build"],
        });

        Assert.Equal(3, plan.Count);
        Assert.Equal(DryRunStepKind.Install, plan[0].Kind);
        Assert.Equal("npm ci", plan[0].Command);
        Assert.Equal(DryRunStepKind.Build, plan[1].Kind);
        Assert.Equal("npm run build", plan[1].Command);
        Assert.Equal("dotnet build", plan[2].Command);
    }

    [Fact]
    public void Plan_OmitsBlankInstall()
    {
        var plan = BuildProfileDryRunPlanner.Plan(new BuildProfile { BuildCmds = ["npm run build"] });
        Assert.Single(plan);
        Assert.Equal(DryRunStepKind.Build, plan[0].Kind);
    }

    [Fact]
    public void Plan_DoesNotIncludeTestCommands()
    {
        var plan = BuildProfileDryRunPlanner.Plan(new BuildProfile
        {
            InstallCmd = "npm ci",
            TestCmds = ["npm test"],
        });
        Assert.Single(plan);
        Assert.Equal(DryRunStepKind.Install, plan[0].Kind);
    }
}

public sealed class BuildProfileValidationServiceTests : IDisposable
{
    private readonly string _workspace;

    public BuildProfileValidationServiceTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "rdo-bp-validate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task RemoteRequiredRunner_FailsClosedWithoutExecutingBuildCommand()
    {
        var settings = BuildSettings();
        settings.SetBuildProfile("remote-required", new BuildProfile { BuildCmds = ["touch should-not-run"] });
        var svc = BuildValidator(settings, new RemoteRequiredBuildCommandRunner());

        var result = await svc.ValidateAsync("remote-required", _workspace, CancellationToken.None);

        Assert.False(result.Green);
        Assert.Equal(RemoteExecutionRequirement.Code, result.FailureCode);
        Assert.Equal(BuildProfileStatuses.ValidationFailed, settings.Get("remote-required").BuildProfile!.Status);
        Assert.False(File.Exists(Path.Combine(_workspace, "should-not-run")));
        Assert.False(BuildProfileGate.AllowsAutoPickup(settings.Get("remote-required").BuildProfile));
    }

    [Fact]
    public async Task GreenRun_FlipsProfileToPipelineReady()
    {
        var settings = BuildSettings();
        settings.SetBuildProfile("runbook", new BuildProfile { InstallCmd = "npm ci", BuildCmds = ["npm run build"] });
        var svc = BuildValidator(settings, new FakeRunner(_ => 0));

        var result = await svc.ValidateAsync("runbook", _workspace, CancellationToken.None);

        Assert.True(result.Green);
        Assert.Equal(BuildProfileStatuses.PipelineReady, settings.Get("runbook").BuildProfile!.Status);
        Assert.True(BuildProfileGate.AllowsAutoPickup(settings.Get("runbook").BuildProfile));
    }

    [Fact]
    public async Task RedRun_FlipsToValidationFailed_AndStopsAtFirstFailure()
    {
        var settings = BuildSettings();
        settings.SetBuildProfile("runbook", new BuildProfile { InstallCmd = "npm ci", BuildCmds = ["npm run build"] });
        var ran = new List<string>();
        var runner = new FakeRunner(cmd => { ran.Add(cmd); return cmd == "npm ci" ? 1 : 0; });
        var svc = BuildValidator(settings, runner);

        var result = await svc.ValidateAsync("runbook", _workspace, CancellationToken.None);

        Assert.False(result.Green);
        Assert.Equal("npm ci", result.FailedCommand);
        Assert.Equal(["npm ci"], ran); // build never ran after install failed
        var p = settings.Get("runbook").BuildProfile!;
        Assert.Equal(BuildProfileStatuses.ValidationFailed, p.Status);
        Assert.False(BuildProfileGate.AllowsAutoPickup(p));
    }

    [Fact]
    public async Task NoCommands_IsTriviallyGreen()
    {
        var settings = BuildSettings();
        settings.SetBuildProfile("runbook", new BuildProfile { Stack = "docs-only" });
        var svc = BuildValidator(settings, new FakeRunner(_ => throw new Xunit.Sdk.XunitException("runner must not be invoked")));

        var result = await svc.ValidateAsync("runbook", _workspace, CancellationToken.None);

        Assert.True(result.Green);
        Assert.Equal(BuildProfileStatuses.PipelineReady, settings.Get("runbook").BuildProfile!.Status);
    }

    [Fact]
    public async Task NoProfile_ReturnsNotGreen_WithoutTouchingState()
    {
        var settings = BuildSettings();
        var svc = BuildValidator(settings, new FakeRunner(_ => 0));

        var result = await svc.ValidateAsync("runbook", _workspace, CancellationToken.None);

        Assert.False(result.Green);
        Assert.Null(settings.Get("runbook").BuildProfile);
    }

    private ProjectSettingsService BuildSettings()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _workspace })
            .Build();
        return new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
    }

    private static BuildProfileValidationService BuildValidator(ProjectSettingsService settings, IBuildCommandRunner runner) =>
        new(settings, runner, NullLogger<BuildProfileValidationService>.Instance);

    private sealed class FakeRunner : IBuildCommandRunner
    {
        private readonly Func<string, int> _exitFor;
        public FakeRunner(Func<string, int> exitFor) => _exitFor = exitFor;
        public Task<BuildCommandResult> RunAsync(string workingDir, string command, CancellationToken ct) =>
            Task.FromResult(new BuildCommandResult(_exitFor(command), $"output of {command}"));
    }
}

public sealed class BuildProfileValidationWorkspaceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "rdo-bp-workspace-" + Guid.NewGuid().ToString("N"));

    public BuildProfileValidationWorkspaceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Resolve_PrefersRealRepositoryOverTaskWorkspace()
    {
        var repository = Path.Combine(_root, "repository");
        var taskStore = Path.Combine(_root, "tasks", "2-ready");
        Directory.CreateDirectory(repository);
        Directory.CreateDirectory(taskStore);

        var resolved = BuildProfileValidationWorkspace.Resolve(new WatchPathEntry
        {
            Path = taskStore,
            RootPath = taskStore,
            RepositoryPath = repository,
        });

        Assert.Equal(Path.GetFullPath(repository), resolved);
    }

    [Fact]
    public void Resolve_DoesNotFallBackToTaskWorkspace()
    {
        var taskStore = Path.Combine(_root, "tasks", "2-ready");
        Directory.CreateDirectory(taskStore);

        var resolved = BuildProfileValidationWorkspace.Resolve(new WatchPathEntry
        {
            Path = taskStore,
        });

        Assert.Null(resolved);
    }
}
