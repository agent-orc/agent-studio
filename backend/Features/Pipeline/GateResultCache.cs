using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentStudio.Runner;

namespace AgentStudio.Pipeline;

/// <summary>How a gate verdict was obtained. This is independent of pass/fail.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<GateVerdictSource>))]
public enum GateVerdictSource
{
    Executed,
    CacheHit,
}

public sealed record GateRetestReport(
    int Executed,
    int SameShaRetests,
    double SameShaRetestRate,
    int CacheHits,
    int WindowSize);

/// <summary>
/// Durable exact-subject verdicts. Entries live in local application data, not
/// in a task worktree. One project lock serializes a lookup with its execution
/// and write, and also fences operator invalidation. Only deterministic terminal
/// verdicts are retained. An entry file is the original execution evidence.
/// </summary>
public sealed class GateResultCache
{
    public const int MaxEntriesPerProject = 128;
    public const int RetestWindowSize = 4096;
    private const int MaxEntryBytes = 2_000_000;
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProjectLocks = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _root;

    public GateResultCache(string? root = null)
    {
        var data = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _root = root ?? Path.Combine(
            string.IsNullOrWhiteSpace(data) ? AppContext.BaseDirectory : data,
            "agentstudio", "gate-results");
    }

    public async Task<IDisposable> AcquireAsync(string project, CancellationToken ct)
    {
        var gate = ProjectLocks.GetOrAdd(ProjectPath(project), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        return new Releaser(gate);
    }

    public BuildTestGateResult? TryRead(string project, string sha, string digest)
    {
        try
        {
            var dir = ProjectPath(project);
            if (!Directory.Exists(dir)) return null;
            var path = Directory.EnumerateFiles(dir, $"entry-{sha.ToLowerInvariant()}-{digest}-*.json")
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (path is null || DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > MaxAge)
                return null;
            var entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(path), Json);
            if (entry is null
                || !string.Equals(entry.Sha, sha, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(entry.Digest, digest, StringComparison.Ordinal)
                || entry.Result.GateCompletedAtUtc is null
                || entry.Result.OriginEvidencePath != path
                || entry.Result.VerdictSource != GateVerdictSource.Executed
                || !Eligible(entry.Result))
                return null;
            CountHit(project);
            return entry.Result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null; // Corrupt or unavailable cache never creates a verdict.
        }
    }

    public BuildTestGateResult Record(string project, string sha, string digest, BuildTestGateResult result)
    {
        try
        {
            // Count actual revalidations even if the result is unsuitable for reuse.
            RecordExecution(project, sha);
            if (!Eligible(result) || result.GateCompletedAtUtc is null)
                return result;
            var path = EntryPath(project, sha, digest, result.GateRunId ?? Guid.NewGuid().ToString("N"));
            var evidenced = result with { OriginEvidencePath = path };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new Entry(sha, digest, evidenced), Json);
            if (bytes.Length > MaxEntryBytes) return result;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path);
            Prune(project);
            return evidenced;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return result; // A cache write is never part of the gate verdict.
        }
    }

    public async Task InvalidateAsync(string project, CancellationToken ct = default)
    {
        using (await AcquireAsync(project, ct).ConfigureAwait(false))
        {
            var path = ProjectPath(project);
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    public GateRetestReport Report(string project)
    {
        try
        {
            var stats = ReadStats(project);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var repeated = stats.Shas.Count(sha => !seen.Add(sha));
            return new GateRetestReport(stats.Shas.Count, repeated,
                stats.Shas.Count == 0 ? 0 : (double)repeated / stats.Shas.Count,
                stats.CacheHits, RetestWindowSize);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new GateRetestReport(0, 0, 0, 0, RetestWindowSize);
        }
    }

    public static string ProfileDigest(
        BuildTestGateRequest request, BuildProfile? profile, PostStepMode mode,
        IReadOnlyList<string>? changedFiles, IReadOnlyList<VerifyCommand> commands,
        string toolchainIdentity)
    {
        var payload = JsonSerializer.Serialize(new
        {
            Version = 1,
            request.GateId,
            request.PipelineDefinitionVersion,
            ToolchainIdentity = toolchainIdentity,
            ProfileFingerprint = BuildProfileValidationFingerprint.Create(profile),
            request.TestExecution,
            request.RequiredTestLevel,
            request.Lane,
            Mode = mode.ToString(),
            ChangedFiles = changedFiles?.OrderBy(path => path, StringComparer.Ordinal).ToArray(),
            Commands = commands,
        }, Json);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    public static string LocalToolchainIdentity(IReadOnlyList<VerifyCommand> commands)
    {
        var names = commands.Select(command => command.Command.Split(' ', '\t', '\r', '\n')
                .FirstOrDefault() ?? "")
            .Concat(["dotnet", "node", "npm", "bash", "sh"])
            .Where(name => name.Length > 0 && !name.Contains('/'))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal);
        var parts = new List<string>
        {
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeInformation.FrameworkDescription,
        };
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        foreach (var name in names)
        {
            var path = paths.Select(dir => Path.Combine(dir, name))
                .FirstOrDefault(File.Exists);
            if (path is null) { parts.Add(name + ":missing"); continue; }
            var info = new FileInfo(path);
            parts.Add($"{name}:{info.FullName}:{info.Length}:{info.LastWriteTimeUtc.Ticks}");
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", parts))))
            .ToLowerInvariant();
    }

    private static bool Eligible(BuildTestGateResult result) =>
        (result.Verdict is BuildTestGateVerdict.Ok or BuildTestGateVerdict.Warn
            or BuildTestGateVerdict.Fail)
        && (result.FailureKind is BuildTestGateFailureKind.None or BuildTestGateFailureKind.Code);

    private void RecordExecution(string project, string sha)
    {
        var stats = ReadStats(project);
        stats.Shas.Add(sha);
        if (stats.Shas.Count > RetestWindowSize)
            stats.Shas.RemoveRange(0, stats.Shas.Count - RetestWindowSize);
        WriteStats(project, stats);
    }

    private void CountHit(string project)
    {
        var stats = ReadStats(project);
        stats.CacheHits++;
        WriteStats(project, stats);
    }

    private Stats ReadStats(string project)
    {
        var path = Path.Combine(ProjectPath(project), "statistics.json");
        return File.Exists(path)
            ? JsonSerializer.Deserialize<Stats>(File.ReadAllText(path), Json) ?? new Stats()
            : new Stats();
    }

    private void WriteStats(string project, Stats stats)
    {
        var path = Path.Combine(ProjectPath(project), "statistics.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(stats, Json));
        File.Move(temporary, path, overwrite: true);
    }

    private void Prune(string project)
    {
        var dir = ProjectPath(project);
        var entries = Directory.EnumerateFiles(dir, "entry-*.json")
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc).ToList();
        for (var index = 0; index < entries.Count; index++)
            if (index >= MaxEntriesPerProject || DateTime.UtcNow - entries[index].LastWriteTimeUtc > MaxAge)
                entries[index].Delete();
    }

    private string ProjectPath(string project) => Path.Combine(_root, Hash(project.Trim().ToUpperInvariant()));
    private string EntryPath(string project, string sha, string digest, string runId) =>
        Path.Combine(ProjectPath(project), $"entry-{sha.ToLowerInvariant()}-{digest}-{runId}.json");
    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record Entry(string Sha, string Digest, BuildTestGateResult Result);
    private sealed class Stats { public List<string> Shas { get; set; } = []; public int CacheHits { get; set; } }
    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}
