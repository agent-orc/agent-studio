using System.Text.RegularExpressions;

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
