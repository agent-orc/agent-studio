using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Deploy;

/// <summary>
/// The stored release: the contract record plus the environment declarations
/// as committed at the release commit. Every deploy of the release runs with
/// these declarations, so a later change in the working tree cannot alter
/// what the release deploys.
/// </summary>
public sealed record ReleaseRecord(Release Release, IReadOnlyList<EnvironmentDeclaration> Environments);

/// <summary>Release identity, numbering and snapshot versions. Pure.</summary>
public static class ReleaseSnapshot
{
    public const string DefaultChannel = "default";

    /// <summary>Same project, commit and artifacts always name the same release.</summary>
    public static string Id(string project, string commitSha, IEnumerable<string> artifactDigests)
        => DeployIds.For($"{project.ToLowerInvariant()}|{commitSha}|{string.Join(",", artifactDigests)}");

    /// <summary><c>yyyy.MM.dd.N</c>: the UTC day of creation and its running number.</summary>
    public static string NextVersion(IEnumerable<Release> existing, DateTime nowUtc)
    {
        var day = nowUtc.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture);
        var highest = existing.Select(item => item.Version)
            .Where(version => version.StartsWith(day + ".", StringComparison.Ordinal))
            .Select(version => int.TryParse(version[(day.Length + 1)..], NumberStyles.None,
                CultureInfo.InvariantCulture, out var number) ? number : 0)
            .DefaultIfEmpty(0).Max();
        return $"{day}.{highest + 1}";
    }

    /// <summary>
    /// Hash of everything that decides how a release is deployed: the
    /// project.yml schema 2 artifacts, standard lifecycle and deploy steps at
    /// the release commit, plus the environment declarations the deploy host
    /// executes.
    /// </summary>
    public static string ProcessVersion(ProjectDeploymentDefinition? definition,
        IEnumerable<EnvironmentDeclaration> declarations)
        => Hash(new
        {
            artifacts = definition?.Artifacts,
            lifecycle = definition?.StandardLifecycle,
            deploy = definition?.Deploy,
            environments = declarations.OrderBy(item => item.Id, StringComparer.Ordinal).Select(item => new
            {
                item.Id, item.Kind, item.TargetHostId, item.Address, item.Template, item.Destination,
                item.HealthUrl, item.Approval, item.ContainerPort,
            }),
        });

    /// <summary>
    /// P1 has no variables section yet, so the variable set is the secret
    /// reference names each environment hands to the deploy host. Values never
    /// enter the hash.
    /// </summary>
    public static string VariablesVersion(IEnumerable<EnvironmentDeclaration> declarations)
        => Hash(declarations.OrderBy(item => item.Id, StringComparer.Ordinal).Select(item => new
        {
            item.Id, secretRefs = item.SecretRefs.Order(StringComparer.Ordinal),
        }));

    private static string Hash(object value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))))[..12]
            .ToLowerInvariant();
}
