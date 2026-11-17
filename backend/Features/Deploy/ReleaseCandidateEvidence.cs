using AgentStudio.Pipeline;
using AgentStudio.Projects;
using AgentStudio.Shared;
using AgentStudio.Tasks;

namespace AgentStudio.Deploy;

/// <summary>What made a release candidate green, as named in the gate result.</summary>
public static class ReleaseCandidateEvidenceSources
{
    public const string PostMerge = "post-merge";
    public const string PreMainGate = "pre-main-gate";
    public const string BatchGate = "batch-gate";
    /// <summary>Only <c>optional</c>: the project has no post-merge run at all.</summary>
    public const string NoPostMergeHistory = "no-post-merge-history";
}

/// <summary>
/// Integration gate evidence for one exact candidate SHA (F2). A project that
/// is not in post-merge mode never gets a post-merge verdict; its full suite
/// ran in the pre-main gate or the batch gate. That verdict counts only for
/// the very SHA it tested: a pre-main receipt whose expected and tested SHA
/// are the candidate, or a batch publication whose verified remote SHA is the
/// candidate. A docs-only light gate is Skipped, not Ok, so it never counts.
/// </summary>
public sealed class ReleaseCandidateEvidence(TaskScannerService scanner, BatchGateStore batches,
    PostMergeModeService postMergeMode, ProjectSettingsService settings)
{
    public const string PreMainReceiptPrefix = "pre-main-test-gate";

    public bool InPostMergeMode(ProjectRecord record)
        => postMergeMode.Resolve(record.DisplayName, settings.Get(record.DisplayName))
            == IntegrationTestModes.PostMerge;

    public string? IntegrationGateGreen(ProjectRecord record, string commit)
    {
        bool Same(string? project) => string.Equals(project, record.DisplayName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(project, record.Id, StringComparison.OrdinalIgnoreCase);
        var folders = scanner.ScanAllJobsWithArchive().Where(job => Same(job.ProjectName))
            .Select(job => job.FolderPath);
        if (PreMainGateGreen(folders, commit)) return ReleaseCandidateEvidenceSources.PreMainGate;
        var published = batches.ListManifests().Where(manifest => Same(manifest.Scope.Project))
            .Select(manifest => batches.ReadPublication(manifest.BatchId));
        return BatchGateGreen(published, commit) ? ReleaseCandidateEvidenceSources.BatchGate : null;
    }

    internal static bool PreMainGateGreen(IEnumerable<string> jobFolders, string commit)
        => jobFolders.Where(folder => !string.IsNullOrWhiteSpace(folder)).Any(folder =>
            IntegrationGateReceipts.ReadExact(folder, PreMainReceiptPrefix, commit)?.Verdict
                == BuildTestGateVerdict.Ok);

    /// <summary>A publication is written only for a passing run on its candidate (BatchGateStore.RecordPublication).</summary>
    internal static bool BatchGateGreen(IEnumerable<BatchGatePublication?> publications, string commit)
        => publications.Any(publication => publication is not null
            && string.Equals(publication.VerifiedRemoteSha, commit, StringComparison.OrdinalIgnoreCase)
            && string.Equals(publication.TestedCandidateSha, commit, StringComparison.OrdinalIgnoreCase));
}
