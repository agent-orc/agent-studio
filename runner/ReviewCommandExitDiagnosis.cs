using System.Text.RegularExpressions;

namespace AgentRunner;

/// <summary>Exit-status proof for a reproducible review command failure.</summary>
internal static class ReviewCommandExitDiagnosis
{
    private const int TailLimit = 2048;
    private static readonly Regex Secret = new(
        @"(?i)(?:\b(?:authorization\s*:\s*bearer|(?:token|password|secret|api[_-]?key)\s*[:=])\s*[^\s]+|\bsk-[A-Za-z0-9_-]{6,}\b|\b[A-Za-z0-9_-]{40,}\b)",
        RegexOptions.Compiled);

    internal static bool Reproduced(int baselineExit, int firstExit, int? repeatExit,
        string? firstSignal = null, string? repeatSignal = null)
        => baselineExit == 0
           && firstExit is > 0 and not 124 and not 127
           && repeatExit is > 0 and not 124 and not 127
           && firstSignal is null && repeatSignal is null;

    internal static string Evidence(string command, string baselineSha, string candidateSha,
        int baselineExit, int firstExit, int repeatExit, string stdout, string stderr)
    {
        var output = Secret.Replace((stdout + "\n" + stderr).TrimEnd(), "[redacted]");
        var tail = output.Length > TailLimit ? output[^TailLimit..] : output;
        return $"command={command}; baseline={baselineSha} exit={baselineExit}; " +
               $"candidate={candidateSha} exit={firstExit}; clean-repeat exit={repeatExit}; " +
               $"candidate-output-tail:\n{tail}";
    }
}
