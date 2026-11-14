namespace AgentStudio.Tasks;

/// <summary>
/// Version of the authored brief. A queued follow-up appends execution intent to
/// prompt.md without replacing the authored brief, so it does not change this
/// value. Direct prompt edits do.
/// </summary>
public static class BriefVersionStore
{
    private const string RelativePath = ".metadata/brief-version";
    private const string DifferencePath = ".metadata/brief-difference.json";

    public sealed record BriefDifference(string FromVersion, string ToVersion,
        string Before, string After);

    public static BriefDifference? ReadDifference(string folder)
    {
        var path = Path.Combine(folder, DifferencePath);
        return File.Exists(path)
            ? System.Text.Json.JsonSerializer.Deserialize<BriefDifference>(File.ReadAllText(path))
            : null;
    }

    /// <summary>Read the current version without creating metadata during a GET or audit.</summary>
    public static string ReadCurrent(string folder)
    {
        var path = Path.Combine(folder, RelativePath);
        if (File.Exists(path)) return File.ReadAllText(path).Trim();
        var briefPath = Path.Combine(folder, "prompt.md");
        return AgentStudio.Runner.RunDispositionPolicy.BriefVersion(
            File.Exists(briefPath) ? File.ReadAllText(briefPath) : string.Empty);
    }

    public static string ReadOrCreate(string folder)
    {
        var path = Path.Combine(folder, RelativePath);
        if (File.Exists(path)) return File.ReadAllText(path).Trim();
        return Record(folder, File.Exists(Path.Combine(folder, "prompt.md"))
            ? File.ReadAllText(Path.Combine(folder, "prompt.md")) : string.Empty);
    }

    public static string Record(string folder, string authoredBrief, string? previousBrief = null)
    {
        var version = AgentStudio.Runner.RunDispositionPolicy.BriefVersion(authoredBrief);
        var previousVersion = previousBrief is null ? ReadCurrent(folder)
            : AgentStudio.Runner.RunDispositionPolicy.BriefVersion(previousBrief);
        var path = Path.Combine(folder, RelativePath);
        // Create only the metadata folder: re-creating a task folder that moved
        // away would leave a duplicate shell in the old lane (AGT-2867).
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException($"Task folder '{folder}' no longer exists.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, version);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        if (previousBrief is not null && !string.Equals(previousVersion, version, StringComparison.OrdinalIgnoreCase))
        {
            var difference = new BriefDifference(previousVersion, version, previousBrief, authoredBrief);
            File.WriteAllText(Path.Combine(folder, DifferencePath),
                System.Text.Json.JsonSerializer.Serialize(difference));
        }
        return version;
    }
}
