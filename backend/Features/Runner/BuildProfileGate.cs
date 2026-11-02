
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentStudio.Runner;

/// <summary>
/// Pure onboarding gate (Slice P / ASS-1663). Decides whether the runner may
/// auto-pick a project given its <see cref="BuildProfile"/>. The rule is
/// serial-preserving: a project that has never declared a build profile
/// (<c>profile == null</c>) is always allowed (legacy behaviour, every existing
/// project). A first declaration opens only after green validation. An edit to
/// a validated profile receives a bounded grace window while exact-profile
/// revalidation is pending.
///
/// <para>
/// Kept as a stateless static so the decision is trivially unit-testable and the
/// pickup loop reads one expression. "Ohne gruenen Dry-Run kein Auto-Pickup."
/// </para>
/// </summary>
public static class BuildProfileGate
{
    /// <summary>Pickup decision plus a short human-readable reason for logs / timeline.</summary>
    public readonly record struct Decision(bool AllowsPickup, string Reason);

    /// <summary>
    /// Evaluates the gate. A null profile is "no onboarding gate". A declared but
    /// unvalidated / failed / in-progress profile blocks pickup. Pipeline-ready
    /// opens it unless a pending revalidation has exhausted its grace runs.
    /// </summary>
    public static Decision Evaluate(BuildProfile? profile)
    {
        if (profile is null)
            return new Decision(true, "no build profile declared");

        var status = BuildProfileStatuses.Normalize(profile.Status);
        if (status == BuildProfileStatuses.PipelineReady && profile.RevalidationPending)
        {
            return profile.RevalidationGraceRunsRemaining > 0
                ? new Decision(true,
                    $"build profile revalidation pending ({profile.RevalidationGraceRunsRemaining} grace run(s) remaining)")
                : new Decision(false,
                    "build profile revalidation pending; grace runs exhausted");
        }
        return status switch
        {
            BuildProfileStatuses.PipelineReady => new Decision(true, "pipeline-ready"),
            BuildProfileStatuses.Validating => new Decision(false, "validation dry-run in progress"),
            BuildProfileStatuses.ValidationFailed => new Decision(false,
                "last validation dry-run failed" + (string.IsNullOrWhiteSpace(profile.LastValidationError) ? "" : $": {profile.LastValidationError}")),
            _ => new Decision(false, "build profile declared but not yet validated (no green dry-run)"),
        };
    }

    /// <summary>Convenience boolean for the pickup loop.</summary>
    public static bool AllowsAutoPickup(BuildProfile? profile) => Evaluate(profile).AllowsPickup;
}

/// <summary>
/// Creates the exact-profile identity carried from project settings into an
/// immutable remote Review plan. Only preparation and verification inputs are
/// included; validation lifecycle fields deliberately are not.
/// </summary>
public static class BuildProfileValidationFingerprint
{
    public static string? Create(BuildProfile? profile)
    {
        if (profile is null) return null;
        var fields = new
        {
            stack = Normalize(profile.Stack),
            installCmd = Normalize(profile.InstallCmd),
            buildCmds = Normalize(profile.BuildCmds),
            testCmds = Normalize(profile.TestCmds),
            lockfiles = Normalize(profile.Lockfiles),
            preserveGlobs = Normalize(profile.PreserveGlobs),
            poolSize = profile.PoolSize is > 0 ? profile.PoolSize : null,
        };
        // AGT-3429: the opt-in changes which preparation a cache hit may skip.
        // It enters the payload only when set, so every existing profile keeps
        // its validated fingerprint.
        var payload = profile.InstallCoveredByLockfiles
            ? JsonSerializer.Serialize(new { fields, installCoveredByLockfiles = true })
            : JsonSerializer.Serialize(fields);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))
            .ToLowerInvariant();
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IReadOnlyList<string> Normalize(IReadOnlyList<string>? values) =>
        (values ?? [])
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value.Trim())
        .ToArray();
}
