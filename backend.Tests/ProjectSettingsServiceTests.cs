using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

public sealed class ProjectSettingsServiceTests : IDisposable
{
    private readonly string _workspace;

    public ProjectSettingsServiceTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "rdo-project-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Get_UnconfiguredProject_DefaultsAutoCommitOn()
    {
        var svc = Build();

        var settings = svc.Get("new-project");

        Assert.True(settings.AutoCommit);
        Assert.True(settings.AutomaticFailureContinuationsEnabled);
        Assert.Equal(AutoPushStrategies.AlwaysImmediate, settings.AutoPushStrategy);
    }

    [Fact]
    public void Get_ProjectEntryWithoutAutoCommit_DefaultsAutoCommitOn()
    {
        File.WriteAllText(StorePath(), """
        {
          "new-project": {
            "RunnerMode": "manual"
          }
        }
        """);
        var svc = Build();

        var settings = svc.Get("new-project");

        Assert.True(settings.AutoCommit);
    }

    [Fact]
    public void Get_ExplicitFalseAutoCommit_RemainsOff()
    {
        File.WriteAllText(StorePath(), """
        {
          "runbook": {
            "AutoCommit": false
          }
        }
        """);
        var svc = Build();

        var settings = svc.Get("runbook");

        Assert.False(settings.AutoCommit);
    }

    [Fact]
    public void SetAutoCommit_ReplacesCachedValueImmediately()
    {
        var svc = Build();

        svc.SetAutoCommit("demo", false);
        Assert.False(svc.Get("demo").AutoCommit);

        svc.SetAutoCommit("demo", true);
        Assert.True(svc.Get("demo").AutoCommit);
    }

    [Fact]
    public void AutomaticFailureContinuationSetting_PersistsPerProject()
    {
        var svc = Build();
        svc.SetAutomaticFailureContinuationsEnabled("demo", false);

        Assert.False(Build().Get("demo").AutomaticFailureContinuationsEnabled);
        Assert.True(Build().Get("other").AutomaticFailureContinuationsEnabled);
    }

    [Fact]
    public void SetExecutionRunner_Persists_assignment_and_remote_eligibility_together()
    {
        var svc = Build();

        svc.SetExecutionRunner("demo", " runner-01 ", remoteExecutionEnabled: false);

        var current = svc.Get("demo");
        Assert.Equal("runner-01", current.ExecutionRunner);
        Assert.False(current.RemoteExecutionEnabled);

        var reloaded = Build().Get("demo");
        Assert.Equal("runner-01", reloaded.ExecutionRunner);
        Assert.False(reloaded.RemoteExecutionEnabled);

        svc.SetExecutionRunner("demo", "  ");
        Assert.Null(svc.Get("demo").ExecutionRunner);
        Assert.False(svc.Get("demo").RemoteExecutionEnabled);
    }

    [Theory]
    [InlineData("auto-continuous", PickupModes.Auto, ExecutionLocations.Local)]
    [InlineData("manual", PickupModes.Manual, ExecutionLocations.Local)]
    [InlineData("paused", PickupModes.Paused, ExecutionLocations.Local)]
    [InlineData("runner-01", PickupModes.Auto, "runner-01")]
    public void LegacyExecutionRunner_IsReadCompatibly_AndMigratedOnNextWrite(
        string legacyValue,
        string expectedPickupMode,
        string expectedLocation)
    {
        File.WriteAllText(StorePath(), $$"""
        {
          "legacy": {
            "ExecutionRunner": "{{legacyValue}}"
          }
        }
        """);
        var svc = Build();

        var loaded = svc.Get("legacy");
        Assert.Equal(expectedPickupMode, loaded.PickupMode);
        Assert.Equal(expectedLocation, loaded.ExecutionLocation);

        svc.SetAutoCommit("legacy", false);

        using var persisted = System.Text.Json.JsonDocument.Parse(File.ReadAllText(StorePath()));
        var project = persisted.RootElement.GetProperty("legacy");
        Assert.Equal(expectedPickupMode, project.GetProperty("PickupMode").GetString());
        Assert.Equal(expectedLocation, project.GetProperty("ExecutionLocation").GetString());
    }

    [Fact]
    public void SetExecutionSettings_PausedRemote_PreservesLocationAcrossReload()
    {
        var svc = Build();

        svc.SetExecutionSettings("demo", PickupModes.Auto, "runner-01");
        svc.SetExecutionSettings("demo", PickupModes.Paused, executionLocation: null);

        var reloaded = Build().Get("demo");
        Assert.Equal(PickupModes.Paused, reloaded.PickupMode);
        Assert.Equal("runner-01", reloaded.ExecutionLocation);
        Assert.False(ProjectExecutionPolicy.AllowsAutomaticPickup(reloaded));
        Assert.True(ProjectExecutionPolicy.IsAssignedRemote(reloaded, "runner-01"));
    }

    [Fact]
    public void RekeyProject_MovesAllSettingsAndUpdatesRunnerAcrossReload()
    {
        var svc = Build();
        svc.SetAutoCommit("Old Project", false);
        svc.SetExecutionRunner("Old Project", "runner-old", remoteExecutionEnabled: false);

        var updated = svc.RekeyProject(
            "Old Project",
            "New Project",
            updateExecutionRunner: true,
            executionRunner: " runner-new ",
            remoteExecutionEnabled: true);

        Assert.False(updated.AutoCommit);
        Assert.Equal("runner-new", updated.ExecutionRunner);
        Assert.True(updated.RemoteExecutionEnabled);
        Assert.DoesNotContain("Old Project", svc.GetAll().Keys);

        var reloaded = Build();
        Assert.DoesNotContain("Old Project", reloaded.GetAll().Keys);
        Assert.False(reloaded.Get("New Project").AutoCommit);
        Assert.Equal("runner-new", reloaded.Get("New Project").ExecutionRunner);
        Assert.True(reloaded.Get("New Project").RemoteExecutionEnabled);
    }

    [Fact]
    public void RekeyProject_OldRunnerReadsAndWritesThroughLiveAlias()
    {
        var svc = Build();
        svc.SetExecutionRunner("Old Project", "runner-before", remoteExecutionEnabled: true);
        svc.RekeyProject("Old Project", "New Project");

        svc.SetExecutionRunner("New Project", "runner-after", remoteExecutionEnabled: true);
        Assert.Equal("runner-after", svc.Get("Old Project").ExecutionRunner);

        // A runner constructed before the rename persists mode with its captured
        // old name. The write must land on the new key, not recreate an orphan.
        svc.SetRunnerMode("Old Project", "paused", source: "system");
        Assert.Equal("paused", svc.Get("New Project").RunnerMode);
        Assert.DoesNotContain("Old Project", svc.GetAll().Keys);

        var reloaded = Build();
        Assert.Equal("runner-after", reloaded.Get("New Project").ExecutionRunner);
        Assert.Equal("paused", reloaded.Get("New Project").RunnerMode);
        Assert.DoesNotContain("Old Project", reloaded.GetAll().Keys);
    }

    [Fact]
    public void RekeyProject_PersistFailure_RestoresCacheAndDurableKey()
    {
        var writer = new ControllableAtomicJsonFileWriter();
        var svc = Build(writer);
        svc.SetAutoCommit("Old Project", false);
        svc.SetExecutionRunner("Old Project", "runner-before", remoteExecutionEnabled: true);
        var durableBefore = File.ReadAllText(StorePath());
        writer.ShouldFail = (path, _) => string.Equals(path, StorePath(), StringComparison.OrdinalIgnoreCase);

        Assert.Throws<ProjectPersistenceException>(() => svc.RekeyProject(
            "Old Project",
            "New Project",
            updateExecutionRunner: true,
            executionRunner: "runner-after",
            remoteExecutionEnabled: true));

        Assert.Contains("Old Project", svc.GetAll().Keys);
        Assert.DoesNotContain("New Project", svc.GetAll().Keys);
        Assert.False(svc.Get("Old Project").AutoCommit);
        Assert.Equal("runner-before", svc.Get("Old Project").ExecutionRunner);
        Assert.Equal(durableBefore, File.ReadAllText(StorePath()));

        var reloaded = Build();
        Assert.Contains("Old Project", reloaded.GetAll().Keys);
        Assert.DoesNotContain("New Project", reloaded.GetAll().Keys);
        Assert.Equal("runner-before", reloaded.Get("Old Project").ExecutionRunner);
    }

    [Fact]
    public void SetAutoCommit_PersistsExplicitFalseAcrossReload()
    {
        var svc = Build();
        svc.SetAutoCommit("runbook", false);

        var reloaded = Build();

        Assert.False(reloaded.Get("runbook").AutoCommit);
    }

    [Fact]
    public void SetAutoPushStrategy_NormalizesAndPersistsAcrossReload()
    {
        var svc = Build();

        svc.SetAutoPushStrategy("runbook", "ALWAYS-IMMEDIATE");

        var reloaded = Build();
        Assert.Equal(AutoPushStrategies.AlwaysImmediate, reloaded.Get("runbook").AutoPushStrategy);
    }

    [Fact]
    public void SetAutoPushStrategy_InvalidValueFallsBackToDefault()
    {
        var svc = Build();

        svc.SetAutoPushStrategy("runbook", "ship-it");

        Assert.Equal(AutoPushStrategies.AlwaysImmediate, svc.Get("runbook").AutoPushStrategy);
    }

    [Fact]
    public void Get_UnconfiguredProject_DefaultsParallelismKnobs()
    {
        var svc = Build();

        var settings = svc.Get("new-project");

        Assert.Equal(1, settings.MaxParallelism);
        Assert.Equal(string.Empty, settings.IntegrationBranch);
        Assert.Equal(IntegrationStrategies.DirectMerge, settings.IntegrationStrategy);
    }

    [Fact]
    public void SetMaxParallelism_PersistsAcrossReload()
    {
        var svc = Build();

        svc.SetMaxParallelism("runbook", 4);

        var reloaded = Build();
        Assert.Equal(4, reloaded.Get("runbook").MaxParallelism);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void SetMaxParallelism_ClampsBelowOneToOne(int value)
    {
        var svc = Build();

        svc.SetMaxParallelism("runbook", value);

        Assert.Equal(1, svc.Get("runbook").MaxParallelism);
    }

    [Fact]
    public void SetIntegrationBranch_PersistsAcrossReload()
    {
        var svc = Build();

        svc.SetIntegrationBranch("runbook", "integration");

        var reloaded = Build();
        Assert.Equal("integration", reloaded.Get("runbook").IntegrationBranch);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void SetIntegrationBranch_BlankRevertsToRepositoryDefault(string? value)
    {
        var svc = Build();
        svc.SetIntegrationBranch("runbook", "integration");

        svc.SetIntegrationBranch("runbook", value);

        Assert.Equal(string.Empty, svc.Get("runbook").IntegrationBranch);
    }

    [Fact]
    public void SetIntegrationStrategy_NormalizesAndPersistsAcrossReload()
    {
        var svc = Build();

        svc.SetIntegrationStrategy("runbook", "PULL-REQUEST");

        var reloaded = Build();
        Assert.Equal(IntegrationStrategies.PullRequest, reloaded.Get("runbook").IntegrationStrategy);
    }

    [Fact]
    public void SetIntegrationStrategy_InvalidValueFallsBackToDefault()
    {
        var svc = Build();

        svc.SetIntegrationStrategy("runbook", "rebase-and-pray");

        Assert.Equal(IntegrationStrategies.DirectMerge, svc.Get("runbook").IntegrationStrategy);
    }

    [Fact]
    public void SetCliExecutionEngine_OverridePersistsCanonicalValueAcrossReload()
    {
        var svc = Build();

        svc.SetCliExecutionEngine("runbook", " LEGACY ");

        var reloaded = Build();
        Assert.Equal(CliExecutionEngines.Legacy, reloaded.Get("runbook").CliExecutionEngine);
    }

    [Fact]
    public void SetCliExecutionEngine_BlankClearsOverride()
    {
        var svc = Build();
        svc.SetCliExecutionEngine("runbook", CliExecutionEngines.Legacy);

        svc.SetCliExecutionEngine("runbook", "   ");

        Assert.Null(svc.Get("runbook").CliExecutionEngine);
    }

    [Fact]
    public void SetCliExecutionEngine_InvalidValueIsRejectedWithoutMutation()
    {
        var svc = Build();
        svc.SetCliExecutionEngine("runbook", CliExecutionEngines.Legacy);

        Assert.Throws<ArgumentException>(() =>
            svc.SetCliExecutionEngine("runbook", "automatic"));
        Assert.Equal(CliExecutionEngines.Legacy, svc.Get("runbook").CliExecutionEngine);
    }

    [Fact]
    public void ResolveCliMode_UnconfiguredProject_DefaultsToYolo()
    {
        var svc = Build();

        var r = svc.ResolveCliMode("new-project", CliTypes.Claude);

        Assert.Equal(CliPermissionModes.Yolo, r.Mode);
        Assert.Equal(CliPermissionSources.Default, r.Source);
        Assert.Equal(["--dangerously-skip-permissions"], r.Args);
    }

    [Fact]
    public void SetCliMode_OverridePersistsAcrossReloadAndIsSourcedToProject()
    {
        var svc = Build();

        svc.SetCliMode("runbook", CliTypes.Codex, CliPermissionModes.ReadOnly);

        var reloaded = Build();
        var r = reloaded.ResolveCliMode("runbook", CliTypes.Codex);
        Assert.Equal(CliPermissionModes.ReadOnly, r.Mode);
        Assert.Equal(CliPermissionSources.Project, r.Source);
        Assert.Equal(["--sandbox", "read-only"], r.Args);
    }

    [Fact]
    public void SetCliMode_BlankClearsOverrideRevertingToDefault()
    {
        var svc = Build();
        svc.SetCliMode("runbook", CliTypes.Gemini, CliPermissionModes.WorkspaceWrite);
        Assert.Equal(CliPermissionSources.Project, svc.ResolveCliMode("runbook", CliTypes.Gemini).Source);

        svc.SetCliMode("runbook", CliTypes.Gemini, null);

        var r = svc.ResolveCliMode("runbook", CliTypes.Gemini);
        Assert.Equal(CliPermissionModes.Yolo, r.Mode);
        Assert.Equal(CliPermissionSources.Default, r.Source);
        Assert.Null(svc.Get("runbook").CliModes);
    }

    [Fact]
    public void SetCliMode_UnknownCli_IsIgnored()
    {
        var svc = Build();

        svc.SetCliMode("runbook", "not-a-cli", CliPermissionModes.ReadOnly);

        Assert.Null(svc.Get("runbook").CliModes);
    }

    [Fact]
    public void SetCliMode_OnlyOverridesTheNamedCli_OthersStayDefault()
    {
        var svc = Build();

        svc.SetCliMode("runbook", CliTypes.Claude, CliPermissionModes.ReadOnly);

        Assert.Equal(CliPermissionSources.Project, svc.ResolveCliMode("runbook", CliTypes.Claude).Source);
        // Gemini has no global-config probe, so an un-overridden CLI is
        // always the platform default here regardless of the host's ~/.codex.
        Assert.Equal(CliPermissionSources.Default, svc.ResolveCliMode("runbook", CliTypes.Gemini).Source);
    }

    // --- T1b / ASS-1742: per-project / per-task context mode --------------

    [Fact]
    public void ResolveContextMode_UnconfiguredProject_DefaultsToClean()
    {
        var svc = Build();

        var r = svc.ResolveContextMode("new-project", CliTypes.Claude);

        Assert.Equal(CliContextModes.Clean, r.Mode);
        Assert.Equal(CliContextModeSources.Default, r.Source);
        Assert.True(r.Supported); // Claude can isolate
    }

    [Fact]
    public void ResolveContextMode_SharedOnlyCli_ReportsUnsupported()
    {
        var svc = Build();

        var gemini = svc.ResolveContextMode("new-project", CliTypes.Gemini);
        Assert.False(gemini.Supported);
    }

    [Fact]
    public void SetCliContextMode_OverridePersistsAcrossReloadAndIsSourcedToProject()
    {
        var svc = Build();

        svc.SetCliContextMode("runbook", CliTypes.Codex, CliContextModes.Shared);

        var reloaded = Build();
        var r = reloaded.ResolveContextMode("runbook", CliTypes.Codex);
        Assert.Equal(CliContextModes.Shared, r.Mode);
        Assert.Equal(CliContextModeSources.Project, r.Source);
        Assert.True(r.Supported);
    }

    [Fact]
    public void SetCliContextMode_BlankClearsOverrideRevertingToCleanDefault()
    {
        var svc = Build();
        svc.SetCliContextMode("runbook", CliTypes.Claude, CliContextModes.Shared);
        Assert.Equal(CliContextModeSources.Project, svc.ResolveContextMode("runbook", CliTypes.Claude).Source);

        svc.SetCliContextMode("runbook", CliTypes.Claude, null);

        var r = svc.ResolveContextMode("runbook", CliTypes.Claude);
        Assert.Equal(CliContextModes.Clean, r.Mode);
        Assert.Equal(CliContextModeSources.Default, r.Source);
        Assert.Null(svc.Get("runbook").CliContextModes);
    }

    [Fact]
    public void ResolveContextMode_TaskOverrideBeatsProjectOverride()
    {
        var svc = Build();
        svc.SetCliContextMode("runbook", CliTypes.Codex, CliContextModes.Shared);

        // Task asks clean explicitly -> wins over the project's shared override.
        var r = svc.ResolveContextMode("runbook", CliTypes.Codex, taskOverride: CliContextModes.Clean);

        Assert.Equal(CliContextModes.Clean, r.Mode);
        Assert.Equal(CliContextModeSources.Task, r.Source);
    }

    [Fact]
    public void ResolveContextMode_BlankTaskOverrideFallsThroughToProject()
    {
        var svc = Build();
        svc.SetCliContextMode("runbook", CliTypes.Codex, CliContextModes.Shared);

        var r = svc.ResolveContextMode("runbook", CliTypes.Codex, taskOverride: "   ");

        Assert.Equal(CliContextModes.Shared, r.Mode);
        Assert.Equal(CliContextModeSources.Project, r.Source);
    }

    [Fact]
    public void SetCliContextMode_UnknownCli_IsIgnored()
    {
        var svc = Build();

        svc.SetCliContextMode("runbook", "not-a-cli", CliContextModes.Shared);

        Assert.Null(svc.Get("runbook").CliContextModes);
    }

    [Fact]
    public void SetPipelineStepOrder_NormalizesAndPersistsAcrossReload()
    {
        var svc = Build();

        svc.SetPipelineStepOrder("runbook",
            [" post-lint-scss ", "", "POST-LINT-SCSS", "aspect-code-quality"]);

        var reloaded = Build();

        Assert.Equal(["post-lint-scss", "aspect-code-quality"],
            reloaded.Get("runbook").PipelineStepOrderByType![PipelineTypes.Task]);
    }

    [Fact]
    public void SetPipelineStepOrder_EmptyClearsOverride()
    {
        var svc = Build();
        svc.SetPipelineStepOrder("runbook", ["post-lint-scss"]);

        svc.SetPipelineStepOrder("runbook", []);

        Assert.Null(svc.Get("runbook").PipelineStepOrder);
    }

    [Fact]
    public void TypedPipelineWrites_StayIsolatedAcrossTaskTypes()
    {
        var svc = Build();

        svc.SetPipelineStep(
            "runbook",
            PipelineTypes.Bug,
            PipelineCatalogue.LintScssStepId,
            new PipelineStepSetting { Enabled = false });
        svc.SetPipelineStepOrder(
            "runbook",
            PipelineTypes.Feature,
            [PipelineCatalogue.BuildTestGateStepId]);

        var reloaded = Build().Get("runbook");
        Assert.False(reloaded.PipelineStepsByType![PipelineTypes.Bug]
            [PipelineCatalogue.LintScssStepId].Enabled);
        Assert.Null(PipelineTypeSettings.ForType(reloaded, PipelineTypes.Task)!.PipelineSteps);
        Assert.Equal(
            PipelineCatalogue.BuildTestGateStepId,
            reloaded.PipelineStepOrderByType![PipelineTypes.Feature].Single());
    }

    [Fact]
    public void Get_MigratesLegacyFlatPipelineConfigToThreeCodingTypesOnly()
    {
        File.WriteAllText(StorePath(), """
        {
          "runbook": {
            "pipelineSteps": {
              "post-lint-scss": { "enabled": false },
              "post-build-test-gate": { "enabled": false }
            },
            "pipelineStepOrder": [ "post-lint-scss" ],
            "pipelineStepsByType": {
              "bug": {
                "post-build-test-gate": { "enabled": true }
              }
            }
          }
        }
        """);

        var settings = Build().Get("runbook");

        foreach (var type in PipelineTypes.LegacyCodingTypes)
        {
            Assert.False(settings.PipelineStepsByType![type]
                [PipelineCatalogue.LintScssStepId].Enabled);
            Assert.Equal(
                type == PipelineTypes.Bug,
                settings.PipelineStepsByType[type]
                    [PipelineCatalogue.BuildTestGateStepId].Enabled);
            Assert.Equal(
                PipelineCatalogue.LintScssStepId,
                settings.PipelineStepOrderByType![type].Single());
        }
        Assert.False(settings.PipelineStepsByType!.ContainsKey(PipelineTypes.Planning));
        Assert.False(settings.PipelineStepOrderByType!.ContainsKey(PipelineTypes.Planning));
        Assert.Null(settings.PipelineSteps);
        Assert.Null(settings.PipelineStepOrder);

        var persisted = File.ReadAllText(StorePath());
        Assert.Contains("\"PipelineStepsByType\"", persisted);
        Assert.DoesNotContain("\"PipelineSteps\":", persisted);
        Assert.DoesNotContain("\"PipelineStepOrder\":", persisted);
    }

    [Fact]
    public void Get_UnconfiguredProject_HasNoBuildProfile()
    {
        var svc = Build();

        Assert.Null(svc.Get("new-project").BuildProfile);
    }

    [Fact]
    public void SetBuildProfile_NormalizesAndStartsDeclared_PersistsAcrossReload()
    {
        var svc = Build();

        svc.SetBuildProfile("runbook", new BuildProfile
        {
            Stack = "  node  ",
            InstallCmd = "  npm ci  ",
            BuildCmds = ["npm run build", "  ", ""],
            Lockfiles = ["package-lock.json", "  "],
            PreserveGlobs = ["node_modules"],
            PoolSize = 0,
            Status = BuildProfileStatuses.PipelineReady, // must be forced back to declared
        });

        var reloaded = Build().Get("runbook").BuildProfile;
        Assert.NotNull(reloaded);
        Assert.Equal("node", reloaded!.Stack);
        Assert.Equal("npm ci", reloaded.InstallCmd);
        Assert.Equal(["npm run build"], reloaded.BuildCmds);
        Assert.Equal(["package-lock.json"], reloaded.Lockfiles);
        Assert.Null(reloaded.PoolSize); // non-positive clamps to null
        Assert.Equal(BuildProfileStatuses.Declared, reloaded.Status);
    }

    [Fact]
    public void SetTestExecution_NormalizesLanePolicyAndPersistsAcrossReload()
    {
        var svc = Build();
        svc.SetTestExecution("runbook", new TestExecutionPolicy
        {
            LaneLevels = new()
            {
                [" 4-auto-review "] = " FULL ",
                ["2-ready"] = "unknown",
            },
            ContinuousCommands = [" smoke ", "", "SMOKE"],
            ImpactRules = [new TestImpactRule
            {
                PathPrefixes = [" src/api ", ""],
                TestCommands = [" test-api ", ""],
                Reason = " api ownership ",
            }],
            MappedSourceRoots = [" src ", "src"],
            FolderToTestProjects = [new TestFolderMapping
            {
                Folder = " src/api/ ",
                Module = " api ",
                TestProjects = [" tests/Api.Tests/Api.Tests.csproj ", ""],
            }],
            TestHubHistoryPath = " .test-hub/history.jsonl ",
        });

        var policy = Build().Get("runbook").TestExecution;

        Assert.NotNull(policy);
        Assert.Equal(TestExecutionLevels.Full, policy!.LaneLevels![TaskStates.AutoReview]);
        Assert.Equal(TestExecutionLevels.WorkPackage, policy.LaneLevels![TaskStates.Ready]);
        Assert.Single(policy.ContinuousCommands!);
        Assert.Equal("smoke", policy.ContinuousCommands![0], ignoreCase: true);
        var rule = Assert.Single(policy.ImpactRules!);
        Assert.Equal(["src/api"], rule.PathPrefixes);
        Assert.Equal(["test-api"], rule.TestCommands);
        Assert.Equal("api ownership", rule.Reason);
        Assert.Equal(["src"], policy.MappedSourceRoots);
        var mapping = Assert.Single(policy.FolderToTestProjects!);
        Assert.Equal("src/api", mapping.Folder);
        Assert.Equal("api", mapping.Module);
        Assert.Equal(["tests/Api.Tests/Api.Tests.csproj"], mapping.TestProjects);
        Assert.Equal(".test-hub/history.jsonl", policy.TestHubHistoryPath);
    }

    [Fact]
    public void MarkBuildProfileValidated_FlipsToPipelineReadyAndStampsTime()
    {
        var svc = Build();
        svc.SetBuildProfile("runbook", new BuildProfile { InstallCmd = "npm ci" });

        svc.MarkBuildProfileValidated("runbook");

        var p = svc.Get("runbook").BuildProfile;
        Assert.Equal(BuildProfileStatuses.PipelineReady, p!.Status);
        Assert.NotNull(p.LastValidatedAt);
        Assert.Null(p.LastValidationError);
    }

    [Fact]
    public void MarkBuildProfileValidationFailed_RecordsReason()
    {
        var svc = Build();
        svc.SetBuildProfile("runbook", new BuildProfile { InstallCmd = "npm ci" });

        svc.MarkBuildProfileValidationFailed("runbook", "install exited 1");

        var p = svc.Get("runbook").BuildProfile;
        Assert.Equal(BuildProfileStatuses.ValidationFailed, p!.Status);
        Assert.Equal("install exited 1", p.LastValidationError);
    }

    [Fact]
    public void EditingValidatedBuildProfile_LeavesThreeRunRevalidationGrace()
    {
        var svc = Build();
        svc.SetBuildProfile("runbook", new BuildProfile { InstallCmd = "npm ci" });
        svc.MarkBuildProfileValidated("runbook");
        Assert.Equal(BuildProfileStatuses.PipelineReady, svc.Get("runbook").BuildProfile!.Status);

        svc.SetBuildProfile("runbook", new BuildProfile { InstallCmd = "npm install" });

        var edited = svc.Get("runbook").BuildProfile!;
        Assert.Equal(BuildProfileStatuses.PipelineReady, edited.Status);
        Assert.True(edited.RevalidationPending);
        Assert.Equal(BuildProfile.DefaultRevalidationGraceRuns, edited.RevalidationGraceRunsRemaining);
        Assert.True(BuildProfileGate.AllowsAutoPickup(edited));
    }

    [Fact]
    public void RevalidationGrace_ClosesAfterThreeActualRunAdmissions()
    {
        var svc = Build();
        svc.SetBuildProfile("runbook", new BuildProfile { InstallCmd = "npm ci" });
        svc.MarkBuildProfileValidated("runbook");
        svc.SetBuildProfile("runbook", new BuildProfile { InstallCmd = "npm install" });

        Assert.Equal(2, svc.ConsumeBuildProfileRevalidationGraceRun("runbook"));
        svc.SetBuildProfile("runbook", new BuildProfile { InstallCmd = "pnpm install" });
        Assert.Equal(2, svc.Get("runbook").BuildProfile!.RevalidationGraceRunsRemaining);
        Assert.Equal(1, svc.ConsumeBuildProfileRevalidationGraceRun("runbook"));
        Assert.Equal(0, svc.ConsumeBuildProfileRevalidationGraceRun("runbook"));

        var exhausted = svc.Get("runbook").BuildProfile!;
        Assert.True(exhausted.RevalidationPending);
        Assert.False(BuildProfileGate.AllowsAutoPickup(exhausted));
        Assert.Null(svc.ConsumeBuildProfileRevalidationGraceRun("runbook"));
    }

    [Fact]
    public void GreenRemoteReview_ValidatesOnlyTheExactProfileFingerprint()
    {
        var svc = Build();
        svc.SetBuildProfile("runbook", new BuildProfile { BuildCmds = ["dotnet build"] });
        var fingerprint = BuildProfileValidationFingerprint.Create(svc.Get("runbook").BuildProfile)!;

        Assert.False(svc.MarkBuildProfileRemotelyValidated(
            "runbook", "stale-fingerprint", "review-old", "review-runner", "abc123", DateTime.UtcNow));
        Assert.True(svc.MarkBuildProfileRemotelyValidated(
            "runbook", fingerprint, "review-current", "review-runner", "def456", DateTime.UtcNow));

        var validated = svc.Get("runbook").BuildProfile!;
        Assert.Equal(BuildProfileStatuses.PipelineReady, validated.Status);
        Assert.False(validated.RevalidationPending);
        Assert.Equal("review-current", validated.LastRemoteValidationAttemptId);
        Assert.Equal("review-runner", validated.LastRemoteValidationRunnerId);
        Assert.Equal("def456", validated.LastRemoteValidationResultSha);
        Assert.NotNull(validated.LastRemoteValidationAt);
    }

    [Fact]
    public void SetBuildProfileNull_ClearsProfile()
    {
        var svc = Build();
        svc.SetBuildProfile("runbook", new BuildProfile { InstallCmd = "npm ci" });

        svc.SetBuildProfile("runbook", null);

        Assert.Null(svc.Get("runbook").BuildProfile);
    }

    [Fact]
    public void MarkValidating_OnProjectWithoutProfile_IsNoOp()
    {
        var svc = Build();

        svc.MarkBuildProfileValidated("new-project");

        Assert.Null(svc.Get("new-project").BuildProfile);
    }

    // --- ASS-1753: runner-mode durability across restart -----------------

    /// <summary>
    /// A <c>user</c>-sourced mode change (the operator toggle) advances BOTH the
    /// live <see cref="ProjectSettings.RunnerMode"/> mirror and the durable
    /// <see cref="ProjectSettings.DesiredRunnerMode"/> that boot restores from.
    /// </summary>
    [Fact]
    public void SetRunnerMode_UserSourced_AdvancesLiveMirrorAndDurableDesired()
    {
        var svc = Build();

        svc.SetRunnerMode("runbook", "auto-continuous", source: "user");

        var s = svc.Get("runbook");
        Assert.Equal("auto-continuous", s.RunnerMode);
        Assert.Equal("auto-continuous", s.DesiredRunnerMode);
    }

    /// <summary>
    /// The core ASS-1753 regression: a system-driven flip to manual (the
    /// update-service quiescing runners before a deploy) must mirror the live
    /// mode without clobbering the operator's durable auto-continuous intent.
    /// The boot-restore preference (DesiredRunnerMode over RunnerMode) therefore
    /// still resolves to auto-continuous, so a restart that lands mid-quiesce
    /// comes back up in the mode the operator actually asked for.
    /// </summary>
    [Fact]
    public void SetRunnerMode_SystemFlipToManual_PreservesDurableDesiredIntent()
    {
        var svc = Build();
        svc.SetRunnerMode("runbook", "auto-continuous", source: "user");

        // update-quiesce / circuit-breaker style flip: NOT operator intent.
        svc.SetRunnerMode("runbook", "manual", source: "system");

        var s = svc.Get("runbook");
        Assert.Equal("manual", s.RunnerMode);                 // live mirror reflects reality
        Assert.Equal("auto-continuous", s.DesiredRunnerMode); // durable intent untouched

        // Boot restore prefers DesiredRunnerMode -> auto-continuous survives.
        var bootMode = string.IsNullOrWhiteSpace(s.DesiredRunnerMode) ? s.RunnerMode : s.DesiredRunnerMode;
        Assert.Equal("auto-continuous", bootMode);
    }

    [Fact]
    public void SetRunnerMode_SystemFlip_PreservesDesiredAcrossReload()
    {
        var svc = Build();
        svc.SetRunnerMode("runbook", "auto-continuous", source: "user");
        svc.SetRunnerMode("runbook", "manual", source: "system");

        var reloaded = Build().Get("runbook");

        Assert.Equal("manual", reloaded.RunnerMode);
        Assert.Equal("auto-continuous", reloaded.DesiredRunnerMode);
    }

    /// <summary>
    /// Migration guard: a legacy record that predates DesiredRunnerMode has only
    /// RunnerMode on disk. Boot must fall back to RunnerMode so existing projects
    /// keep their persisted auto mode until the operator's next toggle records a
    /// durable DesiredRunnerMode.
    /// </summary>
    [Fact]
    public void Get_LegacyRecordWithoutDesired_BootFallsBackToRunnerMode()
    {
        File.WriteAllText(StorePath(), """
        {
          "runbook": {
            "RunnerMode": "auto-continuous"
          }
        }
        """);
        var svc = Build();

        var s = svc.Get("runbook");
        Assert.Equal("auto-continuous", s.RunnerMode);
        Assert.Null(s.DesiredRunnerMode);

        var bootMode = string.IsNullOrWhiteSpace(s.DesiredRunnerMode) ? s.RunnerMode : s.DesiredRunnerMode;
        Assert.Equal("auto-continuous", bootMode);
    }

    /// <summary>
    /// The hole the backfill closes: on a LEGACY record (no DesiredRunnerMode)
    /// a system-sourced flip used to overwrite RunnerMode — the only field the
    /// boot fallback reads — so one transient CLI-unspawnable pause permanently
    /// downgraded the project to manual across restarts (observed live
    /// 2026-07-07: auto-continuous -> manual because the claude npm shim was
    /// half-healed at boot). The system flip must first preserve the pre-flip
    /// RunnerMode as the durable DesiredRunnerMode.
    /// </summary>
    [Fact]
    public void SetRunnerMode_SystemFlipOnLegacyRecord_BackfillsDesiredFromPreFlipMode()
    {
        File.WriteAllText(StorePath(), """
        {
          "runbook": {
            "RunnerMode": "auto-continuous"
          }
        }
        """);
        var svc = Build();

        svc.SetRunnerMode("runbook", "manual", source: "system");

        var s = svc.Get("runbook");
        Assert.Equal("manual", s.RunnerMode);
        Assert.Equal("auto-continuous", s.DesiredRunnerMode); // backfilled from the pre-flip mirror

        // Boot restore therefore still comes back in the operator's mode.
        var bootMode = string.IsNullOrWhiteSpace(s.DesiredRunnerMode) ? s.RunnerMode : s.DesiredRunnerMode;
        Assert.Equal("auto-continuous", bootMode);
    }

    /// <summary>
    /// Companion guard: the backfill only fires when Desired is EMPTY. A record
    /// whose durable intent is already set keeps it verbatim (ASS-1753 contract),
    /// and a user-sourced change still advances both fields.
    /// </summary>
    [Fact]
    public void SetRunnerMode_SystemFlipWithExistingDesired_DoesNotRewriteDesired()
    {
        var svc = Build();
        svc.SetRunnerMode("runbook", "auto-single", source: "user");

        svc.SetRunnerMode("runbook", "auto-continuous", source: "system");
        svc.SetRunnerMode("runbook", "manual", source: "circuit-breaker");

        var s = svc.Get("runbook");
        Assert.Equal("manual", s.RunnerMode);
        Assert.Equal("auto-single", s.DesiredRunnerMode); // the operator's toggle, not the system flips
    }

    private ProjectSettingsService Build(IAtomicJsonFileWriter? fileWriter = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
            })
            .Build();
        return new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config, fileWriter);
    }

    private string StorePath() => Path.Combine(_workspace, "project-settings.json");
}
