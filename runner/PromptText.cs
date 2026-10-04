using System.Text;

namespace AgentRunner;

/// <summary>
/// Line-ending contract for prompts the standalone runner composes, mirroring
/// the backend's <c>AgentStudio.Prompts.PromptText</c>: prompt text uses LF on
/// every host, so a prompt and its identity-marked blocks are byte-identical on
/// a Windows and a Linux runner. <c>PromptLineEndingGuardTests</c> keeps the
/// prompt builders from going back to <c>AppendLine</c> or
/// <c>Environment.NewLine</c>.
/// </summary>
internal static class PromptText
{
    public const string NewLine = "\n";

    public static StringBuilder AppendLf(this StringBuilder builder) => builder.Append('\n');

    public static StringBuilder AppendLf(this StringBuilder builder, string? value) => builder.Append(value).Append('\n');
}
