using System.Text.Json;
using AgentStudio.Projects;
using AgentStudio.TaskServer.Contracts;

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
        if (record is null || declaration is null || record.Release.ProjectId != project
            || record.Release.CreatedAtUtc > job.CreatedAt
            || !record.Release.ArtifactDigests.Contains(job.Artifact, StringComparer.Ordinal)
            || ProjectEnvironmentService.ArtifactTemplate(job.Template, job.SshSteps) is not { } artifactTemplate
            || registry.VerifiedForDeployment(project, record.Release.CommitSha, job.Artifact, artifactTemplate) is null)
            return null;
        var tenant = record.Tenants.FirstOrDefault(item => item.Name == job.Tenant);
        if (job.Tenant is not null && (tenant is null
            || !declaration.TenantTargets.ContainsKey(job.Tenant))) return null;
        var expected = job.Tenant is null ? declaration
            : ProjectEnvironmentService.RouteTenant(declaration, job.Tenant,
                declaration.TenantTargets[job.Tenant]);
        var environment = declaration.Kind == "stage" ? "staging" : declaration.Kind;
        var variables = record.Variables.Select(item => item.Name).Distinct(StringComparer.Ordinal)
            .Select(name => ProjectVariableResolution.Resolve(record.Variables, name, environment,
                tenant, record.Release.Channel)).Where(item => item is not null).ToArray();
        var plain = variables.Where(item => item!.Value is not null)
            .ToDictionary(item => item!.Name, item => item!.Value!, StringComparer.Ordinal);
        var secrets = variables.Where(item => item!.Secret is not null)
            .ToDictionary(item => item!.Name, item => item!.Secret!, StringComparer.Ordinal);
        var expectedSecretRefs = HostedDataBinding.DeclaredSecretNames(project, expected.Id,
            expected.SecretRefs, expected.Data).Concat(secrets.Values).ToHashSet(StringComparer.Ordinal);
        if (expected.Template != job.Template || expected.Destination != job.Destination
            || expected.HealthUrl != job.HealthUrl || expected.ContainerPort != job.ContainerPort
            || JsonSerializer.Serialize(expected.Data) != JsonSerializer.Serialize(job.Data)
            || JsonSerializer.Serialize(expected.SshSteps) != JsonSerializer.Serialize(job.SshSteps)
            || !SameVariables(plain, job.ResolvedVariables)
            || !SameVariables(secrets, job.SecretVariableRefs)
            || !expectedSecretRefs.SetEquals(job.SecretRefs)) return null;
        return job with { ReleaseId = releaseId };
    }

    private static bool SameVariables(IReadOnlyDictionary<string, string> expected,
        IReadOnlyDictionary<string, string> actual)
        => expected.Count == actual.Count && expected.All(item =>
            actual.TryGetValue(item.Key, out var value) && value == item.Value);

    private static string? AutomaticRollbackRelease(DeployJob rollback, IReadOnlyList<DeployJob> jobs,
        IReadOnlyList<DeploymentRecord> runs)
    {
        var index = jobs.ToList().FindIndex(item => item.Id == rollback.Id);
        if (index < 0) return null;
        var earlier = jobs.Take(index).ToArray();
        // The failed job id is persisted with the rollback before either job
        // can be observed by a restarted process. Clock equality is not evidence.
        var failure = earlier.SingleOrDefault(item => item.Id == rollback.RollbackOfJobId);
        if (failure is null || failure.EnvironmentId != rollback.EnvironmentId
            || failure.Status != "failed" || failure.IsRollback
            || failure.PreviousArtifact != rollback.Artifact) return null;
        var source = earlier.TakeWhile(item => item.Id != failure.Id).LastOrDefault(item => item.EnvironmentId == rollback.EnvironmentId
            && item.Artifact == rollback.Artifact && item.Status == "healthy");
        if (source is null) return null;
        var linked = runs.Where(run => run.JobId == source.Id).Select(run => run.ReleaseId)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (linked.Length != 1 || source.ReleaseId is not null && source.ReleaseId != linked[0]) return null;
        return linked[0];
    }
}
