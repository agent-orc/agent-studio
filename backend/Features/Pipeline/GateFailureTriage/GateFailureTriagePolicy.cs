using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Pipeline;

/// <summary>
/// The four classes a failed merge gate can be assigned (AGT-3009). Each one
/// owns exactly one next action; only <see cref="Undecidable"/> asks a person.
/// </summary>
public static class GateFailureClasses
{
    /// <summary>The host, the run budget, or the network failed; the delivery was never judged.</summary>
    public const string Environment = "environment";

    /// <summary>The delivery made named items fail on an otherwise green tree.</summary>
    public const string Product = "product";

    /// <summary>The same items fail on the integration merge base without this delivery.</summary>
    public const string IntegrationBranch = "integration-branch";

    /// <summary>No gate log, or its markers contradict each other.</summary>
    public const string Undecidable = "undecidable";
}

/// <summary>Which environment fault an <see cref="GateFailureClasses.Environment"/> verdict names.</summary>
public static class GateEnvironmentKinds
{
    public const string WorkerCrash = "worker-crash";
    public const string RunBudget = "run-budget";
    public const string Transport = "transport";

    /// <summary>The gate itself typed the failure as a toolchain crash before test discovery (CAC-18).</summary>
    public const string Toolchain = "toolchain";

    /// <summary>The gate process died before it reached a verdict (AGT-2849).</summary>
    public const string Interrupted = "interrupted";

    /// <summary>A clean repeat of the same item on the same tree passed (AGT-2916 <c>intermittent</c>).</summary>
    public const string ProvenIntermittent = "proven-intermittent";
}

/// <param name="Class">One <see cref="GateFailureClasses"/> value.</param>
/// <param name="Kind">The <see cref="GateEnvironmentKinds"/> value for an environment class, otherwise null.</param>
/// <param name="Fingerprint">
/// Identity of the cause. For a class with failing items it depends on the
/// sorted item names only, so the same failing test on two cards is one
/// fingerprint whatever else their logs contain.
/// </param>
/// <param name="FailingItems">Exact failing test or build-error names read from the log, sorted.</param>
/// <param name="ReasonLine">One sentence for the card, the fix-round prompt, and the park reason.</param>
/// <param name="MissingEvidence">For <see cref="GateFailureClasses.Undecidable"/>: what would have decided it.</param>
/// <param name="Markers">The markers that matched, in the order they were checked.</param>
public sealed record GateFailureTriage(
    string Class,
    string? Kind,
    string Fingerprint,
    IReadOnlyList<string> FailingItems,
    string ReasonLine,
    string? MissingEvidence,
    IReadOnlyList<string> Markers)
{
    public bool IsEnvironment => Class == GateFailureClasses.Environment;
}

/// <summary>
/// Pure classification of one failed merge gate from its evidence log
/// (<c>post-steps/&lt;gate&gt;-N.log</c>, written by
/// <see cref="IntegrationGateReceipts.Record"/>).
/// <para>
/// Until AGT-3009 every red merge gate parked its card for an operator, and
/// the operator ran an out-of-product script that read the same log and
/// matched the same markers. On 2026-10-04, 13 of the 14 cards in human review
/// were such parks. The marker list here is the one that script used, so the
/// product now makes the call the script made, and it records which marker
/// decided it.
/// </para>
/// <para>
/// Order of evidence: the AGT-2916 diagnosis suffix on the <c>reason=</c>
/// line wins when present, because it is an experiment (merge base, clean
/// repeat) rather than a text match. Without it, environment markers and
/// failing item names decide. When both appear, the log contradicts itself,
/// and the verdict is <see cref="GateFailureClasses.Undecidable"/> rather
/// than a guess.
/// </para>
/// </summary>
public static partial class GateFailureTriagePolicy
{
    private static readonly (string Marker, string Kind)[] EnvironmentMarkers =
    [
        ("ECONNRESET", GateEnvironmentKinds.Transport),
        ("ETIMEDOUT", GateEnvironmentKinds.Transport),
        ("unable to access", GateEnvironmentKinds.Transport),
        ("Could not resolve host", GateEnvironmentKinds.Transport),
        ("Worker exited unexpectedly", GateEnvironmentKinds.WorkerCrash),
        ("JavaScript heap out of memory", GateEnvironmentKinds.WorkerCrash),
        ("violated gate-run budget", GateEnvironmentKinds.RunBudget),
    ];

    /// <summary>The verdict for a card whose gate process died before it wrote a verdict.</summary>
    public static GateFailureTriage Interrupted() => Environment(
        GateEnvironmentKinds.Interrupted,
        "The merge gate was interrupted before it reached a verdict.",
        ["gate-interrupted"]);

    /// <summary>The verdict when no gate evidence log exists.</summary>
    public static GateFailureTriage NoLog() => Undecidable(
        [],
        "no merge-gate evidence log exists under post-steps",
        []);

    public static GateFailureTriage Classify(string? gateLog)
    {
        if (string.IsNullOrWhiteSpace(gateLog)) return NoLog();

        var log = GateLog.Parse(gateLog);
        if (!string.Equals(log.Verdict, nameof(BuildTestGateVerdict.Fail), StringComparison.OrdinalIgnoreCase))
            return Undecidable([],
                $"the newest merge-gate log records verdict {log.Verdict ?? "none"}, not a failure",
                []);

        var items = FailingItems(log.Output);
        var flakeClaim = GateFlakeLabelPolicy.Check(log.RetryPerformed, log.FlakyClassification, log.FlakyQuarantined);
        if (!flakeClaim.Allowed)
            return Undecidable(items, flakeClaim.Reason, ["flake-label-without-rerun"]);

        // AGT-2916: an experiment beats a text match.
        var diagnosis = Diagnosis(log.Reason);
        if (diagnosis.Baseline == "red")
        {
            var markers = new[] { "baseline=red" };
            return items.Count == 0
                ? Undecidable([], "the integration merge base is red, but the gate log names no failing item to attach a cause to", markers)
                : new GateFailureTriage(
                    GateFailureClasses.IntegrationBranch,
                    null,
                    ItemFingerprint(items),
                    items,
                    $"The integration branch already fails without this delivery: {Describe(items)}.",
                    null,
                    markers);
        }
        if (diagnosis.Classification == DeliveryFailureDiagnosis.Intermittent && diagnosis.CleanRepeat == "green")
        {
            return Environment(
                GateEnvironmentKinds.ProvenIntermittent,
                "A clean repeat of the same gate on the same tree passed, so the failure is intermittent.",
                ["diagnosis=intermittent", "clean-repeat=green"]);
        }
        if (diagnosis.Classification == DeliveryFailureDiagnosis.Product && items.Count > 0)
            return Product(items, ["diagnosis=product"]);

        var environment = EnvironmentMarkers
            .Where(entry => log.Reason.Contains(entry.Marker, StringComparison.OrdinalIgnoreCase)
                || log.Output.Contains(entry.Marker, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (environment.Count == 0 && log.Budget.StartsWith("gate-run", StringComparison.Ordinal))
            environment.Add(("budget=gate-run", GateEnvironmentKinds.RunBudget));

        if (environment.Count > 0)
        {
            var markers = environment.Select(entry => entry.Marker).ToArray();
            if (items.Count > 0)
            {
                return Undecidable(items,
                    $"the log shows both an environment marker ({markers[0]}) and failing items, " +
                    "and no clean repeat on the same tree says which one caused the red",
                    markers);
            }
            var first = environment[0];
            return Environment(first.Kind, EnvironmentSentence(first.Kind, first.Marker), markers);
        }

        if (string.Equals(log.FailureKind, nameof(BuildTestGateFailureKind.Environment), StringComparison.Ordinal)
            && items.Count == 0)
        {
            return Environment(
                GateEnvironmentKinds.Toolchain,
                "The gate toolchain failed before it reached test discovery.",
                ["failureKind=Environment"]);
        }

        if (items.Count > 0) return Product(items, ["failing-items"]);

        return Undecidable([],
            "the gate log names no failing item and no environment marker",
            []);
    }

    /// <summary>
    /// Exact failing names in the output tail: xUnit/VSTest test names, vitest
    /// file-and-test paths, and MSBuild compiler errors. Deduplicated and
    /// ordered so two logs of the same cause compare equal.
    /// </summary>
    public static IReadOnlyList<string> FailingItems(string output)
    {
        // The evidence log prefixes every output line with its stream; the
        // test-name parsers expect the line as the test runner printed it.
        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(raw => StreamPrefix().Replace(AnsiEscape().Replace(raw, string.Empty), string.Empty))
            .ToArray();
        var items = new SortedSet<string>(
            GateFlakyRerunPolicy.ParseFailedTests(string.Join('\n', lines)), StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var vitest = VitestFailure().Match(line);
            if (vitest.Success) items.Add(WhiteSpace().Replace(vitest.Groups["name"].Value.Trim(), " "));
            var build = BuildError().Match(line);
            if (build.Success) items.Add($"{build.Groups["file"].Value}: {build.Groups["code"].Value}");
        }
        return items.ToArray();
    }

    /// <summary>Identity of a set of failing items, independent of class, card, host, and timing.</summary>
    public static string ItemFingerprint(IReadOnlyList<string> items)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", items.Order(StringComparer.Ordinal))));
        return "gate-items:" + Convert.ToHexString(bytes).ToLowerInvariant()[..16];
    }

    private static GateFailureTriage Product(IReadOnlyList<string> items, IReadOnlyList<string> markers)
        => new(
            GateFailureClasses.Product,
            null,
            ItemFingerprint(items),
            items,
            $"The merge gate failed on {Describe(items)}.",
            null,
            markers);

    private static GateFailureTriage Environment(string kind, string reason, IReadOnlyList<string> markers)
        => new(GateFailureClasses.Environment, kind, "gate-environment:" + kind, [], reason, null, markers);

    private static GateFailureTriage Undecidable(
        IReadOnlyList<string> items, string missing, IReadOnlyList<string> markers)
        => new(
            GateFailureClasses.Undecidable,
            null,
            items.Count > 0 ? ItemFingerprint(items) : "gate-undecidable:no-items",
            items,
            $"The merge-gate failure could not be classified: {missing}.",
            missing,
            markers);

    private static string EnvironmentSentence(string kind, string marker) => kind switch
    {
        GateEnvironmentKinds.Transport => $"The merge gate lost its network transport ({marker}).",
        GateEnvironmentKinds.WorkerCrash => $"A test worker crashed under the gate host ({marker}).",
        GateEnvironmentKinds.RunBudget => "The merge gate ran out of its gate-run budget before it reached a verdict.",
        _ => $"The merge gate hit an environment fault ({marker}).",
    };

    private static string Describe(IReadOnlyList<string> items)
    {
        const int shown = 5;
        var head = string.Join(", ", items.Take(shown));
        return items.Count <= shown ? head : $"{head} and {items.Count - shown} more";
    }

    private static (string? Classification, string? Baseline, string? CleanRepeat) Diagnosis(string reason)
    {
        var classification = DiagnosisClass().Match(reason);
        if (!classification.Success) return (null, null, null);
        var baseline = DiagnosisBaseline().Match(reason);
        var clean = DiagnosisCleanRepeat().Match(reason);
        return (
            classification.Groups["value"].Value,
            baseline.Success ? baseline.Groups["value"].Value : null,
            clean.Success ? clean.Groups["value"].Value : null);
    }

    /// <summary>The header fields and the output tail of one gate evidence log.</summary>
    private sealed record GateLog(
        string? Verdict,
        string? FailureKind,
        string Reason,
        string Budget,
        bool RetryPerformed,
        string? FlakyClassification,
        IReadOnlyList<string> FlakyQuarantined,
        string Output)
    {
        private const string OutputMarker = "--- last-300-lines ---";

        public static GateLog Parse(string text)
        {
            var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            var first = lines.FirstOrDefault() ?? string.Empty;
            string Line(string prefix) => lines.FirstOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal)) ?? string.Empty;
            var flaky = Line("retryPerformed=");
            var outputStart = text.IndexOf(OutputMarker, StringComparison.Ordinal);
            // Logs older than the output marker carry the tail after the three header lines.
            var output = outputStart >= 0
                ? text[(outputStart + OutputMarker.Length)..]
                : string.Join("\n", lines.Skip(3));
            var quarantined = Header(flaky, "flakyQuarantined=", toEnd: true);
            return new GateLog(
                Header(first, "verdict="),
                Header(first, "failureKind="),
                Line("reason="),
                Header(Line("budget="), "budget=") ?? string.Empty,
                string.Equals(Header(flaky, "retryPerformed="), "true", StringComparison.OrdinalIgnoreCase),
                Header(flaky, "classification="),
                string.IsNullOrWhiteSpace(quarantined) || quarantined == "none"
                    ? []
                    : quarantined.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                output);
        }

        private static string? Header(string line, string key, bool toEnd = false)
        {
            var start = line.IndexOf(key, StringComparison.Ordinal);
            if (start < 0) return null;
            start += key.Length;
            if (toEnd) return line[start..].Trim();
            var end = line.IndexOf(' ', start);
            return end < 0 ? line[start..] : line[start..end];
        }
    }

    [GeneratedRegex(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07]*(?:\x07|\x1B\\))")]
    private static partial Regex AnsiEscape();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();

    [GeneratedRegex(@"^\[std(?:out|err)\]\s?")]
    private static partial Regex StreamPrefix();

    /// <summary>
    /// vitest's failure line: <c>FAIL  |frontend| src/x.spec.ts &gt; Suite &gt; test</c>.
    /// The <c>|project|</c> label is dropped so a renamed workspace project does not
    /// change the fingerprint.
    /// </summary>
    [GeneratedRegex(@"^\s*FAIL\s+(?:\|[^|]+\|\s+)?(?<name>\S+\.(?:spec|test)\.[cm]?[jt]sx?(?:\s+>\s+.+)?)\s*$")]
    private static partial Regex VitestFailure();

    [GeneratedRegex(@"(?<file>[^\s\\/:]+\.(?:cs|csproj|ts|razor))\(\d+,\d+\):\s+error\s+(?<code>[A-Z]+\d+)\b")]
    private static partial Regex BuildError();

    [GeneratedRegex(@"diagnosis=(?<value>[a-z-]+)")]
    private static partial Regex DiagnosisClass();

    [GeneratedRegex(@"(?:^|[;\s])baseline=(?<value>red|green|unavailable)")]
    private static partial Regex DiagnosisBaseline();

    [GeneratedRegex(@"clean-repeat=(?<value>red|green|unavailable)")]
    private static partial Regex DiagnosisCleanRepeat();
}

/// <summary>
/// The rule that ended the silent flake label (AGT-3009). One red baseline
/// test was called a flake in more than 20 grades before anyone opened a
/// cause card, because a flake label cost nothing to write. A failure may be
/// called a flake only when the product holds a passing re-run of the same
/// item on the same tree; otherwise it stays a counted failure.
/// </summary>
public static class GateFlakeLabelPolicy
{
    public sealed record FlakeClaim(bool Allowed, string Reason);

    /// <param name="retryPerformed">The gate re-ran its red items in the same workspace and build.</param>
    /// <param name="classification">The flake classification the gate wrote, or null / <c>none</c>.</param>
    /// <param name="quarantined">The names the gate called flaky.</param>
    public static FlakeClaim Check(bool retryPerformed, string? classification, IReadOnlyList<string> quarantined)
    {
        var claimsFlake = quarantined.Count > 0
            || string.Equals(classification, ReviewFlakyQuarantine.Classification, StringComparison.Ordinal);
        if (!claimsFlake) return new(true, "no flake label");
        return retryPerformed
            ? new(true, "a re-run of the same items on the same tree passed")
            : new(false, "the gate calls a failure flaky without a passing re-run of the same item on the same tree");
    }
}
