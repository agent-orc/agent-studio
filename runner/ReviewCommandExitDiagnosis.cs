using System.Text.RegularExpressions;

namespace AgentRunner;

/// <summary>Exit-status proof for a reproducible review command failure.</summary>
internal static class ReviewCommandExitDiagnosis
{
    private const int TailLimit = 2048;
    private static readonly Regex Secret = new(
        @"(?ix)(?:
          https?://[^\s/@:]+:[^\s/@]+@
          | authorization\s*:\s*(?:bearer|basic|token)\s+[^\s,;]+
          | (?<![A-Za-z0-9_-])[""']?(?<key>(?>[A-Za-z0-9_-]{1,128}))[""']?\s*[:=]\s*(?:""[^""\r\n]*""|'[^'\r\n]*'|[^\s,;]+)
          | (?<![A-Za-z0-9_-])gh[sour]_[A-Za-z0-9_]{6,}(?![A-Za-z0-9_])
          | (?<![\w])sk-[A-Za-z0-9_-]{6,}(?![\w])
        )",
        RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    internal static bool Reproduced(int baselineExit, int firstExit, int? repeatExit,
        string? firstSignal = null, string? repeatSignal = null)
        => baselineExit == 0
           && firstExit is > 0 and < 128 and not 124 and not 127
           && repeatExit is > 0 and < 128 and not 124 and not 127
           && firstSignal is null && repeatSignal is null;

    internal static string Evidence(string command, string baselineSha, string candidateSha,
        int baselineExit, int firstExit, int repeatExit, string stdout, string stderr)
    {
        var safeStdout = Redact(stdout);
        var safeStderr = Redact(stderr);
        var stderrTail = safeStderr.Length > TailLimit / 4 ? safeStderr[^(TailLimit / 4)..] : safeStderr;
        var stdoutLimit = TailLimit - stderrTail.Length - (stderrTail.Length > 0 ? 1 : 0);
        var stdoutTail = safeStdout.Length > stdoutLimit ? safeStdout[^stdoutLimit..] : safeStdout;
        var tail = (stdoutTail + (stderrTail.Length > 0 ? "\n" + stderrTail : "")).TrimEnd();
        return $"command={command}; baseline={baselineSha} exit={baselineExit}; " +
               $"candidate={candidateSha} exit={firstExit}; clean-repeat exit={repeatExit}; " +
               $"candidate-output-tail:\n{tail}";
    }

    private static string Redact(string output)
    {
        try
        {
            return Secret.Replace(output, match =>
            {
                var key = match.Groups["key"];
                if (!key.Success) return "[redacted]";
                return key.Value.Contains("token", StringComparison.OrdinalIgnoreCase)
                       || key.Value.Contains("password", StringComparison.OrdinalIgnoreCase)
                       || key.Value.Contains("secret", StringComparison.OrdinalIgnoreCase)
                       || key.Value.Contains("apikey", StringComparison.OrdinalIgnoreCase)
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
