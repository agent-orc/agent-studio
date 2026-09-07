namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Stable failure classes carried by deterministic gates and review reports.
/// The wire values are the lowercase names returned by <see cref="WireValue"/>.
/// </summary>
public enum ReviewFailureClass
{
    Product,
    Infrastructure,
    Quota,
    Unknown,
}

public static class ReviewFailureClasses
{
    public const string Product = "product";
    public const string Infrastructure = "infrastructure";
    public const string Quota = "quota";
    public const string Unknown = "unknown";

    public static string WireValue(this ReviewFailureClass value) => value switch
    {
        ReviewFailureClass.Product => Product,
        ReviewFailureClass.Infrastructure => Infrastructure,
        ReviewFailureClass.Quota => Quota,
        _ => Unknown,
    };

    public static bool IsRetryable(string? value)
        => string.Equals(value, Infrastructure, StringComparison.OrdinalIgnoreCase)
           || string.Equals(value, Quota, StringComparison.OrdinalIgnoreCase);
}

public sealed record ReviewFailureClassification(
    ReviewFailureClass FailureClass,
    string Reason)
{
    public string WireClass => FailureClass.WireValue();
}

/// <summary>
/// Pure message classifier at the gate and review trust boundary. Product is
/// deliberately fail-closed: only a parsed failing test, compiler diagnostic,
/// or explicit reviewer verdict on the diff may create a product failure.
/// </summary>
public static class ReviewFailureClassifier
{
    private static readonly string[] QuotaMarkers =
    [
        "weekly quota",
        "quota exhausted",
        "quota has been exhausted",
        "usage limit reached",
        "you've reached your usage limit",
        "rate limit exceeded",
        "insufficient_quota",
    ];

    private static readonly string[] InfrastructureMarkers =
    [
        "violated gate-run budget",
        "budget (limit=",
        "command timed out",
        "operation timed out",
        "timed out after",
        "timeout after",
        "<unparsed failure",
        "unparsable test",
        "unparseable test",
        "no parseable test",
        "missing test output",
        "without test output",
        "runner disconnect",
        "runner disconnected",
        "connectivity flap",
        "connection was closed",
        "process was aborted",
        "process aborted",
        "exit=n/a",
        "signal=timeout",
    ];

    private static readonly string[] CompilerMarkers =
    [
        ": error cs",
        " error cs",
        "compiler error",
        "compilation failed",
        "build failed with compiler",
        "error ts",
    ];

    public static ReviewFailureClassification Classify(
        string? evidence,
        IReadOnlyCollection<string>? parsedFailingTests = null,
        bool reviewerVerdictOnDiff = false)
    {
        if (parsedFailingTests?.Any(IsParsedFailureName) == true)
        {
            var name = parsedFailingTests.First(IsParsedFailureName);
            return new ReviewFailureClassification(
                ReviewFailureClass.Product,
                $"Parsed failing test: {name}");
        }

        var text = evidence ?? string.Empty;
        if (ContainsAny(text, QuotaMarkers))
            return new ReviewFailureClassification(ReviewFailureClass.Quota, FirstLine(text, "CLI quota exhausted."));
        if (ContainsAny(text, InfrastructureMarkers))
            return new ReviewFailureClassification(ReviewFailureClass.Infrastructure, FirstLine(text, "Review infrastructure failed."));
        if (ContainsAny(text, CompilerMarkers))
            return new ReviewFailureClassification(ReviewFailureClass.Product, FirstLine(text, "Compiler errors were reported."));
        if (reviewerVerdictOnDiff)
            return new ReviewFailureClassification(ReviewFailureClass.Product, FirstLine(text, "Reviewer blocked the diff."));
        return new ReviewFailureClassification(ReviewFailureClass.Unknown, FirstLine(text, "Failure could not be classified."));
    }

    public static IReadOnlyList<string> ParseFailingTests(string? evidence)
    {
        if (string.IsNullOrWhiteSpace(evidence)) return [];
        var failures = new List<string>();
        foreach (var raw in evidence.Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Failed ", StringComparison.Ordinal))
            {
                var name = line["Failed ".Length..];
                var duration = name.LastIndexOf(" [", StringComparison.Ordinal);
                if (duration > 0) name = name[..duration];
                if (IsParsedFailureName(name)) failures.Add(name.Trim());
                continue;
            }
            if (line.StartsWith("FAIL  ", StringComparison.Ordinal))
            {
                var name = line["FAIL  ".Length..].Trim();
                if (IsParsedFailureName(name)) failures.Add(name);
            }
        }
        return failures.Distinct(StringComparer.Ordinal).ToArray();
    }

    public static bool IsParsedFailureName(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && !value.TrimStart().StartsWith("<unparsed failure", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsAny(string text, IEnumerable<string> markers)
        => markers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static string FirstLine(string text, string fallback)
    {
        var line = text.Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(line)) return fallback;
        return line.Length <= 600 ? line : line[..600];
    }
}

public enum ReviewGradingOutcome
{
    Pass,
    PassWithConcerns,
    ProductFailure,
    InfrastructureFailure,
    Quota,
    Unknown,
}

/// <summary>Table-driven aggregation of aspect and command classifications.</summary>
public static class ReviewGradingPolicy
{
    private static readonly IReadOnlyDictionary<ReviewGradingOutcome, int> Priority =
        new Dictionary<ReviewGradingOutcome, int>
        {
            [ReviewGradingOutcome.Pass] = 0,
            [ReviewGradingOutcome.PassWithConcerns] = 1,
            [ReviewGradingOutcome.Unknown] = 2,
            [ReviewGradingOutcome.ProductFailure] = 3,
            [ReviewGradingOutcome.InfrastructureFailure] = 4,
            [ReviewGradingOutcome.Quota] = 5,
        };

    public static ReviewGradingOutcome Map(
        IEnumerable<string?> aspectStatuses,
        ReviewFailureClass? commandFailureClass = null)
    {
        var outcomes = aspectStatuses.Select(StatusOutcome).ToList();
        if (commandFailureClass is { } failureClass)
            outcomes.Add(FailureOutcome(failureClass));
        return outcomes.Count == 0
            ? ReviewGradingOutcome.Pass
            : outcomes.OrderByDescending(outcome => Priority[outcome]).First();
    }

    private static ReviewGradingOutcome StatusOutcome(string? status)
        => status?.Trim().ToLowerInvariant() switch
        {
            "pass" => ReviewGradingOutcome.Pass,
            "concerns" => ReviewGradingOutcome.PassWithConcerns,
            "block" or "fail" => ReviewGradingOutcome.ProductFailure,
            _ => ReviewGradingOutcome.Unknown,
        };

    private static ReviewGradingOutcome FailureOutcome(ReviewFailureClass failureClass)
        => failureClass switch
        {
            ReviewFailureClass.Product => ReviewGradingOutcome.ProductFailure,
            ReviewFailureClass.Infrastructure => ReviewGradingOutcome.InfrastructureFailure,
            ReviewFailureClass.Quota => ReviewGradingOutcome.Quota,
            _ => ReviewGradingOutcome.Unknown,
        };
}

public sealed record ReviewRetryDecision(
    bool Requeue,
    int RetryNumber,
    int MaximumRetries,
    DateTime RetryAtUtc,
    string Reason);

public static class ReviewFailureRetryPolicy
{
    public const int DefaultMaxRetries = 3;

    public static ReviewRetryDecision Decide(
        ReviewFailureClass failureClass,
        int retriesUsed,
        DateTime nowUtc,
        int maximumRetries = DefaultMaxRetries,
        DateTime? quotaResetAtUtc = null,
        DateTime? loadGateReadyAtUtc = null)
    {
        maximumRetries = Math.Max(0, maximumRetries);
        retriesUsed = Math.Max(0, retriesUsed);
        if (failureClass is not (ReviewFailureClass.Infrastructure or ReviewFailureClass.Quota))
            return new ReviewRetryDecision(false, retriesUsed, maximumRetries, nowUtc, $"{failureClass.WireValue()} failures are not automatically retried.");
        if (retriesUsed >= maximumRetries)
            return new ReviewRetryDecision(false, retriesUsed, maximumRetries, nowUtc, $"{failureClass.WireValue()} retry budget exhausted ({retriesUsed}/{maximumRetries}).");

        var retryNumber = retriesUsed + 1;
        var seconds = retryNumber switch
        {
            1 => 30,
            2 => 120,
            _ => 300,
        };
        var retryAt = nowUtc.AddSeconds(seconds);
        if (loadGateReadyAtUtc > retryAt) retryAt = loadGateReadyAtUtc.Value;
        if (failureClass == ReviewFailureClass.Quota && quotaResetAtUtc > retryAt)
            retryAt = quotaResetAtUtc.Value;
        return new ReviewRetryDecision(
            true,
            retryNumber,
            maximumRetries,
            retryAt,
            $"{failureClass.WireValue()} retry {retryNumber}/{maximumRetries} is scheduled for {retryAt:O}.");
    }
}
