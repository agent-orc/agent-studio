using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentStudio.Review;

/// <summary>
/// Separates the model's trailing <c>&lt;assessment&gt;</c> JSON block from the
/// status.md prose and merges it with the deterministic
/// <see cref="SummaryAssessmentPolicy"/> result. A missing or malformed block
/// never fails the summary: the policy result stands and the reason is kept on
/// <see cref="SummaryAssessment.FallbackReason"/>.
/// </summary>
public static class SummaryAssessmentParser
{
    public const int MaxInconsistencies = 8;
    private const int MaxFieldChars = 600;

    private static readonly Regex BlockRegex = new(
        @"<assessment>(?<json>[\s\S]*?)</assessment>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex OpenTagRegex = new(@"<assessment>[\s\S]*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>Model output split into the status.md prose and the raw assessment JSON, if any.</summary>
    public sealed record SplitOutput(string Markdown, string? AssessmentJson);

    /// <summary>
    /// Removes every assessment block (and an unterminated trailing one) from
    /// the output so the prose validates as a normal status.md. The last
    /// complete block is returned.
    /// </summary>
    public static SplitOutput Split(string? output)
    {
        var text = output ?? "";
        var matches = BlockRegex.Matches(text);
        var json = matches.Count > 0 ? matches[^1].Groups["json"].Value : null;
        var markdown = BlockRegex.Replace(text, "");
        markdown = OpenTagRegex.Replace(markdown, "");
        return new SplitOutput(markdown.TrimEnd() + (markdown.Length > 0 ? "\n" : ""), json);
    }

    /// <summary>Parsed model block; null fields mean the model omitted them.</summary>
    public sealed record ModelAssessment(string? Outcome, string? OutcomeReason, List<SummaryInconsistency> Inconsistencies);

    public static ModelAssessment? TryParse(string? json, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "the model returned no assessment block";
            return null;
        }

        var body = json.Trim();
        // Tolerate a fenced JSON body inside the block.
        body = Regex.Replace(body, @"^```(?:json)?\s*|\s*```$", "", RegexOptions.IgnoreCase).Trim();
        try
        {
            var wire = JsonSerializer.Deserialize<WireAssessment>(body, ReadOptions);
            if (wire is null)
            {
                error = "the assessment block is empty";
                return null;
            }

            var outcome = wire.Outcome?.Trim().ToLowerInvariant();
            if (outcome is not null && !SummaryOutcomes.IsKnown(outcome))
            {
                error = $"unknown outcome '{Clip(wire.Outcome)}'";
                outcome = null;
            }

            var items = (wire.Inconsistencies ?? [])
                .Where(i => i is not null
                    && !string.IsNullOrWhiteSpace(i.SignalA)
                    && !string.IsNullOrWhiteSpace(i.SignalB))
                .Select(i => new SummaryInconsistency
                {
                    Kind = Slug(i!.Kind),
                    SignalA = Clip(i.SignalA)!,
                    SignalB = Clip(i.SignalB)!,
                    Explanation = Clip(i.Explanation) ?? "",
                    WorkMayBeLost = i.WorkMayBeLost ?? false,
                })
                .ToList();
            return new ModelAssessment(outcome, Clip(wire.OutcomeReason), items);
        }
        catch (JsonException ex)
        {
            error = "the assessment block is not valid JSON: " + Clip(ex.Message);
            return null;
        }
    }

    /// <summary>
    /// The model's outcome wins when it is a known value. Every policy
    /// inconsistency survives; a model item of the same kind contributes its
    /// explanation, and "work may be lost" is the conservative OR. Model-only
    /// items are appended up to <see cref="MaxInconsistencies"/>.
    /// </summary>
    public static SummaryAssessment Merge(SummaryAssessment policy, ModelAssessment? model, string? modelName, string? parseError)
    {
        if (model is null)
            return policy with { Model = modelName, FallbackReason = parseError ?? "the model returned no assessment block" };

        var merged = new List<SummaryInconsistency>();
        var consumed = new HashSet<int>();
        foreach (var item in policy.Inconsistencies)
        {
            var index = model.Inconsistencies.FindIndex(m => m.Kind.Length > 0 && string.Equals(m.Kind, item.Kind, StringComparison.Ordinal));
            if (index < 0)
            {
                merged.Add(item);
                continue;
            }
            consumed.Add(index);
            var match = model.Inconsistencies[index];
            merged.Add(item with
            {
                Explanation = string.IsNullOrWhiteSpace(match.Explanation) ? item.Explanation : match.Explanation,
                WorkMayBeLost = item.WorkMayBeLost || match.WorkMayBeLost,
            });
        }
        merged.AddRange(model.Inconsistencies
            .Where((_, i) => !consumed.Contains(i))
            .Select(m => m.Kind.Length > 0 ? m : m with { Kind = "model-observation" }));

        var useModelOutcome = model.Outcome is not null;
        return policy with
        {
            Outcome = useModelOutcome ? model.Outcome! : policy.Outcome,
            OutcomeReason = useModelOutcome ? model.OutcomeReason ?? policy.OutcomeReason : policy.OutcomeReason,
            Source = useModelOutcome ? SummaryAssessmentSources.Model : SummaryAssessmentSources.Policy,
            FallbackReason = useModelOutcome ? null : parseError,
            Model = modelName,
            Inconsistencies = merged.Take(MaxInconsistencies).ToList(),
        };
    }

    private static string Slug(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind)) return "";
        var slug = Regex.Replace(kind.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        return slug.Length > 60 ? slug[..60] : slug;
    }

    private static string? Clip(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();
        return trimmed.Length <= MaxFieldChars ? trimmed : trimmed[..MaxFieldChars] + "...";
    }

    private sealed record WireAssessment
    {
        public string? Outcome { get; init; }
        public string? OutcomeReason { get; init; }
        public List<WireInconsistency?>? Inconsistencies { get; init; }
    }

    private sealed record WireInconsistency
    {
        public string? Kind { get; init; }
        public string? SignalA { get; init; }
        public string? SignalB { get; init; }
        public string? Explanation { get; init; }
        public bool? WorkMayBeLost { get; init; }
    }
}
