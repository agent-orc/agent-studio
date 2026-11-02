using System.Text.RegularExpressions;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>Exit-status proof for a reproducible review command failure.</summary>
internal static class ReviewCommandExitDiagnosis
{
    private const int TailLimit = 2048;
    private const int RedactionWindow = 16 * 1024;
    private static readonly Regex Secret = new(
        @"(?ix)(?:
          https?://[^\s/@]+@[^\s/]+
          | [""']?authorization[""']?\s*:\s*[""']?(?:bearer|basic|token)\s+[^\s,""';]+[""']?
          | (?<![A-Za-z0-9_-])(?<key>(?>[A-Za-z0-9_-]{1,128}))[""']?\s*[:=]\s*(?:""[^""\r\n]*""|'[^'\r\n]*'|[^\s,;]+)
          | (?<![A-Za-z0-9_])(?:sk-[A-Za-z0-9_-]{6,}|gh[psour]_[A-Za-z0-9_]{6,}|github_pat_[A-Za-z0-9_]{6,}|glpat-[A-Za-z0-9_-]{6,}|xox[a-z]-[A-Za-z0-9-]{6,})(?![A-Za-z0-9_])
        )",
        RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private const int FailureTextLines = 3;
    private const int FailureLineLimit = 240;
    private static readonly Regex AnsiEscape = new(
        "\\x1B(?:\\[[0-?]*[ -/]*[@-~]|\\][^\\x07]*(?:\\x07|\\x1B\\\\))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Lines that explain a failure. A zero count ("0 Error(s)", "Failed: 0")
    // reports the absence of one and is excluded below.
    private static readonly Regex Diagnostic = new(
        @"\b(?:error|errors|fatal|failed|failure|exception|cannot)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ZeroCount = new(
        @"\b0\s+(?:errors?|failed|failures?)\b|\b(?:errors?|failed|failures?)\s*:\s*0\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Known noise: npm's version hints and warnings say nothing about why a
    // command failed, but they are often the first stderr line.
    private static readonly Regex Noise = new(
        @"^(?:npm\s+(?:notice|warn|warning|http|timing|verb|verbose|info)\b|\(node:\d+\)\s+\w*Warning\b)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // npm's own wrapper lines name the failed script, not the cause.
    private static readonly Regex Wrapper = new(
        @"^npm\s+(?:error|err!)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static bool Reproduced(int baselineExit, int firstExit, int? repeatExit,
        string? firstSignal = null, string? repeatSignal = null)
        => baselineExit == 0
           && firstExit is > 0 and < 128 and not 124 and not 127
           && repeatExit is > 0 and < 128 and not 124 and not 127
           && firstSignal is null && repeatSignal is null;

    internal static string Evidence(string command, string baselineSha, string candidateSha,
        int baselineExit, int firstExit, int repeatExit, string stdout, string stderr)
    {
        var safeStdout = RedactTail(stdout);
        var safeStderr = RedactTail(stderr);
        var stderrTail = safeStderr.Length > TailLimit / 4 ? safeStderr[^(TailLimit / 4)..] : safeStderr;
        var stdoutLimit = TailLimit - stderrTail.Length - (stderrTail.Length > 0 ? 1 : 0);
        var stdoutTail = safeStdout.Length > stdoutLimit ? safeStdout[^stdoutLimit..] : safeStdout;
        var tail = (stdoutTail + (stderrTail.Length > 0 ? "\n" + stderrTail : "")).TrimEnd();
        return $"command={command}; baseline={baselineSha} exit={baselineExit}; " +
               $"candidate={candidateSha} exit={firstExit}; clean-repeat exit={repeatExit}; " +
               $"candidate-output-tail:\n{tail}";
    }

    /// <summary>
    /// The lines that explain why a command without a test result failed
    /// (AGT-3430): diagnostic lines from stdout, then stderr, ahead of npm's
    /// wrapper lines; known noise such as <c>npm notice</c> is skipped. Without
    /// a diagnostic line the first other line of stderr, else stdout, is used.
    /// At most three lines of at most 240 characters each, secrets redacted.
    /// Null when both streams carry nothing but noise.
    /// </summary>
    internal static string? FailureText(string? stdout, string? stderr)
    {
        var lines = Lines(stdout).Concat(Lines(stderr)).ToArray();
        var diagnostic = lines.Where(line => Diagnostic.IsMatch(line) && !ZeroCount.IsMatch(line)).ToArray();
        var selected = diagnostic.Where(line => !Wrapper.IsMatch(line))
            .Concat(diagnostic.Where(line => Wrapper.IsMatch(line)))
            .Distinct(StringComparer.Ordinal)
            .Take(FailureTextLines)
            .ToArray();
        if (selected.Length == 0)
        {
            var fallback = Lines(stderr).FirstOrDefault() ?? Lines(stdout).FirstOrDefault();
            if (fallback is null) return null;
            selected = [fallback];
        }
        return string.Join(" | ", selected.Select(RedactTail).Select(line =>
            line.Length > FailureLineLimit ? line[..FailureLineLimit] + "..." : line));
    }

    /// <summary>
    /// Bounded, secret-scrubbed tail of a failed command's streams for the
    /// settled report (AGT-3430). Each stream keeps its last
    /// <see cref="ReviewCommandOutputTailDto.MaximumCharacters"/> characters
    /// after redaction.
    /// </summary>
    internal static ReviewCommandOutputTailDto OutputTail(string? stdout, string? stderr)
    {
        var (safeStdout, stdoutTruncated) = BoundedTail(stdout ?? string.Empty);
        var (safeStderr, stderrTruncated) = BoundedTail(stderr ?? string.Empty);
        return new ReviewCommandOutputTailDto(safeStdout, safeStderr, stdoutTruncated, stderrTruncated);
    }

    private static (string Tail, bool Truncated) BoundedTail(string output)
    {
        const int limit = ReviewCommandOutputTailDto.MaximumCharacters;
        var redacted = RedactTail(output);
        var truncated = output.Length > RedactionWindow || redacted.Length > limit;
        return (redacted.Length > limit ? redacted[^limit..] : redacted, truncated);
    }

    private static IEnumerable<string> Lines(string? text)
        => (text ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => AnsiEscape.Replace(line, string.Empty).Trim())
            .Where(line => line.Length > 0 && !Noise.IsMatch(line));

    private static string RedactTail(string output)
    {
        var tail = output.Length > RedactionWindow ? output[^RedactionWindow..] : output;
        try
        {
            return Secret.Replace(tail, match =>
            {
                var key = match.Groups["key"];
                if (!key.Success) return "[redacted]";
                var parts = key.Value.Split(['_', '-'], StringSplitOptions.RemoveEmptyEntries);
                return parts.Any(part => part.Equals("token", StringComparison.OrdinalIgnoreCase)
                    || part.Equals("password", StringComparison.OrdinalIgnoreCase)
                    || part.Equals("secret", StringComparison.OrdinalIgnoreCase)
                    || part.Equals("apikey", StringComparison.OrdinalIgnoreCase))
                    || key.Value.Contains("api_key", StringComparison.OrdinalIgnoreCase)
                    || key.Value.Contains("api-key", StringComparison.OrdinalIgnoreCase)
                    ? "[redacted]" : match.Value;
            });
        }
        catch (RegexMatchTimeoutException)
        {
            return "[output withheld]";
        }
    }
}
