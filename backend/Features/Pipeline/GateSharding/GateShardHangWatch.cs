using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace AgentStudio.Pipeline;

/// <summary>Tracks output progress and a completed TRX while a shard process is alive.</summary>
internal sealed class GateShardHangWatch(
    string suite, int index, int count, IReadOnlyList<string> classes,
    TimeSpan idleBound, TimeSpan completedGrace)
{
    private static readonly Regex Result = new(
        @"^\s*(?:Passed|Failed|Skipped)\s+(?<name>.+?)\s+\[[^]]+\]\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Started = new(
        @"^\s*(?:Starting|Running)\s+(?<name>\S+\.\S+)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly object _sync = new();
    private long _lastOutput = Stopwatch.GetTimestamp();
    private long? _reportComplete;
    private readonly Queue<string> _recentResults = new();
    private string? _started;

    public TimeSpan IdleBound { get; } = idleBound;
    public TimeSpan CompletedGrace { get; } = completedGrace;

    public void Observe(string line)
    {
        lock (_sync)
        {
            _lastOutput = Stopwatch.GetTimestamp();
            if (Result.Match(line) is { Success: true } result)
            {
                _recentResults.Enqueue(result.Groups["name"].Value);
                while (_recentResults.Count > 8) _recentResults.Dequeue();
                _started = null;
            }
            if (Started.Match(line) is { Success: true } started)
                _started = started.Groups["name"].Value;
        }
    }

    public GateShardHangEvidence? Check(string? reportDirectory, string? reportPrefix)
    {
        lock (_sync)
        {
            var now = Stopwatch.GetTimestamp();
            if (_reportComplete is null && ReportComplete(reportDirectory, reportPrefix))
                _reportComplete = now;
            var names = _started is not null ? new[] { _started }
                : _recentResults.Count > 0 ? _recentResults.ToArray()
                : classes.Take(8).ToArray();
            if (_reportComplete is { } completed && Stopwatch.GetElapsedTime(completed, now) >= CompletedGrace)
                return new GateShardHangEvidence(suite, index, count, "completed report but host still alive",
                    (long)Stopwatch.GetElapsedTime(completed, now).TotalMilliseconds, names);
            var silent = Stopwatch.GetElapsedTime(_lastOutput, now);
            return silent >= IdleBound
                ? new GateShardHangEvidence(suite, index, count, "no result or output",
                    (long)silent.TotalMilliseconds, names)
                : null;
        }
    }

    public GateShardHangEvidence BlameExit()
    {
        lock (_sync)
        {
            var names = _started is not null ? new[] { _started }
                : _recentResults.Count > 0 ? _recentResults.ToArray()
                : classes.Take(8).ToArray();
            return new GateShardHangEvidence(suite, index, count, "blame hang timeout",
                (long)(IdleBound.TotalMilliseconds * 0.9), names);
        }
    }

    /// <summary>Reads VSTest's sequence attachment when its hang collector ended the host.</summary>
    public static IReadOnlyList<string> BlameSequenceNames(string output, string? resultsDirectory)
    {
        if (resultsDirectory is null) return [];
        foreach (var line in output.Split('\n'))
        {
            var path = line.Trim().Trim('"', '\'');
            if (!Path.IsPathRooted(path)) continue;
            var fileName = Path.GetFileName(path);
            if (!fileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                || !fileName.Contains("Sequence", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var full = Path.GetFullPath(path);
                var relative = Path.GetRelativePath(resultsDirectory, full);
                if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar,
                        StringComparison.Ordinal) || new FileInfo(full).Length > 1024 * 1024) continue;
                using var reader = XmlReader.Create(full, new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    MaxCharactersInDocument = 1024 * 1024,
                });
                return XDocument.Load(reader).Descendants()
                    .Where(item => item.Name.LocalName == "Test")
                    .Select(item => (string?)item.Attribute("Name"))
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name!).TakeLast(8).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
            { SilentCatch.Note(ex, "GateShardHangWatch: unreadable blame sequence"); }
        }
        return [];
    }

    private static bool ReportComplete(string? directory, string? prefix)
    {
        if (directory is null || prefix is null) return false;
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, prefix + "*.trx", SearchOption.TopDirectoryOnly))
            {
                using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (stream.Length < 10) continue;
                stream.Seek(-Math.Min(stream.Length, 256), SeekOrigin.End);
                using var reader = new StreamReader(stream);
                if (reader.ReadToEnd().Contains("</TestRun>", StringComparison.Ordinal)) return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { SilentCatch.Note(ex, "GateShardHangWatch: incomplete test report"); }
        return false;
    }
}
