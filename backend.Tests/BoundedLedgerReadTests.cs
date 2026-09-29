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
/// events. Writers that update a row rewrite only the newest window from its
/// line-aligned start offset, so an oversized session ledger still gets its
/// latest run closed and backfilled while every older row keeps its bytes.
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
    public void AppendSessionEvent_OversizedLedger_ClosesThePredecessorAndKeepsOlderRows()
    {
        var folder = JobFolder("session-task");
        var path = TaskPaths.SessionEventsLog(folder);
        var total = WriteOversizedSessionLedger(path);
        var before = File.ReadAllBytes(path);
        var windowStart = BoundedFileRead.ReadTailLineWindow(path, BoundedFileRead.LedgerBytes).Offset;
        Assert.True(windowStart > 0, "the fixture must exceed the ledger window");
        var sessions = BuildSessions();
        var successorTs = new DateTime(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc);

        Assert.True(sessions.AppendSessionEventToFolder(folder, new SessionEvent
        {
            Ts = successorTs,
            Kind = "continue",
            InputSessionId = "session-successor",
        }, "session-task"));

        var after = File.ReadAllBytes(path);
        Assert.Equal(before[..(int)windowStart], after[..(int)windowStart]);
        var rows = ParseRows(path);
        Assert.Equal(total + 1, rows.Count);
        Assert.Equal("session-0", rows[0].InputSessionId);
        var predecessor = rows[^2];
        Assert.Equal($"session-{total - 1}", predecessor.InputSessionId);
        Assert.Equal(successorTs, predecessor.FinishedAt);
        Assert.Equal("superseded", predecessor.Status);
        Assert.Equal("session-successor", rows[^1].InputSessionId);
        Assert.Single(rows, row => row.FinishedAt is null);
    }

    [Fact]
    public void AppendSessionEvent_OversizedLedger_RepeatedStartsNeverLeaveTwoOpenRuns()
    {
        var folder = JobFolder("repeat-task");
        var path = TaskPaths.SessionEventsLog(folder);
        var total = WriteOversizedSessionLedger(path);
        var sessions = BuildSessions();

        for (var i = 0; i < 3; i++)
        {
            Assert.True(sessions.AppendSessionEventToFolder(folder, new SessionEvent
            {
                Ts = new DateTime(2026, 9, 29, 0, 0, i, DateTimeKind.Utc),
                Kind = "start",
                InputSessionId = $"session-new-{i}",
            }, "repeat-task"));
        }

        var rows = ParseRows(path);
        Assert.Equal(total + 3, rows.Count);
        var open = Assert.Single(rows, row => row.FinishedAt is null);
        Assert.Equal("session-new-2", open.InputSessionId);
    }

    [Fact]
    public void BackfillSessionEvent_OversizedLedger_UpdatesTheLatestRowAndKeepsOlderRows()
    {
        var folder = JobFolder("backfill-task");
        var path = TaskPaths.SessionEventsLog(folder);
        var total = WriteOversizedSessionLedger(path);
        var before = File.ReadAllBytes(path);
        var windowStart = BoundedFileRead.ReadTailLineWindow(path, BoundedFileRead.LedgerBytes).Offset;
        var sessions = BuildSessions();

        Assert.True(sessions.BackfillLatestSessionEventCapturedId("backfill-task", "session-xyz"));
        Assert.True(sessions.BackfillLatestSessionEventHeadShaRange("backfill-task", "aaa111", "bbb222"));
        Assert.True(sessions.BackfillLatestSessionEventResumed("backfill-task", true, "resumed-after-review"));

        var after = File.ReadAllBytes(path);
        Assert.Equal(before[..(int)windowStart], after[..(int)windowStart]);
        var rows = ParseRows(path);
        Assert.Equal(total, rows.Count);
        Assert.Equal("session-0", rows[0].InputSessionId);
        var latest = rows[^1];
        Assert.Equal($"session-{total - 1}", latest.InputSessionId);
        Assert.Equal("session-xyz", latest.CapturedSessionId);
        Assert.Equal("aaa111", latest.HeadShaBefore);
        Assert.Equal("bbb222", latest.HeadShaAfter);
        Assert.True(latest.Resumed);
        Assert.Equal("resumed-after-review", latest.Reason);
        Assert.Null(rows[^2].CapturedSessionId);
    }

    [Fact]
    public void CloseSessionEvent_OversizedLedger_ClosesTheMatchingAttempt()
    {
        var folder = JobFolder("close-task");
        var path = TaskPaths.SessionEventsLog(folder);
        var total = WriteOversizedSessionLedger(path, openAttemptId: "attempt-open");
        var sessions = BuildSessions();
        var finishedAt = new DateTime(2026, 9, 29, 1, 0, 0, DateTimeKind.Utc);

        Assert.True(sessions.CloseSessionEvent("close-task", new RunSessionCloseout
        {
            RunAttemptId = "attempt-open",
            FinishedAt = finishedAt,
            Result = "completed",
            Status = "done",
            ExitCode = 0,
        }));

        var rows = ParseRows(path);
        Assert.Equal(total, rows.Count);
        Assert.Equal(finishedAt, rows[^1].FinishedAt);
        Assert.Equal("done", rows[^1].Status);
        Assert.DoesNotContain(rows, row => row.FinishedAt is null);
    }

    [Fact]
    public void BackfillSessionEvent_NormalLedger_IsRewrittenWhole()
    {
        var folder = JobFolder("small-backfill");
        var sessions = BuildSessions();
        for (var i = 0; i < 3; i++)
        {
            sessions.AppendSessionEventToFolder(folder, new SessionEvent
            {
                Ts = new DateTime(2026, 9, 29, 0, 0, i, DateTimeKind.Utc),
                Kind = "start",
                InputSessionId = $"session-{i}",
            }, "small-backfill");
        }

        Assert.True(sessions.BackfillLatestSessionEventCapturedId("small-backfill", "session-xyz"));

        var rows = sessions.ReadSessionEvents("small-backfill");
        Assert.Equal(["session-0", "session-1", "session-2"], rows.Select(row => row.InputSessionId));
        Assert.Equal("session-xyz", rows[^1].CapturedSessionId);
        Assert.Single(rows, row => row.FinishedAt is null);
    }

    [Fact]
    public void ReadSessionEvents_OversizedLedger_ReturnsNewestEvents()
    {
        var folder = JobFolder("read-task");
        var total = WriteOversizedSessionLedger(TaskPaths.SessionEventsLog(folder));
        var sessions = BuildSessions();

        var events = sessions.ReadSessionEvents("read-task");

        Assert.NotEmpty(events);
        Assert.True(events.Count < total);
        Assert.Equal($"session-{total - 1}", events[^1].InputSessionId);
    }

    private static readonly string Filler = new('f', 180);

    private static string SessionRow(int i, bool open = false, string? attemptId = null) => JsonSerializer.Serialize(new SessionEvent
    {
        Ts = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(i),
        Kind = "start",
        InputSessionId = $"session-{i}",
        RunAttemptId = attemptId,
        Reason = Filler,
        FinishedAt = open ? null : new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(i + 1),
        Status = open ? null : "done",
    }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    /// <summary>
    /// Writes closed runs past the ledger cap and ends with one open run, the
    /// shape a long-lived task's ledger has while its current run is live.
    /// Returns the row count.
    /// </summary>
    private static int WriteOversizedSessionLedger(string path, string? openAttemptId = null)
    {
        var closed = WriteOversizedLedger(path, i => SessionRow(i));
        File.AppendAllText(path, SessionRow(closed, open: true, openAttemptId) + "\n", new UTF8Encoding(false));
        return closed + 1;
    }

    private static readonly JsonSerializerOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };

    private static List<SessionEvent> ParseRows(string path) => File.ReadLines(path)
        .Where(line => !string.IsNullOrWhiteSpace(line))
        .Select(line => JsonSerializer.Deserialize<SessionEvent>(line, CaseInsensitive)!)
        .ToList();

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
