using System.Text;

namespace AgentStudio.Runner;

/// <summary>Turns a disproved base ancestry into an explicit retry on the retained result.</summary>
public static class RemoteFenceRefusalContinuation
{
    public const string Reason = "delivery-base-ancestry-refused";

    public static ContinuationBaseRecord Base(
        string resultRef, string resultSha, string attemptId, DateTime savedAtUtc)
        => new(resultRef, resultSha, Reason, attemptId, savedAtUtc);

    public static string Message(string baseSha, string resultSha)
        => $"Delivery fence refused base {baseSha} against result {resultSha}; "
           + "the card was returned to Ready to continue from the retained result.";

    public static void AppendNote(
        string folderPath, string attemptId, string baseSha, string resultSha, string resultRef)
    {
        var marker = $"<!-- agent-studio:fence-refusal-continuation:{attemptId} -->";
        var detail = Message(baseSha, resultSha);
        AppendOnce(Path.Combine(folderPath, "status.md"), marker,
            $"## Delivery fence continuation{Environment.NewLine}{Environment.NewLine}" +
            $"- {detail}{Environment.NewLine}" +
            $"- Continuation ref: `{resultRef}` at `{resultSha}`.{Environment.NewLine}");
        AppendOnce(Path.Combine(folderPath, "prompt.md"), marker,
            $"## Automatic delivery fence continuation{Environment.NewLine}{Environment.NewLine}" +
            $"{detail} The previous fence base was `{baseSha}` and the retained result is `{resultSha}`. " +
            $"Continue from `{resultRef}`. Preserve the earlier work and complete the delivery.{Environment.NewLine}");
    }

    private static void AppendOnce(string path, string marker, string body)
    {
        var current = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        if (current.Contains(marker, StringComparison.Ordinal)) return;
        var separator = current.Length == 0 ? string.Empty : Environment.NewLine + Environment.NewLine;
        File.AppendAllText(path, separator + marker + Environment.NewLine + body, Encoding.UTF8);
    }
}
