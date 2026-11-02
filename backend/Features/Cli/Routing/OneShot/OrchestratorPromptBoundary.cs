using System.Text;
using System.Text.RegularExpressions;
using AgentStudio.Pipeline;

namespace AgentStudio.Cli;

/// <summary>
/// Final input check for legacy orchestrator one-shot profiles. Prompt
/// builders fit their input inside <see cref="MaxPromptBytes"/> themselves;
/// this check is the last line of defence and stays a hard limit for raw
/// session paths and credentials.
/// </summary>
public static class OrchestratorPromptBoundary
{
    public const int MaxPromptBytes = 32 * 1024;

    /// <summary>
    /// Prefix of the one-shot error for a refused prompt. The same input is
    /// refused again, so a caller must not retry it unchanged.
    /// </summary>
    public const string FailurePrefix = "decision-input-incomplete:";
    private static readonly Regex RawSessionPath = new(
        @"(?i)(?:[a-z]:[\\/]|~[\\/]|/(?!api/))\S{0,240}(?:sessions[\\/]|cli-output\.log|tool-calls\.jsonl)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Credential = new(
        @"(?i)(?:sk-[a-z0-9_-]{12,}|gh[opusr]_[a-z0-9_]{12,}|(?:api[_-]?key|access[_-]?token|authorization|password|secret)\s*[:=]\s*\S+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static OrchestratorPromptCheck Check(string? stepId, string prompt)
    {
        if (PipelineCatalogue.FindStep(stepId)?.Kind != StepKind.Orchestrator)
            return new(prompt, null);
        if (RawSessionPath.IsMatch(prompt))
            return new("", "raw-session-path");
        var redacted = Credential.Replace(prompt, "[omitted: secret]");
        if (!ReferenceEquals(redacted, prompt) && redacted != prompt)
            redacted += "\n[omission: secret]";
        if (Encoding.UTF8.GetByteCount(redacted) > MaxPromptBytes)
            return new("", "oversize-orchestrator-prompt");
        return new(redacted, null);
    }

    /// <summary>True when <paramref name="error"/> is a refusal by <see cref="Check"/>.</summary>
    public static bool IsRefusal(string? error)
        => error?.StartsWith(FailurePrefix, StringComparison.Ordinal) == true;

    /// <summary>
    /// Replaces the raw session paths and credentials that <see cref="Check"/>
    /// refuses or redacts, so a prompt builder can keep its input inside the
    /// boundary.
    /// </summary>
    public static string Redact(string text)
        => Credential.Replace(
            RawSessionPath.Replace(text, "[omitted: raw session path]"),
            "[omitted: secret]");
}

public sealed record OrchestratorPromptCheck(string Prompt, string? Failure);
