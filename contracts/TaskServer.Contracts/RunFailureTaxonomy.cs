namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Class of one gate or review failure. The pipeline routes on this class:
/// only <see cref="Product"/> describes the reviewed change and may park a card
/// in Human Review. <see cref="Infrastructure"/> and <see cref="Quota"/>
/// describe the host or the provider account and are requeued.
/// </summary>
public enum RunFailureClass
{
    /// <summary>No recognized signature. Routed like today: no automatic requeue.</summary>
    Unknown,

    /// <summary>The reviewed change is broken: a parsed failing test, a compiler error, or a reviewer verdict on the diff.</summary>
    Product,

    /// <summary>The host, the network, or the tooling prevented a verdict about the change.</summary>
    Infrastructure,

    /// <summary>A provider or CLI account limit prevented a verdict. Retryable once the limit resets.</summary>
    Quota,
}

/// <summary>
/// Stable signature slugs. They are persisted onto cards and rendered in the
/// lane, so they are additive-only and never renamed.
/// </summary>
public static class RunFailureSignatures
{
    // Product
    public const string NewTestFailures = "new-test-failures";
    public const string CompilerError = "compiler-error";
    public const string ReviewFinding = "review-finding";

    // Infrastructure
    public const string GateBudgetExceeded = "gate-budget-exceeded";
    public const string CommandTimeout = "command-timeout";
    public const string GitNetworkTimeout = "git-network-timeout";
    public const string UnparsedTestOutput = "unparsed-test-output";
    public const string MsBuildNodeUnavailable = "msbuild-node-unavailable";
    public const string PrivateTempUnmounted = "private-temp-unmounted";
    public const string RunnerDisconnected = "runner-disconnected";
    public const string HostResourceExhausted = "host-resource-exhausted";
    public const string ProcessLaunchFailed = "process-launch-failed";

    // Quota
    public const string CliQuotaExhausted = "cli-quota-exhausted";

    public const string Unclassified = "unclassified";
}

/// <summary>
/// One classified failure. <see cref="Detail"/> is the operator sentence shown
/// in the lane and the card header next to <see cref="Class"/>.
/// </summary>
public sealed record RunFailureVerdict(
    RunFailureClass Class,
    string Signature,
    string Detail)
{
    /// <summary>
    /// Whether the pipeline may send the card back to the review lane instead of
    /// parking it. Only host and account faults qualify; an unrecognized failure
    /// keeps the conservative park behaviour.
    /// </summary>
    public bool Requeueable => Class is RunFailureClass.Infrastructure or RunFailureClass.Quota;
}

/// <summary>
/// Facts a caller knows about one failed gate command, review command, or
/// integration step. The classifier reads facts first and text second: a
/// completed process that parsed failing tests stays a product failure even
/// when its log also mentions a timeout.
/// </summary>
public sealed record RunFailureEvidence
{
    /// <summary>Combined stdout, stderr, and reason text.</summary>
    public string? Text { get; init; }

    /// <summary>Process exit code, or null when the process never produced one.</summary>
    public int? ExitCode { get; init; }

    /// <summary>Number of test names the executor could actually parse out of the output.</summary>
    public int ParsedTestFailures { get; init; }

    /// <summary>
    /// True when this command was supposed to emit test results, so "zero parsed
    /// results with a non-zero exit" is a missing-output fault rather than a
    /// normal non-test command failure (a lint run, for example).
    /// </summary>
    public bool ExpectsTestResults { get; init; }

    /// <summary>A declared gate or command budget was consumed without the command finishing.</summary>
    public bool BudgetViolated { get; init; }

    /// <summary>The command was killed because it exceeded its own timeout.</summary>
    public bool TimedOut { get; init; }

    /// <summary>The command never started.</summary>
    public bool LaunchFailed { get; init; }

    /// <summary>The runner or its lease disappeared while the command was in flight.</summary>
    public bool RunnerDisconnected { get; init; }

    /// <summary>A quota probe already established that the account limit is exhausted.</summary>
    public bool QuotaExhausted { get; init; }

    /// <summary>A reviewer returned a blocking verdict about the diff itself.</summary>
    public bool ReviewerBlockedDiff { get; init; }
}

/// <summary>
/// Pure, table-driven classification of gate and review failures.
/// <para>
/// Written after the 2026-09-06 overload night, when thirteen cards were parked
/// in Human Review as product failures. Every one of them was a host fault: a
/// suite that needed a few seconds more than its budget, a 30-second git network
/// cap, an MSBuild node that could not open its pipe because the daemon's
/// private <c>/tmp</c> had been unmounted underneath it, and an exhausted CLI
/// quota. None of them said anything about the reviewed change.
/// </para>
/// </summary>
public static class RunFailureClassifier
{
    /// <summary>
    /// MSBuild could not run at all. <c>MSB1025</c> is the internal-failure code;
    /// the named-pipe <c>SocketException (99)</c> and the <c>mkdtemp</c> ENOENT
    /// are the two shapes the unmounted private <c>/tmp</c> takes (2026-09-07
    /// addendum). No test ran, so no test result exists to grade.
    /// </summary>
    private static readonly string[] MsBuildNodeSignals =
    [
        "msb1025",
        "outofprocnode.run",
        "namedpipeserverstream",
    ];

    private static readonly string[] PrivateTempSignals =
    [
        "cannot assign requested address",
        "socketexception (99)",
        "mkdtemp",
        "/tmp/.dotnet.",
        "no such file or directory: /tmp",
    ];

    private static readonly string[] GitNetworkSignals =
    [
        "git operation timed out after",
        "could not be fetched from origin",
        "could not be fetched:",
        "could not read from remote repository",
        "the remote end hung up unexpectedly",
        "failed to connect to github.com",
    ];

    private static readonly string[] BudgetSignals =
    [
        "violated gate-run budget",
        "violated review-command budget",
        "violated machine-gate-queue budget",
        "violated workspace-materialization budget",
    ];

    private static readonly string[] TimeoutSignals =
    [
        "timed out after",
        "deadline exceeded",
        "operation exceeded its time limit",
    ];

    private static readonly string[] RunnerDisconnectSignals =
    [
        "runner disconnect",
        "lease authority lost",
        "lease expired",
        "heartbeat lost",
        "replacement daemon could not adopt",
        "runner connectivity flap",
        "completed out-of-band during",
    ];

    private static readonly string[] HostResourceSignals =
    [
        "out of memory",
        "outofmemoryexception",
        "cannot allocate memory",
        "no space left on device",
        "disk full",
    ];

    private static readonly string[] ProcessLaunchSignals =
    [
        "process.start failed",
        "failed to start process",
        "executable file not found",
    ];

    private static readonly string[] QuotaSignals =
    [
        "quota exhausted",
        "quota exceeded",
        "weekly quota",
        "usage limit",
        "session limit",
        "rate limit exceeded",
        "insufficient_quota",
        "429 too many requests",
    ];

    /// <summary>
    /// Compiler diagnostics that describe the change. <c>MSB1025</c> is
    /// deliberately absent: it is an MSBuild host failure, not a compile error.
    /// </summary>
    private static readonly string[] CompilerErrorSignals =
    [
        "error cs",
        "error ts",
        ": fatal error",
        "compilation failed",
    ];

    /// <summary>
    /// The placeholder the remote review executor synthesizes when a verify
    /// command failed without a parsable test result. It must never be counted
    /// as a failing test.
    /// </summary>
    public const string UnparsedFailureMarker = "<unparsed failure";

    public static bool IsUnparsedFailurePlaceholder(string? failureName)
        => failureName is not null
           && failureName.Contains(UnparsedFailureMarker, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Facts outrank text, and proof of a verdict outranks a hint that no verdict
    /// exists. The order below is load-bearing:
    /// <list type="number">
    /// <item>caller-established facts (a quota probe, a violated budget, a killed process);</item>
    /// <item>host signatures that prove nothing ran (MSBuild node crash, unmounted temp, unparsed output);</item>
    /// <item>proof that a verdict about the change exists (a parsed failing test, a reviewer block);</item>
    /// <item>text hints that the host was at fault;</item>
    /// <item>a compiler diagnostic, which is a product fault only once the host is cleared;</item>
    /// <item>a test command that exited non-zero without producing one result.</item>
    /// </list>
    /// Step 3 sits above step 4 because a suite whose own assertion message
    /// contains "timed out after" would otherwise be excused as infrastructure.
    /// </summary>
    public static RunFailureVerdict Classify(RunFailureEvidence evidence)
    {
        var text = evidence.Text ?? string.Empty;

        // 1. Caller-established facts.
        if (evidence.QuotaExhausted)
        {
            return Quota(
                RunFailureSignatures.CliQuotaExhausted,
                "The CLI provider quota is exhausted.",
                text,
                QuotaSignals);
        }
        if (evidence.RunnerDisconnected)
        {
            return Infrastructure(
                RunFailureSignatures.RunnerDisconnected,
                "The runner lost its lease while the command was in flight.",
                text,
                RunnerDisconnectSignals);
        }
        if (evidence.LaunchFailed)
        {
            return Infrastructure(
                RunFailureSignatures.ProcessLaunchFailed,
                "The verification command never started.",
                text,
                ProcessLaunchSignals);
        }
        if (evidence.BudgetViolated)
        {
            return Infrastructure(
                RunFailureSignatures.GateBudgetExceeded,
                "The gate run was cut off by its budget before it could finish.",
                text,
                BudgetSignals);
        }
        if (evidence.TimedOut)
        {
            return Infrastructure(
                RunFailureSignatures.CommandTimeout,
                "The verification command was killed on its timeout.",
                text,
                TimeoutSignals);
        }

        // 2. Hard "nothing ran" host signatures.
        if (Contains(text, MsBuildNodeSignals))
        {
            return Infrastructure(
                RunFailureSignatures.MsBuildNodeUnavailable,
                "MSBuild could not start its worker node, so no test ran.",
                text,
                MsBuildNodeSignals);
        }
        if (Contains(text, PrivateTempSignals))
        {
            return Infrastructure(
                RunFailureSignatures.PrivateTempUnmounted,
                "The build tooling could not use the host temp directory, so no test ran.",
                text,
                PrivateTempSignals);
        }

        // The placeholder itself is a host signature: it only exists because a
        // verify command failed without emitting one parsable test result.
        if (text.Contains(UnparsedFailureMarker, StringComparison.OrdinalIgnoreCase))
        {
            return Infrastructure(
                RunFailureSignatures.UnparsedTestOutput,
                "The verification command produced no parsable test result, so no test verdict exists.",
                text,
                [UnparsedFailureMarker]);
        }

        // 3. Proof that a verdict about the change exists. A parsed failing test
        //    name is the strongest signal there is: somebody can open it and read
        //    what broke. It must survive a log that merely mentions a timeout.
        if (evidence.ParsedTestFailures > 0)
        {
            return new RunFailureVerdict(
                RunFailureClass.Product,
                RunFailureSignatures.NewTestFailures,
                $"{evidence.ParsedTestFailures} test(s) fail on the change that pass on the baseline.");
        }
        if (evidence.ReviewerBlockedDiff)
        {
            return new RunFailureVerdict(
                RunFailureClass.Product,
                RunFailureSignatures.ReviewFinding,
                "A reviewer returned a blocking verdict on the diff.");
        }

        // 4. Text hints that the host, the network, or the account was at fault.
        if (Contains(text, QuotaSignals))
        {
            return Quota(
                RunFailureSignatures.CliQuotaExhausted,
                "The CLI provider quota is exhausted.",
                text,
                QuotaSignals);
        }
        if (Contains(text, RunnerDisconnectSignals))
        {
            return Infrastructure(
                RunFailureSignatures.RunnerDisconnected,
                "The runner lost its lease while the command was in flight.",
                text,
                RunnerDisconnectSignals);
        }
        if (Contains(text, ProcessLaunchSignals))
        {
            return Infrastructure(
                RunFailureSignatures.ProcessLaunchFailed,
                "The verification command never started.",
                text,
                ProcessLaunchSignals);
        }
        if (Contains(text, HostResourceSignals))
        {
            return Infrastructure(
                RunFailureSignatures.HostResourceExhausted,
                "The host ran out of memory or disk while verifying.",
                text,
                HostResourceSignals);
        }
        if (Contains(text, BudgetSignals))
        {
            return Infrastructure(
                RunFailureSignatures.GateBudgetExceeded,
                "The gate run was cut off by its budget before it could finish.",
                text,
                BudgetSignals);
        }
        if (Contains(text, GitNetworkSignals))
        {
            return Infrastructure(
                RunFailureSignatures.GitNetworkTimeout,
                "A git network operation against origin did not complete in time.",
                text,
                GitNetworkSignals);
        }
        if (Contains(text, TimeoutSignals))
        {
            return Infrastructure(
                RunFailureSignatures.CommandTimeout,
                "The verification command was killed on its timeout.",
                text,
                TimeoutSignals);
        }

        // 5. A compiler diagnostic is a product fault only once the host is
        //    cleared: the same message also appears when a build dies on OOM.
        if (Contains(text, CompilerErrorSignals))
        {
            return new RunFailureVerdict(
                RunFailureClass.Product,
                RunFailureSignatures.CompilerError,
                FirstMatchingLine(text, CompilerErrorSignals) ?? "The change does not compile.");
        }

        // 6. A test command that exited non-zero without producing a single
        //    parsable result did not test anything. Before AGT-2749 this became
        //    "1 new failures: <unparsed failure in verify-2>" and parked the card.
        if (evidence.ExpectsTestResults && evidence.ExitCode is not null and not 0)
        {
            return Infrastructure(
                RunFailureSignatures.UnparsedTestOutput,
                $"The verification command exited {evidence.ExitCode} without producing a single parsable test result.",
                text,
                []);
        }

        return new RunFailureVerdict(
            RunFailureClass.Unknown,
            RunFailureSignatures.Unclassified,
            FirstLine(text) ?? "The failure carried no recognizable signature.");
    }

    private static RunFailureVerdict Infrastructure(
        string signature,
        string detail,
        string text,
        string[] signals)
        => new(RunFailureClass.Infrastructure, signature, Explain(detail, text, signals));

    private static RunFailureVerdict Quota(
        string signature,
        string detail,
        string text,
        string[] signals)
        => new(RunFailureClass.Quota, signature, Explain(detail, text, signals));

    private static string Explain(string detail, string text, string[] signals)
    {
        var evidence = signals.Length == 0 ? null : FirstMatchingLine(text, signals);
        return evidence is null ? detail : $"{detail} Evidence: {evidence}";
    }

    private static bool Contains(string text, IEnumerable<string> signals)
        => signals.Any(signal => text.Contains(signal, StringComparison.OrdinalIgnoreCase));

    private static string? FirstMatchingLine(string text, string[] signals)
        => Lines(text).FirstOrDefault(line => Contains(line, signals)) is { } line
            ? Trim(line)
            : null;

    private static string? FirstLine(string text)
        => Lines(text).FirstOrDefault() is { } line ? Trim(line) : null;

    private static IEnumerable<string> Lines(string text)
        => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0);

    private static string Trim(string value)
        => value.Length <= 300 ? value : value[..300] + "...";
}
