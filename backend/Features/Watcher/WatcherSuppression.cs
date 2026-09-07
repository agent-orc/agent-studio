namespace AgentStudio.Watcher;

/// <summary>
/// One suppressed fingerprint. Suppression is always visible and always
/// expires: the dossier forbids a silent permanent mute, because a rejected
/// finding that comes back is itself information.
/// </summary>
public sealed record WatcherSuppressionEntry(
    string Fingerprint,
    string Reason,
    DateTime SuppressedAt,
    DateTime ExpiresAt,
    string? Source = null)
{
    public bool IsActive(DateTime at) => at < ExpiresAt;
}

/// <summary>
/// Expiring suppression list fed by proposal rejections (dossier section 10.4).
/// Known-noise sources are declared, not guessed, so an entry only ever enters
/// through an explicit operator rejection or an explicit declaration.
/// </summary>
public sealed record WatcherSuppressionList(IReadOnlyList<WatcherSuppressionEntry> Entries)
{
    public static readonly WatcherSuppressionList Empty = new([]);

    /// <summary>Default lifetime of a suppression created by a rejection.</summary>
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromDays(14);

    public bool IsSuppressed(string fingerprint, DateTime at)
        => Entries.Any(entry =>
            string.Equals(entry.Fingerprint, fingerprint, StringComparison.Ordinal)
            && entry.IsActive(at));

    /// <summary>Entries still in force, newest first. This is what the UI renders.</summary>
    public IReadOnlyList<WatcherSuppressionEntry> Active(DateTime at)
        =>
        [
            .. Entries
                .Where(entry => entry.IsActive(at))
                .OrderByDescending(entry => entry.SuppressedAt)
                .ThenBy(entry => entry.Fingerprint, StringComparer.Ordinal),
        ];

    /// <summary>
    /// Adds or refreshes a suppression. Re-rejecting a fingerprint extends it
    /// from the new rejection rather than stacking duplicate entries.
    /// </summary>
    public WatcherSuppressionList Suppress(
        string fingerprint,
        string reason,
        DateTime at,
        TimeSpan? duration = null,
        string? source = null)
    {
        var entry = new WatcherSuppressionEntry(
            fingerprint,
            reason,
            at,
            at + (duration ?? DefaultDuration),
            source);

        return new WatcherSuppressionList(
        [
            .. Entries.Where(existing => !string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal)),
            entry,
        ]);
    }

    /// <summary>Drops entries that have expired. Called before persisting so the file does not grow forever.</summary>
    public WatcherSuppressionList Prune(DateTime at)
        => new([.. Entries.Where(entry => entry.IsActive(at))]);
}
