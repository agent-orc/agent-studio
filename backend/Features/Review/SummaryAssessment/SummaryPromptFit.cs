using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentStudio.Review;

/// <summary>One file under <c>results/</c> offered to the summarizer as a deliverable.</summary>
public sealed record SummaryDeliverable(string Path, long Bytes);

/// <summary>What the <c>{{log}}</c> slot of the summary prompt is built from.</summary>
public sealed record SummaryLogSources(
    string Log,
    string? TaskPrompt,
    IReadOnlyList<SummaryDeliverable> Deliverables);

/// <summary>The fitted <c>{{log}}</c> slot and what was left out to fit it.</summary>
public sealed record SummaryLogExcerpt(string Text, int SourceLogBytes, IReadOnlyList<string> Omissions)
{
    public int Bytes => Encoding.UTF8.GetByteCount(Text);

    public bool Shortened => Omissions.Count > 0;
}

/// <summary>
/// Fits the run evidence of the closing summary into the byte budget the
/// orchestrator prompt boundary leaves for it (AGT-3410). The task prompt
/// head, the run status, the deliverables and the final agent messages keep
/// their own bounded sections; the run log before them gets what remains,
/// newest lines first. Everything left out is named in an explicit omission
/// note, so the summarizer never mistakes a shortened log for missing work.
/// Pure: the caller reads the files and measures the rest of the prompt.
/// </summary>
public static partial class SummaryPromptFit
{
    public const int TaskPromptBytes = 3 * 1024;
    public const int StatusBytes = 1024;
    public const int DeliverablesBytes = 1536;
    public const int FinalMessagesBytes = 6 * 1024;
    public const int CompactLineBytes = 400;
    public const int MaxDeliverables = 40;
    public const int MaxFinalMessages = 3;
    public const int MaxStatusLines = 8;

    private const string TaskPromptHeading = "TASK PROMPT (head):";
    private const string RunLogHeading = "RUN LOG (newest lines last):";
    private const string StatusHeading = "RUN STATUS:";
    private const string DeliverablesHeading = "DELIVERABLES (files under results/):";
    private const string FinalMessagesHeading = "FINAL AGENT MESSAGES (newest last):";
    private const string OmissionHeading = "OMITTED TO FIT THE PROMPT LIMIT (the work itself may still exist):";
    private const string MessageSeparator = "\n---\n";
    private const string ClipMarker = " [...]";

    /// <summary>Room kept for the run-log and redaction omission lines, which are written last.</summary>
    private const int OmissionReserveBytes = 480;

    /// <summary>
    /// Builds the slot text within <paramref name="maxBytes"/> UTF-8 bytes.
    /// When everything fits, the log is passed through whole and no omission
    /// is recorded.
    /// </summary>
    public static SummaryLogExcerpt Fit(SummaryLogSources sources, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(sources);
        maxBytes = Math.Max(0, maxBytes);
        var omissions = new List<string>();
        var redacted = false;
        string Clean(string text)
        {
            var clean = OrchestratorPromptBoundary.Redact(text);
            redacted |= !string.Equals(clean, text, StringComparison.Ordinal);
            return clean;
        }

        var log = (sources.Log ?? string.Empty).Replace("\r\n", "\n").TrimEnd('\n');
        var lines = log.Length == 0 ? [] : log.Split('\n');

        // Allocation order is importance order: the final messages first, the
        // run log last. Render order puts the log before the sections it leads to.
        var remaining = maxBytes - FixedBytes();
        var messages = FinalMessages(lines, Clean, Math.Min(FinalMessagesBytes, remaining), omissions);
        remaining -= Bytes(messages);
        var status = Status(lines, Clean, Math.Min(StatusBytes, remaining), omissions);
        remaining -= Bytes(status);
        var taskPrompt = TaskPromptHead(sources.TaskPrompt, Clean, Math.Min(TaskPromptBytes, remaining), omissions);
        remaining -= Bytes(taskPrompt);
        var deliverables = Deliverables(sources.Deliverables, Clean, Math.Min(DeliverablesBytes, remaining), omissions);
        remaining -= Bytes(deliverables);

        // The run log is the only section that shrinks to absorb the omission
        // note, which is complete only once the log itself is fitted.
        var logBudget = remaining - OmissionNoteBytes(omissions) - OmissionReserveBytes;
        var whole = Bytes(log) <= logBudget && Bytes(OrchestratorPromptBoundary.Redact(log)) <= logBudget;
        while (true)
        {
            var all = new List<string>(omissions);
            var runLog = whole ? Clean(log) : RunLog(lines, Clean, logBudget, all);
            if (redacted) all.Add("secrets or raw session paths, replaced by [omitted: ...] markers");
            var text = Render(all, taskPrompt, runLog, status, deliverables, messages);
            if (Bytes(text) <= maxBytes || logBudget <= 0)
                return new SummaryLogExcerpt(ClipHead(text, maxBytes), Bytes(log), all);
            whole = false;
            logBudget -= Bytes(text) - maxBytes;
        }
    }

    private static string Render(
        IReadOnlyList<string> omissions, string taskPrompt, string runLog, string status, string deliverables, string messages)
    {
        var builder = new StringBuilder();
        if (omissions.Count > 0)
        {
            builder.Append(OmissionHeading).Append('\n');
            foreach (var omission in omissions) builder.Append("- ").Append(omission).Append('\n');
            builder.Append('\n');
        }
        builder.Append(TaskPromptHeading).Append('\n').Append(taskPrompt).Append("\n\n");
        builder.Append(RunLogHeading).Append('\n').Append(runLog).Append("\n\n");
        builder.Append(StatusHeading).Append('\n').Append(status).Append("\n\n");
        builder.Append(DeliverablesHeading).Append('\n').Append(deliverables).Append("\n\n");
        builder.Append(FinalMessagesHeading).Append('\n').Append(messages);
        return builder.ToString();
    }

    private static int FixedBytes()
        => Bytes(TaskPromptHeading) + Bytes(RunLogHeading) + Bytes(StatusHeading)
           + Bytes(DeliverablesHeading) + Bytes(FinalMessagesHeading) + 5 + 4 * 2;

    private static int OmissionNoteBytes(IReadOnlyList<string> omissions)
        => Bytes(OmissionHeading) + 2 + omissions.Sum(omission => Bytes(omission) + 3);

    private static string TaskPromptHead(string? prompt, Func<string, string> clean, int budget, List<string> omissions)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return "(no task prompt on the card)";
        var text = clean(prompt.Replace("\r\n", "\n").Trim());
        if (Bytes(text) <= budget) return text;
        var head = ClipHead(text, Math.Max(0, budget - Bytes(ClipMarker))) + ClipMarker;
        omissions.Add(string.Create(CultureInfo.InvariantCulture,
            $"task prompt: {Bytes(text) - Bytes(head) + Bytes(ClipMarker):N0} of {Bytes(text):N0} bytes after the head"));
        return head;
    }

    private static string Status(string[] lines, Func<string, string> clean, int budget, List<string> omissions)
    {
        var kept = new List<string>();
        string? sentinel = null;
        for (var i = lines.Length - 1; i >= 0 && (kept.Count < MaxStatusLines || sentinel is null); i--)
        {
            var line = lines[i];
            if (sentinel is null && SentinelRegex().Matches(line) is { Count: > 0 } found)
                sentinel = found[^1].Value;
            if (kept.Count < MaxStatusLines && StatusLineRegex().IsMatch(line))
                kept.Add(ClipHead(clean(line.Trim()), CompactLineBytes));
        }
        kept.Reverse();
        var text = new StringBuilder();
        foreach (var line in kept) text.Append("- ").Append(line).Append('\n');
        text.Append("- Last terminal sentinel in the log: ").Append(sentinel is null ? "none" : clean(sentinel));
        var status = text.ToString();
        if (Bytes(status) <= budget) return status;
        omissions.Add("run status: earlier status lines");
        return ClipTail(status, budget);
    }

    private static string Deliverables(
        IReadOnlyList<SummaryDeliverable> deliverables, Func<string, string> clean, int budget, List<string> omissions)
    {
        if (deliverables.Count == 0) return "None.";
        var text = new StringBuilder();
        var listed = 0;
        foreach (var file in deliverables.OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            var line = string.Create(CultureInfo.InvariantCulture,
                $"- `{clean(file.Path.Replace('\\', '/'))}` ({file.Bytes:N0} bytes)\n");
            if (listed >= MaxDeliverables || Bytes(text.ToString()) + Bytes(line) > budget) break;
            text.Append(line);
            listed++;
        }
        if (listed < deliverables.Count)
            omissions.Add(string.Create(CultureInfo.InvariantCulture,
                $"deliverables: {deliverables.Count - listed} of {deliverables.Count} files not listed"));
        return text.Length == 0 ? "(not listed)" : text.ToString().TrimEnd('\n');
    }

    private static string FinalMessages(string[] lines, Func<string, string> clean, int budget, List<string> omissions)
    {
        // Newest first; older messages stay in the run log, as far as it reaches.
        var found = new List<string>();
        for (var i = lines.Length - 1; i >= 0 && found.Count < MaxFinalMessages; i--)
        {
            if (AgentMessage(lines[i]) is { } message) found.Add(clean(message.Trim()));
        }
        if (found.Count == 0)
            return "(no structured agent message in the log; the run log above ends with the final output)";

        // The newest message keeps its head when it alone is too long; older
        // messages join only while they fit whole.
        var kept = new List<string>();
        var used = 0;
        foreach (var message in found)
        {
            var separator = kept.Count == 0 ? 0 : Bytes(MessageSeparator);
            if (used + separator + Bytes(message) <= budget)
            {
                kept.Add(message);
                used += separator + Bytes(message);
                continue;
            }
            if (kept.Count == 0)
            {
                var head = ClipHead(message, Math.Max(0, budget - Bytes(ClipMarker))) + ClipMarker;
                kept.Add(head);
                omissions.Add(string.Create(CultureInfo.InvariantCulture,
                    $"final agent message: {Bytes(message) - Bytes(head) + Bytes(ClipMarker):N0} of {Bytes(message):N0} bytes after its head"));
            }
            break;
        }
        if (kept.Count < found.Count)
            omissions.Add(string.Create(CultureInfo.InvariantCulture,
                $"final agent messages: {found.Count - kept.Count} of the latest {found.Count} quoted only in the run log"));
        kept.Reverse();
        return string.Join(MessageSeparator, kept);
    }

    private static string RunLog(string[] lines, Func<string, string> clean, int budget, List<string> omissions)
    {
        var kept = new List<string>();
        var used = 0;
        var markers = 0;
        var clipped = 0;
        var index = lines.Length - 1;
        for (; index >= 0; index--)
        {
            var line = lines[index];
            if (line.Contains("[runner-log-delivery:", StringComparison.Ordinal))
            {
                markers++;
                continue;
            }
            var compact = clean(line);
            if (Bytes(compact) > CompactLineBytes)
            {
                compact = ClipHead(compact, CompactLineBytes - Bytes(ClipMarker)) + ClipMarker;
                clipped++;
            }
            if (used + Bytes(compact) + 1 > budget) break;
            kept.Add(compact);
            used += Bytes(compact) + 1;
        }
        kept.Reverse();

        var earlierLines = index + 1;
        if (earlierLines > 0)
        {
            var earlierBytes = lines.Take(earlierLines).Sum(line => Bytes(line) + 1);
            omissions.Add(string.Create(CultureInfo.InvariantCulture,
                $"run log: the first {earlierLines:N0} of {lines.Length:N0} lines ({earlierBytes:N0} bytes) before the latest lines"));
        }
        if (markers > 0)
            omissions.Add(string.Create(CultureInfo.InvariantCulture,
                $"run log: {markers:N0} runner-log-delivery marker lines"));
        if (clipped > 0)
            omissions.Add(string.Create(CultureInfo.InvariantCulture,
                $"run log: {clipped:N0} long lines (tool calls and their output) cut to {CompactLineBytes} bytes"));
        return kept.Count == 0 ? "(no room left for the run log)" : string.Join('\n', kept);
    }

    /// <summary>
    /// The text of one agent message line: a Codex <c>agent_message</c> item or
    /// the text blocks of a Claude <c>assistant</c> event. Other lines are null.
    /// </summary>
    private static string? AgentMessage(string line)
    {
        if (!line.Contains("\"agent_message\"", StringComparison.Ordinal)
            && !line.Contains("\"type\":\"assistant\"", StringComparison.Ordinal))
            return null;
        var start = line.IndexOf('{');
        if (start < 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(line.AsMemory(start));
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;
            if (type == "item.completed"
                && root.TryGetProperty("item", out var item)
                && item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("type", out var itemType) && itemType.GetString() == "agent_message"
                && item.TryGetProperty("text", out var itemText) && itemText.ValueKind == JsonValueKind.String)
                return NullIfBlank(itemText.GetString());
            if (type == "assistant"
                && root.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.Object
                && message.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.Array)
            {
                var texts = content.EnumerateArray()
                    .Where(block => block.ValueKind == JsonValueKind.Object
                        && block.TryGetProperty("type", out var blockType) && blockType.GetString() == "text"
                        && block.TryGetProperty("text", out var blockText) && blockText.ValueKind == JsonValueKind.String)
                    .Select(block => block.GetProperty("text").GetString())
                    .Where(text => !string.IsNullOrWhiteSpace(text));
                return NullIfBlank(string.Join("\n", texts));
            }
        }
        catch (JsonException ex)
        {
            SilentCatch.Note(ex, "SummaryPromptFit: a cut or malformed JSON line is no message; the run log keeps it.");
        }
        return null;
    }

    private static string? NullIfBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

    private static int Bytes(string text) => Encoding.UTF8.GetByteCount(text);

    /// <summary>The longest prefix of <paramref name="text"/> within <paramref name="maxBytes"/> UTF-8 bytes.</summary>
    internal static string ClipHead(string text, int maxBytes)
    {
        if (maxBytes <= 0) return string.Empty;
        if (Bytes(text) <= maxBytes) return text;
        var length = Math.Min(text.Length, maxBytes);
        while (length > 0 && (char.IsHighSurrogate(text[length - 1]) || Bytes(text.AsSpan(0, length)) > maxBytes))
            length--;
        return text[..length];
    }

    /// <summary>The longest suffix of <paramref name="text"/> within <paramref name="maxBytes"/> UTF-8 bytes.</summary>
    internal static string ClipTail(string text, int maxBytes)
    {
        if (maxBytes <= 0) return string.Empty;
        if (Bytes(text) <= maxBytes) return text;
        var start = Math.Max(0, text.Length - maxBytes);
        while (start < text.Length && (char.IsLowSurrogate(text[start]) || Bytes(text.AsSpan(start)) > maxBytes))
            start++;
        return text[start..];
    }

    private static int Bytes(ReadOnlySpan<char> text) => Encoding.UTF8.GetByteCount(text);

    [GeneratedRegex(@"\[\[TASK_(?:DONE|BLOCKED|NEEDS_INPUT|NOOP)(?::[^\]\r\n]{0,200})?\]\]", RegexOptions.CultureInvariant)]
    private static partial Regex SentinelRegex();

    [GeneratedRegex(@"(?i)^\[[^\]]*\]\s+\[(?:system|supervisor|orchestrator)\].*(?:\bCLI exited\b|\bexited: status=|typedOutcome=)", RegexOptions.CultureInvariant)]
    private static partial Regex StatusLineRegex();
}
