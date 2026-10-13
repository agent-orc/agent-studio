using System.Text.Json;
using AgentStudio.Persistence;

namespace AgentStudio.Runner;

public sealed record CrashRecoveryDismissal(
    string ProjectName,
    string Worktree,
    string Classification,
    string PendingId,
    string ContentFingerprint,
    int FileCount,
    DateTimeOffset DismissedAt);

/// <summary>Durable operator decisions for unchanged orphan findings.</summary>
public sealed class CrashRecoveryDismissalStore
{
    public const string RelativePath = ".metadata/crash-recovery-dismissals.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private readonly object _gate = new();
    private readonly string? _path;
    private readonly IAtomicJsonFileWriter _writer;
    private readonly TimeProvider _time;
    private List<CrashRecoveryDismissal> _dismissals;

    public CrashRecoveryDismissalStore(IConfiguration configuration, TimeProvider time,
        IAtomicJsonFileWriter? writer = null)
        : this(configuration["TaskRepository"], time, writer)
    {
    }

    internal CrashRecoveryDismissalStore(string? root, TimeProvider time, IAtomicJsonFileWriter? writer = null)
    {
        _path = string.IsNullOrWhiteSpace(root) ? null : Path.Combine(root, RelativePath);
        _time = time;
        _writer = (writer ?? new AtomicJsonFileWriter()).WithLastGoodBackup();
        _dismissals = _path is not null && (File.Exists(_path) || File.Exists(_path + ".bak"))
            ? JsonSerializer.Deserialize<List<CrashRecoveryDismissal>>(
                AtomicJsonFileWriter.ReadAllTextWithBackup(_path), Json)
                ?? throw new InvalidDataException("Crash recovery dismissal file is empty.")
            : [];
    }

    public CrashRecoveryDismissal? Find(string projectName, string worktree)
    {
        var canonical = CanonicalWorktree(worktree);
        lock (_gate)
            return _dismissals.Find(item => SameLocation(item, projectName, canonical));
    }

    public CrashRecoveryDismissal Record(PendingCrashRecovery pending)
    {
        var item = new CrashRecoveryDismissal(pending.ProjectName, CanonicalWorktree(pending.RepoRoot),
            pending.Classification, pending.Id, pending.ContentFingerprint, pending.Files.Count,
            _time.GetUtcNow());
        lock (_gate)
        {
            var next = _dismissals.Where(existing => !SameLocation(existing, item.ProjectName, item.Worktree))
                .Append(item).ToList();
            Persist(next);
            _dismissals = next;
        }
        return item;
    }

    public void Remove(string projectName, string worktree)
    {
        var canonical = CanonicalWorktree(worktree);
        lock (_gate)
        {
            var next = _dismissals.Where(item => !SameLocation(item, projectName, canonical)).ToList();
            if (next.Count == _dismissals.Count) return;
            Persist(next);
            _dismissals = next;
        }
    }

    private void Persist(List<CrashRecoveryDismissal> items)
        => _writer.Write(_path ?? throw new InvalidOperationException(
            "TaskRepository is required for durable crash recovery dismissals."),
            JsonSerializer.Serialize(items, Json));

    private static string CanonicalWorktree(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool SameLocation(CrashRecoveryDismissal item, string projectName, string worktree)
        => string.Equals(item.ProjectName, projectName, StringComparison.Ordinal)
            && string.Equals(item.Worktree, worktree,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
