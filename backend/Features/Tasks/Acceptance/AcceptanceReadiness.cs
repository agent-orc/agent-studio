using System.Text;
using System.Text.RegularExpressions;
using AgentStudio.Prompts;

namespace AgentStudio.Tasks;

/// <summary>Where <see cref="TaskTransitionService.PreviewCompletion"/> stopped, in check order.</summary>
public static class CompletionAdmissionStages
{
    public const string Lane = "lane";
    public const string Decision = "decision";
    public const string Lineage = "lineage";
    public const string Content = "content";
    public const string BatchGate = "batch-gate";
    public const string IntegrationUnverified = "integration-unverified";
    public const string IntegrationMissing = "integration-missing";
    public const string Contract = "contract";
    public const string Binding = "binding";
    public const string Admitted = "admitted";
}

/// <summary>
/// The answer a Human Review acceptance would give right now, computed without
/// moving the card (AGT-3564). <see cref="Message"/> is the completion's own
/// wording; <see cref="Content"/> carries the content verdict facts it read.
/// </summary>
public sealed record CompletionAdmissionPreview(
    bool Accepted,
    string Stage,
    string Message,
    CompletionContentFacts? Content = null,
    TaskIntegrationVerification? Verification = null)
{
    public static CompletionAdmissionPreview Admitted(string message, CompletionContentFacts? content,
        TaskIntegrationVerification? verification)
        => new(true, CompletionAdmissionStages.Admitted, message, content, verification);

    public static CompletionAdmissionPreview Refused(string stage, string message,
        CompletionContentFacts? content = null, TaskIntegrationVerification? verification = null)
        => new(false, stage, message, content, verification);
}

/// <summary>The acceptance-readiness classes of a Human Review card.</summary>
public static class AcceptanceReadinessClasses
{
    public const string Ready = "ready";
    public const string ReviewConcerns = "review-concerns";
    public const string IntegratedUnverified = "integrated-unverified";
    public const string NotIntegrated = "not-integrated";
    public const string VerdictStale = "verdict-stale";
    public const string NoContentVerdict = "no-content-verdict";
    public const string WaitingDeploy = "waiting-deploy";
    public const string OwnerDecision = "owner-decision";
    public const string Blocked = "blocked";

    /// <summary>Display order of the Human Review groups: acceptable first, waits last.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        Ready, ReviewConcerns, IntegratedUnverified, NotIntegrated, VerdictStale,
        NoContentVerdict, Blocked, WaitingDeploy, OwnerDecision,
    ];

    /// <summary>Classes a new round can close. Waits belong to a deploy or a person.</summary>
    public static bool OffersRound(string readiness)
        => readiness is not (Ready or WaitingDeploy or OwnerDecision);
}

/// <summary>Facts the classification reads besides the admission preview.</summary>
public sealed record AcceptanceReadinessFacts(
    CompletionAdmissionPreview Admission,
    string? Mode,
    ParkedBlockerStatus? Park);

/// <summary>One card's class, in plain words, with the concern the review raised.</summary>
public sealed record AcceptanceReadinessVerdict(
    string Readiness,
    string Headline,
    string Reason,
    string? Aspect = null,
    string? ContentStatus = null,
    string? Concern = null,
    string? Missing = null);

/// <summary>
/// AGT-3564 - classifies the admission preview of a Human Review card into one
/// readiness class. Admission decides "ready"; only a park whose reason waits
/// for a deploy or a person outranks it, because accepting such a card would
/// answer a question the completion checks cannot see.
/// </summary>
public static class AcceptanceReadinessPolicy
{
    private static readonly Regex DeployWait = new(
        @"\b(re)?deploy(s|ed|ing|ment|ments)?\b|\blive measurement\b|\bnext release\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> DecisionParks = new(StringComparer.OrdinalIgnoreCase)
    {
        "operator-decision",
        "agent-needs-input",
        "needs-human-input",
    };

    public static AcceptanceReadinessVerdict Classify(AcceptanceReadinessFacts facts)
    {
        var admission = facts.Admission;
        if (facts.Park is { } park)
        {
            var parkText = string.IsNullOrWhiteSpace(park.Reason) ? park.ConditionDescription : park.Reason;
            if (DeployWait.IsMatch(parkText ?? "") || DeployWait.IsMatch(park.ConditionDescription ?? ""))
                return new(AcceptanceReadinessClasses.WaitingDeploy, "Waiting for a deploy",
                    $"Parked until the next deploy: {Sentence(parkText)}");
            if (park.RequiresDecisionCard || park.Decision is not null || DecisionParks.Contains(park.BlockerType))
                return new(AcceptanceReadinessClasses.OwnerDecision, "Owner decision",
                    $"Parked for an owner decision: {Sentence(FirstText(park.Decision?.Question, parkText))}");
        }

        if (admission.Accepted)
            return new(AcceptanceReadinessClasses.Ready, "Ready to accept",
                "Every completion check passes: " + Sentence(admission.Message),
                admission.Content?.RequiredAspect, admission.Content?.ContentStatus);

        return admission.Stage switch
        {
            CompletionAdmissionStages.Decision => new(AcceptanceReadinessClasses.OwnerDecision, "Owner decision",
                Sentence(admission.Message)),
            CompletionAdmissionStages.Content => ClassifyContent(admission.Content, facts.Mode, admission.Message),
            CompletionAdmissionStages.BatchGate => new(AcceptanceReadinessClasses.IntegratedUnverified,
                "Integrated, not verified",
                $"The batch gate holds no passing evidence for this delivery ({admission.Message}).",
                admission.Content?.RequiredAspect, admission.Content?.ContentStatus),
            CompletionAdmissionStages.IntegrationUnverified => new(AcceptanceReadinessClasses.IntegratedUnverified,
                "Integrated, not verified",
                "The delivery is on the integration branch, but no gate passed on the merged tree"
                + (string.IsNullOrWhiteSpace(admission.Verification?.Sha) ? "." : $" {Short(admission.Verification!.Sha)}.")
                + (string.IsNullOrWhiteSpace(admission.Verification?.Reason) ? "" : " " + Sentence(admission.Verification!.Reason)),
                admission.Content?.RequiredAspect, admission.Content?.ContentStatus),
            CompletionAdmissionStages.IntegrationMissing => new(AcceptanceReadinessClasses.NotIntegrated,
                "Not integrated", Sentence(admission.Message),
                admission.Content?.RequiredAspect, admission.Content?.ContentStatus),
            _ => new(AcceptanceReadinessClasses.Blocked, "Completion refused", Sentence(admission.Message),
                admission.Content?.RequiredAspect, admission.Content?.ContentStatus),
        };
    }

    private static AcceptanceReadinessVerdict ClassifyContent(CompletionContentFacts? content, string? mode, string message)
    {
        if (content is null)
            return new(AcceptanceReadinessClasses.Blocked, "Completion refused", Sentence(message));
        var aspect = content.RequiredAspect;
        var kind = TaskModes.IsReadOnly(mode) ? "This research card has" : "The current run has";
        if (string.IsNullOrWhiteSpace(content.ContentStatus))
        {
            if (content.Settlement?.Kind == CompletionSettlementProblem.ReviewForOtherRun)
                return new(AcceptanceReadinessClasses.VerdictStale, "Verdict stale",
                    $"The {aspect} verdict belongs to an earlier run: {Sentence(content.Settlement.Describe())}", aspect);
            var why = content.Settlement?.Describe()
                ?? (content.RanAspects.Count == 0 ? "no review aspect ran" : $"only {string.Join(", ", content.RanAspects)} ran");
            return new(AcceptanceReadinessClasses.NoContentVerdict, "No content verdict",
                $"{kind} no {aspect} verdict: {Sentence(why)}", aspect);
        }

        var reviewStale = !Same(content.ReviewBriefVersion, content.CurrentBriefVersion);
        var deliveryStale = !Same(content.DeliveryBriefVersion, content.CurrentBriefVersion);
        if (reviewStale || deliveryStale)
            return new(AcceptanceReadinessClasses.VerdictStale, "Verdict stale",
                reviewStale
                    ? $"The {aspect} verdict was given for brief {Label(content.ReviewBriefVersion)}; the brief is now {Label(content.CurrentBriefVersion)}."
                    : $"The delivery was built for brief {Label(content.DeliveryBriefVersion)}; the brief is now {Label(content.CurrentBriefVersion)}.",
                aspect, content.ContentStatus, content.ContentSummary, content.Missing);

        if (!string.Equals(content.ContentStatus, "pass", StringComparison.OrdinalIgnoreCase))
            return new(AcceptanceReadinessClasses.ReviewConcerns, "Review concerns",
                $"The {aspect} review returned {content.ContentStatus}"
                + (string.IsNullOrWhiteSpace(content.ContentSummary) ? "." : $": {Sentence(content.ContentSummary)}"),
                aspect, content.ContentStatus, content.ContentSummary, content.Missing);

        return new(AcceptanceReadinessClasses.Blocked, "Completion refused", Sentence(message),
            aspect, content.ContentStatus, content.ContentSummary, content.Missing);
    }

    /// <summary>
    /// The prefilled prompt of a round that closes the gap. The owner edits and
    /// confirms it before it is sent through the normal continue path.
    /// </summary>
    public static string RoundPrompt(AcceptanceReadinessVerdict verdict, string refusal, string? latestResultRef)
    {
        var prompt = new StringBuilder();
        prompt.AppendLf("Close the acceptance gap on this card. Human Review acceptance was refused:");
        prompt.AppendLf();
        prompt.AppendLf(refusal.Trim());
        if (!string.IsNullOrWhiteSpace(verdict.Concern))
        {
            prompt.AppendLf();
            prompt.AppendLf($"{verdict.Aspect} concern: {verdict.Concern!.Trim()}");
        }
        if (!string.IsNullOrWhiteSpace(verdict.Missing))
            prompt.AppendLf($"Missing: {verdict.Missing!.Trim()}");
        prompt.AppendLf();
        prompt.Append(string.IsNullOrWhiteSpace(latestResultRef)
            ? "Change only what closes this gap, then deliver again."
            : $"Start from the latest result {latestResultRef} and change only what closes this gap, then deliver again.");
        return prompt.ToString();
    }

    private static bool Same(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left)
           && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static string Label(string? version) => string.IsNullOrWhiteSpace(version) ? "unknown" : Short(version);

    private static string Short(string value) => value.Length > 12 ? value[..12] : value;

    private static string? FirstText(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string Sentence(string? text)
    {
        var value = (text ?? "").Trim();
        if (value.StartsWith("Completion refused: ", StringComparison.Ordinal))
            value = value["Completion refused: ".Length..];
        if (value.Length == 0) return "no reason was recorded.";
        value = char.ToUpperInvariant(value[0]) + value[1..];
        return value.EndsWith('.') || value.EndsWith('?') || value.EndsWith('!') ? value : value + ".";
    }
}
