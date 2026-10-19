using System.Text.Json;

namespace AgentStudio.Deploy;

/// <summary>
/// Files under <c>&lt;TaskRepository&gt;/deployments/projects/&lt;project&gt;/</c>:
/// one write-once file per release, the run list, an append-only log per run
/// and an append-only audit trail. Callers serialize access per project.
/// </summary>
public sealed class DeployStore
{
    private const int MaxLogLinesRead = 2000;
    private readonly IConfiguration _configuration;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly JsonSerializerOptions _line = new(JsonSerializerDefaults.Web);

    public DeployStore(IConfiguration configuration) => _configuration = configuration;

    public IReadOnlyList<Release> ReadReleases(string project)
    {
        var directory = Path.Combine(ProjectRoot(project), "releases");
        if (!Directory.Exists(directory)) return [];
        return Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
            .Select(file => JsonSerializer.Deserialize<Release>(File.ReadAllText(file), _json)
                ?? throw new JsonException($"Release file {Path.GetFileName(file)} is empty."))
            .OrderBy(item => item.CreatedAtUtc).ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Writes a release once. A second write with the same id never replaces
    /// the file; it returns the stored release and <c>false</c>.
    /// </summary>
    public (Release Stored, bool Created) AppendRelease(string project, Release release)
    {
        var directory = Path.Combine(ProjectRoot(project), "releases");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, release.Id + ".json");
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(stream, release, _json);
            return (release, true);
        }
        catch (IOException) when (File.Exists(path))
        {
            return (JsonSerializer.Deserialize<Release>(File.ReadAllText(path), _json)
                ?? throw new JsonException($"Release file {release.Id}.json is empty."), false);
        }
    }

    public IReadOnlyList<DeploymentRecord> ReadDeployments(string project)
    {
        var path = Path.Combine(ProjectRoot(project), "deployments.json");
        return File.Exists(path)
            ? JsonSerializer.Deserialize<List<DeploymentRecord>>(File.ReadAllText(path), _json) ?? []
            : [];
    }

    public void SaveDeployments(string project, IReadOnlyList<DeploymentRecord> deployments)
    {
        var root = ProjectRoot(project);
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "deployments.json");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(deployments, _json));
        File.Move(temporary, path, true);
    }

    public void AppendLog(string project, IEnumerable<DeploymentLogEntry> entries)
    {
        foreach (var group in entries.GroupBy(item => item.DeploymentId, StringComparer.Ordinal))
        {
            var directory = Path.Combine(ProjectRoot(project), "logs");
            Directory.CreateDirectory(directory);
            File.AppendAllLines(Path.Combine(directory, SafeId(group.Key) + ".jsonl"),
                group.Select(item => JsonSerializer.Serialize(
                    new DeploymentLogLine(item.Step, item.AtUtc, item.Message), _line)));
        }
    }

    public IReadOnlyList<DeploymentLogLine> ReadLog(string project, string deploymentId)
    {
        var path = Path.Combine(ProjectRoot(project), "logs", SafeId(deploymentId) + ".jsonl");
        if (!File.Exists(path)) return [];
        return File.ReadLines(path).Where(line => line.Length > 0).Take(MaxLogLinesRead)
            .Select(line => JsonSerializer.Deserialize<DeploymentLogLine>(line, _line)!)
            .ToArray();
    }

    public void AppendAudit(string project, IEnumerable<DeployAuditEntry> entries)
    {
        var lines = entries.Select(item => JsonSerializer.Serialize(item, _line)).ToArray();
        if (lines.Length == 0) return;
        var root = ProjectRoot(project);
        Directory.CreateDirectory(root);
        File.AppendAllLines(Path.Combine(root, "audit.jsonl"), lines);
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<DeployAuditEntry> ReadAudit(string project, int limit)
    {
        var path = Path.Combine(ProjectRoot(project), "audit.jsonl");
        if (!File.Exists(path)) return [];
        return File.ReadLines(path).Where(line => line.Length > 0)
            .Select(line => JsonSerializer.Deserialize<DeployAuditEntry>(line, _line)!)
            .Reverse().Take(limit).ToArray();
    }

    private string ProjectRoot(string project) => Path.Combine(_configuration["TaskRepository"]
            ?? throw new InvalidOperationException("TaskRepository is required."),
        "deployments", "projects", ProjectEnvironmentService.SafeProjectName(project));

    private static string SafeId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new ArgumentException("Invalid deployment id.", nameof(id));
        return id;
    }
}
