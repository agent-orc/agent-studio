using AgentStudio.Operator;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The persisted decision log of OP-2: a reader that resumes with its last
/// cursor receives every later event once, across a restart, within a
/// retention window of at least 24 hours.
/// </summary>
public sealed class OperatorDecisionLogTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "operator-decision-log-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-11-04T09:00:00Z"));

    [Fact]
    public void Resuming_with_the_last_cursor_returns_every_later_event_once()
    {
        var log = Log();
        log.Append([Fact("a"), Fact("b")]);
        var first = log.Read(0, 100, _ => true);
        log.Append([Fact("c"), Fact("d")]);

        var resumed = log.Read(first.Cursor, 100, _ => true);
        var again = log.Read(resumed.Cursor, 100, _ => true);

        Assert.Equal(["a", "b"], first.Events.Select(Name));
        Assert.Equal(["c", "d"], resumed.Events.Select(Name));
        Assert.Empty(again.Events);
        Assert.False(resumed.Gap);
    }

    [Fact]
    public void A_restarted_log_keeps_its_events_and_continues_the_sequence()
    {
        var before = Log();
        before.Append([Fact("a"), Fact("b")]);
        var cursor = before.Read(0, 1, _ => true).Cursor;

        var after = Log();
        after.Append([Fact("c")]);

        Assert.Equal(["b", "c"], after.Read(cursor, 100, _ => true).Events.Select(Name));
        Assert.Equal(["1", "2", "3"], after.Read(0, 100, _ => true).Events.Select(item => item.Cursor));
    }

    [Fact]
    public void Paging_and_filtering_advance_the_cursor_past_hidden_events()
    {
        var log = Log();
        log.Append([Fact("a", "beta"), Fact("b"), Fact("c", "beta"), Fact("d")]);

        var page = log.Read(0, 1, item => item.Project == "alpha");
        var rest = log.Read(page.Cursor, 100, item => item.Project == "alpha");

        Assert.Equal(["b"], page.Events.Select(Name));
        Assert.True(page.HasMore);
        Assert.Equal(["d"], rest.Events.Select(Name));
        Assert.Equal(4, rest.Cursor);
    }

    [Fact]
    public void Events_stay_for_the_retention_window_and_an_older_cursor_reads_as_a_gap()
    {
        var log = Log(retention: TimeSpan.FromHours(1));
        Assert.Equal(OperatorDecisionLog.MinimumRetention, log.Retention);
        log.Append([Fact("old")]);
        _clock.Advance(TimeSpan.FromHours(23));
        log.Append([Fact("recent")]);
        Assert.Equal(["old", "recent"], log.Read(0, 100, _ => true).Events.Select(Name));

        // Trimming runs once the oldest event is an hour past the window.
        _clock.Advance(TimeSpan.FromHours(3));
        log.Append([Fact("new")]);
        var reopened = Log(retention: TimeSpan.FromHours(1));
        var page = reopened.Read(0, 100, _ => true);

        Assert.Equal(["recent", "new"], page.Events.Select(Name));
        Assert.True(page.Gap);
        Assert.False(reopened.Read(1, 100, _ => true).Gap);
    }

    [Fact]
    public void The_sequence_survives_when_every_event_expired()
    {
        var log = Log();
        log.Append([Fact("a"), Fact("b")]);
        log.SavePublishedFacts(new HashSet<string>(StringComparer.Ordinal));
        _clock.Advance(TimeSpan.FromHours(30));
        log.Append([Fact("c")]);
        _clock.Advance(TimeSpan.FromHours(30));

        var reopened = Log();
        reopened.Append([Fact("d")]);

        Assert.Equal(["4"], reopened.Read(3, 100, _ => true).Events.Select(item => item.Cursor));
    }

    [Fact]
    public void Facts_appended_but_not_yet_saved_count_as_published_after_a_restart()
    {
        var log = Log();
        log.Append([Fact("a")]);
        log.SavePublishedFacts(new HashSet<string>(["fact|a"], StringComparer.Ordinal));
        // The process stops between this append and the next save.
        log.Append([Fact("b")]);

        Assert.Equal(["fact|a", "fact|b"], Log().LoadPublishedFacts().Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task An_append_wakes_every_waiting_reader()
    {
        var log = Log();
        var waits = Enumerable.Range(0, 3)
            .Select(_ => log.WaitForAppendAsync(log.Head, TimeSpan.FromMinutes(5), CancellationToken.None)).ToArray();
        Assert.All(waits, wait => Assert.False(wait.IsCompleted));

        log.Append([Fact("a")]);

        await Task.WhenAll(waits).WaitAsync(TimeSpan.FromSeconds(30));
    }

    private OperatorDecisionLog Log(TimeSpan? retention = null)
        => new(_folder, retention ?? TimeSpan.FromHours(24), _clock);

    private OperatorDecisionFact Fact(string name, string project = "alpha")
        => new(OperatorDecisionTypes.EscalationOpened, "fact|" + name, project, name, name, _clock.GetUtcNow().UtcDateTime, []);

    private static string Name(OperatorDecisionEvent item) => item.Reason;

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }
}
