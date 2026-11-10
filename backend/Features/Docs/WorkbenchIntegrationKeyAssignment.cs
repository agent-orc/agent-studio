using System.Text.Json.Nodes;
using AgentStudio.Persistence;

namespace AgentStudio.Docs;

/// <summary>Assigns keys to descriptors changed by a delivery before its integration gate.</summary>
public static class WorkbenchIntegrationKeyAssignment
{
    public static IReadOnlyList<string> Assign(string repositoryRoot,
        IReadOnlyList<string> changedPaths, ProjectRecord project, ProjectRegistry registry)
    {
        var docsRoot = Path.Combine(repositoryRoot, "docs");
        if (!Directory.Exists(docsRoot)) return [];
        var highest = 0;
        foreach (var path in EnumerateDescriptors(docsRoot))
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject descriptor
                    || descriptor["key"] is not JsonValue value
                    || !value.TryGetValue<string>(out var key)) continue;
                var prefix = project.ShortCode + "-W";
                if (key.StartsWith(prefix, StringComparison.Ordinal)
                    && int.TryParse(key.AsSpan(prefix.Length), out var number))
                    highest = Math.Max(highest, number);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or System.Text.Json.JsonException or InvalidOperationException)
            {
                SilentCatch.Note(ex, "Document key floor skipped an unreadable descriptor.");
            }
        }
        registry.EnsureWorkbenchKeyFloor(project.Id, highest + 1);

        var assigned = new List<string>();
        foreach (var relative in changedPaths.Distinct(StringComparer.Ordinal))
        {
            if (!relative.StartsWith("docs/", StringComparison.Ordinal)
                || !relative.EndsWith("/workbench.json", StringComparison.Ordinal)
                || relative.Split('/').Any(part => part is ".." or ".")) continue;
            var path = Path.Combine(repositoryRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path) || !IsRegularPath(docsRoot, path)) continue;
            JsonObject? descriptor;
            try
            {
                descriptor = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
                if (descriptor is null || descriptor["key"] is not null) continue;
                var id = descriptor["id"]?.GetValue<string>();
                var schema = descriptor["schemaVersion"]?.GetValue<int>();
                if (schema is not (1 or 2) || id is null || id != Path.GetFileName(Path.GetDirectoryName(path))
                    || id.Length is < 1 or > 80
                    || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or System.Text.Json.JsonException or InvalidOperationException)
            {
                SilentCatch.Note(ex, "Invalid delivery descriptor skipped document key assignment.");
                continue;
            }
            descriptor["key"] = $"{project.ShortCode}-W{registry.IssueNextWorkbenchKey(project.Id)}";
            File.WriteAllText(path, RepositoryTextLayout.IndentedJsonFor(path, descriptor));
            assigned.Add(relative);
        }
        return assigned;
    }

    private static IEnumerable<string> EnumerateDescriptors(string docsRoot)
    {
        var pending = new Stack<string>();
        pending.Push(docsRoot);
        while (pending.TryPop(out var directory))
        {
            foreach (var child in Directory.EnumerateDirectories(directory))
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                    pending.Push(child);
            var descriptor = Path.Combine(directory, "workbench.json");
            if (File.Exists(descriptor) && IsRegularPath(docsRoot, descriptor))
                yield return descriptor;
        }
    }

    private static bool IsRegularPath(string docsRoot, string path)
    {
        for (var current = path; !string.Equals(current, docsRoot, StringComparison.Ordinal);
             current = Path.GetDirectoryName(current)!)
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            if (current.Length <= docsRoot.Length) return false;
        }
        return true;
    }
}
