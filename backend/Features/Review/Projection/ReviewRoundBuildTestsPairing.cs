namespace AgentStudio.Review;

/// <summary>One <c>build-tests</c> verdict as the reviewer stated it.</summary>
public readonly record struct ReviewRoundVerdictInput(string Aspect, string Status, string Summary);

/// <summary>One executed verification command as its plane recorded it.</summary>
public readonly record struct ReviewRoundCommandInput(string StepId, string Command, int? ExitCode);

/// <summary>
/// Joins <c>build-tests</c> verdicts to the commands that produced them. Both
/// the live writers and <see cref="ReviewRoundMarkdownBackfill"/> use this, so a
/// backfilled round and a freshly written one pair identically and the Evidence
/// tab cannot show two different step lists for the same report.
///
/// <para>
/// The reviewer usually names the step inside its own summary ("Review command
/// 'verify-1' ..."); when it does not, report order is the fallback the plan
/// guarantees, because verdicts and verification commands are emitted in the
/// same step order.
/// </para>
/// </summary>
public static class ReviewRoundBuildTestsPairing
{
    public const string BuildTestsAspect = "build-tests";

    /// <summary>True for a verification step id such as <c>verify-1</c>.</summary>
    public static bool IsBuildVerifyStep(string? step) =>
        step is not null
        && step.Length > "verify-".Length
        && step.StartsWith("verify-", StringComparison.OrdinalIgnoreCase)
        && step["verify-".Length..].All(char.IsDigit);

    /// <summary>True when the aspect token names the build-tests aspect.</summary>
    public static bool IsBuildTestsAspect(string? aspect) =>
        BuildTestsAspect.Equals(aspect?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The round's build-tests rows. When the round recorded verification
    /// commands but no verdict, the commands are still returned with a
    /// <see cref="ReviewVerdicts.Missing"/> status so the reason can name them
    /// instead of claiming there was no build at all.
    /// </summary>
    public static List<ReviewRoundCommandRow> Pair(
        IReadOnlyList<ReviewRoundVerdictInput> buildVerdicts,
        IReadOnlyList<ReviewRoundCommandInput> buildCommands)
    {
        var rows = new List<ReviewRoundCommandRow>();
        for (var index = 0; index < buildVerdicts.Count; index++)
        {
            var verdict = buildVerdicts[index];
            var namedStep = StepIdFromSummary(verdict.Summary);
            var command = (namedStep is null
                              ? null
                              : FirstOrNull(buildCommands, candidate =>
                                  candidate.StepId.Equals(namedStep, StringComparison.OrdinalIgnoreCase)))
                          ?? (index < buildCommands.Count ? buildCommands[index] : null);
            rows.Add(new ReviewRoundCommandRow
            {
                StepId = namedStep ?? command?.StepId ?? "",
                Command = command?.Command ?? "",
                ExitCode = command?.ExitCode,
                Status = ReviewVerdicts.Normalize(verdict.Status),
                Summary = verdict.Summary.Trim(),
            });
        }

        if (rows.Count == 0)
        {
            rows.AddRange(buildCommands.Select(command => new ReviewRoundCommandRow
            {
                StepId = command.StepId,
                Command = command.Command,
                ExitCode = command.ExitCode,
                Status = ReviewVerdicts.Missing,
                Summary = "",
            }));
        }
        return rows;
    }

    /// <summary>The step id the reviewer quoted inside its summary, when it did.</summary>
    public static string? StepIdFromSummary(string summary)
    {
        const string marker = "Review command '";
        var start = summary.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        start += marker.Length;
        var end = summary.IndexOf('\'', start);
        return end > start ? summary[start..end] : null;
    }

    private static ReviewRoundCommandInput? FirstOrNull(
        IReadOnlyList<ReviewRoundCommandInput> commands,
        Func<ReviewRoundCommandInput, bool> predicate)
    {
        foreach (var command in commands)
            if (predicate(command)) return command;
        return null;
    }
}
