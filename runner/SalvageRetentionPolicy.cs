namespace AgentRunner;

/// <summary>
/// Pure retention decision for the host salvage store (AGT-2999): the legacy
/// worktree tarballs under the salvage directory and this runner's
/// <c>agent-studio/salvage/*</c> Git refs.
///
/// <para>
/// Tarballs: keep every entry while its card is open or its state is unknown,
/// and for <see cref="SalvageRetentionSettings.RetentionDays"/> after the card
/// became completed or archived. Independently of the card state, keep only the
/// newest <see cref="SalvageRetentionSettings.MaxPerCard"/> tarballs of a card.
/// </para>
/// <para>
/// Git refs follow the same two rules, but only a ref whose card is completed or
/// archived and whose commit is contained in the integration branch is eligible
/// at all. A ref is otherwise the only copy of that work and is kept.
/// </para>
/// <para>
/// An entry of a card with an active run on this host is never deleted, and an
/// entry whose card cannot be derived from its name is never deleted.
/// </para>
/// </summary>
internal static class SalvageRetentionPolicy
{
    public static IReadOnlyList<SalvageRetentionDecision> Decide(
        IReadOnlyList<SalvageEntry> entries,
        IReadOnlyDictionary<string, SalvageCardFacts> cards,
        IReadOnlySet<string> activeCardKeys,
        SalvageRetentionSettings settings,
        DateTime utcNow)
    {
        var retentionDays = Math.Max(1, settings.RetentionDays);
        var maxPerCard = Math.Max(1, settings.MaxPerCard);
        var active = activeCardKeys
            .Select(NormalizeCardKey)
            .ToHashSet(StringComparer.Ordinal);

        // Newest first per card and kind. The entry id breaks timestamp ties so
        // the ranking is deterministic across sweeps.
        var ranks = new int[entries.Count];
        var groups = Enumerable.Range(0, entries.Count)
            .Where(index => entries[index].CardKey is not null)
            .GroupBy(index => (entries[index].Kind, Card: NormalizeCardKey(entries[index].CardKey!)));
        foreach (var group in groups)
        {
            var position = 0;
            foreach (var index in group
                         .OrderByDescending(index => entries[index].CreatedAt)
                         .ThenByDescending(index => entries[index].Id, StringComparer.Ordinal))
                ranks[index] = ++position;
        }

        return entries
            .Select((entry, index) => new SalvageRetentionDecision(
                entry,
                Evaluate(entry, cards, active, retentionDays, maxPerCard, utcNow, ranks[index])))
            .ToArray();
    }

    private static SalvageRetentionReason Evaluate(
        SalvageEntry entry,
        IReadOnlyDictionary<string, SalvageCardFacts> cards,
        IReadOnlySet<string> active,
        int retentionDays,
        int maxPerCard,
        DateTime utcNow,
        int rank)
    {
        if (entry.CardKey is null)
            return SalvageRetentionReason.KeepUnrecognized;
        var card = NormalizeCardKey(entry.CardKey);
        if (active.Contains(card))
            return SalvageRetentionReason.KeepActiveRun;

        var facts = cards.TryGetValue(card, out var known)
            ? known
            : new SalvageCardFacts(card, SalvageCardLifecycle.Unknown, null);

        if (entry.Kind == SalvageEntryKind.GitRef)
        {
            if (facts.Lifecycle != SalvageCardLifecycle.Terminal)
                return KeepFor(facts.Lifecycle);
            if (entry.OnIntegrationBranch is null)
                return SalvageRetentionReason.KeepIntegrationUnknown;
            if (entry.OnIntegrationBranch == false)
                return SalvageRetentionReason.KeepNotOnIntegrationBranch;
        }

        if (facts.Lifecycle == SalvageCardLifecycle.Terminal
            && facts.TerminalSince is { } since
            && utcNow - since >= TimeSpan.FromDays(retentionDays))
            return SalvageRetentionReason.DeleteRetentionElapsed;

        if (rank > maxPerCard)
            return SalvageRetentionReason.DeleteOverPerCardLimit;

        return facts.Lifecycle == SalvageCardLifecycle.Terminal
            ? SalvageRetentionReason.KeepWithinRetention
            : KeepFor(facts.Lifecycle);
    }

    private static SalvageRetentionReason KeepFor(SalvageCardLifecycle lifecycle) => lifecycle switch
    {
        SalvageCardLifecycle.Open => SalvageRetentionReason.KeepCardOpen,
        SalvageCardLifecycle.Missing => SalvageRetentionReason.KeepCardMissing,
        SalvageCardLifecycle.Terminal => SalvageRetentionReason.KeepWithinRetention,
        _ => SalvageRetentionReason.KeepCardStateUnknown,
    };

    public static string NormalizeCardKey(string cardKey) => cardKey.Trim().ToUpperInvariant();

    public static bool IsDelete(SalvageRetentionReason reason)
        => reason is SalvageRetentionReason.DeleteRetentionElapsed
            or SalvageRetentionReason.DeleteOverPerCardLimit;

    public static string Slug(SalvageRetentionReason reason) => reason switch
    {
        SalvageRetentionReason.DeleteRetentionElapsed => "retention-elapsed",
        SalvageRetentionReason.DeleteOverPerCardLimit => "over-per-card-limit",
        SalvageRetentionReason.KeepUnrecognized => "unrecognized-name",
        SalvageRetentionReason.KeepActiveRun => "active-run",
        SalvageRetentionReason.KeepCardOpen => "card-open",
        SalvageRetentionReason.KeepCardMissing => "card-missing",
        SalvageRetentionReason.KeepCardStateUnknown => "card-state-unknown",
        SalvageRetentionReason.KeepWithinRetention => "within-retention",
        SalvageRetentionReason.KeepNotOnIntegrationBranch => "not-on-integration-branch",
        SalvageRetentionReason.KeepIntegrationUnknown => "integration-unknown",
        _ => "unknown",
    };
}

internal sealed record SalvageRetentionSettings(int RetentionDays = 14, int MaxPerCard = 3);

internal enum SalvageEntryKind { Tarball, GitRef }

/// <summary>
/// One salvage store entry. <see cref="Id"/> is the tarball file name or the
/// full ref name; <see cref="OnIntegrationBranch"/> is only meaningful for refs
/// and is null when containment could not be established.
/// </summary>
internal sealed record SalvageEntry(
    SalvageEntryKind Kind,
    string Id,
    string? ProjectId,
    string? CardKey,
    DateTime CreatedAt,
    long SizeBytes,
    bool? OnIntegrationBranch = null,
    string? Sha = null);

public enum SalvageCardLifecycle { Unknown, Missing, Open, Terminal }

/// <summary>
/// What the Task Server knows about a card. <see cref="TerminalSince"/> is the
/// latest completion or archive timestamp; a later value only extends retention.
/// </summary>
internal sealed record SalvageCardFacts(
    string CardKey,
    SalvageCardLifecycle Lifecycle,
    DateTime? TerminalSince);

public enum SalvageRetentionReason
{
    DeleteRetentionElapsed,
    DeleteOverPerCardLimit,
    KeepUnrecognized,
    KeepActiveRun,
    KeepCardOpen,
    KeepCardMissing,
    KeepCardStateUnknown,
    KeepWithinRetention,
    KeepNotOnIntegrationBranch,
    KeepIntegrationUnknown,
}

internal sealed record SalvageRetentionDecision(SalvageEntry Entry, SalvageRetentionReason Reason)
{
    public bool Delete => SalvageRetentionPolicy.IsDelete(Reason);
}
