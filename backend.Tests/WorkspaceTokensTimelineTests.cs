

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the workspace token-timeline math: orchestrator-log entries
/// in (per project), one (project, bucket) cell out per non-empty
/// bucket. Same matrix style as <see cref="TokenSummaryTests"/>.
/// </summary>
public class WorkspaceTokensTimelineTests
{
    private static OrchestratorLogEntry Entry(DateTime ts, string model, long input, long output, long cacheRead = 0, long cacheCreate = 0)
        => new()
        {
            Ts = ts,
            Kind = OrchestratorLogKinds.Decision,
            Topic = OrchestratorLogTopics.General,
            Summary = "test entry",
            TokenUsage = new OrchestratorTokenUsage
            {
                Model = model,
                InputTokens = (int)input,
                OutputTokens = (int)output,
                CacheReadTokens = (int)cacheRead,
                CacheCreationTokens = (int)cacheCreate
            }
        };

    [Fact]
    public void Build_SixHourWindow_ThreeProjects_ProducesExpectedCells()
    {
        // Anchor on a bucket-aligned wall clock so the assertions don't
        // wobble across runs.
        var windowEnd = new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc);
        var windowStart = windowEnd.AddHours(-6);
        const int bucketMinutes = 60;

        // Project A: two calls in the same bucket (windowStart + 30 min),
        // one call in a later bucket. Same model both buckets.
        var projectA = new[]
        {
            Entry(windowStart.AddMinutes(15), "claude-opus-4-7", 100_000, 10_000),
            Entry(windowStart.AddMinutes(45), "claude-opus-4-7",  50_000,  5_000),
            Entry(windowStart.AddHours(2).AddMinutes(10), "claude-opus-4-7", 30_000, 3_000)
        };

        // Project B: one call inside the window, one BEFORE the window
        // (must be excluded), one AFTER (must also be excluded).
        var projectB = new[]
        {
            Entry(windowStart.AddHours(-1), "claude-haiku-4-5", 999, 999),       // before window
            Entry(windowStart.AddHours(3).AddMinutes(5), "claude-haiku-4-5", 200_000, 20_000),
            Entry(windowEnd.AddMinutes(5), "claude-haiku-4-5", 999, 999)         // after window
        };

        // Project C: one call with a model not in the pricing catalog.
        // Total tokens still flow through; allModelsPriced must be false
        // for that cell and dollars must be null.
        var projectC = new[]
        {
            Entry(windowStart.AddHours(4).AddMinutes(20), "unknown-catalog-model", 11_000, 1_100)
        };

        var input = new (string Project, IReadOnlyList<OrchestratorLogEntry> Entries)[]
        {
            ("alpha", projectA),
            ("bravo", projectB),
            ("charlie", projectC),
        };

        var t = WorkspaceTokensTimelineService.BuildFromEntries(input, windowStart, windowEnd, bucketMinutes);

        // Window stats.
        Assert.Equal(6, t.WindowHours);
        Assert.Equal(60, t.BucketMinutes);
        Assert.Equal(6, t.BucketCount);

        // Three project rollups, one per project. (Even bravo's two
        // out-of-window calls are silently dropped, so the project still
        // appears with its in-window totals.)
        Assert.Equal(3, t.Projects.Count);

        // ------ Cells ------

        // Project alpha contributes two non-empty buckets: bucket 0 (two calls
        // merged) and bucket 2 (one call).
        var alpha0 = t.Cells.Single(c => c.Project == "alpha" && c.BucketStart == windowStart.ToString("o"));
        Assert.Equal(2, alpha0.Calls);
        Assert.Equal(150_000L, alpha0.Input);
        Assert.Equal(15_000L, alpha0.Output);
        Assert.Equal(165_000L, alpha0.Total);
        Assert.True(alpha0.AllModelsPriced);
        // 150K input @ $5/M = $0.75; 15K output @ $25/M = $0.375; total $1.125.
        Assert.Equal(1.125m, alpha0.Dollars);

        var alpha2 = t.Cells.Single(c => c.Project == "alpha" && c.BucketStart == windowStart.AddHours(2).ToString("o"));
        Assert.Equal(1, alpha2.Calls);
        Assert.Equal(33_000L, alpha2.Total);

        // Project bravo: only the windowStart + 3h call survives.
        var bravoCells = t.Cells.Where(c => c.Project == "bravo").ToList();
        var bravo3 = Assert.Single(bravoCells);
        Assert.Equal(1, bravo3.Calls);
        Assert.Equal(220_000L, bravo3.Total);
        Assert.True(bravo3.AllModelsPriced);

        // Project charlie: the unknown-model call appears, but its dollar number
        // is null (model not in the catalog) and allModelsPriced is false.
        var charlieCells = t.Cells.Where(c => c.Project == "charlie").ToList();
        var charlie4 = Assert.Single(charlieCells);
        Assert.Equal(12_100L, charlie4.Total);
        Assert.False(charlie4.AllModelsPriced);
        Assert.Null(charlie4.Dollars);

        // ------ Project rollups ------

        var alphaTotal = t.Projects.Single(p => p.Project == "alpha");
        Assert.Equal(3, alphaTotal.Calls);
        Assert.Equal(180_000L + 18_000L, alphaTotal.Total);
        // Peak bucket should be the first one (165K > 33K).
        Assert.Equal(windowStart.ToString("o"), alphaTotal.PeakBucketStart);
        Assert.Equal(165_000L, alphaTotal.PeakBucketTotal);

        var bravoTotal = t.Projects.Single(p => p.Project == "bravo");
        Assert.Equal(1, bravoTotal.Calls);

        var charlieTotal = t.Projects.Single(p => p.Project == "charlie");
        Assert.Equal(1, charlieTotal.Calls);
        Assert.False(charlieTotal.AllModelsPriced);
        Assert.Null(charlieTotal.Dollars);
    }

    [Fact]
    public void Build_EntriesWithoutTokenUsage_AreIgnored()
    {
        var windowEnd = new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc);
        var windowStart = windowEnd.AddHours(-1);
        var entries = new[]
        {
            // Inside the window but no token usage - should not produce a cell.
            new OrchestratorLogEntry { Ts = windowStart.AddMinutes(10), Summary = "queued follow-up" },
            Entry(windowStart.AddMinutes(20), "claude-haiku-4-5", 1_000, 100)
        };

        var t = WorkspaceTokensTimelineService.BuildFromEntries(
            new[] { ("solo", (IReadOnlyList<OrchestratorLogEntry>)entries) },
            windowStart, windowEnd, 60);

        var cell = Assert.Single(t.Cells);
        Assert.Equal("solo", cell.Project);
        Assert.Equal(1, cell.Calls);
    }

    [Theory]
    [InlineData(0, 24)]      // default
    [InlineData(1, 1)]
    [InlineData(6, 6)]
    [InlineData(24, 24)]
    [InlineData(168, 168)]
    [InlineData(2, 24)]      // out of range -> default
    [InlineData(72, 24)]     // out of range -> default
    public void ResolveWindowHours_SnapsToAllowedSetOrDefault(int requested, int expected)
    {
        Assert.Equal(expected, WorkspaceTokensTimelineService.ResolveWindowHours(requested));
    }

    [Theory]
    [InlineData(0, 60)]      // default
    [InlineData(5, 5)]
    [InlineData(15, 15)]
    [InlineData(60, 60)]
    [InlineData(30, 60)]     // out of range -> default
    [InlineData(120, 60)]    // out of range -> default
    public void ResolveBucketMinutes_SnapsToAllowedSetOrDefault(int requested, int expected)
    {
        Assert.Equal(expected, WorkspaceTokensTimelineService.ResolveBucketMinutes(requested));
    }

    [Fact]
    public void Build_NoProjects_StillReturnsWindowMetadata()
    {
        var windowEnd = new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc);
        var windowStart = windowEnd.AddHours(-24);
        var t = WorkspaceTokensTimelineService.BuildFromEntries(
            Array.Empty<(string, IReadOnlyList<OrchestratorLogEntry>)>(),
            windowStart, windowEnd, 60);

        Assert.Equal(24, t.WindowHours);
        Assert.Equal(60, t.BucketMinutes);
        Assert.Equal(24, t.BucketCount);
        Assert.Empty(t.Cells);
        Assert.Empty(t.Projects);
    }
}
