using AgentStudio.Secrets;

namespace AgentStudio.Deploy;

/// <summary>
/// F14: the secret binding check, asked before a run is queued. A host claim
/// checks the same bindings again (<c>CompleteClaimAsync</c>) and fails the job
/// with <c>secret-reference-missing</c>; this answer shows the gap on the
/// release, the environment cards and the approval drawer first, and lets a
/// Production approval refuse it.
/// </summary>
public sealed class DeploySecretReadiness(DeployService deploy, ProjectSecretBindings bindings)
{
    /// <summary>The 409 reason of an approval refused for missing secret bindings.</summary>
    public const string MissingReason = "secret-bindings-missing";

    /// <summary>Readiness per lifecycle environment for the release, or the newest release; null when unknown.</summary>
    public async Task<SecretReadinessResponse?> ReadAsync(string project, string? releaseId, CancellationToken ct = default)
    {
        var demands = deploy.SecretDemand(project, releaseId);
        if (demands is null) return null;
        var environments = new List<EnvironmentSecretReadiness>();
        foreach (var demand in demands) environments.Add(await CheckAsync(project, demand, ct));
        return new(project, releaseId, environments);
    }

    public async Task<EnvironmentSecretReadiness> CheckAsync(string project, EnvironmentSecretDemand demand,
        CancellationToken ct = default)
    {
        var environment = demand.Environment;
        IReadOnlyList<MissingSecretReference> missing;
        try
        {
            missing = demand.Names.Count == 0 ? []
                : (await bindings.CheckAsync(project, environment.Id, demand.Names, demand.DeclarationKind, ct)).Missing;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The host claim fails closed in the same case; so does the preflight.
            missing = demand.Names.Select(name => new MissingSecretReference(name,
                "secret://" + project + "/" + environment.Id + "/" + name, project + "/" + environment.Id,
                "preflight-unavailable")).ToArray();
        }
        return new(environment.Id, environment.Name, environment.Kind, demand.ReleaseId,
            StandardLifecyclePolicy.RequiresApproval(environment),
            missing.Select(item => new SecretReadinessItem(item.Name, item.Reference, item.Reason,
                item.Reason is "not-bound" or "approval-required"
                && demand.Declared.Contains(item.Name, StringComparer.Ordinal))).ToArray());
    }

    /// <summary>
    /// The secrets an approval would queue a Production run without. Empty for
    /// an environment that needs no approval or a run that no longer resolves.
    /// </summary>
    public async Task<IReadOnlyList<SecretReadinessItem>> ApprovalGapAsync(string project,
        EnvironmentSecretDemand? demand, CancellationToken ct = default)
    {
        if (demand is null || !StandardLifecyclePolicy.RequiresApproval(demand.Environment)) return [];
        return (await CheckAsync(project, demand, ct)).Missing;
    }
}
