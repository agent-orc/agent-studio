using AgentStudio.TaskServer.Contracts;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentStudio.Git;

public sealed record IntegrationConflictCommit(string Sha, IReadOnlyList<string> CardKeys);
public sealed record IntegrationConflictFile(string Path, int? HunkCount, IReadOnlyList<string> CardKeys)
{
    public IReadOnlyList<IntegrationConflictCommit> Commits { get; init; } = [];
}

/// <summary>Version 1 selection evidence. Unknown evidence stays null rather than becoming zero.</summary>
public sealed record IntegrationConflictEntry(
    int Version, string Id, string Project, string CardKey, string IntegrationTarget,
    string DeliverySha, string DeliveryBaseSha, string IntegrationTipSha,
    DateTimeOffset DetectedAtUtc, DateTimeOffset? ReviewPassedAtUtc, double? ReviewWaitSeconds,
    int CommitsSinceBase, IReadOnlyList<IntegrationConflictFile> Files,
    string Resolution, DateTimeOffset ResolutionAtUtc);

/// <summary>
/// Application-owned evidence, independent of task moves and worktree cleanup.
/// A delivery/tip/target collision is one record even when several recovery stages see it.
/// </summary>
public sealed class IntegrationConflictLedger(string? root = null)
{
    private static readonly ConcurrentDictionary<string, object> Locks = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _root = root ?? LocalStateRoot.Combine("agentstudio", "integration-conflicts");
    private readonly ConcurrentDictionary<string, (long Length, DateTime Modified, IntegrationConflictEntry Row)> _readCache = new(StringComparer.Ordinal);
    public static IntegrationConflictLedger Default { get; } = new();

    private string Folder(string project) => Path.Combine(_root, Hash(project.ToUpperInvariant()));
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public void Record(IntegrationConflictEntry row)
    {
        var folder = Folder(row.Project);
        lock (Locks.GetOrAdd(folder, _ => new object()))
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, row.Id + ".json");
            if (File.Exists(path))
            {
                var existing = JsonSerializer.Deserialize<IntegrationConflictEntry>(File.ReadAllText(path), Json)!;
                var files = existing.Files.Concat(row.Files).DistinctBy(file => file.Path, StringComparer.Ordinal).ToArray();
                if (files.Length != existing.Files.Count) Write(path, existing with { Files = files });
                return;
            }
            Write(path, row);
        }
    }

    public void Resolve(string project, string cardKey, string deliverySha, string target, string resolution, bool preserveUnion = false, string? integrationTip = null)
    {
        try { ResolveCore(project, cardKey, deliverySha, target, resolution, preserveUnion, integrationTip); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AgentStudio.Diagnostics.SilentCatch.Note(ex, "Integration conflict resolution evidence could not be persisted.");
        }
    }

    private void ResolveCore(string project, string cardKey, string deliverySha, string target, string resolution,
        bool preserveUnion, string? integrationTip)
    {
        if (string.IsNullOrWhiteSpace(integrationTip)) return;
        var folder = Folder(project);
        lock (Locks.GetOrAdd(folder, _ => new object()))
        {
            var matches = Read(project).Where(row =>
                         row.CardKey.Equals(cardKey, StringComparison.OrdinalIgnoreCase)
                         && row.DeliverySha.Equals(deliverySha, StringComparison.OrdinalIgnoreCase)
                         && row.IntegrationTarget.Equals(target, StringComparison.Ordinal)
                         && row.IntegrationTipSha.Equals(integrationTip, StringComparison.OrdinalIgnoreCase)
                         && (!preserveUnion || row.Resolution != "union"));
            foreach (var row in matches.OrderByDescending(row => row.DetectedAtUtc).Take(1))
                Write(Path.Combine(folder, row.Id + ".json"), row with
                {
                    Resolution = resolution,
                    ResolutionAtUtc = DateTimeOffset.UtcNow,
                });
        }
    }

    /// <summary>Read one known collision without enumerating the project ledger.</summary>
    internal IntegrationConflictEntry? ReadCollision(string project, string cardKey, string target,
        string deliverySha, string tip)
    {
        var id = Hash($"{project.ToUpperInvariant()}\n{cardKey.ToUpperInvariant()}\n{target}\n{deliverySha}\n{tip}");
        var folder = Folder(project);
        lock (Locks.GetOrAdd(folder, _ => new object()))
        {
            var path = Path.Combine(folder, id + ".json");
            return File.Exists(path)
                ? JsonSerializer.Deserialize<IntegrationConflictEntry>(File.ReadAllText(path), Json) : null;
        }
    }

    public IReadOnlyList<IntegrationConflictEntry> Read(string project, DateTimeOffset? from = null, DateTimeOffset? to = null, string? target = null)
    {
        var folder = Folder(project);
        lock (Locks.GetOrAdd(folder, _ => new object()))
        {
            if (!Directory.Exists(folder)) return [];
            return Directory.EnumerateFiles(folder, "*.json")
                .Select(path =>
                {
                    var file = new FileInfo(path);
                    if (_readCache.TryGetValue(path, out var cached)
                        && cached.Length == file.Length && cached.Modified == file.LastWriteTimeUtc) return cached.Row;
                    var row = JsonSerializer.Deserialize<IntegrationConflictEntry>(File.ReadAllText(path), Json)!;
                    _readCache[path] = (file.Length, file.LastWriteTimeUtc, row);
                    return row;
                })
                .Where(row => (from is null || row.DetectedAtUtc >= from)
                    && (to is null || row.DetectedAtUtc < to)
                    && (target is null || row.IntegrationTarget == target))
                .OrderBy(row => row.DetectedAtUtc).ToArray();
        }
    }

    private void Write(string path, IntegrationConflictEntry row)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(row, Json));
        File.Move(temporary, path, overwrite: true);
        _readCache.TryRemove(path, out _);
    }
}

/// <summary>Flows identity through isolated Git recovery worktrees without changing transition rules.</summary>
public static class IntegrationConflictCapture
{
    internal sealed record Context(string Project, string CardKey, string Target, string DeliverySha,
        string? BaseSha, Lazy<DateTimeOffset?> ReviewedAt, IntegrationConflictLedger Ledger, string? Tip = null);
    private static readonly AsyncLocal<Context?> Current = new();
    internal static Context? Active => Current.Value;

    public static IDisposable Begin(string project, string cardKey, string target, string deliverySha,
        string? baseSha = null, DateTimeOffset? reviewedAt = null, IntegrationConflictLedger? ledger = null,
        Func<DateTimeOffset?>? reviewTimeProvider = null)
        => Set(new Context(project, cardKey, target, deliverySha, baseSha, new Lazy<DateTimeOffset?>(() => reviewTimeProvider is null ? reviewedAt : reviewTimeProvider()),
            ledger ?? IntegrationConflictLedger.Default));

    internal static IDisposable Pin(string deliverySha, string tip, string? target = null)
        => Set(Current.Value is { } current ? current with { DeliverySha = deliverySha, Tip = tip, Target = target ?? current.Target } : null);

    private static IDisposable Set(Context? next)
    {
        var previous = Current.Value;
        Current.Value = next;
        return new Scope(() => Current.Value = previous);
    }
    private sealed class Scope(Action restore) : IDisposable { public void Dispose() => restore(); }

    internal static void Resolve(string resolution)
    {
        if (Active is { } current)
            current.Ledger.Resolve(current.Project, current.CardKey, current.DeliverySha, current.Target, resolution, preserveUnion: resolution == "mechanical", integrationTip: current.Tip);
    }
}
