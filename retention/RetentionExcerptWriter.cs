using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentStudio.Retention;

/// <summary>
/// Writes the class B excerpt that stays hot when class C originals move to the cold archive.
/// Every section is bounded: the excerpt is evidence about the run, not a second copy of it.
/// </summary>
public sealed class RetentionExcerptWriter
{
    /// <summary>Hard ceiling for the whole excerpt; the originals stay complete in the cold archive.</summary>
    public const int MaxExcerptBytes = 256 * 1024;
    public const int HeadLines = 200;
    public const int TailLines = 500;
    public const int MaxErrorWindows = 20;
    public const int ErrorWindowRadius = 25;
    public const int MaxCommandLines = 100;
    public const int MaxGapEntries = 50;
    private const int MaxInlineReportBytes = 64 * 1024;
    private const int MaxLineLength = 2000;

    private static readonly TimeSpan GapThreshold = TimeSpan.FromMinutes(1);

    private static readonly Regex ErrorPattern = new(
        @"error|exception|failed|exit(?:ed)?\s*(?:code|=|:)\s*[1-9]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CommandPattern = new(
        @"(?:^|\])\s*(?:\$|>|command:|exec(?:ute|uting|uted)?\s*:?)\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TimestampPattern = new(
        @"^\s*[\[\(]?\s*(?<ts>\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:[.,]\d+)?(?:Z|[+-]\d{2}:?\d{2})?|\d{2}:\d{2}:\d{2}(?:[.,]\d+)?)",
        RegexOptions.Compiled);

    public async Task<string> WriteAsync(
        string taskRoot,
        IReadOnlyList<RetentionFile> files,
        CancellationToken cancellationToken = default)
    {
        var output = new CappedBuilder(MaxExcerptBytes);
        output.AppendLine("# Retention excerpt").AppendLine();
        output.AppendLine("## Source").AppendLine();
        output.AppendLine("Generated before cold archival. Paths and hashes refer to the complete originals.").AppendLine();
        output.AppendLine("## Summary").AppendLine();
        output.AppendLine($"- Files: {files.Count}");
        output.AppendLine($"- Bytes: {files.Sum(file => file.Size)}").AppendLine();

        var cliLines = await ReadCliLinesAsync(taskRoot, files, cancellationToken);

        output.AppendLine("## Head").AppendLine();
        AppendLines(output, cliLines.Take(HeadLines));

        var errorIndices = cliLines.Select((line, index) => (line, index))
            .Where(item => ErrorPattern.IsMatch(item.line)).Select(item => item.index).ToList();
        output.AppendLine().AppendLine("## Errors").AppendLine();
        AppendErrorWindows(output, cliLines, errorIndices);

        output.AppendLine().AppendLine("## Tail").AppendLine();
        AppendLines(output, cliLines.Skip(Math.Max(0, cliLines.Count - TailLines)));

        var stamps = ParseTimestamps(cliLines);
        output.AppendLine().AppendLine("## Metrics").AppendLine();
        output.AppendLine($"- Error markers: {errorIndices.Count}");
        output.AppendLine($"- Tool calls: {Count(cliLines, "tool")}");
        output.AppendLine($"- Test runs: {Count(cliLines, "test")}");
        output.AppendLine($"- Commits: {Count(cliLines, "commit")}");
        output.AppendLine($"- Token lines: {Count(cliLines, "token")}");
        output.AppendLine($"- Timestamp/duration lines: {stamps.Count}");

        output.AppendLine().AppendLine("## Timestamps and duration").AppendLine();
        AppendTimestampSummary(output, stamps);

        output.AppendLine().AppendLine("## Commands").AppendLine();
        AppendCommands(output, cliLines);

        output.AppendLine().AppendLine("## Review verdicts and findings").AppendLine();
        foreach (var file in files.Where(file => file.Classification.Family == "review-stdout"))
        {
            var path = SafePath(taskRoot, file.RelativePath);
            if (!File.Exists(path)) continue;
            foreach (var line in await File.ReadAllLinesAsync(path, cancellationToken))
                if (line.Contains("verdict", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("finding", StringComparison.OrdinalIgnoreCase))
                    output.AppendLine($"- {line}");
        }

        output.AppendLine().AppendLine("## Inventory").AppendLine();
        output.AppendLine("| Path | Bytes | SHA-256 | Retained content |");
        output.AppendLine("|---|---:|---|---|");
        var inlined = new List<(string Relative, string Path)>();
        foreach (var file in files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            var path = SafePath(taskRoot, file.RelativePath);
            var hash = File.Exists(path) ? await Sha256Async(path, cancellationToken) : "missing";
            var retainFull = IsInlineReport(file);
            output.AppendLine($"| `{file.RelativePath.Replace("|", "\\|")}` | {file.Size} | `{hash}` | {(retainFull ? "full below" : "inventory only")} |");
            if (retainFull && File.Exists(path))
                inlined.Add((file.RelativePath, path));
        }

        foreach (var (relative, path) in inlined)
        {
            output.AppendLine().AppendLine($"### {relative}").AppendLine();
            output.AppendLine(await ReadCappedAsync(path, MaxInlineReportBytes, cancellationToken)).AppendLine();
        }

        return output.Build();
    }

    private static bool IsInlineReport(RetentionFile file)
        => file.Classification.Family == "results"
           && (file.RelativePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
               || file.RelativePath.Contains("report", StringComparison.OrdinalIgnoreCase));

    private static async Task<List<string>> ReadCliLinesAsync(
        string taskRoot,
        IReadOnlyList<RetentionFile> files,
        CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        foreach (var file in files.Where(file => file.Classification.Family == "cli-output"))
        {
            var path = SafePath(taskRoot, file.RelativePath);
            if (File.Exists(path))
                lines.AddRange(await File.ReadAllLinesAsync(path, cancellationToken));
        }
        return lines;
    }

    /// <summary>
    /// Keeps at most <see cref="MaxErrorWindows"/> merged windows around error markers, preferring the
    /// last ones: a failing run explains itself at the end, and thousands of markers must not be copied.
    /// </summary>
    private static void AppendErrorWindows(CappedBuilder output, List<string> lines, List<int> errorIndices)
    {
        var windows = MergeWindows(lines.Count, errorIndices);
        var kept = windows.Count > MaxErrorWindows ? windows[^MaxErrorWindows..] : windows;
        if (windows.Count > kept.Count)
            output.AppendLine($"> {windows.Count - kept.Count} earlier error windows omitted; showing the last {kept.Count} of {windows.Count}.").AppendLine();

        foreach (var (start, end) in kept)
        {
            output.AppendLine($"Lines {start + 1}-{end + 1}:");
            AppendLines(output, lines.Take(end + 1).Skip(start));
        }
        if (kept.Count == 0)
            output.AppendLine("No error markers found.");
    }

    /// <summary>
    /// Merges neighbouring error windows, but never past <see cref="MaxWindowLines"/>. Dense logs (an error
    /// every few lines) would otherwise merge into one window spanning the whole file - which is the copy
    /// the excerpt exists to avoid.
    /// </summary>
    private static List<(int Start, int End)> MergeWindows(int lineCount, List<int> indices)
    {
        const int MaxWindowLines = (2 * ErrorWindowRadius) + 1;
        var windows = new List<(int Start, int End)>();
        foreach (var index in indices)
        {
            var start = Math.Max(0, index - ErrorWindowRadius);
            var end = Math.Min(lineCount - 1, index + ErrorWindowRadius);
            if (windows.Count > 0 && start <= windows[^1].End + 1)
            {
                var merged = (windows[^1].Start, End: Math.Max(windows[^1].End, end));
                if (merged.End - merged.Start + 1 <= MaxWindowLines)
                {
                    windows[^1] = merged;
                    continue;
                }
                start = Math.Max(start, windows[^1].End + 1);
                if (start > end) continue;
            }
            windows.Add((start, end));
        }
        return windows;
    }

    private static List<(int Index, DateTime Stamp)> ParseTimestamps(List<string> lines)
    {
        var result = new List<(int, DateTime)>();
        DateTime? previous = null;
        for (var index = 0; index < lines.Count; index++)
        {
            if (!TryParseTimestamp(lines[index], previous, out var stamp)) continue;
            result.Add((index, stamp));
            previous = stamp;
        }
        return result;
    }

    private static bool TryParseTimestamp(string line, DateTime? previous, out DateTime stamp)
    {
        stamp = default;
        var match = TimestampPattern.Match(line);
        if (!match.Success) return false;
        var text = match.Groups["ts"].Value.Replace(',', '.');

        if (text.Length > 8 && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var absolute))
        {
            stamp = absolute.UtcDateTime;
            return true;
        }
        if (!TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var time)) return false;

        // Time-only logs carry no date; anchor them and roll forward across midnight so durations stay positive.
        var candidate = DateTime.MinValue.Add(time);
        if (previous is not null)
            while (candidate < previous.Value) candidate = candidate.AddDays(1);
        stamp = candidate;
        return true;
    }

    /// <summary>
    /// Reports only first, last, duration and the gaps worth noticing - never one line per timestamp.
    /// </summary>
    private static void AppendTimestampSummary(CappedBuilder output, List<(int Index, DateTime Stamp)> stamps)
    {
        if (stamps.Count == 0)
        {
            output.AppendLine("No timestamps found.");
            return;
        }

        var first = stamps[0];
        var last = stamps[^1];
        output.AppendLine($"- First timestamp: {Format(first.Stamp)} (line {first.Index + 1})");
        output.AppendLine($"- Last timestamp: {Format(last.Stamp)} (line {last.Index + 1})");
        output.AppendLine($"- Duration: {last.Stamp - first.Stamp:g}");

        var gaps = new List<(int Index, TimeSpan Gap)>();
        for (var index = 1; index < stamps.Count; index++)
        {
            var gap = stamps[index].Stamp - stamps[index - 1].Stamp;
            if (gap > GapThreshold) gaps.Add((stamps[index].Index, gap));
        }

        output.AppendLine($"- Gaps above {GapThreshold:g}: {gaps.Count}");
        foreach (var (index, gap) in gaps.OrderByDescending(item => item.Gap).Take(MaxGapEntries))
            output.AppendLine($"  - line {index + 1}: {gap:g}");
        if (gaps.Count > MaxGapEntries)
            output.AppendLine($"  - {gaps.Count - MaxGapEntries} further gaps omitted.");
    }

    private static void AppendCommands(CappedBuilder output, List<string> lines)
    {
        var commands = lines.Select(line => CommandPattern.Match(line)).Where(match => match.Success)
            .Select(match => match.Groups[1].Value.Trim()).Distinct(StringComparer.Ordinal).ToList();
        foreach (var command in commands.Take(MaxCommandLines))
            output.AppendLine($"- `{command.Replace("`", "'")}`");
        if (commands.Count > MaxCommandLines)
            output.AppendLine($"- {commands.Count - MaxCommandLines} further distinct commands omitted.");
    }

    private static async Task<string> ReadCappedAsync(string path, int maxBytes, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (info.Length <= maxBytes)
            return await File.ReadAllTextAsync(path, cancellationToken);

        var buffer = new byte[maxBytes];
        await using var stream = File.OpenRead(path);
        var read = await stream.ReadAtLeastAsync(buffer, maxBytes, throwOnEndOfStream: false, cancellationToken);
        return Encoding.UTF8.GetString(buffer, 0, read)
               + Environment.NewLine + $"> Truncated at {maxBytes} bytes of {info.Length}; the original is in the cold archive.";
    }

    private static int Count(IEnumerable<string> lines, string token)
        => lines.Count(line => line.Contains(token, StringComparison.OrdinalIgnoreCase));

    private static string Format(DateTime value)
        => value.Date == DateTime.MinValue.Date
            ? value.TimeOfDay.ToString("c", CultureInfo.InvariantCulture)
            : value.ToString("O", CultureInfo.InvariantCulture);

    private static void AppendLines(CappedBuilder output, IEnumerable<string> lines)
    {
        output.AppendLine("```text");
        // A single pathological log line (a base64 blob, a minified bundle) must not spend the whole budget.
        foreach (var line in lines)
            output.AppendLine(line.Length <= MaxLineLength ? line : line[..MaxLineLength] + " ...[line truncated]");
        output.AppendLine("```");
    }

    private static string SafePath(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative));
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Artifact path escapes task root: {relative}");
        return full;
    }

    private static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Appends until the byte budget is spent, then stops and records a single truncation marker.
    /// Room for the marker is reserved up front so the budget is never exceeded.
    /// </summary>
    private sealed class CappedBuilder
    {
        private const string Marker = "> Excerpt truncated at the retention size cap; the complete originals are in the cold archive.";

        private readonly StringBuilder _builder = new();
        private readonly int _budget;
        private int _bytes;
        private bool _truncated;

        public CappedBuilder(int maxBytes)
            => _budget = maxBytes - Encoding.UTF8.GetByteCount(Marker) - (2 * Environment.NewLine.Length);

        public CappedBuilder AppendLine(string text = "")
        {
            if (_truncated) return this;
            var cost = Encoding.UTF8.GetByteCount(text) + Environment.NewLine.Length;
            if (_bytes + cost > _budget)
            {
                _truncated = true;
                return this;
            }
            _bytes += cost;
            _builder.AppendLine(text);
            return this;
        }

        public string Build()
            => _truncated ? _builder.AppendLine().AppendLine(Marker).ToString() : _builder.ToString();
    }
}
