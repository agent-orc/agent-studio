using System.Text;

namespace AgentRunner;

/// <summary>Writes the NeedsInput artifact before result upload and worktree teardown.</summary>
internal static class NeedsInputArtifactWriter
{
    internal const string FileName = "needs-input.md";

    internal static string? Write(
        string resultsDirectory,
        RunOutcome outcome,
        string runAttemptId,
        string salvageBranch)
    {
        if (outcome.Kind != RunOutcomeKind.NeedsInput
            || string.IsNullOrWhiteSpace(outcome.NeedsInputMessage))
            return null;

        Directory.CreateDirectory(resultsDirectory);
        var path = Path.Combine(resultsDirectory, FileName);
        var markdown = $"# Needs input{Environment.NewLine}{Environment.NewLine}"
            + $"- Run attempt ID: `{EscapeCode(runAttemptId)}`{Environment.NewLine}"
            + $"- Salvage branch: `{EscapeCode(salvageBranch)}`{Environment.NewLine}{Environment.NewLine}"
            + $"## Agent message{Environment.NewLine}{Environment.NewLine}"
            + outcome.NeedsInputMessage.Trim('\r', '\n') + Environment.NewLine;
        File.WriteAllText(path, markdown, Encoding.UTF8);
        return path;
    }

    private static string EscapeCode(string value) => value.Replace("`", "\\`");
}
