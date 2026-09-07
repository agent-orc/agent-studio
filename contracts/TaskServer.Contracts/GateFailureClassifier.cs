using System.Text.RegularExpressions;

namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Why a gate run or a review attempt failed, independent of which gate produced
/// it. The class decides whether the card is requeued or parked, so it is a
/// product decision and not a log detail.
/// </summary>
public enum GateFailureClass
{
    /// <summary>No signature matched. Treated as non-retryable so an unknown
    /// cause reaches an operator instead of looping.</summary>
    Unknown,

    /// <summary>The change itself is at fault: a parsed failing test, a build
    /// with compiler errors, or a reviewer verdict on the diff.</summary>
    Product,

    /// <summary>The host, the network, or the toolchain is at fault. The same
    /// change would have passed on a healthy host.</summary>
    Infrastructure,

    /// <summary>A provider or CLI quota is exhausted. Retryable, but only after
    /// the quota resets.</summary>
    Quota,
}

/// <summary>
/// Stable signature names for <see cref="GateFailureClassifier"/>. The signature
/// travels with the class so a lane badge and a card header can name the cause
/// without re-parsing the reason text.
/// </summary>
public static class GateFailureSignatures
{
    // Infrastructure.
    public const string GateRunBudgetExceeded = "gate-run-budget-exceeded";
    public const string GitNetworkTimeout = "git-network-timeout";
    public const string CommandTimeout = "command-timeout";
    public const string MsBuildNodeUnavailable = "msbuild-node-unavailable";
    public const string TempDirectoryUnmounted = "temp-directory-unmounted";
    public const string UnparsedTestOutcome = "unparsed-test-outcome";
    public const string RunnerDisconnected = "runner-disconnected";
    public const string ToolchainUnavailable = "toolchain-unavailable";

    // Quota.
    public const string CliQuotaExhausted = "cli-quota-exhausted";

    // Product.
    public const string NewTestFailures = "new-test-failures";
    public const string CompilerError = "compiler-error";
    public const string ReviewerVerdict = "reviewer-verdict";

    public const string Unclassified = "unclassified";
}

/// <summary>
/// One classified gate or review failure.
/// </summary>
/// <param name="Class">The failure class that decides requeue versus park.</param>
/// <param name="Signature">Stable <see cref="GateFailureSignatures"/> name.</param>
/// <param name="Detail">The line that matched, capped for card display.</param>
public sealed record GateFailureClassification(
    GateFailureClass Class,
    string Signature,
    string Detail)
{
    /// <summary>
    /// Whether the pipeline may send the card back to the review lane instead of
    /// parking it. Only <see cref="GateFailureClass.Infrastructure"/> and
    /// <see cref="GateFailureClass.Quota"/> qualify: an unknown cause parks so an
    /// operator sees it, and a product failure is the card's own fault.
    /// </summary>
    public bool IsRetryable
        => Class is GateFailureClass.Infrastructure or GateFailureClass.Quota;

    /// <summary>Whether the backoff must wait for a CLI quota reset.</summary>
    public bool WaitsForQuotaReset => Class == GateFailureClass.Quota;
}

/// <summary>
/// Pure, table-driven classifier for gate and review failure text.
/// <para>
/// On the night of 06.09.2026 the Windows Studio host was overloaded for hours.
/// Thirteen cards were parked in <c>5-human-review</c> as broken product while
/// every one of them had failed for a host reason: a 30-minute gate budget
/// missed by 488 ms, the 30-second git network cap, an MSBuild node that could
/// not open its pipe because <c>PrivateTmp</c> had been unmounted under a
/// running worker, and an exhausted CLI quota. The pipeline could not tell those
/// apart from a failing test because nothing ever assigned a class.
/// </para>
/// <para>
/// The signature tables below are ordered: quota first, then infrastructure,
/// then product. That order matters. Infrastructure output frequently also
/// contains the word "error" or a non-zero exit, so a product signature checked
/// first would swallow a host failure. The reverse is safe: a genuinely failing
/// test does not print <c>MSB1025</c> or a git timeout.
/// </para>
/// </summary>
public static partial class GateFailureClassifier
{
    private const int MaxDetailLength = 300;

    /// <summary>
    /// A CLI quota is exhausted. Retryable only once the provider resets, which
    /// the quota service already tracks, so the backoff is not a fixed delay.
    /// </summary>
    private static readonly (string Signal, string Signature)[] QuotaSignals =
    [
        ("weekly quota", GateFailureSignatures.CliQuotaExhausted),
        ("quota exhausted", GateFailureSignatures.CliQuotaExhausted),
        ("quota exceeded", GateFailureSignatures.CliQuotaExhausted),
        ("usage limit", GateFailureSignatures.CliQuotaExhausted),
        ("session limit", GateFailureSignatures.CliQuotaExhausted),
        ("rate limit exceeded", GateFailureSignatures.CliQuotaExhausted),
        ("insufficient_quota", GateFailureSignatures.CliQuotaExhausted),
        ("too many requests", GateFailureSignatures.CliQuotaExhausted),
    ];

    /// <summary>
    /// Host, network, and toolchain signatures. Each entry is a substring taken
    /// from a real 2026-09-06 log line; see the fixtures in
    /// <c>GateFailureClassifierTests</c>.
    /// </summary>
    private static readonly (string Signal, string Signature)[] InfrastructureSignals =
    [
        // AGT-2707, AGT-2710: the suite needed a few seconds more than the
        // 30-minute budget. A budget is a host capacity statement, not a verdict
        // on the diff.
        ("violated gate-run budget", GateFailureSignatures.GateRunBudgetExceeded),
        ("violated machine-gate-queue budget", GateFailureSignatures.GateRunBudgetExceeded),
        ("violated workspace-materialization budget", GateFailureSignatures.GateRunBudgetExceeded),
        ("violated workspace-cleanup budget", GateFailureSignatures.GateRunBudgetExceeded),

        // AGT-2708, AGT-2713: GitNetworkProcessRunner.DefaultTimeout.
        ("git operation timed out after", GateFailureSignatures.GitNetworkTimeout),
        ("could not be fetched from origin", GateFailureSignatures.GitNetworkTimeout),

        // Addendum 07.09.: PrivateTmp=yes plus KillMode=process unmounted the
        // private /tmp under still-running detached workers. MSBuild pipes live
        // in /tmp/MSBuild<pid>, so the node cannot bind and no test ever runs.
        ("msb1025", GateFailureSignatures.MsBuildNodeUnavailable),
        ("outofprocnode", GateFailureSignatures.MsBuildNodeUnavailable),
        ("namedpipeserverstream", GateFailureSignatures.MsBuildNodeUnavailable),
        ("socketexception (99)", GateFailureSignatures.MsBuildNodeUnavailable),
        ("cannot assign requested address", GateFailureSignatures.MsBuildNodeUnavailable),

        // Same root cause on the NuGet side: the migration mutex calls
        // mkdtemp("/tmp/.dotnet.XXXXXX"), which fails once /tmp is gone
        // (AGT-2716, AGT-2719 PreparationFailed on dotnet restore).
        ("mkdtemp", GateFailureSignatures.TempDirectoryUnmounted),
        ("/tmp/.dotnet.", GateFailureSignatures.TempDirectoryUnmounted),

        // AGT-2721, AGT-2723: the runner link flapped and the run completed
        // out-of-band.
        ("runner disconnected", GateFailureSignatures.RunnerDisconnected),
        ("runner connection lost", GateFailureSignatures.RunnerDisconnected),
        ("lease expired", GateFailureSignatures.RunnerDisconnected),
        ("completed out-of-band", GateFailureSignatures.RunnerDisconnected),

        ("timed out after", GateFailureSignatures.CommandTimeout),
        ("was cancelled", GateFailureSignatures.CommandTimeout),

        ("lost its declared toolchain", GateFailureSignatures.ToolchainUnavailable),
        ("command not found", GateFailureSignatures.ToolchainUnavailable),
        ("no such file or directory", GateFailureSignatures.ToolchainUnavailable),
    ];

    /// <summary>
    /// The change itself is at fault. Checked last, because host output is noisy
    /// and often contains the same words.
    /// </summary>
    private static readonly (string Signal, string Signature)[] ProductSignals =
    [
        ("reviewer verdict", GateFailureSignatures.ReviewerVerdict),
        ("aspect verdict", GateFailureSignatures.ReviewerVerdict),
    ];

    /// <summary>
    /// Classifies one failure reason plus any captured command output.
    /// <paramref name="reason"/> is the short sentence the pipeline already
    /// carries; <paramref name="output"/> is the optional raw stdout/stderr,
    /// which is where the MSBuild and mkdtemp signatures appear.
    /// </summary>
    public static GateFailureClassification Classify(string? reason, string? output = null)
    {
        var text = Join(reason, output);
        if (text.Length == 0)
        {
            return new GateFailureClassification(
                GateFailureClass.Unknown,
                GateFailureSignatures.Unclassified,
                "The gate reported no diagnostic output.");
        }

        // An unparsed failure marker is the pipeline's own admission that it
        // could not read a test result. It is never evidence of a failing test.
        if (UnparsedFailureMarker().IsMatch(text))
        {
            return new GateFailureClassification(
                GateFailureClass.Infrastructure,
                GateFailureSignatures.UnparsedTestOutcome,
                Detail(text, UnparsedFailureMarker()));
        }

        if (Match(text, QuotaSignals) is { } quota)
            return new GateFailureClassification(GateFailureClass.Quota, quota.Signature, quota.Detail);

        if (Match(text, InfrastructureSignals) is { } infrastructure)
        {
            return new GateFailureClassification(
                GateFailureClass.Infrastructure,
                infrastructure.Signature,
                infrastructure.Detail);
        }

        // Product signatures are regex-anchored rather than substring matches so
        // "0 new failures" and a compiler warning do not read as a failure.
        if (ParsedTestFailure().IsMatch(text))
        {
            return new GateFailureClassification(
                GateFailureClass.Product,
                GateFailureSignatures.NewTestFailures,
                Detail(text, ParsedTestFailure()));
        }

        if (CompilerError().IsMatch(text))
        {
            return new GateFailureClassification(
                GateFailureClass.Product,
                GateFailureSignatures.CompilerError,
                Detail(text, CompilerError()));
        }

        if (Match(text, ProductSignals) is { } product)
            return new GateFailureClassification(GateFailureClass.Product, product.Signature, product.Detail);

        return new GateFailureClassification(
            GateFailureClass.Unknown,
            GateFailureSignatures.Unclassified,
            FirstLine(text));
    }

    /// <summary>
    /// Classifies one verification command from its evidence rather than only
    /// its text.
    /// <para>
    /// The extra rule this adds is the one that cost five cards: a command that
    /// exited non-zero while producing zero parsed test results did not observe
    /// a failing test, it failed to run one. Treating that as
    /// <c>NewTestFailures</c> is what turned an unmounted <c>/tmp</c> into five
    /// <c>ProductFailure</c> verdicts.
    /// </para>
    /// </summary>
    /// <param name="exitCode">Process exit code, or null when it never exited.</param>
    /// <param name="parsedTestFailureCount">Test failures actually parsed out of
    /// the output. Zero means the parser found nothing, not that nothing failed.</param>
    /// <param name="reason">Short reason the gate recorded, if any.</param>
    /// <param name="output">Captured stdout and stderr.</param>
    public static GateFailureClassification ClassifyVerificationCommand(
        int? exitCode,
        int parsedTestFailureCount,
        string? reason = null,
        string? output = null)
    {
        var textual = Classify(reason, output);

        // A parsed failing test outranks nothing else in the table, but it does
        // settle the ambiguous case below.
        if (parsedTestFailureCount > 0 && textual.Class == GateFailureClass.Unknown)
        {
            return new GateFailureClassification(
                GateFailureClass.Product,
                GateFailureSignatures.NewTestFailures,
                $"{parsedTestFailureCount} parsed test failure(s).");
        }

        if (textual.Class != GateFailureClass.Unknown) return textual;

        // Never exited, or exited non-zero with nothing parseable to show for it.
        if (exitCode is null or not 0 && parsedTestFailureCount == 0)
        {
            return new GateFailureClassification(
                GateFailureClass.Infrastructure,
                GateFailureSignatures.UnparsedTestOutcome,
                exitCode is null
                    ? "The verification command never exited; no test result was produced."
                    : $"The verification command exited {exitCode} without producing a parsable test result.");
        }

        return textual;
    }

    private static (string Signature, string Detail)? Match(
        string text,
        (string Signal, string Signature)[] table)
    {
        foreach (var (signal, signature) in table)
        {
            var index = text.IndexOf(signal, StringComparison.OrdinalIgnoreCase);
            if (index >= 0) return (signature, LineAt(text, index));
        }
        return null;
    }

    private static string Join(string? reason, string? output)
        => string.Join(
                '\n',
                new[] { reason, output }.Where(value => !string.IsNullOrWhiteSpace(value)))
            .Trim();

    private static string LineAt(string text, int index)
    {
        var start = text.LastIndexOfAny(['\r', '\n'], Math.Max(0, index - 1)) + 1;
        var end = text.IndexOfAny(['\r', '\n'], index);
        return Cap(text[start..(end < 0 ? text.Length : end)].Trim());
    }

    private static string Detail(string text, Regex pattern)
    {
        var match = pattern.Match(text);
        return match.Success ? LineAt(text, match.Index) : FirstLine(text);
    }

    private static string FirstLine(string text)
        => Cap(text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim() ?? text.Trim());

    private static string Cap(string value)
        => value.Length <= MaxDetailLength ? value : value[..MaxDetailLength];

    /// <summary>The <c>&lt;unparsed failure in verify-2&gt;</c> marker the runner
    /// used to synthesise when a command failed without a readable result.</summary>
    [GeneratedRegex(@"<unparsed failure(?: in [^>]*)?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnparsedFailureMarker();

    /// <summary>A test result line a parser actually read: xunit/VSTest
    /// <c>Failed Namespace.Type.Method</c>, or a non-zero "N new failures" count.</summary>
    [GeneratedRegex(
        @"(?:^\s*(?:\[xUnit[^\]]*\]\s*)?Failed\s+[\w.+<>,`\[\] ]+)|(?:\b[1-9]\d*\s+new\s+failures?\b)|(?:\bFailed!\s*-\s*Failed:\s*[1-9])",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ParsedTestFailure();

    /// <summary>A compiler diagnostic that fails the build: C# <c>CSxxxx</c>,
    /// TypeScript <c>TSxxxx</c>, or an MSBuild <c>MSBxxxx</c> that is not the
    /// MSB1025 node-startup failure already claimed above.</summary>
    [GeneratedRegex(
        @"\berror\s+(?:CS\d{2,4}|TS\d{4}|MSB[13-9]\d{3})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CompilerError();
}
