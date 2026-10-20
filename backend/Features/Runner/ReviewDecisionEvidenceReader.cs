using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using AgentStudio.Cli;
using AgentStudio.Shared;
using AgentStudio.Tasks;

namespace AgentStudio.Runner;

/// <summary>Produces a bounded, redacted review signal from a mutable CLI log.</summary>
public static class ReviewDecisionEvidenceReader
{
    public const int SourceWindowBytes = 256 * 1024;
    public const int PromptLines = 80;
    public const int PromptBytes = 8 * 1024;
    private static readonly Regex Credential = new(
        @"(?i)(?:sk-[a-z0-9_-]{12,}|gh[opusr]_[a-z0-9_]{12,}|(?:api[_-]?key|access[_-]?token|authorization|password|secret)\s*[:=]\s*\S+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SessionPath = new(
        @"(?i)(?:[a-z]:[\\/]|~[\\/]|/(?!api/))\S{0,240}(?:sessions[\\/]|cli-output\.log|tool-calls\.jsonl)\S*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static int CountSessionEvents(TaskSessionLog sessions, string jobId, string watchPath)
        => sessions.ReadSessionEvents(jobId, watchPath).Count;

    public static RunTimeline BuildTimeline(TaskSessionLog sessions, string jobId,
        string watchPath, string folderPath, DateTime nowUtc)
    {
        var events = sessions.ReadSessionEvents(jobId, watchPath);
        var lines = CliOutputLogParser.ParseFile(TaskPaths.CliOutputLog(folderPath));
        return RunTimelineBuilder.Build(events, lines, nowUtc);
    }

    public static string ReadBlockedMessage(string folderPath)
    {
        var agentTurn = CliOutputLogParser.ParseFile(TaskPaths.CliOutputLog(folderPath))
            .Where(line => line is not null && !PendingDecisionScanner.IsFollowUpLine(line))
            .ToList();
        return Bound(Redact(AgentOutcomeAnalyzer.ExtractBlockedMessage(agentTurn) ?? string.Empty), 2_000);
    }

    public static ReviewDecisionLogEvidence Read(string logPath, int? activeEpochLineBoundary = null)
    {
        var lines = BoundedFileRead.ReadTailLines(logPath, SourceWindowBytes, out var truncated);
        var selected = lines;
        var lineOffset = 0;
        if (!truncated && activeEpochLineBoundary is >= 0)
        {
            lineOffset = activeEpochLineBoundary.Value <= lines.Count
                ? activeEpochLineBoundary.Value : 0;
            selected = lines.Skip(lineOffset).ToArray();
        }
        var text = string.Join('\n', selected);
        var sourceDigest = "sha256:" + Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        // A truncated source cannot establish whether its first signal was
        // already resolved before the window. Hold rather than re-enter work.
        if (truncated)
            return new(null, string.Empty, sourceDigest, ["source-window-truncated"]);

        var needs = ReviewDecisionParsing.FindUnresolvedNeedsInput(text);
        var noop = ReviewDecisionParsing.FindUnresolvedNoOp(text);
        var blocked = ReviewDecisionParsing.FindUnresolvedBlocked(text);
        var done = ReviewDecisionParsing.FindUnresolvedDone(text);
        var latest = new (string Kind, int Line, string? Reason)[]
        {
            ("needs-input", needs?.LineNumber ?? -1, needs?.Reason),
            ("noop", noop?.LineNumber ?? -1, noop?.Reason),
            ("blocked", blocked?.LineNumber ?? -1, blocked?.Reason),
            ("done", done?.LineNumber ?? -1, null),
        }.OrderByDescending(item => item.Line).First();
        ReviewDecisionSignal? signal = latest.Line >= 0
            ? new(latest.Kind, latest.Line + lineOffset,
                Bound(Redact(latest.Reason ?? string.Empty), 512))
            : ReviewDecisionParsing.LacksTerminalSentinelInLatestRun(text)
                ? new("no-completion-signal", -1, null) : null;
        var prompt = string.Join('\n', selected.TakeLast(PromptLines)
            .Select(Redact));
        var omissions = new List<string>();
        if (prompt.Contains("[omitted: secret]", StringComparison.Ordinal)
            || Credential.IsMatch(latest.Reason ?? string.Empty))
            omissions.Add("secret");
        if (prompt.Contains("[omitted: raw session path]", StringComparison.Ordinal)
            || SessionPath.IsMatch(latest.Reason ?? string.Empty))
            omissions.Add("raw-session-path");
        if (Encoding.UTF8.GetByteCount(prompt) > PromptBytes)
        {
            prompt = Bound(prompt, PromptBytes);
            while (Encoding.UTF8.GetByteCount(prompt) > PromptBytes)
                prompt = prompt[1..];
            omissions.Add("prompt-byte-cap");
        }
        return new(signal, prompt, sourceDigest, omissions);
    }

    /// <summary>
    /// Preserves legacy Monolith sentinel detection when a CLI log exceeds
    /// the prompt window. The scan is linewise; the returned prompt and digest
    /// still describe only the bounded, redacted evidence window.
    /// </summary>
    public static ReviewDecisionLogEvidence ReadForMonolith(string logPath,
        int? activeEpochLineBoundary = null)
    {
        var evidence = Read(logPath, activeEpochLineBoundary);
        if (!evidence.Omissions.Contains("source-window-truncated")) return evidence;

        using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var scan = ReviewDecisionParsing.ScanUnresolvedLines(ReadLines(reader), activeEpochLineBoundary);
        var signal = scan.Kind is null ? null : new ReviewDecisionSignal(scan.Kind,
            scan.LineNumber, Bound(Redact(scan.Reason ?? string.Empty), 512));
        var omissions = evidence.Omissions.ToList();
        if (Credential.IsMatch(scan.Reason ?? string.Empty)) omissions.Add("secret");
        if (SessionPath.IsMatch(scan.Reason ?? string.Empty)) omissions.Add("raw-session-path");
        return evidence with { Signal = signal, Omissions = omissions.Distinct().ToArray() };
    }

    private static IEnumerable<string> ReadLines(StreamReader reader)
    {
        while (reader.ReadLine() is { } line) yield return line;
    }

    public static NoOpProgressEvidence ReadRecoveryProgress(string logPath, int noopLineNumber)
    {
        if (!File.Exists(logPath)) return NoOpProgressEvidence.None;

        IReadOnlyList<string> lines;
        bool truncated;
        try { lines = BoundedFileRead.ReadTailLines(logPath, SourceWindowBytes, out truncated); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SilentCatch.Note(ex, "ReviewDecisionEvidenceReader: recovery progress source unavailable.");
            return NoOpProgressEvidence.None;
        }
        if (truncated || lines.Count == 0) return NoOpProgressEvidence.None;
        var noopIndex = Math.Clamp(noopLineNumber - 1, 0, Math.Max(lines.Count - 1, 0));

        var reissueIndex = -1;
        for (var i = noopIndex - 1; i >= 0; i--)
        {
            var line = lines[i];
            if (line.Contains("[orchestrator]", StringComparison.Ordinal)
                && line.Contains("[reissue]", StringComparison.Ordinal)
                && line.Contains("NOOP recovery", StringComparison.OrdinalIgnoreCase))
            {
                reissueIndex = i;
                break;
            }
        }

        if (reissueIndex < 0) return NoOpProgressEvidence.None;

        var toolCalls = 0;
        var fileChanges = 0;
        var agentSubstanceChars = 0;

        for (var i = reissueIndex + 1; i <= noopIndex && i < lines.Count; i++)
        {
            InspectProgressLine(lines[i], ref toolCalls, ref fileChanges, ref agentSubstanceChars);
        }

        return new NoOpProgressEvidence(
            SawNoOpRecoveryReissue: true,
            ToolCalls: toolCalls,
            FileChanges: fileChanges,
            AgentSubstanceChars: agentSubstanceChars);
    }

    private static void InspectProgressLine(
        string line,
        ref int toolCalls,
        ref int fileChanges,
        ref int agentSubstanceChars)
    {
        var jsonStart = line.IndexOf('{');
        if (jsonStart >= 0)
        {
            var json = line[jsonStart..].Trim();
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeProp)) return;
                var type = typeProp.GetString();
                if (!string.Equals(type, "item.started", StringComparison.Ordinal)
                    && !string.Equals(type, "item.completed", StringComparison.Ordinal))
                {
                    return;
                }

                if (!root.TryGetProperty("item", out var item)
                    || item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("type", out var itemTypeProp))
                {
                    return;
                }

                var itemType = itemTypeProp.GetString();
                if (string.Equals(itemType, "agent_message", StringComparison.Ordinal))
                {
                    var text = item.TryGetProperty("text", out var textProp)
                        ? textProp.GetString()
                        : null;
                    agentSubstanceChars += CountAgentSubstanceChars(text);
                    return;
                }

                if (string.Equals(itemType, "file_change", StringComparison.Ordinal))
                {
                    fileChanges++;
                    return;
                }

                if (!string.IsNullOrWhiteSpace(itemType))
                {
                    toolCalls++;
                }
                return;
            }
            catch (JsonException __ex)
            {
                SilentCatch.Note(__ex, "ReviewDecisionEvidenceReader: Fall through and treat it as ordinary output text.");
                // Fall through and treat it as ordinary output text.
            }
        }

        var payloadText = ExtractStreamPayload(line);
        agentSubstanceChars += CountAgentSubstanceChars(payloadText);
    }

    private static string ExtractStreamPayload(string line)
    {
        var stdoutMarker = "] [stdout] ";
        var stderrMarker = "] [stderr] ";
        var stdoutAt = line.IndexOf(stdoutMarker, StringComparison.Ordinal);
        if (stdoutAt >= 0) return line[(stdoutAt + stdoutMarker.Length)..];
        var stderrAt = line.IndexOf(stderrMarker, StringComparison.Ordinal);
        if (stderrAt >= 0) return line[(stderrAt + stderrMarker.Length)..];
        return line;
    }

    private static int CountAgentSubstanceChars(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var trimmed = text.Trim();
        if (trimmed.StartsWith("[[TASK_NOOP", StringComparison.OrdinalIgnoreCase)
            && trimmed.EndsWith("]]", StringComparison.Ordinal))
        {
            return 0;
        }

        var withoutNoOp = text
            .Replace("[[TASK_NOOP]]", "", StringComparison.OrdinalIgnoreCase)
            .Trim();
        return withoutNoOp.Length;
    }

    private static string Bound(string value, int maxChars)
        => value.Length <= maxChars ? value : value[^maxChars..];

    private static string Redact(string value)
        => SessionPath.Replace(Credential.Replace(value, "[omitted: secret]"),
            "[omitted: raw session path]");
}

public sealed record ReviewDecisionSignal(string Kind, int LineNumber, string? Reason);
public sealed record ReviewDecisionLogEvidence(ReviewDecisionSignal? Signal, string PromptWindow,
    string SourceDigest, IReadOnlyList<string> Omissions);

public sealed record NoOpProgressEvidence(
    bool SawNoOpRecoveryReissue,
    int ToolCalls,
    int FileChanges,
    int AgentSubstanceChars)
{
    public static readonly NoOpProgressEvidence None = new(false, 0, 0, 0);
}
