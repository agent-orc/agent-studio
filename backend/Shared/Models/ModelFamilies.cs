using System.Text.RegularExpressions;

namespace AgentStudio.Shared;

/// <summary>
/// Model family ids and the convention that maps a concrete model id onto one.
///
/// <para>A family is the product line an operator actually reasons about
/// ("Haiku", "the cheap Codex model"), not a vendor and not a single release.
/// Runtime defaults reference a family instead of a pinned id so a new release
/// of the installed CLI becomes the default without a code change
/// (see <see cref="AgentStudio.Cli.ModelFamilyResolver"/>).</para>
///
/// <para>Membership is derived from the id, never from a maintained table: the
/// same house rule that keeps <c>gpt-5.6</c> out of the static catalog
/// (AGT-2025). A family the convention cannot name returns <c>null</c>, which
/// callers read as "no family policy applies".</para>
/// </summary>
public static class ModelFamilies
{
    public const string ClaudeHaiku = "claude-haiku";
    public const string ClaudeSonnet = "claude-sonnet";
    public const string ClaudeOpus = "claude-opus";
    public const string ClaudeFable = "claude-fable";

    /// <summary>Bounded supporting-agent Codex tier (<c>gpt-*-mini</c>).</summary>
    public const string GptMini = "gpt-mini";

    /// <summary>Top Codex tier: every non-mini OpenAI model the CLI advertises.</summary>
    public const string GptFlagship = "gpt-flagship";

    /// <summary>
    /// The families that carry a runtime default. Used by the resolver contract
    /// test: each of these must resolve from the registry alone, so a stale or
    /// missing discovery snapshot can never leave a call site without a model.
    /// </summary>
    public static readonly string[] All =
        [ClaudeHaiku, ClaudeSonnet, ClaudeOpus, ClaudeFable, GptMini, GptFlagship];

    private static readonly string[] ClaudeFamilies = [ClaudeHaiku, ClaudeSonnet, ClaudeOpus, ClaudeFable];

    private static readonly Regex LeadingVersionRegex = new(
        @"^(\d+)(?:[-.](\d+))*", RegexOptions.Compiled);

    /// <summary>
    /// The family a model id belongs to, or null when the convention does not
    /// recognise it (Gemini, a bespoke deployment id, an empty value).
    /// Aliases are normalised through the registry first, so
    /// <c>claude-haiku-4.5</c> and <c>claude-haiku-4-5-20251001</c> both land on
    /// <see cref="ClaudeHaiku"/> with the canonical generation.
    /// </summary>
    public static string? Of(string? modelId)
    {
        var normalized = Normalize(modelId);
        if (normalized == null) return null;

        foreach (var family in ClaudeFamilies)
        {
            if (normalized.StartsWith(family + "-", StringComparison.Ordinal)
                || string.Equals(normalized, family, StringComparison.Ordinal))
            {
                return family;
            }
        }

        if (!normalized.StartsWith("gpt-", StringComparison.Ordinal)) return null;
        return normalized.Contains("-mini", StringComparison.Ordinal) ? GptMini : GptFlagship;
    }

    /// <summary>
    /// The CLI that can run a family, so a resolver never mixes an Anthropic id
    /// into a Codex call site.
    /// </summary>
    public static string? CliFor(string? familyId)
    {
        if (string.IsNullOrWhiteSpace(familyId)) return null;
        var trimmed = familyId.Trim().ToLowerInvariant();
        if (ClaudeFamilies.Contains(trimmed, StringComparer.Ordinal)) return CliTypes.Claude;
        return trimmed is GptMini or GptFlagship ? CliTypes.Codex : null;
    }

    /// <summary>
    /// The numeric generation of a model id inside its family, most significant
    /// segment first: <c>claude-opus-5</c> is <c>[5]</c>, <c>claude-opus-4-8</c>
    /// is <c>[4, 8]</c>, <c>gpt-5.6-sol</c> is <c>[5, 6]</c>. Trailing
    /// non-numeric segments (the Codex reasoning-size suffix, <c>-codex</c>) are
    /// not part of the generation; they are ranked by catalog order instead.
    /// Returns an empty list when no version follows the family prefix.
    /// </summary>
    public static IReadOnlyList<int> GenerationOf(string? modelId)
    {
        var normalized = Normalize(modelId);
        var family = Of(normalized);
        if (normalized == null || family == null) return [];

        var prefix = family == GptMini || family == GptFlagship ? "gpt" : family;
        var remainder = normalized.Length > prefix.Length ? normalized[prefix.Length..] : "";
        remainder = remainder.TrimStart('-');

        var match = LeadingVersionRegex.Match(remainder);
        if (!match.Success) return [];

        var segments = new List<int>();
        foreach (Group group in match.Groups)
        {
            if (group.Name == "0") continue;
            foreach (Capture capture in group.Captures)
                segments.Add(int.Parse(capture.Value));
        }
        return segments;
    }

    /// <summary>
    /// Compares two generations segment by segment, treating a missing segment
    /// as <c>0</c> so <c>[5]</c> and <c>[5, 0]</c> are peers. Positive when
    /// <paramref name="left"/> is the newer generation.
    /// </summary>
    public static int CompareGeneration(IReadOnlyList<int> left, IReadOnlyList<int> right)
    {
        var length = Math.Max(left.Count, right.Count);
        for (var index = 0; index < length; index++)
        {
            var difference = (index < left.Count ? left[index] : 0) - (index < right.Count ? right[index] : 0);
            if (difference != 0) return difference;
        }
        return 0;
    }

    private static string? Normalize(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;
        var canonical = ModelMetadataRegistry.NormalizeId(modelId);
        if (string.IsNullOrWhiteSpace(canonical)) return null;
        return canonical.Trim().ToLowerInvariant().Replace('.', '-');
    }
}
