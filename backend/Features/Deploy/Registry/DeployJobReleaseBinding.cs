using AgentStudio.Projects;

namespace AgentStudio.Deploy;

/// <summary>Recovers old queue jobs from persisted job-to-run evidence, never from a digest search.</summary>
public static class DeployJobReleaseBinding
{
    public static DeployJob? Verify(string project, DeployJob job, IReadOnlyList<DeployJob> jobs,
        IReadOnlyList<ReleaseRecord> releases, IReadOnlyList<DeploymentRecord> runs,
        ReleaseArtifactRegistry registry)
    {
        var linkedRuns = runs.Where(run => run.JobId == job.Id).ToArray();
        if (linkedRuns.Any(run => run.EnvironmentId != job.EnvironmentId)) return null;
        var linked = linkedRuns.Select(run => run.ReleaseId)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (linked.Length > 1 || job.ReleaseId is not null && linked.Length == 1 && job.ReleaseId != linked[0])
            return null;
        var releaseId = job.ReleaseId ?? linked.SingleOrDefault();
        if (job.IsRollback && job.RequestedBy == "automatic-rollback")
        {
            var sourceReleaseId = AutomaticRollbackRelease(job, jobs, runs);
            if (sourceReleaseId is null || releaseId is not null && releaseId != sourceReleaseId) return null;
            releaseId = sourceReleaseId;
        }
        if (releaseId is null) return null;

        var record = releases.SingleOrDefault(item => item.Release.Id == releaseId);
        var declaration = record?.Environments.SingleOrDefault(item => item.Id == job.EnvironmentId);
        if (record is null || record.Release.ProjectId != project || record.Release.CreatedAtUtc > job.CreatedAt
            || !record.Release.ArtifactDigests.Contains(job.Artifact, StringComparer.Ordinal)
            || declaration is null || declaration.Template != job.Template
            || declaration.Destination != job.Destination || declaration.HealthUrl != job.HealthUrl
            || declaration.ContainerPort != job.ContainerPort
            || !declaration.SecretRefs.SequenceEqual(job.SecretRefs, StringComparer.Ordinal)
            || registry.VerifiedForDeployment(project, record.Release.CommitSha, job.Artifact, job.Template) is null)
            return null;
        return job with { ReleaseId = releaseId };
    }

    private static string? AutomaticRollbackRelease(DeployJob rollback, IReadOnlyList<DeployJob> jobs,
        IReadOnlyList<DeploymentRecord> runs)
    {
        var index = jobs.ToList().FindIndex(item => item.Id == rollback.Id);
        if (index < 0) return null;
        var earlier = jobs.Take(index).ToArray();
        // Report queues this rollback at the failed job's finish time, using
        // the last healthy job for the same environment and previous digest.
        var failures = earlier.Where(item => item.EnvironmentId == rollback.EnvironmentId
            && item.Status == "failed" && !item.IsRollback && item.PreviousArtifact == rollback.Artifact
            && item.FinishedAt == rollback.CreatedAt).ToArray();
        if (failures.Length != 1) return null;
        var source = earlier.LastOrDefault(item => item.EnvironmentId == rollback.EnvironmentId
            && item.Artifact == rollback.Artifact && item.Status == "healthy");
        if (source is null) return null;
        var linked = runs.Where(run => run.JobId == source.Id).Select(run => run.ReleaseId)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (linked.Length != 1 || source.ReleaseId is not null && source.ReleaseId != linked[0]) return null;
        return linked[0];
    }
}
