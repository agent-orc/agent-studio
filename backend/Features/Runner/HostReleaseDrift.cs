using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

/// <summary>Drift verdict vocabulary shared by the API, the UI and the alarm.</summary>
public static class HostReleaseDriftStates
{
    /// <summary>The host runs the Stable release, or something newer.</summary>
    public const string Current = "current";

    /// <summary>The host provably runs an older release than Stable.</summary>
    public const string Behind = "behind";

    /// <summary>Nothing comparable was reported. Never an alarm, never "current".</summary>
    public const string Unknown = "unknown";
}

/// <summary>
/// The release this server process is running, used as the reference every
/// execution host is compared against.
/// </summary>
public sealed record StableReleaseIdentity(string Version, string? Commit, DateTime? BuiltAt)
{
    public static StableReleaseIdentity FromBuildIdentity(BuildIdentity identity) => new(
        identity.Version,
        string.Equals(identity.Commit, "unknown", StringComparison.OrdinalIgnoreCase)
            ? null
            : identity.Commit,
        identity.BuiltAt?.UtcDateTime);
}

/// <summary>
/// Outcome of one host-versus-Stable comparison.
/// <para>
/// <see cref="BehindBy"/> is the release-age gap - how much older the host build
/// is than the Stable build - and is what the operator sees as "23d behind".
/// <see cref="BehindFor"/> is the duration the alarm is judged on; see
/// <see cref="HostReleaseDriftPolicy"/> for why the two can differ.
/// </para>
/// </summary>
public sealed record HostReleaseDriftVerdict(
    string State,
    TimeSpan? BehindBy,
    TimeSpan? BehindFor,
    bool AlarmDue,
    string Reason);

/// <summary>
/// Pure comparison between one host's reported release identity and the Stable
/// release (AGT-2826). No clock, no I/O, no store: the watchdog, the management
/// route and the tests all call this one function.
/// </summary>
/// <remarks>
/// <para>
/// Ordering is established from the strongest available evidence, in order:
/// an identical commit means identical code; two build timestamps give both an
/// order and an age; otherwise a semantic version ordering says which side is
/// older without saying by how much. When none of that applies the verdict is
/// <see cref="HostReleaseDriftStates.Unknown"/> rather than a guess.
/// </para>
/// <para>
/// The alarm fires once a host has been behind for longer than
/// <see cref="DefaultGrace"/>. "For longer than" reads from the release-age gap
/// when both build stamps are known, so a host three weeks behind alarms on the
/// first observation instead of waiting a day after a server restart. When only
/// the version ordering is known there is no gap to read, so the fallback is how
/// long this process has continuously observed the drift.
/// </para>
/// </remarks>
public static class HostReleaseDriftPolicy
{
    /// <summary>A rolling update may legitimately leave a host briefly behind.</summary>
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromHours(24);

    public static HostReleaseDriftVerdict Evaluate(
        Contract.RunnerReleaseIdentityDto? host,
        StableReleaseIdentity? stable,
        DateTime nowUtc,
        DateTime? firstObservedBehindAtUtc = null,
        TimeSpan? grace = null)
    {
        if (stable is null || string.IsNullOrWhiteSpace(stable.Version))
            return Unknown("The Stable release identity is unavailable on this server.");
        if (host is null || string.IsNullOrWhiteSpace(host.Version))
            return Unknown("The host has not reported a release identity yet.");

        if (CommitsMatch(host.Commit, stable.Commit))
            return Current("The host runs the same commit as Stable.");

        var behindBy = BuildAgeGap(host.BuiltAt, stable.BuiltAt);
        if (behindBy is not null)
        {
            return behindBy <= TimeSpan.Zero
                ? Current("The host build is not older than the Stable build.")
                : Behind(
                    behindBy,
                    nowUtc,
                    firstObservedBehindAtUtc,
                    grace,
                    $"The host build is {Describe(behindBy.Value)} older than the Stable build.");
        }

        var ordering = CompareVersions(host.Version, stable.Version);
        if (ordering is null)
            return Unknown($"Release '{host.ReleaseId}' cannot be ordered against Stable {stable.Version}.");
        if (ordering > 0)
            return Current("The host version is not older than Stable.");
        if (ordering == 0)
        {
            if (CommitIsUnusable(host.Commit) || CommitIsUnusable(stable.Commit))
                return Unknown("The reported commit is not a comparable hexadecimal identity.");
            return Current("The host version is not older than Stable.");
        }

        return Behind(
            null,
            nowUtc,
            firstObservedBehindAtUtc,
            grace,
            $"The host runs {host.Version} while Stable runs {stable.Version}.");
    }

    /// <summary>
    /// Two release ids describe the same code when their commits agree. Release
    /// pipelines abbreviate the sha differently per artifact, so a prefix match
    /// of at least seven hex characters counts.
    /// </summary>
    internal static bool CommitsMatch(string? left, string? right)
    {
        var a = left?.Trim();
        var b = right?.Trim();
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        if (CommitIsUnusable(a) || CommitIsUnusable(b)) return false;
        var shortest = Math.Min(a.Length, b.Length);
        return a.AsSpan(0, shortest).Equals(b.AsSpan(0, shortest), StringComparison.OrdinalIgnoreCase);
    }

    private static bool CommitIsUnusable(string? value)
    {
        var commit = value?.Trim();
        return !string.IsNullOrEmpty(commit)
            && (commit.Length < 7 || !commit.All(Uri.IsHexDigit));
    }

    /// <summary>Negative when the host is newer, positive when it is older, null when unknown.</summary>
    internal static TimeSpan? BuildAgeGap(DateTime? hostBuiltAt, DateTime? stableBuiltAt)
        => hostBuiltAt is { } host && stableBuiltAt is { } stable
            ? stable.ToUniversalTime() - host.ToUniversalTime()
            : null;

    /// <summary>
    /// Compares two product versions, ignoring a build-metadata suffix. Returns
    /// null when either side is not a dotted numeric version, because an
    /// unorderable pair must not be reported as drift.
    /// </summary>
    internal static int? CompareVersions(string? host, string? stable)
    {
        var left = ParseVersion(host);
        var right = ParseVersion(stable);
        if (left is null || right is null) return null;
        return left.CompareTo(right);
    }

    private static Version? ParseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var core = value.Trim().Split('+', 2)[0].Split('-', 2)[0].TrimStart('v', 'V');
        return Version.TryParse(core, out var parsed) ? parsed : null;
    }

    private static HostReleaseDriftVerdict Behind(
        TimeSpan? behindBy,
        DateTime nowUtc,
        DateTime? firstObservedBehindAtUtc,
        TimeSpan? grace,
        string reason)
    {
        var observedFor = firstObservedBehindAtUtc is { } since
            ? nowUtc.ToUniversalTime() - since.ToUniversalTime()
            : TimeSpan.Zero;
        var behindFor = behindBy ?? observedFor;
        if (behindFor < TimeSpan.Zero) behindFor = TimeSpan.Zero;
        return new HostReleaseDriftVerdict(
            HostReleaseDriftStates.Behind,
            behindBy,
            behindFor,
            behindFor > (grace ?? DefaultGrace),
            reason);
    }

    private static HostReleaseDriftVerdict Current(string reason)
        => new(HostReleaseDriftStates.Current, null, null, false, reason);

    private static HostReleaseDriftVerdict Unknown(string reason)
        => new(HostReleaseDriftStates.Unknown, null, null, false, reason);

    private static string Describe(TimeSpan span)
        => span.TotalDays >= 1
            ? $"{(int)span.TotalDays}d {span.Hours}h"
            : $"{(int)span.TotalHours}h {span.Minutes}m";
}
