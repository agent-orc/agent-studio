using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2991: <c>logs/timeline.jsonl</c> and <c>logs/session-events.jsonl</c>
/// have no size cap, so their readers are bounded to
/// <see cref="BoundedFileRead.LedgerBytes"/>. Readers get the newest complete
/// events; writers that rewrite the ledger from what they read must never do
/// so from a truncated window, because that would delete the older rows.
/// </summary>
public sealed class BoundedLedgerReadTests : IDisposable
{
    private readonly string _watchPath = Path.Combine(
        Path.GetTempPath(), "bounded-ledger-tests-" + Guid.NewGuid().ToString("N"));

    public BoundedLedgerReadTests()
    {
        foreach (var state in TaskStates.All) Directory.CreateDirectory(Path.Combine(_watchPath, state));
    }

    public void Dispose()
    {
        try { Directory.Delete(_watchPath, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void TimelineReadAll_OversizedLedger_ReturnsNewestCompleteEvents()
    {
        var folder = JobFolder("timeline-task");
        var path = TaskPaths.TimelineLog(folder);
        var total = WriteOversizedLedger(path, i => JsonSerializer.Serialize(new TimelineEvent
        {
            Ts = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(i),
            Kind = "note",
            Actor = "test",
            Summary = $"event {i} {Filler}",
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

        var events = new TimelineLog(NullLogger<TimelineLog>.Instance).ReadAll(folder);

        Assert.NotEmpty(events);
        Assert.True(events.Count < total, "an oversized ledger must not be read whole");
        Assert.StartsWith($"event {total - 1} ", events[^1].Summary);
        // Consecutive and complete: no torn leading row was parsed or skipped mid-window.
        var first = int.Parse(events[0].Summary.Split(' ')[1]);
        Assert.Equal(total - first, events.Count);
    }

    [Fact]
    public void TimelineReadAll_NormalLedger_IsReturnedWhole()
    {
        var folder = JobFolder("small-timeline");
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        for (var i = 0; i < 20; i++) timeline.Append(folder, "note", "test", $"event {i}");

        var events = timeline.ReadAll(folder);

        Assert.Equal(20, events.Count);
        Assert.Equal("event 0", events[0].Summary);
    }

    [Fact]
    public void AppendSessionEvent_OversizedLedger_AppendsWithoutRewritingOlderRows()
    {
        var folder = JobFolder("session-task");
        var path = TaskPaths.SessionEventsLog(folder);
        // Every row is an open run, so the normal path would close the last one
        // by rewriting the whole file.
        WriteOversizedLedger(path, i => SessionRow(i));
        var before = File.ReadAllBytes(path);
        var sessions = BuildSessions();

        Assert.True(sessions.AppendSessionEventToFolder(folder, new SessionEvent
        {
            Ts = new DateTime(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc),
            Kind = "continue",
        }, "session-task"));

        var after = File.ReadAllBytes(path);
        Assert.True(after.Length > before.Length);
        Assert.Equal(before, after[..before.Length]);
        var appended = Encoding.UTF8.GetString(after, before.Length, after.Length - before.Length);
        Assert.Contains("\"continue\"", appended);
        Assert.Single(appended.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void BackfillSessionEvent_OversizedLedger_LeavesTheFileUntouched()
    {
        var folder = JobFolder("backfill-task");
        var path = TaskPaths.SessionEventsLog(folder);
        WriteOversizedLedger(path, i => SessionRow(i));
        var before = File.ReadAllBytes(path);
        var sessions = BuildSessions();

        Assert.False(sessions.BackfillLatestSessionEventCapturedId("backfill-task", "session-xyz"));

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void ReadSessionEvents_OversizedLedger_ReturnsNewestEvents()
    {
        var folder = JobFolder("read-task");
        var total = WriteOversizedLedger(TaskPaths.SessionEventsLog(folder), i => SessionRow(i));
        var sessions = BuildSessions();

        var events = sessions.ReadSessionEvents("read-task");

        Assert.NotEmpty(events);
        Assert.True(events.Count < total);
        Assert.Equal($"session-{total - 1}", events[^1].InputSessionId);
    }

    private static readonly string Filler = new('f', 180);

    private static string SessionRow(int i) => JsonSerializer.Serialize(new SessionEvent
    {
        Ts = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(i),
        Kind = "start",
        InputSessionId = $"session-{i}",
        Reason = Filler,
    }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    /// <summary>Writes rows until the file is past the ledger cap; returns the row count.</summary>
    private static int WriteOversizedLedger(string path, Func<int, string> row)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var writer = new StreamWriter(path, append: false, new UTF8Encoding(false));
        var count = 0;
        long written = 0;
        while (written <= BoundedFileRead.LedgerBytes + 64 * 1024)
        {
            var line = row(count++);
            writer.Write(line);
            writer.Write('\n');
            written += Encoding.UTF8.GetByteCount(line) + 1;
        }
        return count;
    }

    private string JobFolder(string slug)
    {
        var folder = Path.Combine(_watchPath, TaskStates.Progress, slug);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = slug,
            ["title"] = slug,
            ["state"] = TaskStates.Progress,
            ["order"] = 1,
            ["agent"] = "claude",
        }));
        return folder;
    }

    private TaskSessionLog BuildSessions()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = "test",
                ["WatchPaths:0:Path"] = _watchPath,
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        return new TaskSessionLog(scanner, NullLogger<TaskSessionLog>.Instance);
    }
}
