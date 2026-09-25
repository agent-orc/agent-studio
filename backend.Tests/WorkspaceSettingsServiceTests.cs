using AgentStudio.Registry;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-1812 — persistence tests for the per-workspace default settings store,
/// mirroring <see cref="ProjectSettingsServiceTests"/>. Covers defaults on a
/// miss, round-trip persistence to <c>.metadata/workspace-settings.json</c>,
/// autonomy clamping, and clearing back to "no workspace default".
/// </summary>
public sealed class WorkspaceSettingsServiceTests : IDisposable
{
    private readonly string _workspace;

    public WorkspaceSettingsServiceTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "ws-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Get_UnknownWorkspace_ReturnsAllNullDefaults()
    {
        var svc = Build();

        var s = svc.Get("ws-default");

        Assert.Null(s.OrchestratorModel);
        Assert.Null(s.OrchestratorThinkingLevel);
        Assert.Null(s.AutonomyLevel);
    }

    [Fact]
    public void Get_NullOrBlankId_ReturnsEmptyRecord()
    {
        var svc = Build();

        Assert.Null(svc.Get(null).OrchestratorModel);
        Assert.Null(svc.Get("   ").AutonomyLevel);
    }

    [Fact]
    public void SetOrchestratorModel_PersistsAcrossReload()
    {
        var svc = Build();

        svc.SetOrchestratorModel("ws-default", "claude-opus-4-8", "high");

        var reloaded = Build();
        var s = reloaded.Get("ws-default");
        Assert.Equal("claude-opus-4-8", s.OrchestratorModel);
        Assert.Equal("high", s.OrchestratorThinkingLevel);
        Assert.True(File.Exists(StorePath()));
    }

    [Fact]
    public void SetOrchestratorModel_BlankClearsModel()
    {
        var svc = Build();
        svc.SetOrchestratorModel("ws-default", "claude-opus-4-8");
        Assert.Equal("claude-opus-4-8", svc.Get("ws-default").OrchestratorModel);

        svc.SetOrchestratorModel("ws-default", "   ");

        Assert.Null(svc.Get("ws-default").OrchestratorModel);
    }

    [Fact]
    public void SetOrchestratorModel_NullThinkingLevel_LeavesThinkingUntouched()
    {
        var svc = Build();
        svc.SetOrchestratorModel("ws-default", "claude-opus-4-8", "high");

        // Re-set the model without passing a thinking level: it must be preserved.
        svc.SetOrchestratorModel("ws-default", "claude-sonnet-5", thinkingLevel: null);

        var s = svc.Get("ws-default");
        Assert.Equal("claude-sonnet-5", s.OrchestratorModel);
        Assert.Equal("high", s.OrchestratorThinkingLevel);
    }

    [Fact]
    public void SetAutonomyLevel_PersistsAcrossReload()
    {
        var svc = Build();

        svc.SetAutonomyLevel("ws-default", 3);

        var reloaded = Build();
        Assert.Equal(3, reloaded.Get("ws-default").AutonomyLevel);
    }


    [Theory]
    [InlineData(-2, 0)]
    [InlineData(9, 4)]
    public void SetAutonomyLevel_ClampsToRange(int input, int expected)
    {
        var svc = Build();

        svc.SetAutonomyLevel("ws-default", input);

        Assert.Equal(expected, svc.Get("ws-default").AutonomyLevel);
    }

    [Fact]
    public void SetAutonomyLevel_NullClearsWorkspaceDefault()
    {
        var svc = Build();
        svc.SetAutonomyLevel("ws-default", 4);
        Assert.Equal(4, svc.Get("ws-default").AutonomyLevel);

        svc.SetAutonomyLevel("ws-default", null);

        Assert.Null(svc.Get("ws-default").AutonomyLevel);
    }

    [Fact]
    public void Settings_AreScopedPerWorkspaceId()
    {
        var svc = Build();

        svc.SetAutonomyLevel("ws-default", 4);
        svc.SetAutonomyLevel("ws-frontend", 0);

        Assert.Equal(4, svc.Get("ws-default").AutonomyLevel);
        Assert.Equal(0, svc.Get("ws-frontend").AutonomyLevel);
    }

    private WorkspaceSettingsService Build()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
            })
            .Build();
        return new WorkspaceSettingsService(NullLogger<WorkspaceSettingsService>.Instance, config);
    }

    private string StorePath() =>
        Path.Combine(_workspace, ".metadata", WorkspaceSettingsService.FileName);
}
