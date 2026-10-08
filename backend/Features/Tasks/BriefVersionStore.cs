namespace AgentStudio.Tasks;

/// <summary>
/// Version of the authored brief. A queued follow-up appends execution intent to
/// prompt.md without replacing the authored brief, so it does not change this
/// value. Direct prompt edits do.
/// </summary>
public static class BriefVersionStore
{
    private const string RelativePath = ".metadata/brief-version";

    public static string ReadOrCreate(string folder)
    {
        var path = Path.Combine(folder, RelativePath);
        if (File.Exists(path)) return File.ReadAllText(path).Trim();
        return Record(folder, File.Exists(Path.Combine(folder, "prompt.md"))
            ? File.ReadAllText(Path.Combine(folder, "prompt.md")) : string.Empty);
    }

    public static string Record(string folder, string authoredBrief)
    {
        var version = AgentStudio.Runner.RunDispositionPolicy.BriefVersion(authoredBrief);
        var path = Path.Combine(folder, RelativePath);
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
        return version;
    }
}
