using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Drift guard for <c>docs/studio-route-ownership/routes.json</c>, the Studio
/// route ownership inventory. That file is also embedded into the backend
/// build as <see cref="AgentStudio.Connector.ConnectorRouteInventory"/>'s
/// routing table, but nothing previously verified that it stayed in sync
/// with the Angular source it claims to describe: a new
/// <c>this.http.get(...)</c> call site could land without ever being added
/// to the inventory (see AGT-2835 - four route migrations, AGT-2756/2757/
/// 2758, landed without the inventory being reconciled against them).
///
/// This test re-runs the generator
/// (<c>docs/studio-route-ownership/build-inventory.mjs</c>, which itself
/// shells out to <c>extract-routes.mjs</c> over
/// <c>frontend/src/app/**/*.ts</c>) and compares the method+path operations
/// it derives from current source against the committed <c>routes.json</c>.
/// A frontend operation present in one but not the other means someone
/// needs to run <c>node docs/studio-route-ownership/build-inventory.mjs
/// --write</c>, review the diff (new routes need a classification; routes
/// that were split or hand-classified in the past should keep their
/// recorded classification - the generator preserves that), and commit the
/// result together with the code change.
/// </summary>
public sealed class StudioRouteInventorySyncTests
{
    [SkippableFact]
    public void Committed_inventory_matches_the_regenerated_frontend_route_set()
    {
        Skip.IfNot(NodeAvailable(), "node is not on PATH; the route inventory generator requires Node.js.");

        var root = RepoRoot();
        var generatorPath = Path.Combine(root, "docs", "studio-route-ownership", "build-inventory.mjs");
        var committedPath = Path.Combine(root, "docs", "studio-route-ownership", "routes.json");

        var regenerated = RunGenerator(generatorPath, root);
        var committed = JsonDocument.Parse(File.ReadAllText(committedPath));

        var regeneratedKeys = RouteKeys(regenerated.RootElement);
        var committedKeys = RouteKeys(committed.RootElement);

        var missing = regeneratedKeys.Except(committedKeys).OrderBy(key => key).ToArray();
        var stale = committedKeys.Except(regeneratedKeys).OrderBy(key => key).ToArray();

        Assert.True(missing.Length == 0 && stale.Length == 0, BuildFailureMessage(missing, stale));
    }

    private static string BuildFailureMessage(IReadOnlyCollection<string> missing, IReadOnlyCollection<string> stale)
    {
        var message = "docs/studio-route-ownership/routes.json is out of sync with the frontend and task-server "
            + "source it is generated from. Run 'node docs/studio-route-ownership/build-inventory.mjs --write', "
            + "review the diff, and commit the result.";
        if (missing.Count > 0)
            message += $"\nFrontend operations missing from the committed inventory: {string.Join(", ", missing)}";
        if (stale.Count > 0)
            message += $"\nInventory operations with no current frontend call site: {string.Join(", ", stale)}";
        return message;
    }

    private static HashSet<string> RouteKeys(JsonElement root) => root
        .GetProperty("frontendRoutes")
        .EnumerateArray()
        .Select(route => $"{route.GetProperty("method").GetString()} {route.GetProperty("path").GetString()}")
        .ToHashSet();

    private static JsonDocument RunGenerator(string generatorPath, string workingDirectory)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "node",
            ArgumentList = { generatorPath },
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(60_000), "Route inventory generator timed out.");
        Assert.True(process.ExitCode == 0, $"Route inventory generator failed: {stderr}");
        return JsonDocument.Parse(stdout);
    }

    private static bool NodeAvailable()
    {
        try
        {
            using var probe = Process.Start(new ProcessStartInfo
            {
                FileName = "node",
                ArgumentList = { "--version" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (probe == null || !probe.WaitForExit(8_000)) return false;
            return probe.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string RepoRoot([CallerFilePath] string sourceFile = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(sourceFile)!);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AGENTS.md")) &&
                Directory.Exists(Path.Combine(dir.FullName, "docs", "studio-route-ownership")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException($"Repo root not found by walking up from {sourceFile}.");
    }
}
