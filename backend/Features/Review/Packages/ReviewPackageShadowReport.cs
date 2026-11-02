using AgentStudio.Pipeline;
using AgentStudio.Tasks;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Review;

/// <summary>Per-card result of one package-tier aspect in a member's card-check round.</summary>
public sealed record ReviewPackageCardAspect(string Aspect, string? Status, int? Findings, ReviewStepTokens? Tokens);

/// <summary>One member and aspect: the package verdict beside the per-card verdict.</summary>
public sealed record ReviewPackageShadowRow(
    string PackageId, string TaskKey, string Aspect,
    string? PackageStatus, int PackageFindings, int CrossCardFindings, long PackageTokens,
    string? CardStatus, int? CardFindings, long? CardTokens, string Comparison);

/// <summary>
/// Token totals of the compared aspects. Package tokens are what the package
/// calls spent on those aspects; card tokens are the members' per-card calls
/// of the same aspects. <see cref="CardRowsUnknown"/> rows had no token count.
/// The cross-card aspect has no per-card counterpart and is totalled apart.
/// </summary>
public sealed record ReviewPackageShadowTokens(
    long PackagePrompt, long PackageOutput, long CardPrompt, long CardOutput, int CardRowsUnknown,
    long CrossCardPrompt, long CrossCardOutput, double? PackageShareOfCard);

/// <summary>
/// Verdict agreement over compared rows. <see cref="CardBlocksMissed"/> counts
/// per-card blocks the package did not block: the miss signal of PK9.
/// </summary>
public sealed record ReviewPackageShadowAgreement(
    int Compared, int Same, int PackageStricter, int CardStricter, int CardBlocksMissed);

public sealed record ReviewPackageShadowReport(
    string Project, int Days, DateTime GeneratedAt, string Method,
    int Packages, IReadOnlyDictionary<string, int> Outcomes, int Members,
    ReviewPackageShadowTokens Tokens, ReviewPackageShadowAgreement Agreement,
    IReadOnlyList<ReviewPackageShadowRow> Rows);

/// <summary>
/// Pure fold of the shadow comparison (AGT-3400 S4): for every member of a
/// package reported inside the window, the package verdict and findings per
/// aspect beside the member's per-card verdict and findings of the same aspect
/// in its card-check round, plus the token totals of both.
/// </summary>
public static class ReviewPackageShadowFold
{
    public const string Method =
        "Package verdicts per member and aspect against the member's per-card verdict of the same aspect "
        + "in the review round that passed its card check; package tokens against the per-card tokens of the same aspects.";

    public static ReviewPackageShadowReport Build(
        string project, int days, DateTime nowUtc,
        IReadOnlyList<ReviewPackageRecordDto> records,
        Func<ReviewPackageMemberDto, IReadOnlyList<ReviewPackageCardAspect>> cardAspects)
    {
        var since = nowUtc.AddDays(-days);
        var window = records.Where(record => record.ReportedAt is { } reported && reported >= since && reported <= nowUtc
                && record.Report is not null)
            .ToArray();
        var rows = new List<ReviewPackageShadowRow>();
        long packagePrompt = 0, packageOutput = 0, cardPrompt = 0, cardOutput = 0, crossPrompt = 0, crossOutput = 0;
        var unknown = 0;
        foreach (var record in window)
        {
            var report = record.Report!;
            foreach (var command in report.Commands)
            {
                var prompt = command.InputTokens + command.CacheReadTokens + command.CacheCreationTokens;
                if (command.Aspect == ReviewPackageAspects.CrossCardConsistency)
                {
                    crossPrompt += prompt;
                    crossOutput += command.OutputTokens;
                }
                else
                {
                    packagePrompt += prompt;
                    packageOutput += command.OutputTokens;
                }
            }
            if (report.Outcome != ReviewPackageOutcomes.Completed) continue;
            var members = record.Subject.Members.Count;
            foreach (var member in record.Subject.Members.OrderBy(member => member.Order))
            {
                var card = cardAspects(member);
                foreach (var aspect in ReviewPackageAspects.Agent)
                {
                    var verdict = report.Verdicts.FirstOrDefault(item => item.TaskKey == member.TaskKey && item.Aspect == aspect);
                    var findings = report.Findings.Where(finding => finding.Aspect == aspect
                        && finding.OwnerTaskKey == member.TaskKey).ToArray();
                    var command = report.Commands.FirstOrDefault(item => item.Aspect == aspect);
                    var share = command is null ? 0
                        : (command.InputTokens + command.CacheReadTokens + command.CacheCreationTokens + command.OutputTokens) / members;
                    var cardRow = card.FirstOrDefault(item => item.Aspect == aspect);
                    if (ReviewPackageAspects.ComparedWithCard.Contains(aspect) && cardRow is not null)
                    {
                        if (cardRow.Tokens is { } tokens)
                        {
                            cardPrompt += tokens.Prompt;
                            cardOutput += tokens.Output;
                        }
                        else unknown++;
                    }
                    rows.Add(new ReviewPackageShadowRow(record.Subject.PackageId, member.TaskKey, aspect,
                        verdict?.Status, findings.Length, findings.Count(finding => finding.CrossCard), share,
                        cardRow?.Status, cardRow?.Findings,
                        cardRow?.Tokens is { } cardTokens ? cardTokens.Prompt + cardTokens.Output : null,
                        Compare(verdict?.Status, cardRow?.Status, ReviewPackageAspects.ComparedWithCard.Contains(aspect))));
                }
            }
        }

        var compared = rows.Where(row => row.Comparison is not ("not-compared" or "unknown")).ToArray();
        var cardTotal = cardPrompt + cardOutput;
        return new ReviewPackageShadowReport(project, days, nowUtc, Method, window.Length,
            window.GroupBy(record => record.Report!.Outcome).OrderBy(group => group.Key, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count()),
            window.Sum(record => record.Subject.Members.Count),
            new ReviewPackageShadowTokens(packagePrompt, packageOutput, cardPrompt, cardOutput, unknown,
                crossPrompt, crossOutput,
                cardTotal == 0 ? null : (double)(packagePrompt + packageOutput) / cardTotal),
            new ReviewPackageShadowAgreement(compared.Length,
                compared.Count(row => row.Comparison == "same"),
                compared.Count(row => row.Comparison == "package-stricter"),
                compared.Count(row => row.Comparison == "card-stricter"),
                compared.Count(row => row.Comparison == "card-stricter" && row.CardStatus == "block")),
            rows);
    }

    /// <summary>
    /// <c>same</c>, <c>package-stricter</c> or <c>card-stricter</c> by severity
    /// rank (pass &lt; concerns &lt; block); <c>unknown</c> when either side has
    /// no verdict; <c>not-compared</c> for the cross-card aspect.
    /// </summary>
    internal static string Compare(string? package, string? card, bool comparable)
    {
        if (!comparable) return "not-compared";
        var left = Rank(package);
        var right = Rank(card);
        if (left is null || right is null) return "unknown";
        return left == right ? "same" : left > right ? "package-stricter" : "card-stricter";
    }

    private static int? Rank(string? status)
        => ReviewGradingPolicy.IsBlockingToken(status) ? 2
            : ReviewGradingPolicy.IsConcernToken(status) ? 1
            : string.Equals(status?.Trim(), "pass", StringComparison.OrdinalIgnoreCase) ? 0
            : null;
}

/// <summary>
/// Reads the shadow comparison for one project: package records from the
/// package authority and each member's per-card aspect rows from its pipeline
/// record, in the round that passed its card check.
/// </summary>
public sealed class ReviewPackageShadowReportService(
    IReviewPackageAuthority packages, TaskScannerService scanner, PipelineExecutionLog pipeline,
    ITokenPricing pricing, TimeProvider clock)
{
    public const int DefaultDays = 14;

    public async Task<ReviewPackageShadowReport> BuildAsync(string project, int days, CancellationToken ct)
    {
        var records = await packages.ListAsync(project, ct).ConfigureAwait(false);
        var tasks = scanner.ScanAllAutomationJobsWithArchive()
            .Where(task => string.Equals(task.ProjectName, project, StringComparison.OrdinalIgnoreCase))
            .GroupBy(task => string.IsNullOrWhiteSpace(task.Key) ? task.Id : task.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        return ReviewPackageShadowFold.Build(project, days, clock.GetUtcNow().UtcDateTime, records,
            member => tasks.TryGetValue(member.TaskKey, out var task) && member.CardCheckAttemptId is { } round
                && pipeline.Read(task.FolderPath) is { } record
                    ? CardAspects(record, round, pricing) : []);
    }

    /// <summary>The package-tier aspect executions of one review round.</summary>
    internal static IReadOnlyList<ReviewPackageCardAspect> CardAspects(
        PipelineExecutionRecord record, string roundId, ITokenPricing pricing)
    {
        var aspects = new List<ReviewPackageCardAspect>();
        foreach (var run in record.PreviousAttempts.Append(record))
        foreach (var step in run.Steps)
        {
            if (ReviewSteps.Of(step.StepId) is not { } aspect || !ReviewPackageAspects.ComparedWithCard.Contains(aspect))
                continue;
            var modelBacked = StepCostMeasurement.IsModelBacked(step.StepId, step.Kind);
            foreach (var execution in StepCostMeasurement.Executions(pricing, step))
            {
                if (execution.ExecutionAttemptId != roundId || execution.CompletedAt is null) continue;
                aspects.RemoveAll(item => item.Aspect == aspect);
                aspects.Add(new ReviewPackageCardAspect(aspect, execution.Verdict, execution.ReviewFindings?.Total,
                    ReviewStepSampleReader.Tokens(execution, modelBacked)));
            }
        }
        return aspects;
    }
}
