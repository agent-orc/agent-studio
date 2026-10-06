using System.Security.Cryptography;
using System.Text;

namespace AgentStudio.TaskServer.Contracts;

/// <summary>One identity for named failed items across gate and review reports.</summary>
public static class FailureItemFingerprint
{
    public static string Compute(IEnumerable<string> items)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n",
            items.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))));
        return "gate-items:" + Convert.ToHexString(bytes).ToLowerInvariant()[..16];
    }
}

public sealed record RecordFailureFingerprintRequest(
    string Fingerprint,
    string CardKey,
    string Executor,
    string Source,
    string ReportKey);

public sealed record FailureFingerprintHistoryDto(
    string Fingerprint,
    DateTime FirstSeen,
    DateTime LastSeen,
    int Count,
    IReadOnlyList<string> Executors,
    IReadOnlyList<string> CardKeys);
