namespace AgentStudio.Runner;

/// <summary>
/// Pure mapping from CLI / IO failure shapes to a user-facing message
/// the orchestrator chat bubble can render without presenting raw .NET
/// exception text as the whole explanation. The 2026-05-24 incident showed the operator the bare
/// string <c>"The pipe is being closed."</c> as an orchestrator reply -
/// a <see cref="System.IO.IOException.Message"/> that means nothing to a
/// human looking at the chat surface.
///
/// <para>
/// Kept as a static helper with a small switch on the raw error string
/// so the translator is trivially unit-testable and so the same shapes
/// are recognised everywhere (the chat send path today; supervisor /
/// notification paths later, if they grow a similar "raw .NET text leaks
/// to the user" risk). Adding a shape here is the one change required
/// when a new failure mode shows up in the wild.
/// </para>
/// </summary>
public static class OrchestratorChatErrorTranslator
{
    /// <summary>
    /// Resolve a raw error string (typically <see cref="System.Exception.Message"/>
    /// or the runner's <c>ErrorMessage</c> bus field) to a friendly
    /// explanation plus a flag indicating whether the underlying CLI
    /// session is likely dead and should be re-bootstrapped on the next
    /// turn. Falls back to a generic envelope when no shape matches, so
    /// the bubble never shows a bare .NET message.
    /// </summary>
    public static OrchestratorChatErrorTranslation Translate(string? rawError, string? cliType = null)
    {
        if (string.IsNullOrWhiteSpace(rawError))
        {
            return new OrchestratorChatErrorTranslation(
                FriendlyMessage: "The orchestrator did not return a reply. Please try again - the session will be re-bootstrapped if needed.",
                SessionLikelyLost: false,
                RawDetail: null);
        }

        var raw = rawError.Trim();
        var lower = raw.ToLowerInvariant();

        if (lower.Contains("project chat cannot run on the studio host in the networked profile"))
            return new OrchestratorChatErrorTranslation(raw, false, raw);

        // Pipe / closed-stream IOException family. The Claude CLI process
        // exited before the backend finished writing the prompt to stdin,
        // so .NET throws "The pipe is being closed." or "Cannot access a
        // closed pipe.". This is the 2026-05-24 incident shape.
        if (lower.Contains("pipe is being closed")
            || lower.Contains("pipe has been ended")
            || lower.Contains("cannot access a closed pipe")
            || lower.Contains("broken pipe"))
        {
            return new OrchestratorChatErrorTranslation(
                FriendlyMessage:
                    "The orchestrator session ended unexpectedly: the underlying CLI process closed its input pipe before the reply finished. " +
                    "This usually means the CLI crashed or exited early (e.g. broken install, quota error, watchdog kill). " +
                    "Please re-send your last message - the session will be re-bootstrapped on the next turn.",
                SessionLikelyLost: true,
                RawDetail: raw);
        }

        // Timeout - the runner enforces a per-call deadline and surfaces
        // it as "timeout after Ns" so the caller can distinguish a hung
        // CLI from a cancelled run.
        if (lower.StartsWith("timeout after"))
        {
            return new OrchestratorChatErrorTranslation(
                FriendlyMessage:
                    "The orchestrator did not reply in time. The CLI may be overloaded or stuck. " +
                    "Please try again; if this repeats, check the backend log for a stalled CLI process.",
                SessionLikelyLost: false,
                RawDetail: raw);
        }

        if (lower == "cancelled" || lower == "canceled")
        {
            return new OrchestratorChatErrorTranslation(
                FriendlyMessage:
                    "The orchestrator reply was cancelled (the backend was shutting down or the request was aborted). Please try again.",
                SessionLikelyLost: false,
                RawDetail: raw);
        }

        // Process spawn errors. The OneShot driver's pre-spawn heal hook
        // CAR's built-in Claude health hook attempts to recover from the half-installed
        // claude.exe stub before the spawn, but some hosts still hit raw
        // Win32 / FileNotFound exceptions here.
        if (lower.Contains("the system cannot find the file")
            || lower.Contains("no such file or directory")
            || lower.Contains("process.start returned null")
            || lower.Contains("an error occurred trying to start"))
        {
            return new OrchestratorChatErrorTranslation(
                FriendlyMessage:
                    "The orchestrator could not start the underlying CLI process. " +
                    "The CLI binary may be missing or broken (check 'claude --version' and the npm install).",
                SessionLikelyLost: true,
                RawDetail: raw);
        }

        // Rate limit / quota - the CLI returns these on stdout/stderr
        // and the runner surfaces the message verbatim. Keep the model's
        // own wording (it is usually quite readable) but wrap it.
        if (lower.Contains("rate limit") || lower.Contains("quota") || lower.Contains("usage limit"))
        {
            return new OrchestratorChatErrorTranslation(
                FriendlyMessage:
                    "The orchestrator hit a rate limit or quota cap. Please wait a moment and try again.",
                SessionLikelyLost: false,
                RawDetail: raw);
        }

        // Resume-rejection ("No conversation found with session ID: ..."):
        // already handled by ResumeWithFallbackAsync's re-bootstrap path,
        // so on the chat-send path this only surfaces when even the fresh
        // one-shot failed. Treat it as session-lost so the next turn
        // re-bootstraps cleanly.
        if (OrchestratorRunner.IsSessionRejection(raw))
        {
            return new OrchestratorChatErrorTranslation(
                FriendlyMessage:
                    "The orchestrator session expired and the automatic re-bootstrap did not complete. " +
                    "Please re-send your last message; the next turn will start a fresh session.",
                SessionLikelyLost: true,
                RawDetail: raw);
        }

        // Empty / "is_error=true" envelope from the CLI - the JSON
        // parsed but flagged itself as an error or had no result text.
        if (lower.Contains("is_error=true") || lower == "empty stdout" || lower == "orchestrator reply was empty.")
        {
            return new OrchestratorChatErrorTranslation(
                FriendlyMessage:
                    "The orchestrator CLI returned an empty or error reply. Please try again; if this repeats, check the backend log for the raw CLI output.",
                SessionLikelyLost: false,
                RawDetail: raw);
        }

        // Generic fallback. Keep the system explanation as the primary
        // message, then surface one bounded CLI stderr line so an operator can
        // diagnose an unknown refusal without opening the backend log.
        var cliCause = FormatCliCause(cliType, raw);
        return new OrchestratorChatErrorTranslation(
            FriendlyMessage:
                "The orchestrator could not produce a reply (the underlying CLI reported an error). Please try again." +
                (cliCause == null ? " If this repeats, check the backend log." : $"\n{cliCause}"),
            SessionLikelyLost: false,
            RawDetail: raw);
    }

    private static string? FormatCliCause(string? cliType, string raw)
    {
        if (string.IsNullOrWhiteSpace(cliType)) return null;

        const int maxLength = 300;
        var summary = string.Join(" ", raw
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (summary.Length > maxLength)
            summary = summary[..(maxLength - 3)].TrimEnd() + "...";

        var label = cliType.Trim().ToLowerInvariant();
        return summary.StartsWith(label + ":", StringComparison.OrdinalIgnoreCase)
            ? summary
            : $"{label}: {summary}";
    }
}

/// <summary>
/// Result of <see cref="OrchestratorChatErrorTranslator.Translate"/>.
/// <see cref="FriendlyMessage"/> is what the chat bubble shows;
/// <see cref="RawDetail"/> is the original error string preserved for
/// the backend log and a future "expand detail" slot in the UI;
/// <see cref="SessionLikelyLost"/> tells the caller whether to clear
/// the global session record so the next turn re-bootstraps from scratch.
/// </summary>
public sealed record OrchestratorChatErrorTranslation(
    string FriendlyMessage,
    bool SessionLikelyLost,
    string? RawDetail);
