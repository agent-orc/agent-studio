using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentStudio.Tasks;

/// <summary>
/// Owns replacement of the task's current <c>status.md</c>. Before a producer
/// publishes a new result, the prior document is copied into a readable
/// <c>results/history/&lt;n&gt;-&lt;timestamp&gt;/</c> snapshot together with its
/// provenance. The current file remains the compatibility contract consumed by
/// the runner and Studio.
/// </summary>
public sealed class ResultVersionStore
{
    public const string CurrentMetadataPath = ".metadata/current-result.json";
    public const string VersionMetadataFile = "result.json";

    private static readonly ConcurrentDictionary<string, object> PathLocks =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly TimeSpan SwapRetryBudget = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    private readonly TimelineLog? _timeline;
    private readonly ILogger<ResultVersionStore> _logger;

    public ResultVersionStore(ILogger<ResultVersionStore> logger, TimelineLog? timeline = null)
    {
        _logger = logger;
        _timeline = timeline;
    }

    public ResultWriteOutcome Replace(
        string folderPath,
        string content,
        ResultProducer producer,
        string lane,
        DateTime? producedAtUtc = null,
        string? actor = null)
    {
        var statusPath = Path.Combine(folderPath, "status.md");
        lock (PathLocks.GetOrAdd(statusPath, static _ => new object()))
        {
            Directory.CreateDirectory(folderPath);
            var at = NormalizeUtc(producedAtUtc ?? DateTime.UtcNow);
            ResultHistoryVersion? preserved = null;
            if (File.Exists(statusPath))
            {
                var previous = File.ReadAllText(statusPath);
                if (!string.IsNullOrWhiteSpace(previous))
                    preserved = PreserveCurrent(folderPath, previous, lane);
            }

            WriteAtomic(statusPath, content);
            WriteCurrentMetadata(folderPath, new CurrentResultMetadata(at, producer, lane));

            if (preserved is not null)
            {
                _timeline?.Append(
                    folderPath,
                    TimelineEventKinds.ResultReplaced,
                    actor ?? ActorFor(producer.Kind),
                    $"Result replaced by {producer.Label}, previous version kept as #{preserved.Number}",
                    payloadRef: preserved.StatusPath,
                    details: new Dictionary<string, string>
                    {
                        ["producer"] = producer.Label,
                        ["producerKind"] = producer.Kind,
                        ["previousVersion"] = preserved.Number.ToString(CultureInfo.InvariantCulture),
                        ["lane"] = lane,
                    });
            }

            return new ResultWriteOutcome(true, preserved, null);
        }
    }

    public IReadOnlyList<ResultHistoryVersion> ReadLocalHistory(string folderPath)
    {
        var historyRoot = Path.Combine(folderPath, "results", "history");
        if (!Directory.Exists(historyRoot)) return [];

        var result = new List<ResultHistoryVersion>();
        foreach (var metadataPath in Directory.EnumerateFiles(
                     historyRoot, VersionMetadataFile, SearchOption.AllDirectories))
        {
            try
            {
                var metadata = JsonSerializer.Deserialize<ResultHistoryVersion>(
                    File.ReadAllText(metadataPath), Json);
                if (metadata is null) continue;
                var versionDir = Path.GetDirectoryName(metadataPath)!;
                var statusPath = Path.Combine(versionDir, "status.md");
                if (!File.Exists(statusPath)) continue;
                result.Add(metadata with
                {
                    StatusPath = Path.GetRelativePath(folderPath, statusPath).Replace('\\', '/'),
                    DeliverablesPath = File.Exists(Path.Combine(versionDir, "deliverables.md"))
                        ? Path.GetRelativePath(folderPath, Path.Combine(versionDir, "deliverables.md")).Replace('\\', '/')
                        : null,
                });
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "result-history: ignored invalid metadata {Path}", metadataPath);
            }
        }

        return result.OrderByDescending(item => item.Number).ToList();
    }

    public string? ReadLocalVersion(string folderPath, int number)
    {
        var match = ReadLocalHistory(folderPath).SingleOrDefault(item => item.Number == number);
        if (match is null) return null;
        var full = Path.GetFullPath(Path.Combine(folderPath, match.StatusPath.Replace('/', Path.DirectorySeparatorChar)));
        return File.Exists(full) ? File.ReadAllText(full) : null;
    }

    private ResultHistoryVersion PreserveCurrent(string folderPath, string content, string fallbackLane)
    {
        var current = ReadCurrentMetadata(folderPath)
            ?? InferCurrent(folderPath, content, fallbackLane);
        var number = NextNumber(folderPath);
        var stamp = current.ProducedAtUtc.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture);
        var relativeDir = Path.Combine("results", "history", $"{number:0000}-{stamp}");
        var directory = Path.Combine(folderPath, relativeDir);
        while (Directory.Exists(directory))
        {
            number++;
            relativeDir = Path.Combine("results", "history", $"{number:0000}-{stamp}");
            directory = Path.Combine(folderPath, relativeDir);
        }

        Directory.CreateDirectory(directory);
        var status = Path.Combine(directory, "status.md");
        WriteAtomic(status, content);
        var deliverablesSource = Path.Combine(folderPath, "results", "deliverables.md");
        string? deliverablesRelative = null;
        if (File.Exists(deliverablesSource))
        {
            var destination = Path.Combine(directory, "deliverables.md");
            File.Copy(deliverablesSource, destination, overwrite: false);
            deliverablesRelative = Path.GetRelativePath(folderPath, destination).Replace('\\', '/');
        }

        var version = new ResultHistoryVersion(
            Number: number,
            ProducedAtUtc: current.ProducedAtUtc,
            Producer: current.Producer,
            Lane: current.Lane,
            StatusPath: Path.GetRelativePath(folderPath, status).Replace('\\', '/'),
            DeliverablesPath: deliverablesRelative);
        WriteAtomic(
            Path.Combine(directory, VersionMetadataFile),
            JsonSerializer.Serialize(version, Json));
        return version;
    }

    private CurrentResultMetadata InferCurrent(string folderPath, string content, string fallbackLane)
    {
        var marker = content.Contains(TaskTransitionService.ResultScaffoldMarker, StringComparison.Ordinal);
        var producer = marker
            ? ResultProducer.Scaffold()
            : InferGeneratedProducer(folderPath);
        var at = File.GetLastWriteTimeUtc(Path.Combine(folderPath, "status.md"));
        if (at == DateTime.MinValue) at = DateTime.UtcNow;
        return new CurrentResultMetadata(NormalizeUtc(at), producer, fallbackLane);
    }

    private static ResultProducer InferGeneratedProducer(string folderPath)
    {
        try
        {
            var path = Path.Combine(folderPath, AgentStudio.GeneratedFiles.FileGenerationIndex.RelativePath);
            if (File.Exists(path))
            {
                var entries = JsonSerializer.Deserialize<List<FileGenerationMeta>>(File.ReadAllText(path), Json) ?? [];
                var status = entries.LastOrDefault(item =>
                    string.Equals(item.File.Replace('\\', '/'), "status.md", StringComparison.OrdinalIgnoreCase));
                if (status?.RunIndex is int runIndex)
                    return ResultProducer.RunAttempt(runIndex.ToString(CultureInfo.InvariantCulture));
            }
        }
        catch (Exception ex)
        {
            // A legacy result remains valuable even when its optional provenance sidecar is corrupt.
            SilentCatch.Note(ex, "ResultVersionStore: invalid legacy generation metadata");
        }
        return ResultProducer.RunAttempt();
    }

    private CurrentResultMetadata? ReadCurrentMetadata(string folderPath)
    {
        var path = Path.Combine(folderPath, CurrentMetadataPath);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<CurrentResultMetadata>(File.ReadAllText(path), Json);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "result-history: ignored invalid current metadata {Path}", path);
            return null;
        }
    }

    private static void WriteCurrentMetadata(string folderPath, CurrentResultMetadata metadata)
        => WriteAtomic(
            Path.Combine(folderPath, CurrentMetadataPath),
            JsonSerializer.Serialize(metadata, Json));

    private int NextNumber(string folderPath)
        => ReadLocalHistory(folderPath).Select(item => item.Number).DefaultIfEmpty(0).Max() + 1;

    private static void WriteAtomic(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            MoveWithRetry(temp, path);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static void MoveWithRetry(string temp, string path)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException
                && started.Elapsed < SwapRetryBudget)
            {
                Thread.Sleep(1);
            }
        }
    }

    private static DateTime NormalizeUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();

    private static string ActorFor(string producerKind) => producerKind switch
    {
        ResultProducerKinds.RunAttempt => TimelineActors.Agent,
        ResultProducerKinds.ReviewAttempt => TimelineActors.Orchestrator,
        ResultProducerKinds.ExternalCompletion => TimelineActors.External,
        _ => TimelineActors.System,
    };
}

public static class ResultProducerKinds
{
    public const string RunAttempt = "run-attempt";
    public const string ReviewAttempt = "review-attempt";
    public const string ExternalCompletion = "external-completion";
    public const string Scaffold = "scaffold";
}

public sealed record ResultProducer(string Kind, string Label, string? Attempt = null)
{
    public static ResultProducer RunAttempt(string? attempt = null) => new(
        ResultProducerKinds.RunAttempt,
        attempt is null ? "run attempt" : $"run attempt #{attempt}",
        attempt);

    public static ResultProducer ReviewAttempt(string? attempt = null) => new(
        ResultProducerKinds.ReviewAttempt,
        attempt is null ? "review attempt" : $"review attempt #{attempt}",
        attempt);

    public static ResultProducer ExternalCompletion(string? source = null) => new(
        ResultProducerKinds.ExternalCompletion,
        string.IsNullOrWhiteSpace(source) ? "external completion" : $"external completion ({source})",
        source);

    public static ResultProducer Scaffold() => new(ResultProducerKinds.Scaffold, "scaffold");
}

public sealed record CurrentResultMetadata(DateTime ProducedAtUtc, ResultProducer Producer, string Lane);

public sealed record ResultHistoryVersion(
    int Number,
    DateTime ProducedAtUtc,
    ResultProducer Producer,
    string Lane,
    string StatusPath,
    string? DeliverablesPath);

public sealed record ResultWriteOutcome(bool Success, ResultHistoryVersion? Preserved, string? Error);
