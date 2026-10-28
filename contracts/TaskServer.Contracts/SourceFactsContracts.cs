using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Derived source data for review and card open (AGT-3318, pipeline-delivery
/// target P11, decision D7 A-B). One producer per repository on a runner host
/// computes diff objects against the merge base, path history and blame, and
/// publishes them to the Task Server under a producer epoch. The Task Server
/// stores them by immutable key and serves readers without starting Git.
/// </summary>
public static class SourceFactKinds
{
    public const string Diff = "diff";
    public const string PathHistory = "path-history";
    public const string Blame = "blame";
    public const string Reachability = "reachability";

    public static bool IsKnown(string? kind)
        => kind is Diff or PathHistory or Blame or Reachability;
}

/// <summary>
/// The diff-object key of AGT-3017 D1: repository, base SHA, head SHA, diff
/// options and renderer version. Commits are content addressed, so an object
/// under this key never changes and never needs a TTL.
/// </summary>
public static class SourceDiffObjectOptions
{
    /// <summary>Renderer version of the review material body. Bump on any output change.</summary>
    public const int RendererVersion = 1;
    public const int ReviewMaximumFiles = 200;
    public const int ReviewMaximumLines = 12_000;

    /// <summary>The options string of review material within the given budget.</summary>
    public static string Review(int maximumFiles, int maximumLines)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"merge-base;find-renames;unified=3;max-files={maximumFiles};max-lines={maximumLines}");

    public static string DefaultReview { get; } = Review(ReviewMaximumFiles, ReviewMaximumLines);
}

/// <summary>
/// Canonical content digest of a diff object body. The producer, the fallback
/// in Remote Review and the Task Server compute the same value, so a mismatch
/// proves that two computations of one key differ.
/// </summary>
public static class SourceDiffMaterialDigest
{
    public static string Compute(
        IReadOnlyList<string> changedFiles,
        IReadOnlyList<string> diffLines,
        int totalFiles,
        int totalDiffLines,
        int maximumFiles,
        int maximumLines)
    {
        var text = new StringBuilder();
        text.Append("files ").Append(totalFiles.ToString(CultureInfo.InvariantCulture))
            .Append(" lines ").Append(totalDiffLines.ToString(CultureInfo.InvariantCulture))
            .Append(" budget ").Append(maximumFiles.ToString(CultureInfo.InvariantCulture))
            .Append('/').Append(maximumLines.ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (var file in changedFiles) text.Append("F ").Append(file).Append('\n');
        foreach (var line in diffLines) text.Append("L ").Append(line).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))
            .ToLowerInvariant();
    }
}

public sealed record SourceProducerLeaseRequest(
    string ProducerId,
    int RequestedTtlSeconds = 300);

/// <summary>
/// The single-writer gate of one repository. Taking the lease from another
/// producer raises <see cref="Epoch"/>; a publish under a lower epoch is
/// rejected with <c>stale-source-producer</c>.
/// </summary>
public sealed record SourceProducerLeaseDto(
    string RepositoryId,
    string ProducerId,
    long Epoch,
    DateTime ExpiresAt);

public sealed record SourceDiffObjectPublishRequest(
    string ProducerId,
    long ProducerEpoch,
    string BaseSha,
    string HeadSha,
    string Options,
    int RendererVersion,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<string> DiffLines,
    int TotalFiles,
    int TotalDiffLines,
    int MaximumFiles,
    int MaximumLines,
    string ContentSha256);

public sealed record SourceDiffObjectDto(
    string RepositoryId,
    string BaseSha,
    string HeadSha,
    string Options,
    int RendererVersion,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<string> DiffLines,
    int TotalFiles,
    int TotalDiffLines,
    int MaximumFiles,
    int MaximumLines,
    string ContentSha256,
    long SizeBytes,
    string ProducerId,
    long ProducerEpoch,
    DateTime ProducedAt);

public sealed record SourcePathHistoryEntry(
    string Sha,
    DateTime? AuthoredAt,
    string Author,
    string Subject,
    string Body);

public sealed record SourcePathHistoryPublishRequest(
    string ProducerId,
    long ProducerEpoch,
    string HeadSha,
    string Path,
    IReadOnlyList<SourcePathHistoryEntry> Entries,
    bool Truncated);

public sealed record SourcePathHistoryDto(
    string RepositoryId,
    string HeadSha,
    string Path,
    IReadOnlyList<SourcePathHistoryEntry> Entries,
    bool Truncated,
    DateTime ProducedAt);

/// <summary>One run of consecutive final-file lines attributed to one commit.</summary>
public sealed record SourceBlameRange(int StartLine, int LineCount, string Sha);

public sealed record SourceBlameCommit(
    string Sha,
    string Author,
    DateTime? AuthoredAt,
    string Summary);

public sealed record SourceBlamePublishRequest(
    string ProducerId,
    long ProducerEpoch,
    string HeadSha,
    string Path,
    IReadOnlyList<SourceBlameRange> Ranges,
    IReadOnlyList<SourceBlameCommit> Commits);

public sealed record SourceBlameDto(
    string RepositoryId,
    string HeadSha,
    string Path,
    IReadOnlyList<SourceBlameRange> Ranges,
    IReadOnlyList<SourceBlameCommit> Commits,
    DateTime ProducedAt);

/// <summary>The head of a target branch as the producer observed it.</summary>
public sealed record SourceRefHead(string Ref, string Sha);

/// <summary>
/// Whether <see cref="Sha"/> is contained in <see cref="TargetRef"/> at
/// <see cref="RefHeadSha"/>. A positive fact survives every fast-forward; a
/// negative fact holds only while the target head is unchanged.
/// </summary>
public sealed record SourceReachabilityFact(
    string Sha,
    string TargetRef,
    bool Contained,
    string RefHeadSha);

public sealed record SourceReachabilityPublishRequest(
    string ProducerId,
    long ProducerEpoch,
    IReadOnlyList<SourceRefHead> Heads,
    IReadOnlyList<SourceReachabilityFact> Facts);

/// <summary>
/// Reachability answer for a card. <see cref="Facts"/> holds only current
/// facts; every requested pair without one is listed in <see cref="Missing"/>
/// and the reader shows it as warming.
/// </summary>
public sealed record SourceReachabilityView(
    string RepositoryId,
    IReadOnlyList<SourceRefHead> Heads,
    IReadOnlyList<SourceReachabilityFact> Facts,
    IReadOnlyList<SourceReachabilityPair> Missing);

public sealed record SourceReachabilityPair(string Sha, string TargetRef);

/// <summary>
/// A reader's request for a fact it did not find. Requests are deduplicated
/// by their content, so a repeated card open adds no new work.
/// </summary>
public sealed record SourceFactDemandRequest(
    string Kind,
    string Reason,
    string? BaseSha = null,
    string? HeadSha = null,
    string? Path = null,
    IReadOnlyList<string>? Shas = null,
    IReadOnlyList<string>? TargetRefs = null,
    string? FetchRef = null);

public sealed record SourceFactDemandDto(
    long DemandId,
    string RepositoryId,
    string Kind,
    string Reason,
    string? BaseSha,
    string? HeadSha,
    string? Path,
    IReadOnlyList<string> Shas,
    IReadOnlyList<string> TargetRefs,
    string? FetchRef,
    int Attempts,
    string? LastError,
    DateTime RequestedAt);

public sealed record SourceFactDemandCompleteRequest(
    string ProducerId,
    long ProducerEpoch,
    bool Succeeded,
    string? Error = null);

public static class SourceReviewMaterialSources
{
    /// <summary>The review read the producer's diff object from the Task Server.</summary>
    public const string Object = "object";
    /// <summary>The object was missing and the review ran a local <c>git diff</c>.</summary>
    public const string Fallback = "fallback";
}

public sealed record SourceReviewMaterialReadReport(
    string BaseSha,
    string HeadSha,
    string Source,
    long ElapsedMilliseconds,
    string? MissReason = null);

/// <summary>Review material read time over a window, for the AGT-3318 acceptance report.</summary>
public sealed record SourceReviewMaterialTimingDto(
    string? RepositoryId,
    DateTime SinceUtc,
    int Samples,
    int ObjectReads,
    int FallbackReads,
    long P50Milliseconds,
    long P95Milliseconds,
    long ObjectP95Milliseconds,
    long FallbackP95Milliseconds);
