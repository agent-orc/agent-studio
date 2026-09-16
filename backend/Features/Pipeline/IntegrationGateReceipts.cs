using AgentStudio.Tasks;

namespace AgentStudio.Pipeline;

/// <summary>
/// The durable receipt side of a merge gate: one numbered evidence log per gate
/// run, and the exact-SHA reader that decides whether a recovery may reuse a
/// verdict instead of running the gate again.
///
/// <para>
/// Split out of <see cref="MergeIntoDevelopRunner"/> so that restart recovery
/// (<see cref="InterruptedIntegrationGateRecoveryService"/>) reads receipts
/// through exactly the same rule the runner writes and reuses them by: a
/// receipt applies only when its expected AND tested SHA are the very object
/// the branch would release. Anything else - a receipt for an earlier merge, a
/// half-written file from a killed process - is not a verdict.
/// </para>
///
/// <para>
/// AGT-2853: writing a receipt is also the point where the gate's flaky re-run
/// becomes visible, so <see cref="Record"/> keeps both halves of that evidence
/// together - the <c>flakyQuarantined</c> header line in the log, and the card
/// timeline event when a timeline is available.
/// </para>
/// </summary>
public static class IntegrationGateReceipts
{
    /// <summary>
    /// Reads the newest durable gate receipt for one exact subject. A receipt is
    /// applicable only when both the expected and tested SHAs match; malformed
    /// or partial crash debris is ignored and forces a fresh gate.
    /// </summary>
    public static BuildTestGateResult? ReadExact(
        string jobFolderPath,
        string prefix,
        string expectedSha)
    {
        var dir = Path.Combine(jobFolderPath, "post-steps");
        if (!Directory.Exists(dir)) return null;

        foreach (var path in Directory.GetFiles(dir, $"{prefix}-*.log")
                     .OrderByDescending(EvidenceIndex))
        {
            try
            {
                using var reader = new StreamReader(path);
                var verdictLine = reader.ReadLine();
                var shaLine = reader.ReadLine();
                var reasonLine = reader.ReadLine();
                if (verdictLine is null || shaLine is null || reasonLine is null) continue;

                var verdictValue = HeaderValue(verdictLine, "verdict=");
                var recordedExpected = HeaderValue(shaLine, "expectedSha=");
                var recordedTested = HeaderValue(shaLine, "testedSha=");
                if (!Enum.TryParse<BuildTestGateVerdict>(verdictValue, ignoreCase: true, out var verdict)
                    || !string.Equals(recordedExpected, expectedSha, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(recordedTested, expectedSha, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var exitValue = HeaderValue(verdictLine, "exit=");
                int? exitCode = int.TryParse(exitValue, out var parsedExitCode)
                    ? parsedExitCode
                    : null;
                _ = long.TryParse(HeaderValue(verdictLine, "durationMs="), out var durationMs);
                var reason = reasonLine.StartsWith("reason=", StringComparison.Ordinal)
                    ? reasonLine["reason=".Length..]
                    : "Recovered durable gate verdict.";
                return new BuildTestGateResult(
                    verdict,
                    exitCode,
                    durationMs,
                    string.Empty,
                    reason,
                    false,
                    false)
                {
                    ExpectedSha = recordedExpected,
                    TestedSha = recordedTested == "n/a" ? null : recordedTested,
                };
            }
            catch (Exception ex)
            {
                SilentCatch.Note(ex, "IntegrationGateReceipts: corrupt gate evidence is ignored");
            }
        }

        return null;
    }

    private static int EvidenceIndex(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var separator = name.LastIndexOf('-');
        return separator >= 0 && int.TryParse(name[(separator + 1)..], out var index)
            ? index
            : 0;
    }

    private static string? HeaderValue(string line, string key)
    {
        var start = line.IndexOf(key, StringComparison.Ordinal);
        if (start < 0) return null;
        start += key.Length;
        var end = line.IndexOf(' ', start);
        return end < 0 ? line[start..] : line[start..end];
    }

    /// <summary>
    /// Writes one numbered gate-evidence log into the job's <c>post-steps</c>
    /// folder (<c>&lt;prefix&gt;-N.log</c>): verdict, exact expected / tested SHA,
    /// the flaky re-run audit, the test-selection audit, and the tail of the
    /// command output. Same shape for both merge gates, so the evidence reads
    /// identically whether main or develop was the target. When
    /// <paramref name="timeline"/> is supplied, quarantined flaky tests are also
    /// appended to the card timeline. <paramref name="reuse"/> is the
    /// integration gate's Remote Review reuse decision (AGT-2839); a gate that
    /// never evaluated one records that explicitly rather than staying silent.
    /// </summary>
    public static void Record(
        string jobFolderPath,
        string prefix,
        BuildTestGateResult result,
        TimelineLog? timeline = null,
        IntegrationGateReuseDecision? reuse = null)
    {
        var dir = Path.Combine(jobFolderPath, "post-steps");
        Directory.CreateDirectory(dir);
        var index = Directory.GetFiles(dir, $"{prefix}-*.log").Length + 1;
        var selection = System.Text.Json.JsonSerializer.Serialize(
            result.TestSelection,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        var dependencyCache = System.Text.Json.JsonSerializer.Serialize(
            result.DependencyCache,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        var dependencyCacheDecision = System.Text.Json.JsonSerializer.Serialize(
            result.DependencyCacheDecision,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        var budget = result.ViolatedBudget is null
            ? "budget=none"
            : $"budget={result.ViolatedBudget.Name} limitMs={result.ViolatedBudget.LimitMs} " +
              $"consumedMs={result.ViolatedBudget.ConsumedMs} phase={result.ViolatedBudget.Phase}";
        // AGT-2853: the flake list is evidence, not a footnote. It is written on
        // its own header line so an operator greps one gate log for it, and it is
        // appended to the card timeline so the same names accumulate across cards.
        var flaky =
            $"retryPerformed={result.RetryPerformed.ToString().ToLowerInvariant()} " +
            $"classification={result.FlakyClassification ?? "none"} " +
            $"flakyQuarantined={(result.FlakyQuarantinedFailures.Count == 0
                ? "none"
                : string.Join(", ", result.FlakyQuarantinedFailures))}";
        // The first three lines are the durable-recovery header parsed by
        // ReadExact; the reuse line is appended after it so a new field can
        // never shift that contract (AGT-2839).
        var reuseLine = reuse is null
            ? "reviewReuse=not-evaluated"
            : $"reviewReuse={reuse.Token} attempt={reuse.ReviewAttemptId ?? "none"} reason={reuse.Reason}";
        var body =
            $"verdict={result.Verdict} exit={result.ExitCode?.ToString() ?? "n/a"} durationMs={result.DurationMs}\n" +
            $"expectedSha={result.ExpectedSha ?? "n/a"} testedSha={result.TestedSha ?? "n/a"}\n" +
            $"reason={result.Reason}\n" +
            reuseLine + "\n" +
            budget + "\n" +
            flaky + "\n" +
            "--- dependency-cache-decision.json ---\n" +
            dependencyCacheDecision + "\n" +
            "--- dependency-cache.json ---\n" +
            dependencyCache + "\n" +
            "--- test-selection.json ---\n" +
            selection + "\n" +
            "--- last-300-lines ---\n" +
            result.Output;
        File.WriteAllText(
            Path.Combine(dir, $"{prefix}-{index}.log"),
            body);
        if (timeline is not null)
        {
            GateFlakyRerunReceipts.Record(
                timeline, jobFolderPath, prefix, result.TestedSha, result.FlakyQuarantinedFailures);
        }
    }
}
