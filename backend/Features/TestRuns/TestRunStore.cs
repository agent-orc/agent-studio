using System.Text.Json;

namespace AgentStudio.TestRuns;

/// <summary>
/// Durable project-scoped test-run store. Test runs are independent evidence
/// objects and therefore live beside registry metadata, never inside a task.
/// </summary>
public sealed class TestRunStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly string? _root;
    private readonly IAtomicJsonFileWriter _writer;
    private readonly object _gate = new();

    public TestRunStore(IConfiguration configuration, IAtomicJsonFileWriter? writer = null)
    {
        _root = configuration["TaskRepository"];
        _writer = (writer ?? new AtomicJsonFileWriter()).WithLastGoodBackup();
    }

    public IReadOnlyList<TestRunRecord> List(string projectId)
    {
        lock (_gate) return Read(projectId).Runs.ToList();
    }

    public TestRunRecord Add(string projectId, TestRunRecord run)
    {
        lock (_gate)
        {
            var file = Read(projectId);
            if (file.Runs.Any(item => string.Equals(item.Id, run.Id, StringComparison.OrdinalIgnoreCase)))
                throw new TestRunValidationException($"Test run '{run.Id}' already exists.");
            file.Runs.Add(run);
            Write(projectId, file);
            return run;
        }
    }

    public TestRunRecord? Update(string projectId, string runId, Func<TestRunRecord, TestRunRecord> update)
    {
        lock (_gate)
        {
            var file = Read(projectId);
            var index = file.Runs.FindIndex(item => string.Equals(item.Id, runId, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return null;
            file.Runs[index] = update(file.Runs[index]);
            Write(projectId, file);
            return file.Runs[index];
        }
    }

    private TestRunsFile Read(string projectId)
    {
        var path = PathFor(projectId);
        if (path is null || (!File.Exists(path) && !File.Exists(path + ".bak"))) return new();
        try
        {
            return JsonSerializer.Deserialize<TestRunsFile>(AtomicJsonFileWriter.ReadAllTextWithBackup(path), Json) ?? new();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new ProjectPersistenceException($"Test runs for project '{projectId}' could not be read.", ex);
        }
    }

    private void Write(string projectId, TestRunsFile file)
    {
        var path = PathFor(projectId)
            ?? throw new ProjectPersistenceException("Task repository is not configured.", new InvalidOperationException());
        try
        {
            _writer.Write(path, JsonSerializer.Serialize(file, Json) + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ProjectPersistenceException($"Test runs for project '{projectId}' could not be persisted.", ex);
        }
    }

    private string? PathFor(string projectId)
    {
        if (string.IsNullOrWhiteSpace(_root)) return null;
        if (!System.Text.RegularExpressions.Regex.IsMatch(projectId, "^PROJ-[0-9]{3,}$"))
            throw new TestRunValidationException("A stable project id is required for test-run storage.");
        return Path.Combine(_root, ".metadata", "test-runs", projectId + ".json");
    }

    private sealed record TestRunsFile
    {
        public int SchemaVersion { get; init; } = 1;
        public List<TestRunRecord> Runs { get; init; } = [];
    }
}
