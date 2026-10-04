using System.Text;

namespace AgentStudio.Prompts;

/// <summary>
/// Line-ending contract for prompt composition. Prompt text is composed with LF
/// on every host: <see cref="StringBuilder.AppendLine()"/> and
/// <see cref="Environment.NewLine"/> emit CRLF on Windows, so the same prompt
/// would differ between the Windows merge gate and a Linux runner (exact block
/// comparisons, character budgets, marker detection). Prompt builders append
/// lines through <see cref="AppendLf(StringBuilder, string?)"/> instead;
/// <c>PromptLineEndingGuardTests</c> keeps them from regressing.
/// </summary>
public static class PromptText
{
    public const string NewLine = "\n";

    public static StringBuilder AppendLf(this StringBuilder builder) => builder.Append('\n');

    public static StringBuilder AppendLf(this StringBuilder builder, string? value) => builder.Append(value).Append('\n');
}
