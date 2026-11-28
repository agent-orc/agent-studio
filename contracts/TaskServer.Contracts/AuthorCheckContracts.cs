namespace AgentStudio.TaskServer.Contracts;

public static class AuthorCheckProtocol
{
    public const string Kind = "author-check";
    public const string Name = "build";
    public const string BundleRef = "refs/heads/author-check";
    public const int BundleLimit = 64 * 1024 * 1024;
    public const int ExpandedLimit = 256 * 1024 * 1024;
    public const int ObjectLimit = 100_000;
    public const int LogLimit = 4 * 1024 * 1024;
    public const int QueueSeconds = 90;
    public const int WorkSeconds = 1500;
    public const int LeaseSeconds = 30;

    public static bool IsHex(string? value, int length)
        => value?.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public sealed record AuthorCheckParent(string RunId, string RunnerId, string InstanceId, string LeaseId, long Fence);

public sealed record AuthorCheckSnapshot(
    string Base, string Commit, string Tree, string Sha256, int Bytes);

public sealed record AuthorCheckSubmission(
    AuthorCheckParent Parent, Guid RequestId, string Name, AuthorCheckSnapshot Snapshot);

public sealed record AuthorCheckProfile(
    string RepositoryId, string Base, string Solution, string Image, int TimeoutSeconds, int HalfCores, int MemoryMiB);

public sealed record AuthorCheckWork(
    AuthorCheckSnapshot Snapshot, AuthorCheckProfile Profile, string PlanDigest, DateTime DeadlineUtc);

public sealed record AuthorCheckReceipt(
    string CheckId, string Status, string Tree, string PlanDigest, int InputBytes,
    string? HostId = null, int? ExitCode = null, long? ElapsedMs = null, long? QueueMs = null,
    int LogBytes = 0, long OmittedLogBytes = 0, string? LogPath = null,
    IReadOnlyList<string>? Diagnostics = null, string? Reason = null, bool LogRedacted = false);

public sealed record AuthorCheckSettlement(
    PostWorkAuthority Authority, string Status, int? ExitCode, long ElapsedMs,
    byte[] Log, long OmittedLogBytes, bool LogRedacted);

public sealed record AuthorCheckSourceGrant(PostWorkAuthority Authority, string Grant);

public sealed record AuthorCheckProtectedWork(string HostId, int Active, int Queued);
