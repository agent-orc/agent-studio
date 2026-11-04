using System.Text.Json;
using Microsoft.AspNetCore.Routing;

namespace AgentStudio.Security;

/// <summary>
/// Whether a commit carries full gate evidence (decision D2), and which commit
/// a release carries. A missing answer reads as no evidence, so the request
/// becomes a gate bypass (T2) and fails closed.
/// </summary>
public interface IOperatorGateEvidence
{
    bool HasFullGateEvidence(string project, string commit);

    /// <summary>The commit of the release with <paramref name="releaseId"/>, or of the release carrying <paramref name="artifact"/>.</summary>
    string? ReleaseCommit(string project, string? releaseId, string? artifact);
}

/// <summary>
/// Full gate evidence is a green post-merge run on exactly this commit in
/// which every configured command passed without a timeout: the gate proved
/// the tree the commit names.
/// </summary>
public sealed class PostMergeGateEvidence(
    AgentStudio.Registry.ProjectRegistry registry,
    AgentStudio.Pipeline.PostMergeRunStore postMerge,
    AgentStudio.Deploy.DeployService deploy) : IOperatorGateEvidence
{
    public bool HasFullGateEvidence(string project, string commit)
    {
        var record = registry.FindByIdOrDisplayName(project) ?? registry.FindByShortCode(project);
        if (record is null || string.IsNullOrWhiteSpace(commit)) return false;
        return postMerge.Read(record.Id).Runs.Any(run => run.Status == "green"
            && string.Equals(run.TipSha, commit.Trim(), StringComparison.OrdinalIgnoreCase)
            && run.Verdicts.Count > 0 && run.Verdicts.All(verdict => verdict.ExitCode == 0 && !verdict.TimedOut));
    }

    public string? ReleaseCommit(string project, string? releaseId, string? artifact)
        => deploy.Releases(project)?.FirstOrDefault(release => releaseId is not null
                ? release.Id == releaseId
                : artifact is not null && release.ArtifactDigests.Contains(artifact, StringComparer.Ordinal))
            ?.CommitSha;
}

/// <summary>
/// Resolves one operator session request to its catalogued operation
/// (<see cref="OperatorOperationCatalog"/>): reads are observe, a write is
/// what its route rule names once the refinement read the request body, and
/// anything else is not catalogued (<c>null</c>).
/// </summary>
public static class OperatorRequestClassifier
{
    /// <summary>Bodies the refinements read are small; a larger body reads as carrying no facts.</summary>
    private const long MaxFactBodyBytes = 64 * 1024;

    /// <param name="routeValue">A route value of the matched endpoint by name, as the access middleware resolves it.</param>
    public static async Task<OperatorOperation?> ClassifyAsync(
        HttpContext context, bool safeMethod, IOperatorGateEvidence? evidence,
        Func<string, string, string?> environmentKind, Func<string, string?> routeValue)
    {
        if (safeMethod) return OperatorOperationCatalog.Find(OperatorOperationIds.Observe);
        if (context.GetEndpoint() is not RouteEndpoint endpoint) return null;
        var rule = OperatorOperationCatalog.ResolveWrite(context.Request.Method, RoutePermissionCatalog.Normalize(endpoint.RoutePattern));
        if (rule is null) return null;
        if (rule.Refinement == OperatorRouteRefinement.None) return OperatorOperationCatalog.Refine(rule, new OperatorRequestFacts());

        var body = await BodyStringsAsync(context.Request);
        var project = routeValue("projectName");
        bool Evidence(string? commit)
            => evidence is not null && project is not null && commit is not null && evidence.HasFullGateEvidence(project, commit);
        var facts = rule.Refinement switch
        {
            OperatorRouteRefinement.CardMove => new OperatorRequestFacts(TargetLane: Field(body, "targetState")),
            OperatorRouteRefinement.GateEvidence => new OperatorRequestFacts(FullGateEvidence: Evidence(
                Field(body, "commit") ?? (project is null || Field(body, "artifact") is not { } artifact
                    ? null : evidence?.ReleaseCommit(project, null, artifact)))),
            OperatorRouteRefinement.ReleaseDeployment => new OperatorRequestFacts(
                EnvironmentKind: project is not null && Field(body, "environmentId") is { } environmentId
                    ? environmentKind(project, environmentId) : null,
                FullGateEvidence: Evidence(project is null || routeValue("releaseId") is not { } releaseId
                    ? null : evidence?.ReleaseCommit(project, releaseId, null))),
            _ => new OperatorRequestFacts(),
        };
        return OperatorOperationCatalog.Refine(rule, facts);
    }

    /// <summary>What a T1 request acts on: the permission target's project and environment, or the host a route names.</summary>
    public static OperatorGrantTarget GrantTarget(OperatorOperation operation, PermissionTarget target, Func<string, string?> routeValue)
        => operation.ScopeKind == OperatorScopeKinds.Host
            ? new OperatorGrantTarget(Host: routeValue("id"))
            : new OperatorGrantTarget(
                target.Projects is [{ Length: > 0 } project] ? project : null,
                target.Environment);

    private static string? Field(IReadOnlyDictionary<string, string> body, string name)
        => body.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static async Task<IReadOnlyDictionary<string, string>> BodyStringsAsync(HttpRequest request)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (request.ContentLength is 0 or > MaxFactBodyBytes
            || request.ContentLength is null && request.Headers.TransferEncoding.Count == 0)
            return fields;
        request.EnableBuffering();
        try
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await request.Body.ReadAsync(chunk, request.HttpContext.RequestAborted)) > 0)
            {
                if (buffer.Length + read > MaxFactBodyBytes) return fields;
                buffer.Write(chunk, 0, read);
            }
            using var document = JsonDocument.Parse(buffer.ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Object) return fields;
            foreach (var property in document.RootElement.EnumerateObject())
                if (property.Value.ValueKind == JsonValueKind.String)
                    fields[property.Name] = property.Value.GetString()!;
            return fields;
        }
        catch (JsonException exception)
        {
            AgentStudio.Diagnostics.SilentCatch.Note(exception, "OperatorRequestClassifier: request body is not JSON");
            return fields;
        }
        finally
        {
            request.Body.Position = 0;
        }
    }
}
