using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentStudio.Git;

/// <summary>Stable reason codes a withheld candidate can carry beyond the
/// gate's own finding codes.</summary>
public static class CommitWithholdingReasons
{
    /// <summary>The candidate itself is clean; the gate refused the whole
    /// manifest because of another candidate or a manifest-level finding.</summary>
    public const string WithheldWithManifest = "withheld-with-manifest";
}

/// <summary>One file the commit candidate gate did not commit, plus why.</summary>
public sealed record WithheldCommitCandidate(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("size")] long Size);

/// <summary>
/// The durable record of a refused platform commit: which files the commit
/// would have contained, why the gate stopped, and where the repository is.
/// Written next to <c>task.json</c> as <c>commit-withheld.json</c> so it
/// survives lane moves, restarts, and the scanner cache exactly like the
/// parked-blocker marker.
/// </summary>
public sealed record CommitWithholdingReport
{
    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;

    /// <summary>Gate operation that was refused (<c>auto-commit</c>,
    /// <c>worktree-run</c>, ...).</summary>
    [JsonPropertyName("operation")]
    public string Operation { get; init; } = "";

    /// <summary><c>warn</c> or <c>block</c>; an <c>allow</c> gate never
    /// produces a report.</summary>
    [JsonPropertyName("decision")]
    public string Decision { get; init; } = "";

    [JsonPropertyName("taskId")]
    public string? TaskId { get; init; }

    /// <summary>The repository the candidates are still dirty in. The operator
    /// action commits there, not in whatever checkout it resolves today.</summary>
    [JsonPropertyName("repositoryRoot")]
    public string RepositoryRoot { get; init; } = "";

    [JsonPropertyName("branch")]
    public string? Branch { get; init; }

    [JsonPropertyName("inspectedAtUtc")]
    public DateTime InspectedAtUtc { get; init; }

    /// <summary>Every dirty path the gate inspected, included or not.</summary>
    [JsonPropertyName("candidateCount")]
    public int CandidateCount { get; init; }

    [JsonPropertyName("withheld")]
    public IReadOnlyList<WithheldCommitCandidate> Withheld { get; init; } = [];

    /// <summary>Distinct finding codes behind the refusal, for a one-line
    /// operator summary without re-reading the gate evidence.</summary>
    [JsonPropertyName("findingCodes")]
    public IReadOnlyList<string> FindingCodes { get; init; } = [];

    /// <summary>True when at least one finding is a hard block (a secret, an
    /// unreadable candidate, a policy refusal). A blocked report is never
    /// clearable by explicit review alone.</summary>
    [JsonPropertyName("blocked")]
    public bool Blocked { get; init; }

    [JsonPropertyName("evidencePath")]
    public string? EvidencePath { get; init; }

    public int WithheldCount => Withheld.Count;
}

/// <summary>
/// Pure policy over a <see cref="CommitGateResult"/>: what did the gate
/// withhold, and how is a parked card supposed to say so?
///
/// <para>Before this policy existed a refused gate returned a one-line error
/// that the auto-commit path logged and dropped. WEB-21 was then parked with an
/// empty reason while a complete delivery sat uncommitted in its worktree, and
/// the card could not tell an operator that anything was waiting. The report is
/// that missing sentence, in a form the park marker and an operator action can
/// both read.</para>
/// </summary>
public static class CommitWithholdingPolicy
{
    /// <summary>
    /// Builds the report for a gate result, or null when nothing was withheld.
    /// A gate that can commit withholds nothing: its policy exclusions (root
    /// scratch, credential homes, out-of-scope paths) are deliberate and stay
    /// excluded, so reporting them would bury the one case that matters.
    /// </summary>
    public static CommitWithholdingReport? From(CommitGateResult? gate)
    {
        if (gate is null || gate.CanCommit) return null;

        // What the commit WOULD have contained. Excluded candidates were never
        // going to be committed, so they are not being withheld from anyone.
        var intended = gate.Candidates.Where(c => c.Included).ToArray();
        if (intended.Length == 0) return null;

        var withheld = intended
            .Select(c => new WithheldCommitCandidate(c.Path, ReasonFor(gate, c.Path), c.Size))
            .ToArray();

        return new CommitWithholdingReport
        {
            Operation = gate.Provenance.Operation,
            Decision = gate.Decision,
            TaskId = gate.Provenance.TaskId,
            RepositoryRoot = gate.Provenance.RepositoryRoot,
            Branch = gate.Provenance.Branch,
            InspectedAtUtc = gate.Provenance.InspectedAtUtc,
            CandidateCount = gate.Candidates.Count,
            Withheld = withheld,
            FindingCodes = gate.Findings
                .Where(f => f.Severity != CommitGateSeverities.Notice)
                .Select(f => f.Code)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(c => c, StringComparer.Ordinal)
                .ToArray(),
            Blocked = gate.Findings.Any(f => f.Severity == CommitGateSeverities.Block),
            EvidencePath = gate.EvidencePath,
        };
    }

    /// <summary>
    /// The sentence a parked card carries so the lane says what is waiting.
    /// It names the gate, the decision, the count, and the finding codes, which
    /// is exactly what WEB-21's empty parked reason failed to say.
    /// </summary>
    public static string ParkReason(CommitWithholdingReport report)
    {
        var codes = report.FindingCodes.Count > 0
            ? string.Join(", ", report.FindingCodes)
            : "no finding code recorded";
        var files = report.WithheldCount == 1 ? "file" : "files";
        return $"The commit candidate gate ({report.Decision}) withheld "
             + $"{report.WithheldCount} of {report.CandidateCount} candidate {files} "
             + $"from the {report.Operation} commit ({codes}); the work is still "
             + "uncommitted in the task worktree.";
    }

    /// <summary>
    /// Markdown block listing which files were withheld and why, for the
    /// card's status document. Capped so a runaway dirty tree cannot turn a
    /// status stub into a file listing.
    /// </summary>
    public static string StatusSection(CommitWithholdingReport report, int maxRows = 20)
    {
        var nl = Environment.NewLine;
        var sb = new System.Text.StringBuilder();
        sb.Append("- Withheld by the commit candidate gate: ")
          .Append(report.WithheldCount).Append(nl);
        foreach (var candidate in report.Withheld.Take(maxRows))
            sb.Append("  - `").Append(candidate.Path).Append("` (")
              .Append(candidate.Reason).Append(')').Append(nl);
        if (report.WithheldCount > maxRows)
            sb.Append("  - and ").Append(report.WithheldCount - maxRows)
              .Append(" more.").Append(nl);
        return sb.ToString();
    }

    /// <summary>
    /// The most severe finding recorded for a path, falling back to the
    /// manifest-level refusal. Manifest-level findings are recorded against
    /// <c>.</c>, so a clean candidate still gets an honest reason instead of an
    /// empty string.
    /// </summary>
    private static string ReasonFor(CommitGateResult gate, string path)
    {
        var forPath = gate.Findings
            .Where(f => string.Equals(f.Path, path, StringComparison.Ordinal)
                        && f.Severity != CommitGateSeverities.Notice)
            .ToArray();
        var block = forPath.FirstOrDefault(f => f.Severity == CommitGateSeverities.Block);
        if (block is not null) return block.Code;
        var warning = forPath.FirstOrDefault(f => f.Severity == CommitGateSeverities.Warning);
        if (warning is not null) return warning.Code;

        var manifest = gate.Findings.FirstOrDefault(f =>
            f.Severity == CommitGateSeverities.Block && f.Path == ".");
        return manifest?.Code ?? CommitWithholdingReasons.WithheldWithManifest;
    }
}

/// <summary>
/// Durable sidecar for <see cref="CommitWithholdingReport"/>, written next to
/// <c>task.json</c> in the job folder so it moves with the card through every
/// lane transition.
/// </summary>
public static class CommitWithholdingMarker
{
    public const string FileName = "commit-withheld.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static void Write(string jobFolder, CommitWithholdingReport report, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(jobFolder)) return;
        try
        {
            Directory.CreateDirectory(jobFolder);
            var path = Path.Combine(jobFolder, FileName);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(report, Options));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to persist commit-withheld marker in {Folder}", jobFolder);
        }
    }

    public static CommitWithholdingReport? TryRead(string jobFolder, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(jobFolder)) return null;
        try
        {
            var path = Path.Combine(jobFolder, FileName);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<CommitWithholdingReport>(File.ReadAllText(path), Options);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to read commit-withheld marker in {Folder}", jobFolder);
            return null;
        }
    }

    public static void Clear(string jobFolder, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(jobFolder)) return;
        try
        {
            var path = Path.Combine(jobFolder, FileName);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to clear commit-withheld marker in {Folder}", jobFolder);
        }
    }
}
