using System.Text.Json;

namespace AgentStudio.Deploy;

/// <summary>
/// Files under <c>&lt;TaskRepository&gt;/deployments/projects/&lt;project&gt;/</c>:
/// one write-once file per release (the release and its environment
/// declaration snapshot), the run list, an append-only log per run and an
/// append-only audit trail. Callers serialize access per project; the audit
/// trail is shared by the deployment and runbook services, so the store
/// serializes its appends and reads itself.
/// </summary>
public sealed class DeployStore
{
    private const int MaxLogLinesRead = 2000;
    private readonly IConfiguration _configuration;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly JsonSerializerOptions _line = new(JsonSerializerDefaults.Web);
    private readonly object _auditGate = new();

    public DeployStore(IConfiguration configuration) => _configuration = configuration;

    public IReadOnlyList<Release> ReadReleases(string project)
        => ReadReleaseRecords(project).Select(item => item.Release).ToArray();

    public IReadOnlyList<ReleaseRecord> ReadReleaseRecords(string project)
    {
        var directory = Path.Combine(ProjectRoot(project), "releases");
        if (!Directory.Exists(directory)) return [];
        return Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
            .Select(ReadRelease)
            .OrderBy(item => item.Release.CreatedAtUtc).ThenBy(item => item.Release.Id, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Writes a release once: a flushed temporary file moved into place
    /// without overwrite, so a crash leaves either no release or a complete
    /// one. A second write with the same id never replaces the file; it
    /// returns the stored release and <c>false</c>.
    /// </summary>
    public (ReleaseRecord Stored, bool Created) AppendRelease(string project, ReleaseRecord release)
    {
        var directory = Path.Combine(ProjectRoot(project), "releases");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, release.Release.Id + ".json");
        if (File.Exists(path)) return (ReadRelease(path), false);
        var temporary = WriteFlushed(path, release);
        try
        {
            File.Move(temporary, path, overwrite: false);
            return (release, true);
        }
        catch (IOException) when (File.Exists(path))
        {
            return (ReadRelease(path), false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
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
        File.Move(WriteFlushed(path, deployments), path, true);
    }

    public IReadOnlyList<RunbookRun> ReadRunbookRuns(string project)
    {
        var path = Path.Combine(ProjectRoot(project), "runbooks.json");
        return File.Exists(path)
            ? JsonSerializer.Deserialize<List<RunbookRun>>(File.ReadAllText(path), _json) ?? []
            : [];
    }

    public void SaveRunbookRuns(string project, IReadOnlyList<RunbookRun> runs)
    {
        var root = ProjectRoot(project);
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "runbooks.json");
        File.Move(WriteFlushed(path, runs), path, true);
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
        lock (_auditGate)
        {
            Directory.CreateDirectory(root);
            File.AppendAllLines(Path.Combine(root, "audit.jsonl"), lines);
        }
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<DeployAuditEntry> ReadAudit(string project, int limit)
    {
        var path = Path.Combine(ProjectRoot(project), "audit.jsonl");
        lock (_auditGate)
        {
            if (!File.Exists(path)) return [];
            return File.ReadLines(path).Where(line => line.Length > 0)
                .Select(line => JsonSerializer.Deserialize<DeployAuditEntry>(line, _line)!)
                .Reverse().Take(limit).ToArray();
        }
    }

    private ReleaseRecord ReadRelease(string path)
        => JsonSerializer.Deserialize<ReleaseRecord>(File.ReadAllText(path), _json) is { Release: not null } record
            ? record with { Environments = record.Environments ?? [] }
            : throw new JsonException($"Release file {Path.GetFileName(path)} has no release.");

    private string WriteFlushed<T>(string path, T value)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value, _json);
            stream.Flush(flushToDisk: true);
        }
        return temporary;
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
