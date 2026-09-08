using AgentStudio.Orchestrator;

using Xunit;

namespace AgentStudio.Tests;

public sealed class OrchestratorContextKeyTests
{
    [Fact]
    public void TryParse_WorkbenchKey_PopulatesProjectAndWorkbenchKey()
    {
        Assert.True(OrchestratorContextKey.TryParse("workbench:AGT/AGT-W43", out var key));
        Assert.Equal(OrchestratorContextKey.WorkbenchKind, key!.Kind);
        Assert.Equal("AGT", key.ProjectId);
        Assert.Equal("AGT-W43", key.WorkbenchKey);
        Assert.Null(key.TaskKey);
        Assert.False(key.IsGlobal);
    }

    [Fact]
    public void TryParse_WorkbenchKey_RejectsMissingSlash()
    {
        Assert.False(OrchestratorContextKey.TryParse("workbench:AGT", out _));
    }

    [Fact]
    public void TryParse_WorkbenchKey_RejectsEmptyDossierKey()
    {
        Assert.False(OrchestratorContextKey.TryParse("workbench:AGT/", out _));
    }

    [Fact]
    public void TryParse_WorkbenchKey_RejectsEmptyProjectId()
    {
        Assert.False(OrchestratorContextKey.TryParse("workbench:/AGT-W43", out _));
    }

    [Fact]
    public void TryParse_WorkbenchKey_RejectsExtraPathSegment()
    {
        // Otherwise-opaque ids reject embedded '/' the same way task keys do.
        Assert.False(OrchestratorContextKey.TryParse("workbench:AGT/AGT-W43/extra", out _));
    }

    [Fact]
    public void EncodeThenDecode_WorkbenchKey_RoundTrips()
    {
        Assert.True(OrchestratorContextKey.TryParse("workbench:AGT/AGT-W43", out var key));
        var encoded = key!.Encode();

        Assert.True(OrchestratorContextKey.TryDecode(encoded, out var decoded));
        Assert.Equal(key.Value, decoded!.Value);
        Assert.Equal(key.Kind, decoded.Kind);
        Assert.Equal(key.ProjectId, decoded.ProjectId);
        Assert.Equal(key.WorkbenchKey, decoded.WorkbenchKey);
    }

    [Fact]
    public void TryDecode_StrayDirectoryName_IsRejected()
    {
        // A folder name that decodes to bytes but is not a valid context key
        // (e.g. torn write, unrelated directory) must not silently round-trip.
        Assert.False(OrchestratorContextKey.TryDecode("not-a-context-key", out _));
    }

    [Theory]
    [InlineData("global")]
    [InlineData("project:AGT")]
    [InlineData("task:AGT/AGT-1917")]
    [InlineData("workbench:AGT/AGT-W43")]
    public void TryParse_AllFourShapes_Succeed(string raw)
    {
        Assert.True(OrchestratorContextKey.TryParse(raw, out var key));
        Assert.Equal(raw, key!.Value);
    }

    [Fact]
    public void TryParse_WorkbenchAndTaskKeysWithSameIds_AreDistinctContexts()
    {
        Assert.True(OrchestratorContextKey.TryParse("workbench:AGT/AGT-2725", out var workbench));
        Assert.True(OrchestratorContextKey.TryParse("task:AGT/AGT-2725", out var task));

        Assert.NotEqual(workbench!.Value, task!.Value);
        Assert.NotEqual(workbench.Encode(), task.Encode());
    }
}
