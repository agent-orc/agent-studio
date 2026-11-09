using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AgentRunner.WorkContainers;

/// <summary>
/// SM-11: every work container runs under its own unprivileged UID, chosen
/// from a range no host account uses and distinct from every live attempt on
/// the host. The group is shared so the attempt's repository caches stay
/// writable for the next attempt of the same repository; the caches of other
/// repositories are never mounted.
/// </summary>
public static class WorkContainerIdentity
{
    public const int GroupId = 10001;
    public const int FirstAttemptUid = 200_000;
    public const int AttemptUidCount = 65_536;
    public const string UidLabel = "agent-studio.uid";

    public static bool IsAttemptUid(int uid) => uid >= FirstAttemptUid && uid < FirstAttemptUid + AttemptUidCount;

    public static string User(int uid) => string.Create(CultureInfo.InvariantCulture, $"{uid}:{GroupId}");

    /// <summary>Deterministic first choice per attempt and fence, then the next free UID.</summary>
    public static int Allocate(string attemptId, long fence, IReadOnlySet<int> inUse)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{attemptId}\n{fence}")));
        var start = (int)(BitConverter.ToUInt32(digest, 0) % AttemptUidCount);
        for (var offset = 0; offset < AttemptUidCount; offset++)
        {
            var uid = FirstAttemptUid + (start + offset) % AttemptUidCount;
            if (!inUse.Contains(uid)) return uid;
        }
        throw new InvalidOperationException("Every attempt UID on this host is in use.");
    }
}
