using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AgentStudio.CliHosting;

namespace AgentStudio.Cli;

public static partial class CliOutputLogParser
{
    private static readonly string[] TimeFormats = ["HH:mm:ss.fff", "HH:mm:ss"];

    /// <summary>
    /// Hard memory bounds for <see cref="ParseFile"/>. <c>cli-output.log</c> is
    /// the raw redirect of a child CLI's stdout/stderr and is NOT capped on
    /// disk - a runaway agent can grow it to hundreds of MB or emit a single
    /// newline-less line of arbitrary size. <see cref="ParseFile"/> is called
    /// from many hot paths (the supervisor observation tick, the review-decision
    /// tick, the projection sources, the regression radar, and several
    /// frontend-polled <c>/api/tasks/...</c> endpoints), several of them
    /// concurrently. Materialising the whole file into a
    /// <see cref="List{T}"/> at every call site multiplied peak memory until the
    /// host died with no managed exception (OOM / runtime FailFast - the
    /// "silent disappearance" class the crash markers in Program.cs predict).
    /// These caps keep peak memory bounded regardless of how large the file
    /// grows; the most recent activity is preserved (tail), older bulk is
    /// dropped with a visible marker. Normal jobs (thousands of short lines)
    /// never hit these and parse unchanged.
    /// </summary>
    public const int MaxLinesCap = 50_000;
    public const int MaxLineCharsCap = 64 * 1024;
    public const long MaxTotalCharsCap = 16_000_000;

    public static List<CliOutputLine> ParseFile(string path)
    {
        if (!File.Exists(path)) return [];

        var fallbackDate = File.GetLastWriteTimeUtc(path).Date;
        var (tail, droppedLines) = ReadBoundedTail(path, MaxLinesCap, MaxLineCharsCap, MaxTotalCharsCap);
        var parsed = ParseLines(tail, fallbackDate);

        if (droppedLines > 0)
        {
            // Stay within the cap: the notice takes one slot so the returned
            // list never exceeds MaxLinesCap, and make the truncation visible
            // instead of silently presenting a partial log.
            if (parsed.Count >= MaxLinesCap && parsed.Count > 0)
            {
                parsed.RemoveAt(0);
                droppedLines++;
            }
            parsed.Insert(0, new CliOutputLine
            {
                Timestamp = fallbackDate,
                Stream = "system",
                Text = $"[taskboard] cli-output.log truncated for parsing: dropped {droppedLines} older line(s) " +
                       $"to stay within the {MaxLinesCap:N0}-line parse cap (showing most recent activity)."
            });
        }

        return parsed;
    }

    /// <summary>
    /// Stream <paramref name="path"/> and return only the trailing
    /// <paramref name="maxLines"/> lines (and at most <paramref name="maxTotalChars"/>
    /// characters), truncating any individual line longer than
    /// <paramref name="maxLineChars"/>. Reads with a fixed char buffer rather
    /// than <see cref="StreamReader.ReadLine"/> so a pathological newline-less
    /// line cannot be materialised in full (which would OOM before any cap
    /// could apply). Memory is bounded to roughly
    /// <c>maxTotalChars + maxLineChars</c> regardless of file size.
    /// </summary>
    private static (List<string> Lines, long DroppedLines) ReadBoundedTail(
        string path, int maxLines, int maxLineChars, long maxTotalChars)
    {
        var tail = new LinkedList<string>();
        long totalChars = 0;
        long dropped = 0;

        void Append(string line)
        {
            tail.AddLast(line);
            totalChars += line.Length;
            while (tail.Count > maxLines || (totalChars > maxTotalChars && tail.Count > 1))
            {
                totalChars -= tail.First!.Value.Length;
                tail.RemoveFirst();
                dropped++;
            }
        }

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        var sb = new StringBuilder();
        var overflow = false;
        var buffer = new char[16 * 1024];
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var c = buffer[i];
                if (c == '\n')
                {
                    Append(FinishLine(sb, overflow, maxLineChars));
                    sb.Clear();
                    overflow = false;
                }
                else if (c == '\r')
                {
                    // Swallow CR; a following LF terminates the line (CRLF),
                    // matching the log writer's newline handling.
                }
                else if (!overflow)
                {
                    if (sb.Length < maxLineChars) sb.Append(c);
                    else overflow = true; // drop the rest of an over-long line
                }
            }
        }

        if (sb.Length > 0 || overflow)
            Append(FinishLine(sb, overflow, maxLineChars));

        return (new List<string>(tail), dropped);
    }

    private static string FinishLine(StringBuilder sb, bool overflow, int maxLineChars)
    {
        var prefix = sb.ToString();
        if (!overflow) return prefix;

        var match = PersistedLineRegex().Match(prefix);
        var payloadIndex = match.Success ? match.Groups["text"].Index : 0;
        var payload = prefix[payloadIndex..];
        var payloadCap = maxLineChars - payloadIndex;
        if (payloadCap > 0
            && JsonLineTruncator.TryTruncateCodexFramePrefix(payload, payloadCap, out var bounded))
        {
            return prefix[..payloadIndex] + bounded;
        }

        return prefix + $"…[truncated: line exceeded {maxLineChars:N0} chars]";
    }

    public static List<CliOutputLine> ParseLines(IEnumerable<string> lines, DateTime fallbackDateUtc)
    {
        var fallbackDate = fallbackDateUtc.Kind == DateTimeKind.Utc
            ? fallbackDateUtc.Date
            : fallbackDateUtc.ToUniversalTime().Date;

        var output = new List<CliOutputLine>();
        foreach (var line in lines)
        {
            output.Add(ParseLine(line, fallbackDate));
        }

        return output;
    }

    /// <summary>
    /// Parse one persisted CLI-log row. Internal consumers that must fold a
    /// complete file can stream lines through this method without materializing
    /// the bounded UI-oriented <see cref="ParseFile"/> result.
    /// </summary>
    internal static CliOutputLine ParseLine(string line, DateTime fallbackDateUtc)
    {
        var match = PersistedLineRegex().Match(line);
        if (!match.Success)
        {
            return new CliOutputLine
            {
                Timestamp = fallbackDateUtc,
                Stream = "stdout",
                Text = AnsiText.Strip(line)
            };
        }

        var timestamp = fallbackDateUtc;
        if (DateTime.TryParseExact(match.Groups["time"].Value, TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedTime))
        {
            timestamp = fallbackDateUtc.Add(parsedTime.TimeOfDay);
        }

        return new CliOutputLine
        {
            Timestamp = DateTime.SpecifyKind(timestamp, DateTimeKind.Utc),
            Stream = match.Groups["stream"].Value,
            Text = AnsiText.Strip(match.Groups["text"].Value)
        };
    }

    [GeneratedRegex(@"^\[(?<time>\d{2}:\d{2}:\d{2}(?:\.\d{3})?)\]\s+\[(?<stream>stdout|stderr|user|system|orchestrator)\]\s?(?<text>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex PersistedLineRegex();
}
