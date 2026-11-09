using AgentStudio.Tokens;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3369: the token ledger re-reads every receipt of a project whenever the
/// task index moves, and that walk was the largest single allocator of the
/// API under load. The reader now keeps each folder's parsed receipt against
/// its <c>task.json</c> length and write time. These tests pin that a reread
/// still sees every change and that removed cards drop out.
/// </summary>
public sealed class ProjectTokenReceiptReaderMemoTests : IDisposable
{
    private readonly string _watchPath = Path.Combine(Path.GetTempPath(), "receipt-memo-" + Guid.NewGuid().ToString("N"));

    public ProjectTokenReceiptReaderMemoTests() => Directory.CreateDirectory(_watchPath);

    public void Dispose()
    {
        try { Directory.Delete(_watchPath, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void Unchanged_receipt_is_reused_and_a_rewritten_one_is_read_again()
    {
        WriteTask("T-1", inputTokens: 100);
        WriteTask("T-2", inputTokens: 7);
        var reader = new ProjectTokenReceiptReader();

        var first = reader.Read(_watchPath);
        var second = reader.Read(_watchPath);

        Assert.Equal(107, first.Entries.Sum(entry => entry.TokenUsage!.InputTokens));
        Assert.Same(first.Summaries["t-1"], second.Summaries["t-1"]);

        WriteTask("T-1", inputTokens: 25_000);
        var third = reader.Read(_watchPath);

        Assert.Equal(25_007, third.Entries.Sum(entry => entry.TokenUsage!.InputTokens));
        Assert.Equal(25_000, third.Summaries["t-1"].InputTokens);
        Assert.Same(first.Summaries["t-2"], third.Summaries["t-2"]);
    }

    [Fact]
    public void Removed_card_drops_out_and_a_torn_file_is_read_again_once_complete()
    {
        WriteTask("T-1", inputTokens: 100);
        WriteTask("T-2", inputTokens: 7);
        var reader = new ProjectTokenReceiptReader();
        Assert.Equal(2, reader.Read(_watchPath).Summaries.Count);

        Directory.Delete(TaskFolder("T-2"), recursive: true);
        File.WriteAllText(Path.Combine(TaskFolder("T-1"), "task.json"), "{\"id\":\"t-1\",\"tokenSumm");
        var torn = reader.Read(_watchPath);

        Assert.Empty(torn.Summaries);
        Assert.Equal(1, torn.FailedTaskCount);

        WriteTask("T-1", inputTokens: 300);
        var healed = reader.Read(_watchPath);

        Assert.Equal(0, healed.FailedTaskCount);
        Assert.Equal(300, Assert.Single(healed.Summaries).Value.InputTokens);
    }

    private string TaskFolder(string key) => Path.Combine(_watchPath, "tasks", "000", key);

    private void WriteTask(string key, long inputTokens)
    {
        var folder = TaskFolder(key);
        Directory.CreateDirectory(folder);
        var id = key.ToLowerInvariant();
        // clock-independent: a fixed receipt timestamp, not compared with the wall clock.
        const string at = "2026-11-01T09:00:00Z";
        File.WriteAllText(Path.Combine(folder, "task.json"), $$"""
            {
              "id": "{{id}}",
              "key": "{{key}}",
              "state": "6-completed",
              "tokenSummary": {
                "calls": 1,
                "inputTokens": {{inputTokens}},
                "totalTokens": {{inputTokens}},
                "lastUpdate": "{{at}}",
                "entries": [
                  { "ts": "{{at}}", "model": "claude-opus-4-8", "participantId": "agent:test", "inputTokens": {{inputTokens}} }
                ]
              }
            }
            """);
    }
}
