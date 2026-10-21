namespace AgentStudio.Review;

public sealed record ReviewEffectivenessExportMethod(
    string Description,
    string Delivery,
    string Approval,
    string Miss,
    string BlockerRate,
    string RealFinding,
    string Cost,
    string FindingSeverity);

public sealed record ReviewEffectivenessExportSource(
    string Producer,
    string Selection,
    DateTime? WindowStartUtc,
    DateTime WindowEndUtc,
    string Purpose);

/// <summary>
/// Review-effectiveness observations in the Token Economy evidence layout
/// (AGT-3269): dated, with the method stated, the coverage counted, and the
/// validity limits listed. The document is observational evidence from normal
/// Agent Studio runs (Token Economy <c>EvidenceSource.ObservedRun</c>). It
/// carries no prices, catalogue entries, benchmark results, or other published
/// facts, and must be imported apart from them.
/// </summary>
public sealed record ReviewEffectivenessExportDocument(
    int SchemaVersion,
    string Kind,
    DateTime GeneratedAt,
    string EvidenceClass,
    string EvidenceSource,
    bool PublishedFact,
    ReviewEffectivenessExportSource Source,
    ReviewEffectivenessExportMethod Method,
    ReviewEffectivenessCoverage Coverage,
    IReadOnlyList<ReviewEffectivenessRow> Cohorts,
    IReadOnlyList<string> ValidityWarning);

public static class ReviewEffectivenessExport
{
    public const string Kind = "agent-studio-review-effectiveness-observations";
    public const string EvidenceClass = "observational";
    public const string EvidenceSource = "ObservedRun";

    public static ReviewEffectivenessExportDocument Build(ReviewEffectivenessSummary summary)
        => new(
            SchemaVersion: 1,
            Kind: Kind,
            GeneratedAt: summary.GeneratedAtUtc,
            EvidenceClass: EvidenceClass,
            EvidenceSource: EvidenceSource,
            PublishedFact: false,
            Source: new ReviewEffectivenessExportSource(
                "Agent Studio review-effectiveness ledger",
                "Every aspect and grade review recorded by the pipeline and every deep review recorded through the API in the window, grouped by review level, model and reasoning level.",
                summary.SinceUtc,
                summary.GeneratedAtUtc,
                "Observed review cost and miss signal for review-depth decisions, not a controlled benchmark."),
            Method: new ReviewEffectivenessExportMethod(
                Description: "Counts per review route from the pipeline step ledger and the defect evidence linked to each delivery.",
                Delivery: "One pipeline attempt of a task and the coding round inside it; a reissue or a fix round is a new delivery.",
                Approval: "An aspect verdict of pass or concerns, a grade of A, B or C, or a deep review that did not block.",
                Miss: "An approval of a delivery for which a failed build/test gate, an operator send-back or defect, a production incident, a rollback, or a blocking review of another level was recorded. Miss rate is missed approvals divided by approvals.",
                BlockerRate: "Reviews that blocked divided by reviews.",
                RealFinding: "Findings of severity blocker, major or minor; nits and untagged findings are excluded.",
                Cost: "Token Economy list price at the run date as stamped on the pipeline step; unpriced reviews are counted in coverage and excluded from cost figures, never priced at zero.",
                FindingSeverity: "An aspect block is one blocker and aspect concerns one minor finding; grade findings are classified by their leading severity tag; deep reviews report their own counts."),
            Coverage: summary.Coverage,
            Cohorts: summary.Rows,
            ValidityWarning:
            [
                "Card difficulty, prompt quality and review plan vary between routes; the cohorts are not randomized.",
                "A miss needs later evidence; deliveries without a later gate, operator decision or incident record can only under-count misses.",
                "Operator send-backs are recorded as defect evidence even when the reason was not a code defect.",
                "Verdicts and findings are model-judged; only gate failures and rollbacks are deterministic evidence.",
            ]);
}
