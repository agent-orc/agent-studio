using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Personas;

/// <summary>Evidence annotations cover facts that cannot be inferred from a triage verdict alone.</summary>
public sealed record GuardianReviewEvidence(string Id, string Incident, DateTimeOffset At,
    string Kind, string Source, double Value = 1, DateTimeOffset? End = null,
    string? DecisionId = null, string? Outcome = null, string? Card = null);
public sealed record GuardianCriterion(string Id, string Label, string Unit, string Target, double Weight,
    double? Applied, double? Guardian, double? AppliedScore, double? GuardianScore,
    IReadOnlyList<string> Provenance, string Window, string Note);
public sealed record GuardianDisagreement(string Id, string Test, DateTimeOffset At, string Guardian,
    string Applied, string? Outcome, IReadOnlyList<string> Provenance);
public sealed record GuardianReview(string Project, string Template, DateTimeOffset From, DateTimeOffset To,
    bool Live, DateTimeOffset? ShadowSince, bool ShadowWeekComplete, IReadOnlyList<GuardianCriterion> Criteria,
    double? AppliedValue, double? GuardianValue, IReadOnlyList<GuardianDisagreement> Disagreements,
    IReadOnlyList<string> MissingSources);
public sealed record GuardianMeasurements(IReadOnlyDictionary<string, double?> Values,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Sources);

/// <summary>Read-only min-max comparison on one common weekly cohort. Unknown counterfactuals stay unknown.</summary>
public static class GuardianReviewProjection
{
    public static readonly IReadOnlyDictionary<string, double> DefaultWeights = new Dictionary<string, double>
    {
        ["false-blames"] = 25, ["unusable-minutes"] = 20, ["escapes"] = 20,
        ["isolation-minutes"] = 15, ["repair-cost"] = 10, ["held-wait"] = 10,
    };
    private static readonly (string Id, string Label, string Unit, string Target)[] Definitions =
    [
        ("false-blames", "False blames", "per 10 incidents", "0"),
        ("unusable-minutes", "Time develop was really unusable", "min", "Line stop < 60 min per incident; unrelated refusals 0"),
        ("escapes", "Escapes", "events", "0; includes relapses within 7 days"),
        ("isolation-minutes", "Time to isolate", "min (p50 / p95)", "p50 15 min; p95 45 min"),
        ("repair-cost", "Cost of repairs", "relative to previous week", "Falls week on week; tokens and core-minutes per incident"),
        ("held-wait", "Held-card wait", "card-min", "Reported, used to tune scope"),
    ];
    public static GuardianReview Build(string project, DateTimeOffset from, GuardianProjectState state,
        GuardianMeasurements applied, GuardianMeasurements guardian, DateTimeOffset now,
        IReadOnlyList<string>? missing = null, IReadOnlyDictionary<string, string>? notes = null)
    {
        var to = from.AddDays(7);
        var criteria = Definitions.Select(d =>
        {
            var a = applied.Values.GetValueOrDefault(d.Id);
            var g = guardian.Values.GetValueOrDefault(d.Id);
            // Equal measured values tie at 100. No spread means neither side is worse.
            double? Score(double? value) => value is null || a is null || g is null ? null
                : a == g ? 100 : 100 * (Math.Max(a.Value, g.Value) - value.Value) / Math.Abs(a.Value - g.Value);
            return new GuardianCriterion(d.Id, d.Label, d.Unit, d.Target,
                state.ReviewWeights.GetValueOrDefault(d.Id, DefaultWeights[d.Id]), a, g, Score(a), Score(g),
                (applied.Sources.GetValueOrDefault(d.Id) ?? []).Concat(guardian.Sources.GetValueOrDefault(d.Id) ?? []).Distinct().ToArray(),
                $"[{from:O}, {to:O}) UTC", notes?.GetValueOrDefault(d.Id) ?? "Lower is better. Min-max against applied decisions on the same incidents.");
        }).ToArray();
        double? Value(bool shadow) => criteria.Any(c => c.Weight > 0 && (shadow ? c.GuardianScore : c.AppliedScore) is null)
            ? null : criteria.Sum(c => c.Weight * ((shadow ? c.GuardianScore : c.AppliedScore) ?? 0)) / criteria.Sum(c => c.Weight);
        var disagreements = state.Decisions.Where(d => d.Shadow && d.Log.AtUtc <= now && d.Log.AtUtc >= from && d.Log.AtUtc < to
            && Signature(d.Guardian) != Signature(d.Applied)).Select(d =>
            {
                var outcomes = state.ReviewEvidence.Where(e => e.DecisionId == d.Log.DecisionId && e.At <= now && e.Outcome is not null)
                    .OrderBy(e => e.At).ToArray();
                return new GuardianDisagreement(d.Log.DecisionId, d.Log.Test, d.Log.AtUtc,
                    Signature(d.Guardian), Signature(d.Applied), outcomes.LastOrDefault()?.Outcome,
                    new[] { "guardian-decision:" + d.Log.DecisionId, "receipt:" + d.Log.RunId }.Concat(outcomes.Select(e => e.Source)).ToArray());
            }).ToArray();
        return new(project, "guardian-v1", from, to, state.Live, state.ShadowSince,
            AgentStudio.Runner.MonolithTransitionAdapter.GuardianShadowWeekComplete(state.ShadowSince, now), criteria, Value(false), Value(true), disagreements, missing ?? []);
    }
    public static string Signature(AgentStudio.OrchestrationDecisions.RedTestTriageDecision decision)
        => decision.Classification + ": " + string.Join(", ", decision.Decisions.Select(d => d.Action + "(" + d.Reason + "; duration=" + d.Duration + "; model=" + d.ModelRung
                + "; question=" + System.Text.Json.JsonSerializer.Serialize(d.Question) + ")"))
            + "; attribution=" + decision.AttributedMerge;
    public static double? Percentile(IEnumerable<double> values, double p)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0) return null;
        var index = (sorted.Length - 1) * p;
        return sorted[(int)Math.Floor(index)] + (sorted[(int)Math.Ceiling(index)] - sorted[(int)Math.Floor(index)]) * (index % 1);
    }
}
