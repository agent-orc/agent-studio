using System.Text.Json;

namespace AgentStudio.Git;

/// <summary>Repository-owned, per-feature opt-in for bounded documentation additions.</summary>
internal sealed record AppendOnlyRegistry(string Path, string StartMarker, string EndMarker)
{
    internal const string DirectoryPath = ".agent-studio/append-only-registries";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static AppendOnlyRegistry? Find(string repo, string path)
    {
        var folder = System.IO.Path.Combine(repo, DirectoryPath);
        if (!Directory.Exists(folder)) return null;
        AppendOnlyRegistry? found = null;
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.Ordinal))
            {
                var rule = JsonSerializer.Deserialize<AppendOnlyRegistry>(File.ReadAllText(file), Json);
                if (rule?.Path != path) continue;
                if (found is not null || string.IsNullOrWhiteSpace(rule.StartMarker)
                    || string.IsNullOrWhiteSpace(rule.EndMarker) || rule.StartMarker == rule.EndMarker)
                    return null;
                found = rule;
            }
            return found;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AgentStudio.Diagnostics.SilentCatch.Note(ex, "Append-only registry could not be read.");
            return null;
        }
    }
}
