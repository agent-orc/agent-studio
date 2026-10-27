using AgentStudio.Git;
using Xunit;

namespace AgentStudio.Tests.Architecture;

/// <summary>
/// Freezes the Git read exception manifest of Git facts S1 (AGT-3321,
/// decision D6). The reviewed keys and ceilings below are the measured S1
/// baseline; <see cref="ReviewedDigest"/> pins the full canonical manifest
/// (keys, ceilings, owners, removal slices). The manifest may shrink: remove
/// an entry, or lower a ceiling, and update both in the same reviewed change.
/// It may never grow: a new key or a raised ceiling fails here, and the fix is
/// to move the Git work off the request, not to extend this list.
/// AGT-3323 (Git facts S3) removed R4, R5, R7, R12 and R14 and moved the
/// commit-mode file preview to S4.
/// </summary>
[Trait("Category", "Guard")]
public sealed class GitReadGuardManifestTests
{
    private const string ReviewedDigest = "26e9c57e5b2d256ee3ac08a2f12ff0b3e53698d77c90debc656a8f1e601e616e";

    /// <summary>Method, route template and spawn ceiling of every reviewed exception.</summary>
    private const string Reviewed = """
            GET /api/admin/prompts/ 1
            GET /api/admin/prompts/{name} 2
            GET /api/git/cleanup/plan 209
            GET /api/git/hygiene 5
            GET /api/git/project-commit/diff 1
            GET /api/git/project-commit/files 2
            GET /api/git/summary 3
            GET /api/orchestrator/context/global 3
            GET /api/orchestrator/context/project:{projectId} 2
            GET /api/orchestrator/context/task:{projectId}/{taskKey} 2
            GET /api/orchestrator/context/workbench:{projectId}/{workbenchKey} 12
            GET /api/parked-cards 2
            GET /api/projects/pipeline-catalogue 2
            GET /api/projects/{projectName}/deploy/deployments/page 2
            GET /api/projects/{projectName}/deploy/releases/page 2
            GET /api/projects/{projectName}/deploy/runbooks 5
            GET /api/projects/{projectName}/deploy/status 2
            GET /api/projects/{projectName}/environments/{environmentId}/telemetry/metrics/names 2
            GET /api/projects/{projectName}/wiki 5
            GET /api/projects/{projectName}/wiki/assets/{**relPath} 9
            GET /api/projects/{projectName}/wiki/files/{**relPath} 5
            GET /api/projects/{projectName}/wiki/folder/{**relPath} 6
            GET /api/projects/{projectName}/wiki/history/{**relPath} 10
            GET /api/projects/{projectName}/wiki/home 5
            GET /api/projects/{projectName}/wiki/pulse 2
            GET /api/projects/{projectName}/wiki/recent 6
            GET /api/projects/{projectName}/wiki/revisions/{sha}/{**relPath} 5
            GET /api/projects/{projectName}/wiki/tree 5
            GET /api/projects/{projectName}/workbenches/{key}/references 3
            GET /api/projects/{project}/publish-status 3
            GET /api/projects/{project}/publish/{targetId}/panel 3
            GET /api/projects/{project}/secret-bindings/{environment} 4
            GET /api/projects/{project}/test-runs 2
            GET /api/runner/project:{projectId}/orchestrator-chat 3
            GET /api/runner/task:{projectId}/{taskKey}/orchestrator-chat 3
            GET /api/runner/workbench:{projectId}/{workbenchKey}/orchestrator-chat 3
            GET /api/tasks/{jobId}/checkout/{**path} 2
            GET /api/tasks/{jobId}/commits/{sha}/file 1
            GET /api/tasks/{jobId}/files/{**path} 5
            GET /api/tasks/{jobId}/git/diff 2
            GET /api/tasks/{jobId}/git/file 1
            GET /api/tasks/{jobId}/git/hygiene 5
            GET /api/tasks/{jobId}/git/status 5
            """;

    [Fact]
    public void Manifest_matches_its_reviewed_digest()
        => Assert.True(GitReadSpawnExceptionManifest.Digest() == ReviewedDigest,
            "The Git read exception manifest changed. A reviewed shrink updates ReviewedDigest and the Reviewed list; "
            + "growth is rejected. Current digest: " + GitReadSpawnExceptionManifest.Digest());

    [Fact]
    public void Manifest_never_grows_past_the_reviewed_baseline()
    {
        var reviewed = Reviewed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(' '))
            .ToDictionary(parts => parts[0] + " " + parts[1], parts => int.Parse(parts[2],
                System.Globalization.CultureInfo.InvariantCulture), StringComparer.Ordinal);
        var growth = new List<string>();
        foreach (var entry in GitReadSpawnExceptionManifest.Entries)
        {
            var key = entry.Method + " " + entry.RouteTemplate;
            if (!reviewed.TryGetValue(key, out var ceiling)) growth.Add($"new exception {key}");
            else if (entry.SpawnCeiling > ceiling) growth.Add($"{key}: ceiling {entry.SpawnCeiling} above reviewed {ceiling}");
        }

        Assert.True(growth.Count == 0, "Git read exceptions may only shrink:\n  " + string.Join("\n  ", growth));
    }

    [Fact]
    public void Manifest_keys_are_exact_get_templates_with_owners()
    {
        Assert.All(GitReadSpawnExceptionManifest.Entries, entry =>
        {
            Assert.Equal("GET", entry.Method);
            Assert.StartsWith("/", entry.RouteTemplate);
            Assert.DoesNotContain("*", entry.RouteTemplate.Replace("{**", "{", StringComparison.Ordinal));
            Assert.InRange(entry.SpawnCeiling, 1, 1_000);
            Assert.False(string.IsNullOrWhiteSpace(entry.Owner));
            Assert.False(string.IsNullOrWhiteSpace(entry.RemovalSlice));
        });
        Assert.Equal(GitReadSpawnExceptionManifest.Entries.Count,
            GitReadSpawnExceptionManifest.Entries.Select(entry => entry.Method + " " + entry.RouteTemplate).Distinct().Count());
    }
}
