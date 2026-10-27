using AgentStudio.TaskServer.Contracts;

namespace AgentRunner.SourceFacts;

/// <summary>
/// The diff object of AGT-3017 D1 for one (base, head) pair: the changed-file
/// list and the unified diff, each cut at the review budget while both Git
/// streams are counted in full. The fenced producer and the Remote Review
/// fallback both call <see cref="ComputeAsync"/>, so one base and head yield
/// byte-identical material whichever path produced it (AGT-3318).
/// </summary>
public sealed record MergeBaseDiffMaterial(
    string BaseSha,
    string HeadSha,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<string> DiffLines,
    int TotalFiles,
    int TotalDiffLines,
    int MaximumFiles,
    int MaximumLines)
{
    public bool Truncated => TotalFiles > MaximumFiles || TotalDiffLines > MaximumLines;

    public string Options => SourceDiffObjectOptions.Review(MaximumFiles, MaximumLines);

    public string ContentSha256 => SourceDiffMaterialDigest.Compute(
        ChangedFiles, DiffLines, TotalFiles, TotalDiffLines, MaximumFiles, MaximumLines);

    /// <summary>
    /// Settings pinned on every diff invocation. Host Git configuration such as
    /// <c>diff.noprefix</c>, <c>diff.mnemonicPrefix</c> or colour would change
    /// the bytes between the producer host and the review host.
    /// </summary>
    internal static readonly string[] PinnedConfiguration =
    [
        "-c", "credential.helper=",
        "-c", "core.quotePath=false",
        "-c", "diff.noprefix=false",
        "-c", "diff.mnemonicPrefix=false",
        "-c", "diff.relative=false",
        "-c", "diff.algorithm=myers",
        "-c", "diff.indentHeuristic=true",
        "-c", "color.ui=never",
    ];

    /// <summary>
    /// Computes the material in <paramref name="repositoryPath"/>, which must
    /// contain both commits. <c>--full-index</c> keeps the index lines free of
    /// abbreviated object names, whose length depends on the object count of
    /// the repository that runs the diff.
    /// </summary>
    public static async Task<MergeBaseDiffMaterial> ComputeAsync(
        string repositoryPath,
        string baseSha,
        string headSha,
        int maximumFiles,
        int maximumLines,
        IReadOnlyDictionary<string, string?>? environment,
        bool clearEnvironment,
        CancellationToken ct)
    {
        if (maximumFiles <= 0) throw new ArgumentOutOfRangeException(nameof(maximumFiles));
        if (maximumLines <= 0) throw new ArgumentOutOfRangeException(nameof(maximumLines));

        var changedFiles = new List<string>(Math.Min(maximumFiles, 256));
        var totalFiles = 0;
        var names = await ProcessRunner.RunAsync(
            "git",
            [.. PinnedConfiguration, "diff", "--name-only", "--find-renames", "--no-color", baseSha, headSha],
            repositoryPath,
            onStdOut: line =>
            {
                if (string.IsNullOrEmpty(line)) return;
                totalFiles++;
                if (changedFiles.Count < maximumFiles) changedFiles.Add(line);
            },
            environment: environment,
            clearEnvironment: clearEnvironment,
            ct: ct);
        if (!names.Success)
            throw new MergeBaseDiffUnavailableException(
                $"Changed-file enumeration failed against merge-base '{baseSha}': {names.StdErr.Trim()}");

        var diffLines = new List<string>(Math.Min(maximumLines, 16_384));
        var totalDiffLines = 0;
        var currentFile = 0;
        var diff = await ProcessRunner.RunAsync(
            "git",
            [
                .. PinnedConfiguration,
                "diff", "--no-ext-diff", "--no-textconv", "--no-color", "--full-index",
                "--find-renames", "--unified=3", "--inter-hunk-context=0",
                baseSha, headSha,
            ],
            repositoryPath,
            onStdOut: line =>
            {
                totalDiffLines++;
                if (line.StartsWith("diff --git ", StringComparison.Ordinal)) currentFile++;
                if (currentFile <= maximumFiles && diffLines.Count < maximumLines)
                    diffLines.Add(line);
            },
            environment: environment,
            clearEnvironment: clearEnvironment,
            ct: ct);
        if (!diff.Success)
            throw new MergeBaseDiffUnavailableException(
                $"Unified diff failed against merge-base '{baseSha}': {diff.StdErr.Trim()}");

        return new MergeBaseDiffMaterial(
            baseSha, headSha, changedFiles, diffLines, totalFiles, totalDiffLines, maximumFiles, maximumLines);
    }

    public static MergeBaseDiffMaterial FromObject(SourceDiffObjectDto stored)
        => new(stored.BaseSha, stored.HeadSha, stored.ChangedFiles, stored.DiffLines, stored.TotalFiles,
            stored.TotalDiffLines, stored.MaximumFiles, stored.MaximumLines);

    public SourceDiffObjectPublishRequest ToPublishRequest(string producerId, long producerEpoch)
        => new(producerId, producerEpoch, BaseSha, HeadSha, Options, SourceDiffObjectOptions.RendererVersion,
            ChangedFiles, DiffLines, TotalFiles, TotalDiffLines, MaximumFiles, MaximumLines, ContentSha256);
}

public sealed class MergeBaseDiffUnavailableException(string message) : Exception(message);
