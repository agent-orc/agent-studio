using System.Text.Json;

namespace AgentStudio.Pipeline;

public sealed record ConceptWorkbenchRecord
{
    public string RepoRelativeDirectory { get; init; } = "";
    public string RepoRelativeEntrypoint { get; init; } = "";
    public string Title { get; init; } = "";
    public DateTime PublishedAt { get; init; }
    public string? CommitSha { get; init; }
}

public static class ConceptWorkbenchStore
{
    public const string RelativePath = ".metadata/concept-workbench.json";

    /// <summary>
    /// Checkout-shaped copy of the placed Dossier's contract files
    /// (<c>workbench.json</c>, <c>index.html</c>) at the integrated commit.
    /// Placement writes it; promotion reads it when the developer checkout
    /// does not carry the Dossier yet, so a read request never spawns git.
    /// </summary>
    public const string SnapshotRelativePath = ".metadata/concept-dossier";

    public static string SnapshotRoot(string jobFolderPath)
        => Path.Combine(jobFolderPath, ".metadata", "concept-dossier");
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static ConceptWorkbenchRecord? Read(string jobFolderPath)
    {
        try
        {
            var path = Path.Combine(jobFolderPath, ".metadata", "concept-workbench.json");
            return File.Exists(path)
                ? JsonSerializer.Deserialize<ConceptWorkbenchRecord>(File.ReadAllText(path), Options)
                : null;
        }
        catch
        {
            return null;
        }
    }

    public static bool Write(string jobFolderPath, ConceptWorkbenchRecord record, ILogger? logger = null)
    {
        try
        {
            var directory = Path.Combine(jobFolderPath, ".metadata");
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                Path.Combine(directory, "concept-workbench.json"),
                JsonSerializer.Serialize(record, Options) + Environment.NewLine);
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Could not persist concept Dossier metadata for {Folder}", jobFolderPath);
            return false;
        }
    }
}
