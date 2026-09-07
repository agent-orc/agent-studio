using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;

using Xunit;
using Xunit.Abstractions;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2065: the build/test gate derives its verify commands per project instead
/// of hardcoding <c>backend/OrchestratorApi.csproj</c> (the Studio-specific path
/// that broke on every other repo layout - TE-2, 2026-07-10). These fixtures
/// exercise the four shapes named in the task (.NET-only, npm-only, mixed, empty)
/// plus the explicit build-profile override and the honest empty fallback.
/// </summary>
public sealed class VerifyCommandPlannerTests : IDisposable
{
    private readonly string _root;

    public VerifyCommandPlannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "verify-planner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private void Write(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private string RunGit(params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("git did not start");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {stderr}");
        return stdout;
    }

    // ---- Fixture: .NET only ------------------------------------------------

    [Fact]
    public void DotNetOnly_Sln_DerivesBareBuildAndTest()
    {
        Write("MyApp.sln", "Microsoft Visual Studio Solution File");

        var plan = VerifyCommandPlanner.Plan(_root, profile: null);

        Assert.Equal(VerifyPlan.SourceAutoDiscovery, plan.Source);
        Assert.Collection(plan.Commands,
            c => AssertCommand(c, VerifyEcosystem.DotNet, VerifyCommandKind.Build, "", "dotnet build"),
            c => AssertCommand(c, VerifyEcosystem.DotNet, VerifyCommandKind.Test, "", "dotnet test"));
    }

    [Fact]
    public void DotNetOnly_RootCsproj_DerivesBareBuildAndTest()
    {
        Write("Service.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        var plan = VerifyCommandPlanner.Plan(_root, profile: null);

        Assert.Equal(VerifyPlan.SourceAutoDiscovery, plan.Source);
        Assert.Equal(2, plan.Commands.Count);
        Assert.All(plan.Commands, c => Assert.Equal(VerifyEcosystem.DotNet, c.Ecosystem));
    }

    [Fact]
    public void DotNet_SlnxWithNoRootProject_DerivesCommandsAtRepositoryRoot()
    {
        // TE-3 / AGT-2099: the solution is at the worktree root while its
        // projects live below it. A target-less dotnet command is valid only
        // when the runner preserves that root as its cwd.
        Write("TokenEconomy.slnx", "<Solution><Project Path=\"src/App/App.csproj\" /></Solution>");
        Write("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        var plan = VerifyCommandPlanner.Plan(_root, profile: null);

        Assert.Equal(VerifyPlan.SourceAutoDiscovery, plan.Source);
        Assert.Collection(plan.Commands,
            c => AssertCommandAtRepositoryRoot(c, VerifyCommandKind.Build, "dotnet build"),
            c => AssertCommandAtRepositoryRoot(c, VerifyCommandKind.Test, "dotnet test"));
    }

    [Fact]
    public void DotNet_NestedCsprojOnly_NotDerivable_HonestFallback()
    {
        // A project a level down cannot be resolved by a bare `dotnet build` at the
        // root, so we must not pretend it can - the plan is empty, not a wrong path.
        Write("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        var plan = VerifyCommandPlanner.Plan(_root, profile: null);

        Assert.Equal(VerifyPlan.SourceNone, plan.Source);
        Assert.True(plan.IsEmpty);
    }

    // ---- Fixture: npm only -------------------------------------------------

    [Fact]
    public void NpmOnly_RootManifest_DerivesDeclaredScriptsOnly()
    {
        Write("package.json", """
            { "scripts": { "build": "tsc", "test": "vitest run", "lint": "eslint ." } }
            """);

        var plan = VerifyCommandPlanner.Plan(_root, profile: null);

        Assert.Equal(VerifyPlan.SourceAutoDiscovery, plan.Source);
        Assert.Collection(plan.Commands,
            c => AssertCommand(c, VerifyEcosystem.Node, VerifyCommandKind.Build, "", "npm run build"),
            c => AssertCommand(c, VerifyEcosystem.Node, VerifyCommandKind.Test, "", "npm test"),
            c => AssertCommand(c, VerifyEcosystem.Node, VerifyCommandKind.Lint, "", "npm run lint"));
    }

    [Fact]
    public void NpmOnly_OnlyTestScript_DerivesOnlyThatScript()
    {
        Write("package.json", """{ "scripts": { "test": "vitest run" } }""");

        var plan = VerifyCommandPlanner.Plan(_root, profile: null);

        Assert.Single(plan.Commands);
        AssertCommand(plan.Commands[0], VerifyEcosystem.Node, VerifyCommandKind.Test, "", "npm test");
    }

    [Fact]
    public void NpmOnly_PlaceholderTestScript_IsIgnored()
    {
        // The scaffolded npm default must not turn the gate red.
        Write("package.json", """
            { "scripts": { "test": "echo \"Error: no test specified\" && exit 1" } }
            """);

        var plan = VerifyCommandPlanner.Plan(_root, profile: null);

        Assert.Equal(VerifyPlan.SourceNone, plan.Source);
        Assert.True(plan.IsEmpty);
    }

    [Fact]
    public void Npm_SubdirManifest_DerivesCommandsScopedToThatSubdir()
    {
        Write("frontend/package.json", """{ "scripts": { "build": "ng build", "lint": "ng lint" } }""");

        var plan = VerifyCommandPlanner.Plan(_root, profile: null);

        Assert.Equal(VerifyPlan.SourceAutoDiscovery, plan.Source);
        Assert.Collection(plan.Commands,
            c => AssertCommand(c, VerifyEcosystem.Node, VerifyCommandKind.Build, "frontend", "npm run build"),
            c => AssertCommand(c, VerifyEcosystem.Node, VerifyCommandKind.Lint, "frontend", "npm run lint"));
    }

    [Fact]
    public void Npm_NodeModulesManifest_IsNotScanned()
    {
        // A package.json inside a dependency folder must never be treated as a
        // project manifest.
        Write("node_modules/left-pad/package.json", """{ "scripts": { "build": "noop" } }""");

        var plan = VerifyCommandPlanner.Plan(_root, profile: null);

        Assert.Equal(VerifyPlan.SourceNone, plan.Source);
        Assert.True(plan.IsEmpty);
    }

    [Fact]
    public void Npm_UntrackedChildPackage_IsExcludedFromVerificationAndPreparation()
    {
        Write("tracked-app/package.json", """{ "scripts": { "build": "tsc" } }""");
        Write("tracked-app/package-lock.json", """{ "lockfileVersion": 3 }""");
        RunGit("init", "-q", "-b", "main");
        RunGit("config", "user.email", "test@example.invalid");
        RunGit("config", "user.name", "Verify Planner Test");
        RunGit("add", "tracked-app/package.json", "tracked-app/package-lock.json");
        RunGit("commit", "-q", "-m", "tracked package");

        Write("stale-salvage/package.json", """{ "scripts": { "build": "tsc" } }""");
        Write("stale-salvage/package-lock.json", """{ "lockfileVersion": 3 }""");

        var verify = VerifyCommandPlanner.Plan(_root, profile: null);
        var preparation = GatePreparationPlanner.Plan(_root, profile: null, verify.Commands);

        Assert.DoesNotContain(verify.Commands, command => command.WorkingSubdir == "stale-salvage");
        Assert.DoesNotContain(preparation, command => command.WorkingSubdir == "stale-salvage");
        Assert.Contains(preparation, command => command.WorkingSubdir == "tracked-app");
    }

    [Fact]
    public void Preparation_failure_retry_rebuilds_plan_after_tracked_source_changes()
    {
        Write("removed-app/package.json", """{ "scripts": { "build": "tsc" } }""");
        Write("removed-app/package-lock.json", """{ "lockfileVersion": 3 }""");
        RunGit("init", "-q", "-b", "main");
        RunGit("config", "user.email", "test@example.invalid");
        RunGit("config", "user.name", "Verify Planner Test");
        RunGit("add", "removed-app/package.json", "removed-app/package-lock.json");
        RunGit("commit", "-q", "-m", "package before repair");
        var firstPlan = V1ReviewPlaneEndpoints.FallbackPlan(
            _root, profile: null, integrationRef: "refs/heads/main");
        Assert.Contains(firstPlan.Preparation!, command => command.WorkingSubdir == "removed-app");

        RunGit("rm", "-q", "removed-app/package.json", "removed-app/package-lock.json");
        RunGit("commit", "-q", "-m", "remove stale package");

        var retryPlan = V1ReviewPlaneEndpoints.ReviewPlanForInfrastructureRetry(
            "PreparationFailed",
            firstPlan,
            () => V1ReviewPlaneEndpoints.FallbackPlan(
                _root, profile: null, integrationRef: "refs/heads/main"));

        Assert.NotEqual(firstPlan, retryPlan);
        Assert.Empty(retryPlan!.Preparation ?? []);
    }

    // ---- Fixture: mixed (.NET + npm) --------------------------------------

    [Fact]
    public void Mixed_SlnAndFrontendManifest_DerivesBoth()
    {
        Write("agent-taskboard.sln", "Microsoft Visual Studio Solution File");
        Write("frontend/package.json", """{ "scripts": { "build": "ng build", "test": "ng test", "lint": "ng lint" } }""");

        var plan = VerifyCommandPlanner.Plan(_root, profile: null);

        Assert.Equal(VerifyPlan.SourceAutoDiscovery, plan.Source);
        Assert.Collection(plan.Commands,
            c => AssertCommand(c, VerifyEcosystem.DotNet, VerifyCommandKind.Build, "", "dotnet build"),
            c => AssertCommand(c, VerifyEcosystem.DotNet, VerifyCommandKind.Test, "", "dotnet test"),
            c => AssertCommand(c, VerifyEcosystem.Node, VerifyCommandKind.Build, "frontend", "npm run build"),
            c => AssertCommand(c, VerifyEcosystem.Node, VerifyCommandKind.Test, "frontend", "npm test"),
            c => AssertCommand(c, VerifyEcosystem.Node, VerifyCommandKind.Lint, "frontend", "npm run lint"));
    }

    // ---- Fixture: empty ----------------------------------------------------

    [Fact]
    public void Empty_NothingDerivable_HonestFallback()
    {
        Write("README.md", "# just docs");

        var plan = VerifyCommandPlanner.Plan(_root, profile: null);

        Assert.Equal(VerifyPlan.SourceNone, plan.Source);
        Assert.True(plan.IsEmpty);
    }

    [Fact]
    public void MissingRepositoryPath_HonestFallback()
    {
        var plan = VerifyCommandPlanner.Plan(Path.Combine(_root, "does-not-exist"), profile: null);

        Assert.Equal(VerifyPlan.SourceNone, plan.Source);
        Assert.True(plan.IsEmpty);
    }

    // ---- Explicit build-profile override ----------------------------------

    [Fact]
    public void BuildProfile_WithCommands_OverridesAutoDiscovery()
    {
        // Even though the repo has a .sln that would auto-discover, the declared
        // profile's commands win outright.
        Write("agent-taskboard.sln", "Microsoft Visual Studio Solution File");
        var profile = new BuildProfile
        {
            BuildCmds = ["make build"],
            TestCmds = ["make test"],
        };

        var plan = VerifyCommandPlanner.Plan(_root, profile);

        Assert.Equal(VerifyPlan.SourceBuildProfile, plan.Source);
        Assert.Collection(plan.Commands,
            c => AssertCommand(c, VerifyEcosystem.Custom, VerifyCommandKind.Build, "", "make build"),
            c => AssertCommand(c, VerifyEcosystem.Custom, VerifyCommandKind.Test, "", "make test"));
    }

    [Fact]
    public void BuildProfile_WithoutBuildOrTestCommands_FallsThroughToDiscovery()
    {
        // A profile that only declares install/lockfile metadata is not a verify
        // override; discovery still applies.
        Write("agent-taskboard.sln", "Microsoft Visual Studio Solution File");
        var profile = new BuildProfile { InstallCmd = "dotnet restore", Lockfiles = ["packages.lock.json"] };

        var plan = VerifyCommandPlanner.Plan(_root, profile);

        Assert.Equal(VerifyPlan.SourceAutoDiscovery, plan.Source);
        Assert.All(plan.Commands, c => Assert.Equal(VerifyEcosystem.DotNet, c.Ecosystem));
    }

    [Fact]
    public void GatePreparation_AutoDiscoveredDotNet_RestoresBeforeVerification()
    {
        Write("QualityStudio.slnx", "<Solution><Project Path=\"src/App/App.csproj\" /></Solution>");
        Write("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var verify = VerifyCommandPlanner.Plan(_root, profile: null);

        var preparation = GatePreparationPlanner.Plan(_root, profile: null, verify.Commands);

        var restore = Assert.Single(preparation);
        Assert.Equal(VerifyEcosystem.DotNet, restore.Ecosystem);
        Assert.Equal("dotnet restore", restore.Command);
        Assert.Equal("", restore.WorkingSubdir);
        Assert.Equal(VerifyCommandShell.Platform, restore.Shell);
    }

    [Fact]
    public void GatePreparation_AutoDiscoveredNode_RunsNpmCiPerSelectedPackage()
    {
        Write("frontend/package.json", """{ "scripts": { "build": "ng build" } }""");
        Write("frontend/package-lock.json", """{ "lockfileVersion": 3 }""");
        var verify = VerifyCommandPlanner.Plan(_root, profile: null);

        var preparation = GatePreparationPlanner.Plan(_root, profile: null, verify.Commands);

        var install = Assert.Single(preparation);
        Assert.Equal(VerifyEcosystem.Node, install.Ecosystem);
        Assert.Equal("frontend", install.WorkingSubdir);
        Assert.Equal("npm ci", install.Command);
        Assert.Equal(VerifyCommandShell.Platform, install.Shell);
        var dependency = Assert.Single(install.DependencyScopes);
        Assert.Equal("frontend", dependency.WorkingSubdir);
        Assert.Equal(["package-lock.json"], dependency.Lockfiles);
    }

    [Fact]
    public void GatePreparation_ExplicitInstallCommand_IsAuthoritativeAndUsesBash()
    {
        Write("QualityStudio.slnx", "<Solution />");
        Write("frontend/package.json", """{ "scripts": { "build": "ng build" } }""");
        var installCommand =
            "if [ -f QualityStudio.slnx ]; then dotnet restore QualityStudio.slnx && npm --prefix frontend ci; fi";
        var profile = new BuildProfile
        {
            InstallCmd = installCommand,
            Lockfiles = ["frontend/package-lock.json"],
            BuildCmds =
            [
                "if [ -f QualityStudio.slnx ]; then dotnet build QualityStudio.slnx --configuration Release; fi",
                "npm --prefix frontend run build",
            ],
        };
        var verify = VerifyCommandPlanner.Plan(_root, profile);

        var preparation = GatePreparationPlanner.Plan(_root, profile, verify.Commands);

        var install = Assert.Single(preparation);
        Assert.Equal(installCommand, install.Command);
        Assert.Equal(VerifyCommandShell.Bash, install.Shell);
        var dependency = Assert.Single(install.DependencyScopes);
        Assert.Equal("frontend", dependency.WorkingSubdir);
        Assert.Equal(["package-lock.json"], dependency.Lockfiles);
        Assert.All(verify.Commands, command => Assert.Equal(VerifyCommandShell.Bash, command.Shell));
    }

    [Fact]
    public void RemoteReviewFallbackPlan_CarriesMixedBuildProfilePreparationLockfilesAndPreserveGlobs()
    {
        Write("QualityStudio.slnx", "<Solution />");
        Write("frontend/package.json", """{ "scripts": { "build": "ng build", "test": "ng test" } }""");
        Write("frontend/package-lock.json", """{ "lockfileVersion": 3 }""");
        const string install =
            "dotnet restore QualityStudio.slnx && npm --prefix frontend ci";
        var profile = new BuildProfile
        {
            Stack = "dotnet+node",
            InstallCmd = install,
            BuildCmds =
            [
                "dotnet build QualityStudio.slnx --configuration Release",
                "npm --prefix frontend run build",
            ],
            TestCmds =
            [
                "dotnet test --filter Category!=MachineBound",
                "npm --prefix frontend test",
            ],
            Lockfiles = ["frontend/package-lock.json"],
            PreserveGlobs = ["frontend/node_modules", "frontend/.angular", "**/bin", "**/obj"],
            Status = BuildProfileStatuses.PipelineReady,
        };

        var plan = V1ReviewPlaneEndpoints.FallbackPlan(
            _root,
            profile,
            "refs/heads/main");

        Assert.Equal(4, plan.Commands.Count);
        var preparation = Assert.Single(plan.Preparation!);
        Assert.Equal("prepare-1", preparation.StepId);
        Assert.Equal("bash", preparation.FileName);
        Assert.Equal(["-lc", install], preparation.Arguments);
        var scope = Assert.Single(preparation.DependencyScopes!);
        Assert.Equal("frontend", scope.WorkingSubdir);
        Assert.Equal(["package-lock.json"], scope.Lockfiles);
        Assert.Equal(profile.PreserveGlobs, plan.PreserveGlobs);
        Assert.Equal(BuildProfileValidationFingerprint.Create(profile), plan.BuildProfileFingerprint);
    }

    [Fact]
    public void GatePreparation_ProfileWithoutInstall_DerivesRestoreAndNpmPrefix()
    {
        Write("QualityStudio.slnx", "<Solution />");
        Write("frontend/package.json", """{ "scripts": { "build": "ng build" } }""");
        var profile = new BuildProfile
        {
            BuildCmds =
            [
                "dotnet build QualityStudio.slnx --no-restore",
                "npm --prefix frontend run build",
            ],
        };
        var verify = VerifyCommandPlanner.Plan(_root, profile);

        var preparation = GatePreparationPlanner.Plan(_root, profile, verify.Commands);

        Assert.Collection(preparation,
            restore =>
            {
                Assert.Equal("dotnet restore", restore.Command);
                Assert.Equal(VerifyCommandShell.Bash, restore.Shell);
            },
            install =>
            {
                Assert.Equal("frontend", install.WorkingSubdir);
                Assert.Equal("npm ci", install.Command);
                Assert.Equal(VerifyCommandShell.Bash, install.Shell);
            });
    }

    // ---- Real case: this repo (agent-studio) ------------------------------

    [SkippableFact]
    public void RealCase_ThisRepo_DerivesDotNetPlusFrontendNpm()
    {
        // The TE-2 finding was a real mixed repo escalating on a hardcoded path.
        // The Studio checkout is itself the mixed real case: a root
        // agent-taskboard.sln plus frontend/package.json. Deriving against the
        // actual tree (not a fixture) proves the bare, layout-driven commands.
        var repoRoot = FindRepoRoot();
        Skip.If(repoRoot is null, "agent-taskboard.sln not found above the test assembly");

        var plan = VerifyCommandPlanner.Plan(repoRoot!, profile: null);

        Assert.Equal(VerifyPlan.SourceAutoDiscovery, plan.Source);
        Assert.Contains(plan.Commands, c =>
            c.Ecosystem == VerifyEcosystem.DotNet && c.Kind == VerifyCommandKind.Build && c.Command == "dotnet build");
        Assert.Contains(plan.Commands, c =>
            c.Ecosystem == VerifyEcosystem.DotNet && c.Kind == VerifyCommandKind.Test && c.Command == "dotnet test");
        // frontend/package.json declares build/test/lint scripts.
        Assert.Contains(plan.Commands, c =>
            c.Ecosystem == VerifyEcosystem.Node && c.WorkingSubdir == "frontend" && c.Command == "npm run build");
        // No command references the old hardcoded backend/OrchestratorApi.csproj path.
        Assert.DoesNotContain(plan.Commands, c => c.Command.Contains("OrchestratorApi.csproj"));
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "agent-taskboard.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    private static void AssertCommand(
        VerifyCommand cmd, VerifyEcosystem ecosystem, VerifyCommandKind kind, string subdir, string command)
    {
        Assert.Equal(ecosystem, cmd.Ecosystem);
        Assert.Equal(kind, cmd.Kind);
        Assert.Equal(subdir, cmd.WorkingSubdir);
        Assert.Equal(command, cmd.Command);
    }

    private void AssertCommandAtRepositoryRoot(
        VerifyCommand cmd, VerifyCommandKind kind, string command)
    {
        AssertCommand(cmd, VerifyEcosystem.DotNet, kind, "", command);
        Assert.Equal(Path.GetFullPath(_root), BuildTestGateRunner.ResolveWorkingDirectory(_root, cmd));
    }

    [Theory]
    [InlineData(BuildTestGateVerdict.Ok, true)]
    [InlineData(BuildTestGateVerdict.NotApplicable, true)]
    [InlineData(BuildTestGateVerdict.Skipped, false)]
    [InlineData(BuildTestGateVerdict.Warn, false)]
    [InlineData(BuildTestGateVerdict.Fail, false)]
    public void PreDevelopGate_GreenPolicy_DoesNotTreatSkippedAsVerified(
        BuildTestGateVerdict verdict,
        bool expectedGreen)
    {
        var result = new BuildTestGateResult(verdict, null, 0, "", "test", false, false);

        Assert.Equal(expectedGreen, PreDevelopBuildGate.IsGreen(result));
    }
}

/// <summary>
/// Behavior of <see cref="BuildTestGateRunner"/> around the derived plan: the
/// skip branches, the honest "no verify commands derivable" fallback, and the
/// end-to-end command loop driven through the build-profile override (trivial
/// shell commands so the test needs no real toolchain).
/// </summary>
// MachineBound 22.07.: instanziiert einen echten BuildTestGateRunner und nimmt den
// maschinenweiten Lock %TEMP%\agentstudio-build-test-gate.lock - laeuft die Suite IM
// Gate, haelt das Gate den Lock bereits -> 15s-SLA -> Timeout/Collision -> Gate-Fail
// (Selbstblockade). Klassenweit, weil jede Methode denselben Runner/Lock beruehrt.
[Trait("Category", "MachineBound")]
public sealed class BuildTestGateRunnerBehaviorTests : IDisposable
{
    private readonly string _root;
    private readonly BuildTestGateRunner _runner = new(NullLogger<BuildTestGateRunner>.Instance);
    private readonly ITestOutputHelper _output;

    public BuildTestGateRunnerBehaviorTests(ITestOutputHelper output)
    {
        _output = output;
        _root = Path.Combine(Path.GetTempPath(), "verify-runner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            var cache = GateDependencyCacheSession.CachePath(
                BuildTestGateRunner.ReviewWorkspaceRoot,
                _root);
            if (Directory.Exists(cache)) Directory.Delete(cache, recursive: true);
        }
        catch { /* best-effort */ }
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private Task<BuildTestGateResult> Run(
        BuildProfile? profile,
        IReadOnlyList<string>? changedFiles = null,
        PostStepMode mode = PostStepMode.Fail)
        => _runner.RunAsync(
            new BuildTestGateRequest(_root, null, "test", RequireExactSubject: false),
            changedFiles, profile, mode, TimeSpan.FromSeconds(30), CancellationToken.None);

    [Fact]
    public async Task ModeOff_Skips()
    {
        var r = await Run(profile: null, mode: PostStepMode.Off);
        Assert.Equal(BuildTestGateVerdict.Skipped, r.Verdict);
        Assert.Equal("mode=off", r.Reason);
    }

    [Fact]
    public async Task NoCodeDiff_Skips()
    {
        var r = await Run(profile: null, changedFiles: ["docs/note.md", "README.md"]);
        Assert.Equal(BuildTestGateVerdict.Skipped, r.Verdict);
        Assert.Equal("no code diff", r.Reason);
    }

    [Fact]
    public async Task NoDerivableCommands_IsNotApplicableWithHonestReason()
    {
        // Empty repo, no profile -> the gate runs without a build check and says so.
        var r = await Run(profile: null);
        Assert.Equal(BuildTestGateVerdict.NotApplicable, r.Verdict);
        Assert.Equal("no verify commands derivable", r.Reason);
        Assert.False(r.RanBackendBuild);
        Assert.False(r.RanFrontendBuild);
    }

    [Fact]
    public async Task HealthObserverFailure_DoesNotFailGateOrRetainMachineLock()
    {
        var runner = new BuildTestGateRunner(
            NullLogger<BuildTestGateRunner>.Instance,
            health: new ThrowingPipelineHealthSensor());
        var request = new BuildTestGateRequest(
            _root,
            null,
            "health-observer-test",
            RequireExactSubject: false)
        {
            Project = "Project",
            WatchPath = _root,
            JobId = "health-observer-card",
        };

        var first = await runner.RunAsync(
            request,
            changedFiles: null,
            profile: null,
            PostStepMode.Fail,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);
        var second = await Run(profile: null);

        Assert.Equal(BuildTestGateVerdict.NotApplicable, first.Verdict);
        Assert.Equal(BuildTestGateVerdict.NotApplicable, second.Verdict);
    }

    [Fact]
    public async Task ProfileOverride_AllGreen_ReturnsOk()
    {
        var profile = new BuildProfile { BuildCmds = ["exit 0"], TestCmds = ["exit 0"] };

        var r = await Run(profile);

        Assert.Equal(BuildTestGateVerdict.Ok, r.Verdict);
        Assert.Equal(0, r.ExitCode);
        Assert.True(r.RanBackendBuild);
        Assert.Contains("build-profile", r.Reason);
    }

    [Fact]
    public async Task CommandExecution_CanonicalizesRelativeRepositoryPathAsCwd()
    {
        const string printWorkingDirectory = "pwd";
        var relativeRoot = Path.GetRelativePath(Environment.CurrentDirectory, _root);
        var profile = new BuildProfile { BuildCmds = [printWorkingDirectory] };

        var r = await _runner.RunAsync(
            new BuildTestGateRequest(relativeRoot, null, "test", RequireExactSubject: false),
            changedFiles: null, profile, PostStepMode.Fail,
            TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(BuildTestGateVerdict.Ok, r.Verdict);
        Assert.Contains(Path.GetFullPath(_root), r.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProfileOverride_FirstFailure_StopsAndFails()
    {
        var profile = new BuildProfile { BuildCmds = ["exit 7", "exit 0"] };

        var r = await Run(profile, mode: PostStepMode.Fail);

        Assert.Equal(BuildTestGateVerdict.Fail, r.Verdict);
        Assert.Equal(7, r.ExitCode);
        // The second command must never run once the first fails.
        Assert.DoesNotContain("exit 0", r.Output);
    }

    [Fact]
    public async Task ProfileOverride_Failure_InWarnMode_ReturnsWarn()
    {
        var profile = new BuildProfile { BuildCmds = ["exit 3"] };

        var r = await Run(profile, mode: PostStepMode.Warn);

        Assert.Equal(BuildTestGateVerdict.Warn, r.Verdict);
        Assert.Equal(3, r.ExitCode);
    }

    [Fact]
    public async Task FailureReason_PersistsBoundedStderrExcerpt()
    {
        var profile = new BuildProfile
        {
            BuildCmds = ["echo 'exact gate cause' >&2; exit 9"],
        };

        var result = await Run(profile);

        Assert.Equal(BuildTestGateVerdict.Fail, result.Verdict);
        Assert.Contains("output: stderr: exact gate cause", result.Reason);
        Assert.True(result.Reason.Length <= BuildTestGateRunner.MaxFailureExcerptChars + 500);
    }

    [Fact]
    public async Task ExactSubjectNodeFixture_ReusesDependenciesAndAngularCacheOnSecondRun()
    {
        WriteFixtureFile("package.json", """
            {
              "name": "gate-node-fixture",
              "version": "1.0.0",
              "scripts": {
                "build": "node -e \"const fs=require('fs'); if (!(process.env.NPM_CONFIG_CACHE || process.env.npm_config_cache)) process.exit(2); require('fixture-dep'); const warm=fs.existsSync('.angular/cache/warm'); fs.mkdirSync('.angular/cache',{recursive:true}); fs.writeFileSync('.angular/cache/warm','yes'); console.log(warm?'angular-cache=hit':'angular-cache=miss')\""
              },
              "dependencies": { "fixture-dep": "file:fixture-dep" }
            }
            """);
        WriteFixtureFile("fixture-dep/package.json", """
            { "name": "fixture-dep", "version": "1.0.0", "main": "index.js" }
            """);
        WriteFixtureFile("fixture-dep/index.js", "module.exports = 'installed';");
        WriteFixtureFile("package-lock.json", """
            {
              "name": "gate-node-fixture",
              "version": "1.0.0",
              "lockfileVersion": 3,
              "requires": true,
              "packages": {
                "": {
                  "name": "gate-node-fixture",
                  "version": "1.0.0",
                  "dependencies": { "fixture-dep": "file:fixture-dep" }
                },
                "fixture-dep": { "name": "fixture-dep", "version": "1.0.0" },
                "node_modules/fixture-dep": { "resolved": "fixture-dep", "link": true }
              }
            }
            """);
        var sha = InitializeGitRepository();

        var request = new BuildTestGateRequest(_root, sha, "node-fixture")
        {
            RequiredTestLevel = TestExecutionLevels.BuildOnly,
            InfrastructureTimeout = TimeSpan.FromSeconds(30),
        };
        var first = await _runner.RunAsync(
            request,
            changedFiles: null,
            profile: null,
            PostStepMode.Fail,
            TimeSpan.FromMinutes(2),
            CancellationToken.None);
        var second = await _runner.RunAsync(
            request,
            changedFiles: null,
            profile: null,
            PostStepMode.Fail,
            TimeSpan.FromMinutes(2),
            CancellationToken.None);

        Assert.True(
            first.Verdict == BuildTestGateVerdict.Ok,
            $"{first.Reason}\n{first.Output}\n{string.Join("\n", first.Processes.Select(p => p.StandardError))}");
        Assert.True(
            second.Verdict == BuildTestGateVerdict.Ok,
            $"{second.Reason}\n{second.Output}\n{string.Join("\n", second.Processes.Select(p => p.StandardError))}");
        Assert.Collection(first.Processes,
            install =>
            {
                Assert.Equal("preparation", install.Phase);
                Assert.Equal("npm ci", install.Command);
                Assert.Equal(0, install.ExitCode);
            },
            build =>
            {
                Assert.Equal("verification", build.Phase);
                Assert.Equal("npm run build", build.Command);
                Assert.Equal(0, build.ExitCode);
            });
        var secondBuild = Assert.Single(second.Processes);
        Assert.Equal("verification", secondBuild.Phase);
        Assert.Contains("angular-cache=hit", secondBuild.StandardOutput);
        Assert.Contains(second.DependencyCache, cache =>
            cache.State == "hit"
            && cache.Reason == "lock-unchanged"
            && !cache.InstallRan);
        Assert.True(
            second.DurationMs < first.DurationMs,
            $"Expected warm gate to be faster; cold={first.DurationMs}ms warm={second.DurationMs}ms.");
        _output.WriteLine(
            $"dependency-cache benchmark coldMs={first.DurationMs} warmMs={second.DurationMs} " +
            $"savedMs={first.DurationMs - second.DurationMs}");
        Assert.NotEqual(first.Workspace, second.Workspace);
        Assert.True(Directory.Exists(BuildTestGateRunner.NpmCachePath));
    }

    [Fact]
    public async Task ExactSubjectNodeFixture_LockfileChangeForcesReinstall()
    {
        WriteFixtureFile("package.json", """
            {
              "name": "gate-node-lock-fixture",
              "version": "1.0.0",
              "scripts": { "build": "node -e \"require('fixture-dep')\"" },
              "dependencies": { "fixture-dep": "file:fixture-dep" }
            }
            """);
        WriteFixtureFile("fixture-dep/package.json", """
            { "name": "fixture-dep", "version": "1.0.0", "main": "index.js" }
            """);
        WriteFixtureFile("fixture-dep/index.js", "module.exports = 'installed';");
        WriteFixtureFile("package-lock.json", NodeFixtureLock("1.0.0"));
        var firstSha = InitializeGitRepository();

        var first = await RunExactNodeGate(firstSha);
        var warm = await RunExactNodeGate(firstSha);

        WriteFixtureFile("package-lock.json", NodeFixtureLock("1.0.1"));
        RunGit("add", "package-lock.json");
        RunGit("commit", "-q", "-m", "change lockfile");
        var changedSha = RunGit("rev-parse", "HEAD").Trim();
        var changed = await RunExactNodeGate(changedSha);

        Assert.Contains(first.DependencyCache, cache => cache.State == "miss" && cache.InstallRan);
        Assert.Contains(warm.DependencyCache, cache => cache.State == "hit" && !cache.InstallRan);
        Assert.Contains(changed.DependencyCache, cache =>
            cache.State == "miss"
            && cache.Reason == "lock-changed"
            && cache.InstallRan);
        Assert.Contains(changed.Processes, process =>
            process.Phase == "preparation" && process.Command == "npm ci");
    }

    [Fact]
    public async Task RunTimeoutNamesViolatedBudgetAndConsumption()
    {
        var result = await _runner.RunAsync(
            new BuildTestGateRequest(_root, null, "timeout-budget", RequireExactSubject: false),
            changedFiles: null,
            new BuildProfile { InstallCmd = "sleep 1", BuildCmds = ["exit 0"] },
            PostStepMode.Fail,
            TimeSpan.FromMilliseconds(100),
            CancellationToken.None);

        Assert.Equal(BuildTestGateFailureKind.Timeout, result.FailureKind);
        Assert.NotNull(result.ViolatedBudget);
        Assert.Equal("gate-run", result.ViolatedBudget!.Name);
        Assert.InRange(result.ViolatedBudget.LimitMs, 95, 105);
        Assert.True(result.ViolatedBudget.ConsumedMs >= 90);
        Assert.Contains("gate-run budget", result.Reason);
        Assert.Contains("limit=100ms", result.Reason);
    }

    [Fact]
    public async Task ExactSubjectDotNetFixture_RestoresBeforeNoRestoreBuildUnderBash()
    {
        WriteFixtureFile("Fixture.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <OutputType>Exe</OutputType>
              </PropertyGroup>
            </Project>
            """);
        WriteFixtureFile("Program.cs", "System.Console.WriteLine(\"gate fixture\");");
        WriteFixtureFile("Fixture.slnx", "<Solution><Project Path=\"Fixture.csproj\" /></Solution>");
        var sha = InitializeGitRepository();
        var profile = new BuildProfile
        {
            InstallCmd = "test -f Fixture.slnx && dotnet restore Fixture.slnx",
            BuildCmds = ["test -f Fixture.slnx && dotnet build Fixture.slnx --no-restore"],
        };

        var result = await _runner.RunAsync(
            new BuildTestGateRequest(_root, sha, "dotnet-fixture")
            {
                RequiredTestLevel = TestExecutionLevels.BuildOnly,
                InfrastructureTimeout = TimeSpan.FromSeconds(30),
            },
            changedFiles: null,
            profile,
            PostStepMode.Fail,
            TimeSpan.FromMinutes(2),
            CancellationToken.None);

        Assert.True(
            result.Verdict == BuildTestGateVerdict.Ok,
            $"{result.Reason}\n{result.Output}\n{string.Join("\n", result.Processes.Select(p => p.StandardError))}");
        Assert.Collection(result.Processes,
            restore =>
            {
                Assert.Equal("preparation", restore.Phase);
                Assert.Equal("bash", restore.FileName);
                Assert.Contains("dotnet restore Fixture.slnx", restore.Command);
            },
            build =>
            {
                Assert.Equal("verification", build.Phase);
                Assert.Equal("bash", build.FileName);
                Assert.Contains("--no-restore", build.Command);
                Assert.Equal(0, build.ExitCode);
            });
    }

    [Fact]
    public async Task ThreeParallelExactSubjectGatesSerializeUseDistinctWorkspacesAndCleanEachOne()
    {
        var sha = InitializeGitRepository();
        var profile = new BuildProfile
        {
            BuildCmds = ["sleep 1"],
        };
        var tasks = Enumerable.Range(1, 3).Select(index => _runner.RunAsync(
            new BuildTestGateRequest(_root, sha, $"executor-{index}")
            {
                AttemptChainId = $"attempt-{index}",
                InfrastructureTimeout = TimeSpan.FromSeconds(15),
            },
            changedFiles: null,
            profile,
            PostStepMode.Fail,
            TimeSpan.FromSeconds(10),
            CancellationToken.None)).ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.All(results, result =>
        {
            Assert.Equal(BuildTestGateVerdict.Ok, result.Verdict);
            Assert.Equal(sha, result.ExpectedSha);
            Assert.Equal(sha, result.TestedSha);
            Assert.False(string.IsNullOrWhiteSpace(result.Workspace));
            Assert.NotEqual(Path.GetFullPath(_root), Path.GetFullPath(result.Workspace!));
        });
        Assert.Equal(3, results.Select(result => result.Workspace).Distinct().Count());
        Assert.True(results.Count(result => result.GateCollisionDetected) >= 2);
        Assert.True(results.Where(result => result.GateCollisionDetected).All(result => result.GateQueueWaitMs > 0));
        Assert.All(results, result => Assert.False(Directory.Exists(result.Workspace)));
    }

    [Fact]
    public async Task MissingExactSubjectFailsClosedAsReviewInfrastructure()
    {
        InitializeGitRepository();

        var result = await _runner.RunAsync(
            new BuildTestGateRequest(_root, null, "executor"),
            changedFiles: null,
            new BuildProfile { BuildCmds = ["exit 0"] },
            PostStepMode.Fail,
            TimeSpan.FromSeconds(10),
            CancellationToken.None);

        Assert.Equal(BuildTestGateVerdict.Fail, result.Verdict);
        Assert.Equal(BuildTestGateFailureKind.MissingSource, result.FailureKind);
        Assert.True(result.IsInfrastructureFailure);
        Assert.Empty(result.Processes);
        Assert.Null(result.Workspace);
    }

    [Fact]
    public void ExactSubjectFetchTargets_NeverMaterializeTheOriginNamespace()
    {
        const string sha = "1111111111111111111111111111111111111111";
        var targets = BuildTestGateRunner.SubjectFetchTargets(
            sha,
            "agent-studio/results/run-1/fence-1/1111111");

        Assert.Equal(
            ["agent-studio/results/run-1/fence-1/1111111", sha],
            targets);
        Assert.DoesNotContain(targets, target => target.Contains("refs/heads/*", StringComparison.Ordinal));
        Assert.Equal([sha], BuildTestGateRunner.SubjectFetchTargets(sha, null));
    }

    // MachineBound 21.07.: leader-hold vs 100 ms queue-budget is a wall-clock race
    // that flakes when the shared machine gate is under real load on the host.
    [Trait("Category", "MachineBound")]
    [Fact]
    public async Task MachineGateWaitIsBoundedByExplicitQueueWaitTimeout()
    {
        const string activeFile = "machine-gate-sla-active.tmp";
        var leader = Run(new BuildProfile
        {
            BuildCmds = [$"touch {activeFile}; sleep 2; rm -f {activeFile}"],
        });
        var activePath = Path.Combine(_root, activeFile);
        for (var index = 0; index < 100 && !File.Exists(activePath); index++)
            await Task.Delay(25);
        Assert.True(File.Exists(activePath));

        // The queue wait is now budgeted separately from the run/infra SLA: a card
        // that explicitly caps its queue wait below the leader's hold still times
        // out. (AGT-2182 fix: the DEFAULT budget derives run-timeout+infra so a card
        // queued behind a real 15-25 min gate does NOT time out - see the sibling
        // QueuedGateWaitsThroughLongLeader... test.)
        var follower = await _runner.RunAsync(
            new BuildTestGateRequest(_root, null, "bounded", RequireExactSubject: false)
            {
                QueueWaitTimeout = TimeSpan.FromMilliseconds(100),
            },
            changedFiles: null,
            new BuildProfile { BuildCmds = ["exit 0"] },
            PostStepMode.Fail,
            TimeSpan.FromSeconds(10),
            CancellationToken.None);

        Assert.Equal(BuildTestGateFailureKind.Timeout, follower.FailureKind);
        Assert.True(follower.GateCollisionDetected);
        Assert.True(follower.GateQueueWaitMs >= 50);
        Assert.Equal(BuildTestGateVerdict.Ok, (await leader).Verdict);
    }

    // MachineBound 21.07.: the queued follower must out-wait a ~2 s leader hold; the
    // leader-hold vs queue-budget race is host-timing-sensitive under parallel load.
    [Trait("Category", "MachineBound")]
    [Fact]
    public async Task QueuedGateWaitsThroughLongLeaderWhenQueueBudgetExceedsInfraTimeout()
    {
        // Regression for the "Timeout persisted" cascade (AGT-2182, 21.07.): a card
        // with a SHORT infra-op SLA (100 ms) that queues behind a legitimately
        // running gate must wait the run out and pass - the queue wait is budgeted
        // against the run timeout, not the infra SLA. Before the fix the 100 ms infra
        // SLA also bounded the queue wait, so this follower escalated as Timeout.
        const string activeFile = "queue-budget-active.tmp";
        var leader = Run(new BuildProfile
        {
            BuildCmds = [$"touch {activeFile}; sleep 2; rm -f {activeFile}"],
        });
        var activePath = Path.Combine(_root, activeFile);
        for (var index = 0; index < 100 && !File.Exists(activePath); index++)
            await Task.Delay(25);
        Assert.True(File.Exists(activePath));

        var follower = await _runner.RunAsync(
            new BuildTestGateRequest(_root, null, "queued", RequireExactSubject: false)
            {
                // Short infra SLA, but no explicit queue cap: the derived budget is
                // run-timeout (10 s) + infra (0.1 s), which comfortably covers the
                // ~2 s leader hold.
                InfrastructureTimeout = TimeSpan.FromMilliseconds(100),
            },
            changedFiles: null,
            new BuildProfile { BuildCmds = ["exit 0"] },
            PostStepMode.Fail,
            TimeSpan.FromSeconds(10),
            CancellationToken.None);

        Assert.Equal(BuildTestGateVerdict.Ok, follower.Verdict);
        Assert.NotEqual(BuildTestGateFailureKind.Timeout, follower.FailureKind);
        Assert.True(follower.GateCollisionDetected);
        Assert.Equal(BuildTestGateVerdict.Ok, (await leader).Verdict);
    }

    // MachineBound: exercises a real Bash process and the machine-wide gate while
    // retaining enough output to overflow the bounded display ring.
    [Trait("Category", "MachineBound")]
    [Fact]
    public async Task LateLockEvidenceSurvivesRingBufferAndTimeoutTestNameIsNotMisclassified()
    {
        const string command =
            "i=1; while [ $i -le 350 ]; do echo line-$i; i=$((i+1)); done; " +
            "echo 'error MSB3027: file is locked' >&2; exit 1";

        var result = await Run(new BuildProfile { BuildCmds = [command] });

        Assert.Equal(BuildTestGateFailureKind.Lock, result.FailureKind);
        Assert.Contains("MSB3027", result.Output);
        Assert.DoesNotContain("line-1\n", result.Output);
        var process = Assert.Single(result.Processes);
        Assert.Contains("line-1", process.StandardOutput);
        Assert.Contains("line-350", process.StandardOutput);
        Assert.Equal(BuildTestGateFailureKind.None,
            BuildTestGateRunner.ClassifyFailure("TimeoutBehaviorTests passed"));
    }

    // NB: the AGT-2110 "Lock" misclassification and the AGT-2182 queue-budget fixes
    // are proven deterministically (no shared machine gate) in
    // BuildTestGateClassificationTests / BuildTestGateQueueBudgetTests below. Running
    // the fix end-to-end here would acquire the real machine-wide gate lock, which on
    // a busy host contends with the operator's live gates and flakes.

    [Fact]
    public async Task ConcurrentRuns_ForSameRepository_DoNotOverlapCommands()
    {
        const string activeFile = "build-gate-active.tmp";
        const string overlapFile = "build-gate-overlap.tmp";
        var holdCommand = $"touch {activeFile}; sleep 2; rm -f {activeFile}";
        var probeCommand = $"if [ -e {activeFile} ]; then touch {overlapFile}; fi";

        var first = Run(new BuildProfile { BuildCmds = [holdCommand] });
        var activePath = Path.Combine(_root, activeFile);
        for (var i = 0; i < 100 && !File.Exists(activePath); i++)
            await Task.Delay(25);
        Assert.True(File.Exists(activePath), "The first gate command did not enter its critical section.");

        var second = Run(new BuildProfile { BuildCmds = [probeCommand] });
        var results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.Equal(BuildTestGateVerdict.Ok, result.Verdict));
        Assert.False(File.Exists(Path.Combine(_root, overlapFile)),
            "Two verification command loops ran concurrently in the same repository.");
    }

    // MachineBound 19.07.: TCS/Cancellation-Timing flakt unter Parallellast im Karten-Gate.
    [Trait("Category", "MachineBound")]
    [Fact]
    public async Task CancellationDuringHostLoadWait_ReleasesRepositoryAdmission()
    {
        var loadThrottle = new CancelFirstLoadThrottle();
        var runner = new BuildTestGateRunner(
            NullLogger<BuildTestGateRunner>.Instance,
            loadThrottle);
        var profile = new BuildProfile { BuildCmds = ["exit 0"] };
        using var firstCancellation = new CancellationTokenSource();

        var canceled = runner.RunAsync(
            new BuildTestGateRequest(_root, null, "test", RequireExactSubject: false),
            changedFiles: null, profile, PostStepMode.Fail,
            TimeSpan.FromSeconds(30), firstCancellation.Token);
        await loadThrottle.FirstWaitEntered.WaitAsync(TimeSpan.FromSeconds(5));
        firstCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await canceled);

        using var followupCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var followup = await runner.RunAsync(
            new BuildTestGateRequest(_root, null, "test", RequireExactSubject: false),
            changedFiles: null, profile, PostStepMode.Fail,
            TimeSpan.FromSeconds(30), followupCancellation.Token);

        Assert.Equal(BuildTestGateVerdict.Ok, followup.Verdict);
    }

    // MachineBound 19.07.: Queue-Cancellation-Timing flakt unter Parallellast im Karten-Gate.
    [Trait("Category", "MachineBound")]
    [Fact]
    public async Task CancellationWhileQueued_DoesNotOpenAdmissionForAnotherRun()
    {
        const string activeFile = "build-gate-queued-active.tmp";
        const string overlapFile = "build-gate-queued-overlap.tmp";
        var holdCommand = $"touch {activeFile}; sleep 2; rm -f {activeFile}";
        var probeCommand = $"if [ -e {activeFile} ]; then touch {overlapFile}; fi";

        var leader = Run(new BuildProfile { BuildCmds = [holdCommand] });
        var activePath = Path.Combine(_root, activeFile);
        for (var i = 0; i < 100 && !File.Exists(activePath); i++)
            await Task.Delay(25);
        Assert.True(File.Exists(activePath), "The leader gate did not enter its critical section.");

        using var queuedCancellation = new CancellationTokenSource();
        var queued = _runner.RunAsync(
            new BuildTestGateRequest(_root, null, "test", RequireExactSubject: false),
            changedFiles: null, new BuildProfile { BuildCmds = ["exit 0"] },
            PostStepMode.Fail, TimeSpan.FromSeconds(30), queuedCancellation.Token);
        queuedCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await queued);

        var follower = Run(new BuildProfile { BuildCmds = [probeCommand] });
        var results = await Task.WhenAll(leader, follower);

        Assert.All(results, result => Assert.Equal(BuildTestGateVerdict.Ok, result.Verdict));
        Assert.False(File.Exists(Path.Combine(_root, overlapFile)),
            "Canceling a queued gate released an admission it did not own.");
    }

    private sealed class CancelFirstLoadThrottle : ILoadThrottleGate
    {
        private readonly TaskCompletionSource _firstWaitEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public LoadThrottleDecision Current => new(false, 0, TimeSpan.Zero);
        public bool WasRecentlyActive => false;
        public Task FirstWaitEntered => _firstWaitEntered.Task;

        public async Task WaitUntilReadyAsync(string reason, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) != 1) return;
            _firstWaitEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
    }

    private sealed class ThrowingPipelineHealthSensor : IPipelineHealthSensor
    {
        public void GateAcquired(PipelineGateContext gate)
            => throw new IOException("synthetic acquire observer failure");

        public void GateCompleted(PipelineGateCompletion completion)
            => throw new IOException("synthetic completion observer failure");

        public PipelineHealthSnapshot? Snapshot(string project, DateTime? nowUtc = null)
            => null;
    }

    private void WriteFixtureFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(
            _root,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private Task<BuildTestGateResult> RunExactNodeGate(string sha)
        => _runner.RunAsync(
            new BuildTestGateRequest(_root, sha, "node-lock-fixture")
            {
                RequiredTestLevel = TestExecutionLevels.BuildOnly,
                InfrastructureTimeout = TimeSpan.FromSeconds(30),
            },
            changedFiles: null,
            profile: null,
            PostStepMode.Fail,
            TimeSpan.FromMinutes(2),
            CancellationToken.None);

    private static string NodeFixtureLock(string version)
        => $$"""
            {
              "name": "gate-node-lock-fixture",
              "version": "1.0.0",
              "lockfileVersion": 3,
              "requires": true,
              "packages": {
                "": {
                  "name": "gate-node-lock-fixture",
                  "version": "1.0.0",
                  "dependencies": { "fixture-dep": "file:fixture-dep" }
                },
                "fixture-dep": { "name": "fixture-dep", "version": "{{version}}" },
                "node_modules/fixture-dep": { "resolved": "fixture-dep", "link": true }
              }
            }
            """;

    private string InitializeGitRepository()
    {
        RunGit("init", "-q", "-b", "main");
        RunGit("config", "user.email", "test@example.invalid");
        RunGit("config", "user.name", "Gate Test");
        File.WriteAllText(Path.Combine(_root, "subject.txt"), "exact subject");
        RunGit("add", "--all");
        RunGit("commit", "-q", "-m", "exact subject");
        return RunGit("rev-parse", "HEAD").Trim();
    }

    private string RunGit(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("git did not start");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {stderr}");
        return stdout;
    }
}

/// <summary>
/// Deterministic coverage for the AGT-2110 fix: a verify command that ran to
/// completion and merely PRINTED a lock / OOM string in its own output is a
/// code/test defect, not review infrastructure - so it never poisons the
/// environmental-retry budget. Only a genuine MSBuild build-output lock
/// (MSB3026/MSB3027) or a hard process signal (timeout / kill / launch failure)
/// stays infrastructure. Pure calls into
/// <see cref="BuildTestGateRunner.ClassifyFailure(BuildTestGateProcessEvidence)"/>
/// so the assertions never touch the shared machine gate.
/// </summary>
public sealed class BuildTestGateClassificationTests
{
    private static BuildTestGateProcessEvidence Evidence(
        int? exitCode = 1,
        string stdout = "",
        string stderr = "",
        bool timedOut = false,
        bool cancelled = false,
        string? launchError = null,
        string? terminationSignal = null)
        => new()
        {
            Command = "dotnet test",
            FileName = "cmd.exe",
            ExitCode = exitCode,
            StandardOutput = stdout,
            StandardError = stderr,
            TimedOut = timedOut,
            Cancelled = cancelled,
            LaunchError = launchError,
            TerminationSignal = terminationSignal,
        };

    [Fact]
    public void CompletedTestRun_LoggingAFileLockException_IsCode()
    {
        // The exact AGT-2110 signature: a finished test process (exit 1) whose
        // stdout carries a temp-file IOException. Before the fix this became Lock =
        // infrastructure and was retried twice (2x ~16 min) before escalating.
        var evidence = Evidence(exitCode: 1, stdout:
            "  Failed! System.IO.IOException : The process cannot access the file "
            + "'20260721101251571-acceptance-abc.db' because it is being used by another process.");

        var kind = BuildTestGateRunner.ClassifyFailure(evidence);

        Assert.Equal(BuildTestGateFailureKind.Code, kind);
    }

    [Theory]
    [InlineData("error MSB3027: Could not write to output file 'App.dll' because it is being used by another process.")]
    [InlineData("error MSB3026: Could not copy 'App.dll' to 'bin'. The file is locked by another process.")]
    public void CompletedBuild_WithGenuineMsbOutputLock_StaysLock(string stderr)
    {
        // A real build-output lock (a running service holding the DLL) IS a
        // retryable host fault and must remain infrastructure even though the build
        // process exited normally.
        var kind = BuildTestGateRunner.ClassifyFailure(Evidence(exitCode: 1, stderr: stderr));

        Assert.Equal(BuildTestGateFailureKind.Lock, kind);
    }

    [Fact]
    public void CompletedProcess_WithoutAnyInfraSignal_IsCode()
    {
        var kind = BuildTestGateRunner.ClassifyFailure(Evidence(exitCode: 1, stdout: "3 tests failed"));

        Assert.Equal(BuildTestGateFailureKind.Code, kind);
    }

    [Fact]
    public void UnfinishedProcess_WithLockString_StaysLock_Conservatively()
    {
        // No exit code means the process did not complete on its own - we cannot
        // attribute the lock string to a reported test result, so stay conservative
        // and treat it as (retryable) infrastructure.
        var kind = BuildTestGateRunner.ClassifyFailure(
            Evidence(exitCode: null, stderr: "the file is being used by another process"));

        Assert.Equal(BuildTestGateFailureKind.Lock, kind);
    }

    [Fact]
    public void TimedOutProcess_StaysTimeout_RegardlessOfOutput()
    {
        var kind = BuildTestGateRunner.ClassifyFailure(
            Evidence(exitCode: null, timedOut: true, stdout: "being used by another process"));

        Assert.Equal(BuildTestGateFailureKind.Timeout, kind);
    }

    [Fact]
    public void KilledProcess_Exit137_StaysOutOfMemory()
    {
        var kind = BuildTestGateRunner.ClassifyFailure(Evidence(exitCode: 137));

        Assert.Equal(BuildTestGateFailureKind.OutOfMemory, kind);
    }

    [Fact]
    public void LaunchFailure_StaysProcessLaunch()
    {
        var kind = BuildTestGateRunner.ClassifyFailure(
            Evidence(exitCode: null, launchError: "executable file not found"));

        Assert.Equal(BuildTestGateFailureKind.ProcessLaunch, kind);
    }
}

/// <summary>
/// Deterministic coverage for the AGT-2182 fix: the machine-gate QUEUE wait is
/// budgeted separately from the short infra-op SLA, so a card queued behind a
/// legitimately running 15-25 min gate is not escalated as "Timeout persisted".
/// Pure calls into <see cref="BuildTestGateRunner.ResolveQueueWaitTimeout"/> - no
/// machine gate, no wall-clock race.
/// </summary>
public sealed class BuildTestGateQueueBudgetTests
{
    [Fact]
    public void ExplicitConfiguredQueueWait_Wins()
    {
        var resolved = BuildTestGateRunner.ResolveQueueWaitTimeout(
            TimeSpan.FromMinutes(45), TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(2));

        Assert.Equal(TimeSpan.FromMinutes(45), resolved);
    }

    [Fact]
    public void UnsetQueueWait_DerivesRunPlusInfra()
    {
        // The production wiring: run timeout 20 min + infra 2 min = 22 min, which
        // comfortably out-waits a real gate ahead - unlike the old 2 min infra SLA.
        var resolved = BuildTestGateRunner.ResolveQueueWaitTimeout(
            configured: null, TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(2));

        Assert.Equal(TimeSpan.FromMinutes(22), resolved);
    }

    [Fact]
    public void DerivedQueueWait_FarExceedsInfraTimeout()
    {
        // The heart of the cascade fix: the queue budget must be MUCH larger than
        // the infra SLA that used to (wrongly) bound it.
        var infra = TimeSpan.FromSeconds(120);
        var resolved = BuildTestGateRunner.ResolveQueueWaitTimeout(
            configured: null, TimeSpan.FromSeconds(3600), infra);

        Assert.True(resolved > infra);
        Assert.Equal(TimeSpan.FromSeconds(3720), resolved);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void NonPositiveConfiguredQueueWait_FallsBackToDerived(int seconds)
    {
        var resolved = BuildTestGateRunner.ResolveQueueWaitTimeout(
            TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(300), TimeSpan.FromSeconds(120));

        Assert.Equal(TimeSpan.FromSeconds(420), resolved);
    }

    [Fact]
    public void AllNonPositive_FallsBackToTwoMinuteFloor()
    {
        var resolved = BuildTestGateRunner.ResolveQueueWaitTimeout(
            configured: null, TimeSpan.Zero, TimeSpan.Zero);

        Assert.Equal(TimeSpan.FromMinutes(2), resolved);
    }
}
