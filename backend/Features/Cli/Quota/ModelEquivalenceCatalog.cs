namespace AgentStudio.Cli;

/// <summary>
/// Cross-CLI-family capability-equivalence lookup. Lets
/// <see cref="CliQuotaFallbackService"/> derive an implicit quota fallback for a
/// CLI the operator has not configured a <c>fallbackCliType</c> for, instead of
/// requiring a hand-maintained pair per CLI (AGT-2751).
/// </summary>
/// <remarks>
/// The exhaustive, maintained version of this table is Token Economy's model
/// migration catalogue (<c>model-migration-catalog-safe-auto-rules</c>), which
/// is out of scope for this change. This interface is the seam that lets
/// <see cref="CliQuotaFallbackService"/> consume that catalogue once it lands
/// (swap the registered <see cref="IModelEquivalenceCatalog"/>); the shipped
/// <see cref="ModelEquivalenceCatalog"/> is an interim table covering only the
/// tiers already named in
/// docs/system/domains/model-routing-policy.md ("equivalent-capability
/// provider fallback").
/// </remarks>
public interface IModelEquivalenceCatalog
{
    /// <summary>
    /// The equal-strength (model, thinking level) pair in
    /// <paramref name="toCliType"/> for a request currently pinned to
    /// <paramref name="fromCliType"/>/<paramref name="fromModel"/> at
    /// <paramref name="fromThinkingLevel"/>, or null when no tier is known for
    /// that combination.
    /// </summary>
    (string Model, string? ThinkingLevel)? TryGetEquivalent(
        string fromCliType, string? fromModel, string? fromThinkingLevel, string toCliType);
}

/// <summary>Interim equivalence table; see the remarks on <see cref="IModelEquivalenceCatalog"/>.</summary>
public sealed class ModelEquivalenceCatalog : IModelEquivalenceCatalog
{
    private sealed record Tier(string CliType, string Model, string? ThinkingLevel);

    // One row per documented pair. model-routing-policy.md names Claude
    // Opus 5/high as "a reasonable equivalent-provider signal" for the
    // Codex flagship (Sol/high) and Claude Sonnet 5/medium for the bounded
    // Mini/high support tier. Both sides of a row must be real
    // ModelMetadataRegistry entries; do not add a row for a tier name that
    // has no corresponding ModelIds constant.
    private static readonly Tier[][] Groups =
    [
        [
            new Tier(CliTypes.Codex, ModelIds.Gpt56Sol, "high"),
            new Tier(CliTypes.Claude, ModelIds.ClaudeOpus5, "high"),
        ],
        [
            new Tier(CliTypes.Codex, ModelIds.Gpt54Mini, "high"),
            new Tier(CliTypes.Claude, ModelIds.ClaudeSonnet5, "medium"),
        ],
    ];

    public (string Model, string? ThinkingLevel)? TryGetEquivalent(
        string fromCliType, string? fromModel, string? fromThinkingLevel, string toCliType)
    {
        foreach (var group in Groups)
        {
            // A caller with no explicit primary model pinned (the common case
            // for an operator who configured nothing) matches this family's
            // catalogue row by CLI alone, so GetEffectiveProfile still has a
            // display default; an explicit model must match exactly.
            var from = Array.Find(group, t =>
                string.Equals(t.CliType, fromCliType, StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrWhiteSpace(fromModel) || string.Equals(t.Model, fromModel, StringComparison.OrdinalIgnoreCase)));
            if (from is null) continue;
            var to = Array.Find(group, t => string.Equals(t.CliType, toCliType, StringComparison.OrdinalIgnoreCase));
            if (to is null || ReferenceEquals(to, from)) continue;
            return (to.Model, to.ThinkingLevel);
        }
        return null;
    }
}
