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
                var provenanceLine = reader.ReadLine() ?? "";
                var cached = HeaderValue(provenanceLine, "verdictSource=") == nameof(GateVerdictSource.CacheHit);
                var originalTime = DateTimeOffset.TryParse(
                    HeaderValue(provenanceLine, "originalCompletedAtUtc="), out var parsedTime)
                    ? parsedTime : (DateTimeOffset?)null;
                var encodedOriginPath = HeaderValue(provenanceLine, "originalEvidencePathB64=");
                var originPath = cached && !string.IsNullOrEmpty(encodedOriginPath)
                    ? System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encodedOriginPath))
                    : null;
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
                    FailureKind = Enum.TryParse<BuildTestGateFailureKind>(HeaderValue(verdictLine, "failureKind="), out var kind)
                        ? kind : BuildTestGateFailureKind.None,
                    VerdictSource = cached ? GateVerdictSource.CacheHit : GateVerdictSource.Executed,
                    GateRunId = cached ? HeaderValue(provenanceLine, "originalRunId=") : null,
                    GateCompletedAtUtc = originalTime,
                    OriginEvidencePath = originPath,
                    GateProfileDigest = HeaderValue(provenanceLine, "profileDigest="),
                    PipelineDefinitionVersion = int.TryParse(
                        HeaderValue(provenanceLine, "pipelineDefinitionVersion="), out var definitionVersion)
                        ? definitionVersion : null,
                    ToolchainIdentity = HeaderValue(provenanceLine, "toolchainIdentity="),
                };
            }
            catch (Exception ex)
            {
                SilentCatch.Note(ex, "IntegrationGateReceipts: corrupt gate evidence is ignored");
            }
        }

        return null;
    }

    /// <summary>Only the latest environment failure may offer a candidate for a fresh gate.</summary>
    internal static string? ReadEnvironmentCandidate(string jobFolderPath, string prefix)
    {
        var dir = Path.Combine(jobFolderPath, "post-steps");
        if (!Directory.Exists(dir)) return null;
        var path = Directory.GetFiles(dir, $"{prefix}-*.log").OrderByDescending(EvidenceIndex).FirstOrDefault();
        if (path is null) return null;
        try
        {
            using var reader = new StreamReader(path);
            var verdict = reader.ReadLine() ?? "";
            var shaLine = reader.ReadLine() ?? "";
            var sha = HeaderValue(shaLine, "expectedSha=");
            return HeaderValue(verdict, "failureKind=") == nameof(BuildTestGateFailureKind.Environment)
                   && HeaderValue(verdict, "verdict=") == nameof(BuildTestGateVerdict.Fail)
                   && ReviewSubjectStore.IsValidResultSha(sha)
                   && sha == HeaderValue(shaLine, "testedSha=") ? sha : null;
        }
        catch (IOException ex)
        {
            SilentCatch.Note(ex, "IntegrationGateReceipts: candidate receipt unavailable");
            return null;
        }
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

    internal static string SlowTestReport(BuildTestGateResult result)
    {
        var originalBudget = result.Processes.Select(p => p.OriginalBudgetMs).FirstOrDefault(value => value > 0);
        if (result.ViolatedBudget is null && (originalBudget == 0 || result.DurationMs < originalBudget * .8))
            return string.Empty;
        var slowest = result.Processes.SelectMany(p => p.SlowTests.Select(test => new
            {
                p.Command, test.Name, test.DurationMs, test.Source,
            }))
            .OrderByDescending(test => test.DurationMs).Take(10).ToArray();
        return "--- slowest-tests.json (top 10 completed tests/collections; partial on cutoff) ---\n"
               + System.Text.Json.JsonSerializer.Serialize(slowest,
                   new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n"
               + (slowest.Length == 0 ? "No completed test timings were reported before cutoff.\n" : "");
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
            $"verdict={result.Verdict} exit={result.ExitCode?.ToString() ?? "n/a"} durationMs={result.DurationMs} failureKind={result.FailureKind}\n" +
            $"expectedSha={result.ExpectedSha ?? "n/a"} testedSha={result.TestedSha ?? "n/a"}\n" +
            $"reason={result.Reason}\n" +
            $"verdictSource={result.VerdictSource} originalRunId={result.GateRunId ?? "n/a"} originalCompletedAtUtc={result.GateCompletedAtUtc?.ToString("O") ?? "n/a"} originalEvidencePathB64={Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(result.OriginEvidencePath ?? ""))} profileDigest={result.GateProfileDigest ?? "n/a"} pipelineDefinitionVersion={result.PipelineDefinitionVersion?.ToString() ?? "n/a"} toolchainIdentity={result.ToolchainIdentity ?? "n/a"}\n" +
            reuseLine + "\n" +
            budget + "\n" +
            flaky + "\n" +
            "--- dependency-cache-decision.json ---\n" +
            dependencyCacheDecision + "\n" +
            "--- dependency-cache.json ---\n" +
            dependencyCache + "\n" +
            "--- test-selection.json ---\n" +
            selection + "\n" +
            "--- resource-evidence.json ---\n" +
            System.Text.Json.JsonSerializer.Serialize(result.Processes.Select(process => new
            {
                process.Command, process.Phase, process.Resources, process.OriginalBudgetMs,
                process.BudgetExtension, process.ViolatedBudget, process.FailedTestsObserved,
            }), new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n" +
            SlowTestReport(result) +
            "--- last-300-lines ---\n" +
            result.Output;
        File.WriteAllText(
            Path.Combine(dir, $"{prefix}-{index}.log"),
            body);
        if (timeline is not null)
        {
            if (result.VerdictSource == GateVerdictSource.CacheHit)
                timeline.Append(jobFolderPath, TimelineEventKinds.GateVerdictCacheHit,
                    TimelineActors.System,
                    $"Cached {prefix} {result.Verdict} for {result.TestedSha}; original run {result.GateCompletedAtUtc:O}",
                    details: new Dictionary<string, string>
                    {
                        ["testedSha"] = result.TestedSha ?? "",
                        ["profileDigest"] = result.GateProfileDigest ?? "",
                        ["originalRunId"] = result.GateRunId ?? "",
                        ["originalCompletedAtUtc"] = result.GateCompletedAtUtc?.ToString("O") ?? "",
                        ["originalEvidencePath"] = result.OriginEvidencePath ?? "",
                    });
            GateFlakyRerunReceipts.Record(
                timeline, jobFolderPath, prefix, result.TestedSha, result.FlakyQuarantinedFailures);
        }
    }
}
