using AgentStudio.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Wire-shape test for the new <see cref="TimelineEventKinds.ModelMigrated"/>
/// kind (AGT-2716), per the convention documented on
/// <see cref="TimelineEventKinds"/>: "add a test that asserts the wire shape."
/// Mirrors the detail keys <c>ProjectRunner.ApplyAutoModelMigration</c> writes.
/// </summary>
public sealed class ModelMigratedTimelineEventTests : IDisposable
{
    private readonly string _jobFolder;

    public ModelMigratedTimelineEventTests()
    {
        _jobFolder = Path.Combine(Path.GetTempPath(), "atp-model-migrated-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_jobFolder);
    }

    public void Dispose()
    {
        try { Directory.Delete(_jobFolder, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Append_ModelMigrated_RoundTripsWithExpectedDetails()
    {
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);

        var appended = timeline.Append(
            _jobFolder,
            TimelineEventKinds.ModelMigrated,
            TimelineActors.System,
            summary: "Model migrated: claude-opus-4-8 -> claude-opus-5 (Same family, newer generation.)",
            details: new()
            {
                ["fromModel"] = "claude-opus-4-8",
                ["toModel"] = "claude-opus-5",
                ["family"] = "claude-opus",
                ["catalogVersion"] = "2026-09-08",
                ["reason"] = "Same family, newer generation.",
            });

        Assert.True(appended);

        var events = timeline.ReadAll(_jobFolder);
        var evt = Assert.Single(events);
        Assert.Equal(TimelineEventKinds.ModelMigrated, evt.Kind);
        Assert.Equal(TimelineActors.System, evt.Actor);
        Assert.NotNull(evt.Details);
        Assert.Equal("claude-opus-4-8", evt.Details!["fromModel"]);
        Assert.Equal("claude-opus-5", evt.Details["toModel"]);
        Assert.Equal("claude-opus", evt.Details["family"]);
        Assert.Equal("2026-09-08", evt.Details["catalogVersion"]);
    }
}
