using AgentStudio.Registry;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-1812 — precedence tests for the two-tier orchestrator resolver:
/// project override -> workspace default -> platform constant default. Mirrors
/// the style of the pipeline-config resolution coverage and locks in that a
/// workspace tier only ever sits between the project override and the platform
/// default (never above the project).
/// </summary>
public sealed class OrchestratorSettingsResolverTests
{
    private const string PlatformModel = "claude-haiku-4-5";

    // --- model -----------------------------------------------------------

    [Fact]
    public void ResolveModel_NoTiers_UsesPlatformDefault()
    {
        var r = OrchestratorSettingsResolver.ResolveModel(new ProjectSettings(), new WorkspaceSettings(), PlatformModel);

        Assert.Equal(PlatformModel, r.Model);
        Assert.Equal(OrchestratorSettingsResolver.SourceDefault, r.Source);
    }

    [Fact]
    public void ResolveModel_WorkspaceDefault_WinsOverPlatform()
    {
        var workspace = new WorkspaceSettings { OrchestratorModel = "claude-opus-4-8" };

        var r = OrchestratorSettingsResolver.ResolveModel(new ProjectSettings(), workspace, PlatformModel);

        Assert.Equal("claude-opus-4-8", r.Model);
        Assert.Equal(OrchestratorSettingsResolver.SourceWorkspace, r.Source);
    }

    [Fact]
    public void ResolveModel_ProjectOverride_WinsOverWorkspace()
    {
        var project = new ProjectSettings { OrchestratorModel = "claude-sonnet-5" };
        var workspace = new WorkspaceSettings { OrchestratorModel = "claude-opus-4-8" };

        var r = OrchestratorSettingsResolver.ResolveModel(project, workspace, PlatformModel);

        Assert.Equal("claude-sonnet-5", r.Model);
        Assert.Equal(OrchestratorSettingsResolver.SourceProject, r.Source);
        Assert.Equal("claude-opus-4-8", r.WorkspaceDefault);
    }

    [Fact]
    public void ResolveModel_BlankTiers_TreatedAsUnset()
    {
        var project = new ProjectSettings { OrchestratorModel = "   " };
        var workspace = new WorkspaceSettings { OrchestratorModel = "" };

        var r = OrchestratorSettingsResolver.ResolveModel(project, workspace, PlatformModel);

        Assert.Equal(PlatformModel, r.Model);
        Assert.Equal(OrchestratorSettingsResolver.SourceDefault, r.Source);
    }

    [Fact]
    public void ResolveModelOverride_NoTiers_ReturnsNull()
    {
        Assert.Null(OrchestratorSettingsResolver.ResolveModelOverride(new ProjectSettings(), new WorkspaceSettings()));
        Assert.Null(OrchestratorSettingsResolver.ResolveModelOverride(null, null));
    }

    [Fact]
    public void ResolveModelOverride_WorkspaceThenProject()
    {
        var workspace = new WorkspaceSettings { OrchestratorModel = "claude-opus-4-8" };
        Assert.Equal("claude-opus-4-8",
            OrchestratorSettingsResolver.ResolveModelOverride(new ProjectSettings(), workspace));

        var project = new ProjectSettings { OrchestratorModel = "claude-sonnet-5" };
        Assert.Equal("claude-sonnet-5",
            OrchestratorSettingsResolver.ResolveModelOverride(project, workspace));
    }

    // --- thinking level --------------------------------------------------

    [Fact]
    public void ResolveThinkingLevelOverride_ProjectBeatsWorkspace()
    {
        var project = new ProjectSettings { OrchestratorThinkingLevel = "high" };
        var workspace = new WorkspaceSettings { OrchestratorThinkingLevel = "low" };

        Assert.Equal("high",
            OrchestratorSettingsResolver.ResolveThinkingLevelOverride(project, workspace));
        Assert.Equal("low",
            OrchestratorSettingsResolver.ResolveThinkingLevelOverride(new ProjectSettings(), workspace));
        Assert.Null(
            OrchestratorSettingsResolver.ResolveThinkingLevelOverride(new ProjectSettings(), new WorkspaceSettings()));
    }


    // --- autonomy --------------------------------------------------------

    [Fact]
    public void ResolveAutonomy_NoTiers_UsesPlatformDefault()
    {
        var r = OrchestratorSettingsResolver.ResolveAutonomy(new ProjectSettings(), new WorkspaceSettings());

        Assert.Equal(2, r.Level);
        Assert.Equal(OrchestratorSettingsResolver.SourceDefault, r.Source);
    }

    [Fact]
    public void ResolveAutonomy_WorkspaceDefault_WinsOverPlatform()
    {
        var workspace = new WorkspaceSettings { AutonomyLevel = 4 };

        var r = OrchestratorSettingsResolver.ResolveAutonomy(new ProjectSettings(), workspace);

        Assert.Equal(4, r.Level);
        Assert.Equal(OrchestratorSettingsResolver.SourceWorkspace, r.Source);
    }

    [Fact]
    public void ResolveAutonomy_ProjectOverride_WinsOverWorkspace()
    {
        var project = new ProjectSettings { AutonomyLevel = 0 };
        var workspace = new WorkspaceSettings { AutonomyLevel = 4 };

        var r = OrchestratorSettingsResolver.ResolveAutonomy(project, workspace);

        Assert.Equal(0, r.Level);
        Assert.Equal(OrchestratorSettingsResolver.SourceProject, r.Source);
        Assert.Equal(4, r.WorkspaceDefault);
    }

    [Fact]
    public void ResolveAutonomy_ZeroWorkspaceDefault_IsRespectedNotTreatedAsUnset()
    {
        // 0 (manual) is a real value, distinct from "no default". A workspace
        // that pins autonomy to manual must suppress auto-advance for its
        // projects that have not set their own level.
        var workspace = new WorkspaceSettings { AutonomyLevel = 0 };

        var r = OrchestratorSettingsResolver.ResolveAutonomy(new ProjectSettings(), workspace);

        Assert.Equal(0, r.Level);
        Assert.Equal(OrchestratorSettingsResolver.SourceWorkspace, r.Source);
    }
}
