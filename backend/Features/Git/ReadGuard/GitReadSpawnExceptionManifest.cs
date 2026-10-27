using System.Security.Cryptography;
using System.Text;

namespace AgentStudio.Git;

/// <summary>
/// A GET route that may still start Git while its owner migrates it to
/// published facts. The key is the exact method and route template; scenarios
/// that produced the ceiling live in the census tests, not here.
/// </summary>
/// <param name="SpawnCeiling">Most Git processes one request may start, as measured in S1.</param>
/// <param name="Owner">Who removes the debt.</param>
/// <param name="RemovalSlice">The slice or follow-up that brings the route to zero.</param>
/// <param name="Family">Request family of the Git facts Dossier (R4 to R14) or Inventory boundary group.</param>
public sealed record GitReadSpawnException(
    string Method,
    string RouteTemplate,
    int SpawnCeiling,
    string Owner,
    string RemovalSlice,
    string Family);

/// <summary>
/// Frozen GET routes that still start Git (Git facts S1, decision D6). Every
/// entry is measured debt: <c>GitReadSpawnCensusTests</c> exercises each one
/// and records its scenarios and commands. The list only shrinks.
/// <c>GitReadGuardManifestTests</c> pins <see cref="Digest"/> to a reviewed
/// value and rejects a new key or a raised ceiling, so nothing can be
/// grandfathered without a reviewed change to that test. Never add an entry to
/// make a failing read pass; move the Git work to the fact producer.
/// </summary>
internal static class GitReadSpawnExceptionManifest
{
    // Measured on the 3,000-card board with cold caches (GitReadSpawnCensusTests)
    // and in the census ledger run of the existing host tests; the ceiling is the
    // larger maximum. A route seen only in existing tests carries one spawn of
    // margin for the process-wide toplevel cache, whose order those tests do not
    // control. Ordered by route template.
    //
    // AGT-3323 (Git facts S3) removed the card Git facts it migrated to the
    // store: R4 provenance, the R5 commit and default-diff reads, the R7 run
    // views, R12 the project integration view and R14 the delivery claim. The
    // S4 removed the remaining R11 file and historical-object entries.
    internal static readonly IReadOnlyList<GitReadSpawnException> Entries =
    [
        new("GET", "/api/admin/prompts/", 1, "Prompt administration", "Prompt revisions from the Git fact store", "Other reads"),
        new("GET", "/api/admin/prompts/{name}", 2, "Prompt administration", "Prompt revisions from the Git fact store", "Other reads"),
        new("GET", "/api/git/cleanup/plan", 209, "AGT-3017 route ownership", "AGT-3017 project views and maintenance", "Project views"),
        new("GET", "/api/git/hygiene", 5, "AGT-3017 route ownership", "AGT-3017 project views and maintenance", "Project views"),
        new("GET", "/api/git/project-commit/diff", 1, "AGT-3017 route ownership", "AGT-3017 project views and maintenance", "Project views"),
        new("GET", "/api/git/project-commit/files", 2, "AGT-3017 route ownership", "AGT-3017 project views and maintenance", "Project views"),
        new("GET", "/api/git/summary", 3, "AGT-3017 route ownership", "AGT-3017 project views and maintenance", "Project views"),
        new("GET", "/api/orchestrator/context/global", 3, "Orchestrator chat", "Context from published Git facts", "Other reads"),
        new("GET", "/api/orchestrator/context/project:{projectId}", 2, "Orchestrator chat", "Context from published Git facts", "Other reads"),
        new("GET", "/api/orchestrator/context/task:{projectId}/{taskKey}", 2, "Orchestrator chat", "Context from published Git facts", "Other reads"),
        new("GET", "/api/orchestrator/context/workbench:{projectId}/{workbenchKey}", 12, "Orchestrator chat", "Context from published Git facts", "Other reads"),
        new("GET", "/api/parked-cards", 2, "Pipeline", "Pipeline reads from the Git fact store", "Other reads"),
        new("GET", "/api/projects/pipeline-catalogue", 2, "Pipeline", "Pipeline reads from the Git fact store", "Other reads"),
        new("GET", "/api/projects/{projectName}/deploy/deployments/page", 2, "Deploy and environments", "Environment declarations from the Git fact store", "Other reads"),
        new("GET", "/api/projects/{projectName}/deploy/releases/page", 2, "Deploy and environments", "Environment declarations from the Git fact store", "Other reads"),
        new("GET", "/api/projects/{projectName}/deploy/runbooks", 5, "Deploy and environments", "Environment declarations from the Git fact store", "Other reads"),
        new("GET", "/api/projects/{projectName}/deploy/status", 2, "Deploy and environments", "Environment declarations from the Git fact store", "Other reads"),
        new("GET", "/api/projects/{projectName}/environments/{environmentId}/telemetry/metrics/names", 2, "Deploy and environments", "Environment declarations from the Git fact store", "Other reads"),
        new("GET", "/api/projects/{projectName}/wiki", 5, "Docs/Wiki", "Wiki ref/config-generation projection", "R13"),
        new("GET", "/api/projects/{projectName}/wiki/assets/{**relPath}", 9, "Docs/Wiki", "Wiki ref/config-generation projection", "R13"),
        new("GET", "/api/projects/{projectName}/wiki/files/{**relPath}", 5, "Docs/Wiki", "Wiki ref/config-generation projection", "R13"),
        new("GET", "/api/projects/{projectName}/wiki/folder/{**relPath}", 6, "Docs/Wiki", "Wiki ref/config-generation projection", "R13"),
        new("GET", "/api/projects/{projectName}/wiki/history/{**relPath}", 10, "Docs/Wiki", "Wiki ref/config-generation projection", "R13"),
        new("GET", "/api/projects/{projectName}/wiki/home", 5, "Docs/Wiki", "Wiki ref/config-generation projection", "R13"),
        new("GET", "/api/projects/{projectName}/wiki/pulse", 2, "Docs/Wiki", "Wiki ref/config-generation projection", "R13"),
        new("GET", "/api/projects/{projectName}/wiki/recent", 6, "Docs/Wiki", "Wiki ref/config-generation projection", "R13"),
        new("GET", "/api/projects/{projectName}/wiki/revisions/{sha}/{**relPath}", 5, "Docs/Wiki", "Wiki ref/config-generation projection", "R13"),
        new("GET", "/api/projects/{projectName}/wiki/tree", 5, "Docs/Wiki", "Wiki ref/config-generation projection", "R13"),
        new("GET", "/api/projects/{projectName}/workbenches/{key}/references", 3, "Docs/Wiki", "Wiki ref/config-generation projection", "R13"),
        new("GET", "/api/projects/{project}/publish-status", 3, "Publishing", "Publishing background projection", "Publishing"),
        new("GET", "/api/projects/{project}/publish/{targetId}/panel", 3, "Publishing", "Publishing background projection", "Publishing"),
        new("GET", "/api/projects/{project}/secret-bindings/{environment}", 4, "Deploy and environments", "Environment declarations from the Git fact store", "Other reads"),
        new("GET", "/api/projects/{project}/test-runs", 2, "Pipeline", "Pipeline reads from the Git fact store", "Other reads"),
        new("GET", "/api/runner/project:{projectId}/orchestrator-chat", 3, "Orchestrator chat", "Context from published Git facts", "Other reads"),
        new("GET", "/api/runner/task:{projectId}/{taskKey}/orchestrator-chat", 3, "Orchestrator chat", "Context from published Git facts", "Other reads"),
        new("GET", "/api/runner/workbench:{projectId}/{workbenchKey}/orchestrator-chat", 3, "Orchestrator chat", "Context from published Git facts", "Other reads"),
        new("GET", "/api/tasks/{jobId}/git/diff", 2, "AGT-3017 route ownership", "AGT-3017 D2/D4 dev-seat retirement", "R8"),
        new("GET", "/api/tasks/{jobId}/git/file", 1, "AGT-3017 route ownership", "AGT-3017 D2/D4 dev-seat retirement", "R8"),
        new("GET", "/api/tasks/{jobId}/git/hygiene", 5, "AGT-3017 route ownership", "AGT-3017 D2/D4 dev-seat retirement", "R8"),
        new("GET", "/api/tasks/{jobId}/git/status", 5, "AGT-3017 route ownership", "AGT-3017 D2/D4 dev-seat retirement", "R8"),
    ];

    private static readonly Dictionary<string, GitReadSpawnException> ByKey =
        Entries.ToDictionary(entry => Key(entry.Method, entry.RouteTemplate), StringComparer.Ordinal);

    internal static GitReadSpawnException? Find(string method, string routeTemplate)
        => ByKey.GetValueOrDefault(Key(method, routeTemplate));

    /// <summary>SHA-256 over the canonical, ordered entry lines.</summary>
    internal static string Digest() => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical())));

    internal static string Canonical() => string.Join('\n', Entries
        .OrderBy(entry => entry.RouteTemplate, StringComparer.Ordinal)
        .ThenBy(entry => entry.Method, StringComparer.Ordinal)
        .Select(entry => $"{entry.Method} {entry.RouteTemplate} ceiling={entry.SpawnCeiling} "
            + $"owner={entry.Owner} slice={entry.RemovalSlice} family={entry.Family}"));

    private static string Key(string method, string routeTemplate) => method + " " + routeTemplate;
}
