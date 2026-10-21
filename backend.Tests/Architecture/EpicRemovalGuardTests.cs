using System.Text.RegularExpressions;
using Xunit;

namespace AgentStudio.Tests.Architecture;

public sealed class EpicRemovalGuardTests
{
    [Fact]
    [Trait("Category", "Guard")]
    public void Production_sources_do_not_restore_epic_concepts()
    {
        var root = CSharpSourceScanner.RepoRoot();
        var roots = new[]
        {
            "backend", "runner", "task-server", "contracts", "orchestration-decisions",
            "frontend/src", "frontend/e2e", "frontend/scripts", "frontend/public",
            "scripts", "testsupport", "prompts/runtime",
        };
        var retirementLines = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["backend/Features/Tasks/TaskCrudEndpoints.cs"] =
            [
                "string.Equals(key, \"epicId\", StringComparison.OrdinalIgnoreCase)) == true)",
                "ctx.Response.Headers.Warning = \"299 - \\\"epicId is deprecated and ignored\\\"\";",
            ],
            ["task-server/TaskServerEndpoints.cs"] =
            [
                "string.Equals(key, \"epicId\", StringComparison.OrdinalIgnoreCase)) == true)",
                "context.Response.Headers.Warning = \"299 - \\\"epicId is deprecated and ignored\\\"\";",
            ],
            ["task-server/TaskServerStudioProjectMetaStore.cs"] =
            [
                "await ExecuteAsync(connection, \"DROP TABLE IF EXISTS epic_tasks; DROP TABLE IF EXISTS epics;\", ct);",
            ],
            ["task-server/TaskServerStudioTaskMetadataStore.cs"] =
            [
                "\"SELECT COUNT(*) FROM pragma_table_info('task_studio_fields') WHERE name = 'epic_id';\", ct);",
                "await ExecuteAsync(connection, \"ALTER TABLE task_studio_fields DROP COLUMN epic_id;\", ct);",
            ],
            ["backend/Shared/Models/RetiredContainerTask.cs"] =
            [
                "string.Equals(kind, \"epic\", StringComparison.OrdinalIgnoreCase)",
                "? \"epic\"",
                "string.Equals(task.Kind, \"epic\", StringComparison.OrdinalIgnoreCase);",
            ],
            ["scripts/backfill-cycle-time-completions.mjs"] =
            [
                "return task.kind?.toLowerCase() === 'epic';",
            ],
            ["frontend/src/app/features/studio-shell/services/studio-route.ts"] =
            [
                "if (segments.length === 1 && segments[0] === 'epics') {",
                "if (segments.length === 3 && segments[2] === 'epics') {",
            ],
            ["frontend/src/app/features/studio-shell/services/studio-tab-state.service.ts"] =
            [
                "return legacy?.kind === 'epics'",
                "const activeKey = parsed.activeKey?.startsWith('epics:')",
                "? `workbenches:${parsed.activeKey.slice('epics:'.length)}`",
            ],
        };
        var violations = new List<string>();
        foreach (var relativeRoot in roots)
        {
            var directory = Path.Combine(root, relativeRoot);
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (relative.Split('/').Any(part => part is "bin" or "obj" or "node_modules")) continue;
                if (!new[] { ".cs", ".ts", ".html", ".scss", ".md", ".mjs", ".json" }
                    .Contains(Path.GetExtension(file))) continue;
                if (relative.EndsWith(".spec.ts", StringComparison.Ordinal)
                    || relative.EndsWith(".test.mjs", StringComparison.Ordinal)) continue;

                if (Regex.IsMatch(relative, @"\bepic|Epic|EPIC"))
                    violations.Add(relative);
                var lineNumber = 0;
                foreach (var line in File.ReadLines(file))
                {
                    lineNumber++;
                    if (Regex.IsMatch(line, @"\bepic|Epic|EPIC")
                        && (!retirementLines.TryGetValue(relative, out var allowed)
                            || !allowed.Contains(line.Trim())))
                        violations.Add($"{relative}:{lineNumber}");
                }
            }
        }

        if (Regex.IsMatch(File.ReadAllText(Path.Combine(root, "frontend", "angular.json")),
            @"\bepic|Epic|EPIC"))
            violations.Add("frontend/angular.json");

        Assert.True(violations.Count == 0,
            "Epic concepts may only occur in temporary compatibility, historical data exclusion, and schema cleanup paths:\n  "
            + string.Join("\n  ", violations));
    }
}
