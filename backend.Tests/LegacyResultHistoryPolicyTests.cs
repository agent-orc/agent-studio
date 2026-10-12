using System.Text;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: the policy receives every instant as an argument and the file tests only round-trip recorded timestamps.

/// <summary>
/// The decisions behind the background legacy Result backfill (AGT-3086): when
/// a card's backfill runs, what the <c>X-Result-History-Legacy</c> header says,
/// how the persisted file stays under its cap, and that a broken file reads as
/// failed rather than as "not computed yet".
/// <see cref="TaskFileHistoryEndpointsTests"/> drives the same rules through the
/// endpoints.
/// </summary>
public sealed class LegacyResultHistoryPolicyTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-12T12:00:00Z");
    private static readonly TimeSpan Retry = LegacyResultHistoryPolicy.RetryInterval;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "legacy-result-history-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* best-effort */ }
    }

    public static TheoryData<string, TimeSpan?, TimeSpan?, bool, string, bool> Matrix => new()
    {
        // persisted, computed ago, last attempt ago, running => header, schedule
        // A complete outcome is final: never walk git again for that card.
        { LegacyResultHistoryStates.Complete, TimeSpan.FromDays(30), null, false, LegacyResultHistoryStates.Complete, false },
        { LegacyResultHistoryStates.Complete, TimeSpan.Zero, TimeSpan.Zero, true, LegacyResultHistoryStates.Complete, false },
        // No outcome yet: schedule once, pending while it runs.
        { LegacyResultHistoryStates.Missing, null, null, false, LegacyResultHistoryStates.Pending, true },
        { LegacyResultHistoryStates.Missing, null, TimeSpan.Zero, true, LegacyResultHistoryStates.Pending, false },
        // A run ended without leaving a file: that is a failure, retried on the interval.
        { LegacyResultHistoryStates.Missing, null, TimeSpan.FromMinutes(5), false, LegacyResultHistoryStates.Failed, false },
        { LegacyResultHistoryStates.Missing, null, Retry, false, LegacyResultHistoryStates.Pending, true },
        // Failed: the header stays failed; one retry per interval.
        { LegacyResultHistoryStates.Failed, TimeSpan.FromMinutes(1), null, false, LegacyResultHistoryStates.Failed, false },
        { LegacyResultHistoryStates.Failed, Retry - TimeSpan.FromSeconds(1), null, false, LegacyResultHistoryStates.Failed, false },
        { LegacyResultHistoryStates.Failed, Retry, null, false, LegacyResultHistoryStates.Failed, true },
        { LegacyResultHistoryStates.Failed, Retry, TimeSpan.FromMinutes(1), false, LegacyResultHistoryStates.Failed, false },
        { LegacyResultHistoryStates.Failed, Retry, null, true, LegacyResultHistoryStates.Failed, false },
        // Unreadable file: failed with no known computedAt, so one retry rewrites it.
        { LegacyResultHistoryStates.Failed, null, null, false, LegacyResultHistoryStates.Failed, true },
        { LegacyResultHistoryStates.Failed, null, TimeSpan.FromMinutes(1), false, LegacyResultHistoryStates.Failed, false },
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Decide_SchedulesOnlyWhenNoAnswerOrARetryIsDue(
        string persisted,
        TimeSpan? computedAgo,
        TimeSpan? attemptAgo,
        bool running,
        string expectedState,
        bool expectedSchedule)
    {
        var decision = LegacyResultHistoryPolicy.Decide(
            persisted,
            computedAgo is { } computed ? Now - computed : null,
            attemptAgo is { } attempt ? Now - attempt : null,
            running,
            Now);

        Assert.Equal(new LegacyResultHistoryDecision(expectedState, expectedSchedule), decision);
    }

    [Fact]
    public void Fit_DropsTheMarkdownOfTheOldestEntriesFirst()
    {
        var markdown = new string('x', 400 * 1024);
        var record = Record(
            Entry("newest", Now.AddDays(-1), markdown),
            Entry("oldest", Now.AddDays(-30), markdown),
            Entry("middle", Now.AddDays(-10), markdown));

        var fitted = LegacyResultHistoryFile.Fit(record, BoundedFileRead.SidecarBytes, out var bytes);

        Assert.True(bytes.Length <= BoundedFileRead.SidecarBytes, $"{bytes.Length} bytes");
        Assert.Equal(LegacyResultHistoryStates.Complete, fitted.Outcome);
        var bySha = fitted.Entries.ToDictionary(entry => entry.Sha);
        Assert.True(bySha["oldest"].Truncated);
        Assert.Null(bySha["oldest"].Markdown);
        Assert.Equal(markdown, bySha["middle"].Markdown);
        Assert.Equal(markdown, bySha["newest"].Markdown);
        Assert.False(bySha["newest"].Truncated);
        // The hash survives truncation so the read side can still exclude it.
        Assert.Equal("hash-oldest", bySha["oldest"].ContentHash);
        Assert.Equal(new[] { "newest", "oldest", "middle" }, fitted.Entries.Select(entry => entry.Sha));
    }

    [Fact]
    public void Fit_FailsARecordThatCannotFitEvenWithoutMarkdown()
    {
        var record = Record(Enumerable.Range(0, 20)
            .Select(index => Entry($"sha-{index}", Now.AddDays(-index), "short"))
            .ToArray());

        var fitted = LegacyResultHistoryFile.Fit(record, maxBytes: 1024, out var bytes);

        Assert.Equal(LegacyResultHistoryStates.Failed, fitted.Outcome);
        Assert.Contains("do not fit", fitted.Reason);
        Assert.Empty(fitted.Entries);
        Assert.True(bytes.Length <= 1024);
    }

    [Fact]
    public void Read_RoundTripsAWrittenRecord()
    {
        var record = Record(Entry("abc", Now.AddDays(-2), "# Status\n- Result: Partial\n"));

        LegacyResultHistoryFile.Write(_folder, record);
        var snapshot = LegacyResultHistoryFile.Read(_folder);

        Assert.Equal(LegacyResultHistoryStates.Complete, snapshot.State);
        Assert.Equal(Now, snapshot.Record!.ComputedAt);
        Assert.Equal("# Status\n- Result: Partial\n", Assert.Single(snapshot.Record.Entries).Markdown);
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(LegacyResultHistoryFile.PathFor(_folder))!, "*.tmp"));
    }

    [Fact]
    public void Read_ReportsAMissingFileAsMissing()
    {
        Assert.Equal(LegacyResultHistoryStates.Missing, LegacyResultHistoryFile.Read(_folder).State);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{\"schemaVersion\":99,\"outcome\":\"complete\",\"entries\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"outcome\":\"pending\",\"entries\":[]}")]
    public void Read_ReportsACorruptFileAsFailed(string content)
    {
        WriteRaw(Encoding.UTF8.GetBytes(content));

        var snapshot = LegacyResultHistoryFile.Read(_folder);

        Assert.Equal(LegacyResultHistoryStates.Failed, snapshot.State);
        Assert.Null(snapshot.Record);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.Reason));
    }

    [Fact]
    public void Read_ReportsAnOversizedFileAsFailed()
    {
        var json = Encoding.UTF8.GetBytes(
            "{\"schemaVersion\":1,\"outcome\":\"complete\",\"entries\":[],\"pad\":\""
            + new string('x', BoundedFileRead.SidecarBytes) + "\"}");
        WriteRaw(json);

        var snapshot = LegacyResultHistoryFile.Read(_folder);

        Assert.Equal(LegacyResultHistoryStates.Failed, snapshot.State);
        Assert.Contains("exceeds", snapshot.Reason);
    }

    private void WriteRaw(byte[] content)
    {
        var path = LegacyResultHistoryFile.PathFor(_folder);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    private static LegacyResultHistoryRecord Record(params LegacyResultHistoryEntry[] entries) => new(
        LegacyResultHistoryFile.SchemaVersion,
        Now,
        LegacyResultHistoryStates.Complete,
        null,
        ElapsedMs: 1,
        entries);

    private static LegacyResultHistoryEntry Entry(string sha, DateTimeOffset at, string markdown) => new(
        sha,
        at.UtcDateTime,
        "run attempt",
        ResultProducerKinds.RunAttempt,
        TaskStates.Escalated,
        "Partial",
        "generic",
        "hash-" + sha,
        markdown,
        Truncated: false);
}
