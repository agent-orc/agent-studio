using AgentStudio.Retention;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentStudio.Retention.Tests;

public sealed class RetentionExcerptWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "retention-excerpt-" + Guid.NewGuid().ToString("N"));
    private readonly ArtifactClassifier _classifier = new();

    public RetentionExcerptWriterTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public async Task CliExcerptContainsFixedSectionsErrorsMetricsAndCommands()
    {
        Directory.CreateDirectory(Path.Combine(_root, "logs"));
        var lines = Enumerable.Range(1, 800).Select(index => index switch
        {
            250 => "[12:00:00] command: dotnet test",
            400 => "[12:01:00] ERROR failed exit code 1",
            600 => "[12:02:00] token usage 123",
            _ => $"line {index}",
        }).ToArray();
        var path = Path.Combine(_root, "logs", "cli-output.log");
        await File.WriteAllLinesAsync(path, lines);
        var file = Item("logs/cli-output.log");

        var excerpt = await new RetentionExcerptWriter().WriteAsync(_root, [file]);

        foreach (var heading in new[] { "## Source", "## Summary", "## Head", "## Errors", "## Tail", "## Metrics", "## Timestamps and duration", "## Commands", "## Inventory" })
            Assert.Contains(heading, excerpt);
        Assert.Contains("ERROR failed exit code 1", excerpt);
        Assert.Contains("`dotnet test`", excerpt);
        Assert.Contains("Token lines: 1", excerpt);
    }

    [Theory]
    [InlineData("review/code-review-stdout.log", "verdict: pass", true)]
    [InlineData("review/code-review-stdout.log", "finding: fix this", true)]
    [InlineData("review/code-review-stdout.log", "ordinary chatter", false)]
    public async Task ReviewExcerptKeepsOnlyVerdictAndFindingLines(string relative, string line, bool expected)
    {
        Directory.CreateDirectory(Path.Combine(_root, "review"));
        await File.WriteAllTextAsync(Path.Combine(_root, relative), line);
        var excerpt = await new RetentionExcerptWriter().WriteAsync(_root, [Item(relative)]);
        Assert.Equal(expected, excerpt.Contains($"- {line}", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("results/report.md", "full report", true)]
    [InlineData("results/trace.zip", "binary-ish", false)]
    [InlineData("results/screenshot.png", "image-ish", false)]
    public async Task ResultsExcerptKeepsReportsButOnlyInventoriesHeavyBinary(string relative, string content, bool expectedFull)
    {
        Directory.CreateDirectory(Path.Combine(_root, "results"));
        await File.WriteAllTextAsync(Path.Combine(_root, relative), content);
        var excerpt = await new RetentionExcerptWriter().WriteAsync(_root, [Item(relative)]);
        Assert.Equal(expectedFull, excerpt.Contains($"### {relative}", StringComparison.Ordinal));
    }

    /// <summary>
    /// The first production run wrote 963 excerpts totalling 5.6 GB (largest 39.3 MB) because the error,
    /// timestamp and command sections copied one line per match. The excerpt is evidence, not a second copy.
    /// </summary>
    [Fact]
    public async Task HugeLogWithThousandsOfErrorsStaysWithinEverySectionCap()
    {
        Directory.CreateDirectory(Path.Combine(_root, "logs"));
        var path = Path.Combine(_root, "logs", "cli-output.log");
        await WriteSyntheticLogAsync(path);
        Assert.True(new FileInfo(path).Length > 10 * 1024 * 1024, "fixture must exceed 10 MiB");

        var rawExcerpt = await new RetentionExcerptWriter().WriteAsync(_root, [Item("logs/cli-output.log")]);

        Assert.True(
            Encoding.UTF8.GetByteCount(rawExcerpt) <= RetentionExcerptWriter.MaxExcerptBytes,
            $"excerpt was {Encoding.UTF8.GetByteCount(rawExcerpt)} bytes");

        var excerpt = NormalizeLineEndings(rawExcerpt);

        var windows = Regex.Matches(excerpt, @"^Lines \d+-\d+:$", RegexOptions.Multiline).Count;
        Assert.InRange(windows, 1, RetentionExcerptWriter.MaxErrorWindows);

        var commands = Regex.Matches(Section(excerpt, "## Commands"), @"^- `", RegexOptions.Multiline).Count;
        Assert.InRange(commands, 1, RetentionExcerptWriter.MaxCommandLines);

        var timestamps = Section(excerpt, "## Timestamps and duration");
        Assert.Contains("- First timestamp:", timestamps, StringComparison.Ordinal);
        Assert.Contains("- Last timestamp:", timestamps, StringComparison.Ordinal);
        Assert.Contains("- Duration:", timestamps, StringComparison.Ordinal);
        Assert.Contains("- Gaps above", timestamps, StringComparison.Ordinal);
        // The defect was one output line per timestamped input line; 227,777 lines for a single task.
        Assert.True(timestamps.Split('\n').Length < 100, "timestamp section must summarise, not enumerate");
    }

    [Fact]
    public async Task HeadAndTailStillCarryTheConfiguredLineCounts()
    {
        Directory.CreateDirectory(Path.Combine(_root, "logs"));
        await File.WriteAllLinesAsync(
            Path.Combine(_root, "logs", "cli-output.log"),
            Enumerable.Range(1, 5000).Select(index => $"line {index}"));

        var excerpt = NormalizeLineEndings(
            await new RetentionExcerptWriter().WriteAsync(_root, [Item("logs/cli-output.log")]));

        Assert.Contains("line 1\n", excerpt, StringComparison.Ordinal);
        Assert.Contains($"line {RetentionExcerptWriter.HeadLines}\n", excerpt, StringComparison.Ordinal);
        Assert.DoesNotContain($"line {RetentionExcerptWriter.HeadLines + 1}\n", Section(excerpt, "## Head"), StringComparison.Ordinal);
        Assert.Contains($"line {5000 - RetentionExcerptWriter.TailLines + 1}\n", excerpt, StringComparison.Ordinal);
        Assert.Contains("line 5000\n", excerpt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void SectionParserHandlesPlatformLineEndings(string newline)
    {
        var excerpt = string.Join(newline,
        [
            "## Errors",
            "",
            "Lines 1-3:",
            "```text",
            "error",
            "```",
            "",
            "## Tail",
            "tail",
        ]);

        var section = Section(excerpt, "## Errors");

        Assert.Contains("Lines 1-3:\n", section, StringComparison.Ordinal);
        Assert.DoesNotContain("## Tail", section, StringComparison.Ordinal);
    }

    private static async Task WriteSyntheticLogAsync(string path)
    {
        await using var writer = new StreamWriter(path);
        var filler = new string('x', 90);
        for (var index = 1; index <= 120_000; index++)
        {
            var minute = index / 60 % 60;
            var stamp = $"[2026-09-07T{index / 3600 % 24:00}:{minute:00}:{index % 60:00}Z]";
            if (index % 20 == 0) await writer.WriteLineAsync($"{stamp} ERROR failed exit code 1 at step {index}");
            else if (index % 37 == 0) await writer.WriteLineAsync($"{stamp} command: dotnet run --step {index}");
            else await writer.WriteLineAsync($"{stamp} line {index} {filler}");
        }
    }

    private static string Section(string excerpt, string heading)
    {
        excerpt = NormalizeLineEndings(excerpt);
        var start = excerpt.IndexOf(heading, StringComparison.Ordinal);
        if (start < 0) return string.Empty;
        var next = excerpt.IndexOf("\n## ", start + heading.Length, StringComparison.Ordinal);
        return next < 0 ? excerpt[start..] : excerpt[start..next];
    }

    private static string NormalizeLineEndings(string value)
        => value.ReplaceLineEndings("\n");

    private RetentionFile Item(string relative)
    {
        var info = new FileInfo(Path.Combine(_root, relative));
        return new(relative, info.Length, info.LastWriteTimeUtc, _classifier.Classify(relative));
    }
}
