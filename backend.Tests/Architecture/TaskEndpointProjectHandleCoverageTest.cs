using System.Text.RegularExpressions;

using Xunit;
using AgentStudio.TestSupport;

namespace AgentStudio.Tests;

/// <summary>
/// Phase 2b completeness guard (ASS-1760). Every task HTTP endpoint handler that
/// binds a query-string <c>watchPath</c> must ALSO bind the path-free
/// <c>project</c> handle (a short code / Kürzel or a <c>PROJ-NNN</c> id) and
/// resolve it through <see cref="TaskEndpointHelpers.ResolveWatchPath"/>. This is
/// what lets a caller address a task by its registry identity without ever
/// putting the absolute filesystem path on the wire — the raw <c>watchPath</c>
/// stays accepted only as a deprecated legacy fallback.
///
/// <para>
/// The test scans source (same cheap-review-surface rationale as
/// <see cref="TaskFolderAccessIsolationTest"/>) rather than reflecting over the
/// live route table: a textual rule is the cheapest diff-time signal and it
/// fails loudly the moment a new handler is added with a bare <c>watchPath</c>.
/// </para>
/// </summary>
public class TaskEndpointProjectHandleCoverageTest
{
    // A minimal-API handler is registered by group.Map{Get,Post,Put,Delete,Patch}(.
    // Splitting the file on this boundary yields one segment per handler lambda
    // (the final segment also carries any trailing private helpers, which is
    // harmless: it inherits its own handler's ResolveWatchPath call).
    private static readonly Regex HandlerBoundary =
        new(@"(?=group\.Map(?:Get|Post|Put|Delete|Patch)\s*\()", RegexOptions.Compiled);

    // The handler binds a query-string watchPath parameter (plain or [FromQuery]).
    private static readonly Regex BindsWatchPath =
        new(@"\bstring\?\s+watchPath\b", RegexOptions.Compiled);

    private static readonly Regex BindsProject =
        new(@"\bstring\?\s+project\b", RegexOptions.Compiled);

    private const string ResolveCall = "ResolveWatchPath(projects, project, watchPath)";

    [Fact]
    public void EveryTaskEndpointBindingWatchPath_AlsoBindsProjectAndResolves()
    {
        var repoRoot = ResolveRepoRoot();
        var dir = Path.Combine(repoRoot, "backend", "Features", "Tasks");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(dir, "*Endpoints.cs"))
        {
            var text = File.ReadAllText(file);
            var rel = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
            var segments = HandlerBoundary.Split(text);

            // segments[0] is the pre-first-handler preamble (usings + scaffolding).
            for (var i = 1; i < segments.Length; i++)
            {
                var seg = segments[i];
                if (!BindsWatchPath.IsMatch(seg)) continue;
                if (BindsProject.IsMatch(seg) && seg.Contains(ResolveCall)) continue;

                var route = Regex.Match(seg, "Map(?:Get|Post|Put|Delete|Patch)\\(\\s*\"([^\"]+)\"").Groups[1].Value;
                offenders.Add($"{rel}: handler \"{route}\" binds a raw watchPath without the path-free project handle.");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Phase 2b (ASS-1760): every task endpoint that accepts a query watchPath must also accept a\n" +
            "path-free ?project= handle and call TaskEndpointHelpers.ResolveWatchPath(projects, project, watchPath).\n" +
            "Add `string? project` before `watchPath`, inject `AgentStudio.Registry.ProjectRegistry projects`,\n" +
            "and make `watchPath = ResolveWatchPath(projects, project, watchPath);` the first body statement.\n" +
            "Offending handlers:\n  " + string.Join("\n  ", offenders));
    }

    private static string ResolveRepoRoot()
        => RepositoryRoot.Find();
}
