using System.Text;
using System.Text.RegularExpressions;
using AgentStudio.Shared;

namespace AgentStudio.Tasks;

/// <summary>
/// Durable, bounded copy of the agent's final NeedsInput message. This file is
/// separate from cli-output.log so log rotation cannot erase the question.
/// </summary>
public static class NeedsInputArtifact
{
    public const string RelativePath = "results/needs-input.md";
    public const int MaximumMessageBytes = 16 * 1024;
    private const string MessageHeading = "## Agent message";

    public static NeedsInputStatus? Write(
        string jobFolder,
        string message,
        string runAttemptId,
        string? salvageBranch,
        ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(jobFolder)
            || string.IsNullOrWhiteSpace(message)
            || string.IsNullOrWhiteSpace(runAttemptId))
            return null;

        try
        {
            var bounded = TruncateUtf8(message.Trim('\r', '\n'), MaximumMessageBytes);
            var branch = string.IsNullOrWhiteSpace(salvageBranch) ? "not recorded" : salvageBranch.Trim();
            var markdown = $"# Needs input{Environment.NewLine}{Environment.NewLine}"
                + $"- Run attempt ID: `{EscapeCode(runAttemptId.Trim())}`{Environment.NewLine}"
                + $"- Salvage branch: `{EscapeCode(branch)}`{Environment.NewLine}{Environment.NewLine}"
                + $"{MessageHeading}{Environment.NewLine}{Environment.NewLine}{bounded}{Environment.NewLine}";
            var path = Path.Combine(jobFolder, "results", "needs-input.md");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, markdown, Encoding.UTF8);
            File.Move(temp, path, overwrite: true);
            return Status(bounded, runAttemptId.Trim(), branch == "not recorded" ? null : branch);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to persist NeedsInput message in {Folder}", jobFolder);
            return null;
        }
    }

    public static NeedsInputStatus? TryRead(string jobFolder, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(jobFolder)) return null;
        try
        {
            var path = Path.Combine(jobFolder, "results", "needs-input.md");
            if (!File.Exists(path)) return null;
            var markdown = File.ReadAllText(path, Encoding.UTF8);
            var match = Regex.Match(markdown, @"(?m)^## Agent message\r?\n\r?\n");
            if (!match.Success) return null;
            var message = markdown[(match.Index + match.Length)..].TrimEnd('\r', '\n');
            if (string.IsNullOrWhiteSpace(message)) return null;
            var attempt = MetadataValue(markdown, "Run attempt ID") ?? "unknown";
            var branch = MetadataValue(markdown, "Salvage branch");
            if (string.Equals(branch, "not recorded", StringComparison.OrdinalIgnoreCase)) branch = null;
            return Status(TruncateUtf8(message, MaximumMessageBytes), attempt, branch);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to read NeedsInput message in {Folder}", jobFolder);
            return null;
        }
    }

    /// <summary>
    /// Points the park marker at this artifact and lifts the decision request
    /// out of it: the question, the options the run weighed, and any document it
    /// named. Called once per park, right after the card lands in the lane.
    ///
    /// <para>AGT-2816: before this, the marker carried only the park slug, so
    /// the board could say a card was parked but never what it was parked on.
    /// The seed written at lane-change time (slug only) is replaced here by the
    /// full request whenever the run's message actually states one; a run that
    /// stated nothing keeps the seed, and the surfaces say so.</para>
    /// </summary>
    public static void ReferenceFromParkedBlocker(string jobFolder, ILogger? logger = null)
    {
        var record = ParkedBlockerMarker.TryRead(jobFolder, logger);
        if (record is null) return;
        var message = TryRead(jobFolder, logger)?.Message;
        var decision = ParkedDecisionReader.Read(record.Reason, message);
        ParkedBlockerMarker.Write(
            jobFolder,
            record with
            {
                NeedsInputFile = RelativePath,
                Decision = Richer(decision, record.Decision),
            },
            logger);
    }

    /// <summary>Keeps whichever request actually carries a question, so a
    /// re-reference with an unreadable artifact never erases a stated one.</summary>
    private static ParkedDecisionRequest Richer(
        ParkedDecisionRequest parsed, ParkedDecisionRequest? existing)
    {
        if (existing is null) return parsed;
        if (!parsed.Stated && existing.Stated) return existing;
        return parsed with
        {
            QuestionId = parsed.QuestionId.Length > 0 ? parsed.QuestionId : existing.QuestionId,
            DecisionCardKey = parsed.DecisionCardKey ?? existing.DecisionCardKey,
        };
    }

    private static NeedsInputStatus Status(string message, string attempt, string? branch)
        => new(message, FirstLine(message), attempt, branch, RelativePath);

    private static string FirstLine(string message)
        => message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0) ?? message.Trim();

    private static string? MetadataValue(string markdown, string key)
    {
        var match = Regex.Match(markdown, $@"(?m)^- {Regex.Escape(key)}: `(?<value>.*)`\r?$");
        return match.Success ? match.Groups["value"].Value.Replace("\\`", "`") : null;
    }

    private static string EscapeCode(string value) => value.Replace("`", "\\`");

    private static string TruncateUtf8(string value, int maximumBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maximumBytes) return value;
        var end = Math.Min(value.Length, maximumBytes);
        while (end > 0 && Encoding.UTF8.GetByteCount(value.AsSpan(0, end)) > maximumBytes) end--;
        if (end > 0 && char.IsHighSurrogate(value[end - 1])) end--;
        return value[..end];
    }
}
