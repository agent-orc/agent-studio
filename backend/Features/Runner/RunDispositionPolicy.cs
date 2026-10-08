using System.Security.Cryptography;
using System.Text;

namespace AgentStudio.Runner;

/// <summary>The caller's explicit instruction for a live attempt when its card leaves Progress.</summary>
public enum RunMoveIntent { Revoke, Steer }

public enum RemoteCompletionDisposition { Apply, OfferOlderBrief }

public static class RunDispositionPolicy
{
    public static RunMoveIntent? ParseMoveIntent(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "revoke" => RunMoveIntent.Revoke,
        "steer" => RunMoveIntent.Steer,
        _ => null,
    };

    public static string BriefVersion(string brief)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(brief))).ToLowerInvariant();

    public static RemoteCompletionDisposition DecideCompletion(string? attemptBriefVersion, string currentBriefVersion)
        => string.IsNullOrWhiteSpace(attemptBriefVersion)
           || string.Equals(attemptBriefVersion, currentBriefVersion, StringComparison.OrdinalIgnoreCase)
            ? RemoteCompletionDisposition.Apply
            : RemoteCompletionDisposition.OfferOlderBrief;
}
