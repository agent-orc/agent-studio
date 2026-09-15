using System.Text;
using System.Text.Json.Serialization;

namespace AgentStudio.Git;

/// <summary>One candidate the commit candidate gate did not commit, and why.</summary>
public sealed record WithheldCommitCandidate
{
    [JsonPropertyName("path")]
    public string Path { get; init; } = "";

    /// <summary>Comma-joined gate finding codes, e.g. <c>binary-surprise</c>.</summary>
    [JsonPropertyName("reason")]
    public string Reason { get; init; } = "";

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; init; }

    [JsonPropertyName("binary")]
    public bool Binary { get; init; }
}

/// <summary>
/// The durable "a complete delivery is waiting uncommitted" marker, written next
/// to <c>task.json</c> in the job folder so it survives the lane move that parks
/// the card.
/// </summary>
public sealed record WithheldCommitCandidateRecord
{
    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;

    /// <summary>The gate decision that produced this record (<c>warn</c> / <c>block</c>).</summary>
    [JsonPropertyName("decision")]
    public string Decision { get; init; } = "";

    /// <summary>The gate operation, e.g. <c>worktree-run</c>.</summary>
    [JsonPropertyName("operation")]
    public string Operation { get; init; } = "";

    [JsonPropertyName("taskId")]
    public string? TaskId { get; init; }

    [JsonPropertyName("branch")]
    public string? Branch { get; init; }

    [JsonPropertyName("repositoryRoot")]
    public string RepositoryRoot { get; init; } = "";

    [JsonPropertyName("inspectedAtUtc")]
    public DateTime InspectedAtUtc { get; init; }

    /// <summary>True when the gate committed nothing at all, so the whole
    /// delivery is still sitting in the worktree.</summary>
    [JsonPropertyName("nothingCommitted")]
    public bool NothingCommitted { get; init; }

    [JsonPropertyName("candidates")]
    public IReadOnlyList<WithheldCommitCandidate> Candidates { get; init; } = [];

    /// <summary>Path of the gate evidence json, when the gate persisted one.</summary>
    [JsonPropertyName("evidencePath")]
    public string? EvidencePath { get; init; }

    [JsonIgnore]
    public int Count => Candidates.Count;
}

/// <summary>
/// AGT-2828: pure translation from a commit gate result into the two things an
/// operator needs on a parked card - WHICH files were withheld and WHY, and a
/// park reason that names the gate and the count.
///
/// <para>WEB-21 was parked with cause <c>no-completion-signal</c> and an empty
/// parked reason while a finished delivery (12 screenshots plus two edited
/// files) sat uncommitted in the worktree. The card carried no hint of that. The
/// gate already knew everything needed to say so; nothing translated it into the
/// card's own vocabulary. This does.</para>
/// </summary>
public static class WithheldCommitCandidatePolicy
{
    /// <summary>Repository-scoped findings are recorded against this path.</summary>
    private const string RepositoryScope = ".";

    /// <summary>Longest file list rendered inline before the tail is summarized.</summary>
    public const int MaxRenderedPaths = 20;

    /// <summary>
    /// Projects the gate result onto the withheld set. Returns null when nothing
    /// was withheld, so the caller can clear a stale marker rather than persist
    /// an empty one.
    /// </summary>
    public static WithheldCommitCandidateRecord? Describe(CommitGateResult? gate)
    {
        if (gate is null || gate.Candidates.Count == 0) return null;

        var codesByPath = gate.Findings
            .Where(finding => finding.Severity != CommitGateSeverities.Info)
            .GroupBy(finding => finding.Path, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(finding => finding.Code).Distinct(StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);
        var repositoryCodes = codesByPath.TryGetValue(RepositoryScope, out var scoped) ? scoped : [];

        var withheld = new List<WithheldCommitCandidate>();
        foreach (var candidate in gate.Candidates)
        {
            // A candidate the gate is about to commit is not withheld. When the
            // gate cannot commit at all, every candidate stays behind, included
            // flag or not.
            if (gate.CanCommit && candidate.Included) continue;
            withheld.Add(new WithheldCommitCandidate
            {
                Path = candidate.Path,
                Reason = ReasonFor(candidate, codesByPath, repositoryCodes, gate.Decision),
                SizeBytes = candidate.Size,
                Binary = candidate.Binary,
            });
        }
        if (withheld.Count == 0) return null;

        return new WithheldCommitCandidateRecord
        {
            Decision = gate.Decision,
            Operation = gate.Provenance.Operation,
            TaskId = gate.Provenance.TaskId,
            Branch = gate.Provenance.Branch,
            RepositoryRoot = gate.Provenance.RepositoryRoot,
            InspectedAtUtc = gate.Provenance.InspectedAtUtc,
            NothingCommitted = !gate.CanCommit,
            Candidates = withheld,
            EvidencePath = gate.EvidencePath,
        };
    }

    /// <summary>
    /// What is still waiting after <paramref name="committedPaths"/> landed.
    /// Returns null once the set is empty, so a full commit clears the marker
    /// and a reviewed SUBSET leaves the rest of the delivery visible instead of
    /// silently dropping it off the card.
    /// </summary>
    public static WithheldCommitCandidateRecord? Remaining(
        WithheldCommitCandidateRecord? record,
        IReadOnlyCollection<string>? committedPaths)
    {
        if (record is null || record.Count == 0) return null;
        var landed = (committedPaths ?? []).ToHashSet(StringComparer.Ordinal);
        var remaining = record.Candidates.Where(c => !landed.Contains(c.Path)).ToArray();
        if (remaining.Length == 0) return null;
        // Part of the delivery is now on the branch, so the "nothing was
        // committed" claim no longer holds even if it did a moment ago.
        return record with
        {
            Candidates = remaining,
            NothingCommitted = record.NothingCommitted && remaining.Length == record.Count,
        };
    }

    /// <summary>
    /// Appends the gate sentence to a park reason. The sentence names the gate
    /// and the count, because "no-completion-signal" with an empty reason is
    /// exactly the card WEB-21 produced: correct, and useless to an operator.
    /// </summary>
    public static string ComposeParkReason(string? reason, WithheldCommitCandidateRecord? record)
    {
        var baseReason = (reason ?? string.Empty).Trim();
        if (record is null || record.Count == 0) return baseReason;
        var sentence = record.NothingCommitted
            ? $"The commit candidate gate ({record.Decision}) withheld {record.Count} file(s) and committed nothing; the delivery is still uncommitted in the task worktree."
            : $"The commit candidate gate ({record.Decision}) withheld {record.Count} file(s) from the commit.";
        return baseReason.Length == 0 ? sentence : $"{baseReason} {sentence}";
    }

    /// <summary>
    /// Markdown block listing which files were withheld and why, plus the
    /// operator action that commits them. Safe to append to the escalation
    /// status stub: it introduces no second <c>- Category:</c> / <c>- Reason:</c>
    /// line, which <c>parseStatusStubEscalation</c> lifts back out.
    /// </summary>
    public static string BuildDetail(WithheldCommitCandidateRecord? record, string? jobId = null)
    {
        if (record is null || record.Count == 0) return string.Empty;
        var nl = Environment.NewLine;
        var sb = new StringBuilder();
        sb.Append("- Withheld by the commit candidate gate (").Append(record.Decision).Append("): ")
            .Append(record.Count).Append(" file(s).").Append(nl);
        foreach (var candidate in record.Candidates.Take(MaxRenderedPaths))
            sb.Append("  - `").Append(candidate.Path).Append("` - ").Append(candidate.Reason).Append(nl);
        if (record.Count > MaxRenderedPaths)
        {
            sb.Append("  - ... and ").Append(record.Count - MaxRenderedPaths)
                .Append(" more; the full list is in `")
                .Append(WithheldCommitCandidateStore.FileName).Append("` in this folder.").Append(nl);
        }
        var id = string.IsNullOrWhiteSpace(jobId) ? record.TaskId : jobId;
        sb.Append("- Operator action: `POST /api/tasks/")
            .Append(string.IsNullOrWhiteSpace(id) ? "{jobId}" : id)
            .Append("/git/withheld-candidates/commit` commits them after review.").Append(nl);
        return sb.ToString();
    }

    private static string ReasonFor(
        CommitCandidateManifestEntry candidate,
        IReadOnlyDictionary<string, string[]> codesByPath,
        IReadOnlyList<string> repositoryCodes,
        string decision)
    {
        var codes = new List<string>();
        if (!string.IsNullOrWhiteSpace(candidate.ExclusionReason)) codes.Add(candidate.ExclusionReason!);
        if (codesByPath.TryGetValue(candidate.Path, out var own)) codes.AddRange(own);
        if (codes.Count == 0) codes.AddRange(repositoryCodes);
        if (codes.Count == 0) codes.Add($"gate-{decision}");
        return string.Join(", ", codes.Distinct(StringComparer.Ordinal));
    }
}
