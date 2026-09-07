
using Xunit;
using System.Text.Json;

namespace AgentStudio.Tests;

public class CliOutputLogParserTests
{
    [Fact]
    public void ParseLines_RehydratesPersistedCliOutput()
    {
        var lines = new[]
        {
            "[12:18:54.201] [stdout] * Read prompt.md",
            "[12:18:56.826] [stderr] Build failed"
        };

        var parsed = CliOutputLogParser.ParseLines(lines, new DateTime(2026, 4, 26, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(2, parsed.Count);
        Assert.Equal("stdout", parsed[0].Stream);
        Assert.Equal("* Read prompt.md", parsed[0].Text);
        Assert.Equal(new DateTime(2026, 4, 26, 12, 18, 54, 201, DateTimeKind.Utc), parsed[0].Timestamp);
        Assert.Equal("stderr", parsed[1].Stream);
        Assert.Equal("Build failed", parsed[1].Text);
    }

    [Fact]
    public void ParseLines_KeepsUnknownRowsVisible()
    {
        var parsed = CliOutputLogParser.ParseLines(
            ["raw row without persisted metadata"],
            new DateTime(2026, 4, 26, 0, 0, 0, DateTimeKind.Utc));

        Assert.Single(parsed);
        Assert.Equal("stdout", parsed[0].Stream);
        Assert.Equal("raw row without persisted metadata", parsed[0].Text);
    }

    [Fact]
    public void ParseLines_StripsAnsiFromPlainTextSnippets()
    {
        var parsed = CliOutputLogParser.ParseLines(
            ["[12:18:54.201] [stderr] \u001b[33m[39m Building...\u001b[0m"],
            new DateTime(2026, 4, 26, 0, 0, 0, DateTimeKind.Utc));

        Assert.Single(parsed);
        Assert.Equal(" Building...", parsed[0].Text);
    }

    // Regression: a runaway task can grow logs/cli-output.log without bound.
    // ParseFile is called from the supervisor tick, the review-decision tick,
    // the projection sources, the regression radar, and several frontend-polled
    // endpoints - concurrently. Materialising the whole file at every call site
    // multiplied peak memory until the host died with no managed exception
    // (OOM / runtime FailFast). ParseFile must cap what it loads into memory.
    [Fact]
    public void ParseFile_CapsLineCount_OnPathologicallyLargeLog()
    {
        var path = Path.GetTempFileName();
        try
        {
            var total = CliOutputLogParser.MaxLinesCap + 5_000;
            using (var w = new StreamWriter(path))
            {
                for (var i = 0; i < total; i++)
                    w.WriteLine($"[12:00:00.000] [stdout] line {i}");
            }

            var parsed = CliOutputLogParser.ParseFile(path);

            Assert.True(
                parsed.Count <= CliOutputLogParser.MaxLinesCap,
                $"ParseFile must cap materialised lines at {CliOutputLogParser.MaxLinesCap}; got {parsed.Count}");
            // The tail (most recent activity) is what the UI and live ticks care
            // about, so truncation drops the oldest bulk and keeps the end.
            Assert.Contains($"line {total - 1}", parsed[^1].Text);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ParseFile_TruncatesPathologicalSingleLine()
    {
        var path = Path.GetTempFileName();
        try
        {
            // One line, no trailing newline, far larger than the per-line cap.
            // File.ReadLines would materialise the whole thing as one string and
            // OOM on a multi-GB line; ParseFile must bound each line.
            var giant = new string('x', CliOutputLogParser.MaxLineCharsCap * 4);
            File.WriteAllText(path, "[12:00:00.000] [stdout] " + giant);

            var parsed = CliOutputLogParser.ParseFile(path);

            Assert.Single(parsed);
            Assert.True(
                parsed[0].Text.Length <= CliOutputLogParser.MaxLineCharsCap + 128,
                $"ParseFile must truncate a single giant line; got {parsed[0].Text.Length} chars");
            Assert.Contains("…[truncated: line exceeded ", parsed[0].Text, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ParseFile_KeepsOversizedCodexCommandFrameParseable()
    {
        var path = Path.GetTempFileName();
        try
        {
            var command = "rg -n \"needle\" .";
            var frame = JsonSerializer.Serialize(new
            {
                type = "item.completed",
                item = new
                {
                    id = "item_13",
                    type = "command_execution",
                    command,
                    aggregated_output = new string('x', 100 * 1024),
                    exit_code = 0,
                    status = "completed",
                },
            });
            File.WriteAllText(path, "[12:00:00.000] [stdout] " + frame);

            var parsedLines = CliOutputLogParser.ParseFile(path);

            var line = Assert.Single(parsedLines);
            Assert.True(line.Text.Length <= CliOutputLogParser.MaxLineCharsCap);
            Assert.StartsWith("{\"type\":\"item.completed\"", line.Text, StringComparison.Ordinal);
            using var parsed = JsonDocument.Parse(line.Text);
            var item = parsed.RootElement.GetProperty("item");
            Assert.Equal("item_13", item.GetProperty("id").GetString());
            Assert.Equal("command_execution", item.GetProperty("type").GetString());
            Assert.Equal(command, item.GetProperty("command").GetString());
            Assert.Contains("payload cut at the 64 KiB log line cap", item.GetProperty("aggregated_output").GetString());
            Assert.True(item.GetProperty("truncated").GetBoolean());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
