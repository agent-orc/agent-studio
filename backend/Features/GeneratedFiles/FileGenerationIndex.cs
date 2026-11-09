using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentStudio.GeneratedFiles;

/// <summary>
/// Per-job sidecar for generated Markdown provenance. Producers upsert the
/// exact file they wrote; readers may merge a legacy projection from
/// pipeline-execution.json and cache it into the sidecar.
/// </summary>
public sealed class FileGenerationIndex
{
    public const string RelativePath = ".metadata/files.json";

    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly ConcurrentDictionary<string, object> PathLocks = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private readonly PipelineExecutionLog? _pipelineLog;
    private readonly ILogger<FileGenerationIndex> _logger;

    public FileGenerationIndex(ILogger<FileGenerationIndex> logger, PipelineExecutionLog? pipelineLog = null)
    {
        _logger = logger;
        _pipelineLog = pipelineLog;
    }

    public void Upsert(string jobFolderPath, FileGenerationMeta meta)
    {
        if (string.IsNullOrWhiteSpace(jobFolderPath) || string.IsNullOrWhiteSpace(meta.File)) return;
        try
        {
            lock (LockFor(jobFolderPath))
            {
                var entries = ReadRaw(jobFolderPath);
                var normalized = Normalize(meta.File);
                entries.RemoveAll(e => string.Equals(Normalize(e.File), normalized, StringComparison.OrdinalIgnoreCase));
                entries.Add(NormalizeTotals(meta));
                WriteAtomic(jobFolderPath, entries);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "file-generation-index: failed to upsert {File} in {JobFolder}", meta.File, jobFolderPath);
        }
    }

    public int? CurrentRunIndex(string jobFolderPath)
        => _pipelineLog?.Read(jobFolderPath)?.Attempt;

    public IReadOnlyDictionary<string, FileGenerationMeta> ReadForJob(string jobFolderPath, bool cacheLegacy = true)
    {
        lock (LockFor(jobFolderPath))
        {
            var entries = ReadRaw(jobFolderPath);
            var changed = false;

            if (cacheLegacy)
            {
                foreach (var legacy in LegacyFromPipeline(jobFolderPath))
                {
                    var normalized = Normalize(legacy.File);
                    if (entries.Any(e => string.Equals(Normalize(e.File), normalized, StringComparison.OrdinalIgnoreCase))) continue;
                    entries.Add(legacy);
                    changed = true;
                }
                if (changed) WriteAtomic(jobFolderPath, entries);
            }

            return entries
                .GroupBy(e => Normalize(e.File), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => NormalizeTotals(g.Last()), StringComparer.OrdinalIgnoreCase);
        }
    }

    public static FileGenerationMeta FromStep(
        string file,
        string kind,
        string? cli,
        PipelineStepExecution step,
        int? runIndex = null,
        string? headShaAfter = null)
    {
        var total = step.InputTokens + step.OutputTokens + step.CacheReadTokens + step.CacheCreationTokens;
        return new FileGenerationMeta
        {
            File = file,
            Kind = kind,
            Model = step.Model,
            Cli = cli,
            TokensIn = step.InputTokens,
            TokensOut = step.OutputTokens,
            CacheReadTokens = step.CacheReadTokens,
            CacheCreationTokens = step.CacheCreationTokens,
            TokensTotal = total,
            StartedAt = step.StartedAt,
            EndedAt = step.CompletedAt,
            DurationMs = step.DurationMs,
            RunIndex = runIndex,
            StepId = step.StepId,
            HeadShaAfter = headShaAfter,
        };
    }

    private IEnumerable<FileGenerationMeta> LegacyFromPipeline(string jobFolderPath)
    {
        var record = _pipelineLog?.Read(jobFolderPath);
        if (record == null) yield break;
        foreach (var step in record.Steps)
        {
            var file = FileForStep(step);
            if (file == null) continue;
            yield return FromStep(file, KindForStep(step), "claude", step, record.Attempt);
        }
    }

    private static string? FileForStep(PipelineStepExecution step)
    {
        if (step.Kind == StepKind.Aspect && step.StepId.StartsWith("aspect-", StringComparison.OrdinalIgnoreCase))
            return step.StepId + ".md";
        if (step.Kind == StepKind.Drift)
            return $"logs/drift/{step.StepId}.md";
        return null;
    }

    private static string KindForStep(PipelineStepExecution step) => step.Kind switch
    {
        StepKind.Aspect => "aspect",
        StepKind.Drift => "drift",
        _ => step.Kind.ToString().ToLowerInvariant(),
    };

    private List<FileGenerationMeta> ReadRaw(string jobFolderPath)
    {
        var path = Path.Combine(jobFolderPath, RelativePath);
        if (!File.Exists(path)) return [];
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<List<FileGenerationMeta>>(stream, ReadOpts) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "file-generation-index: failed to read {Path}", path);
            return [];
        }
    }

    private void WriteAtomic(string jobFolderPath, List<FileGenerationMeta> entries)
    {
        var path = Path.Combine(jobFolderPath, RelativePath);
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(entries.Select(NormalizeTotals).ToList(), WriteOpts));
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    private static object LockFor(string jobFolderPath)
        => PathLocks.GetOrAdd(Path.GetFullPath(Path.Combine(jobFolderPath, RelativePath)), static _ => new object());

    private static string Normalize(string file) => file.Replace('\\', '/').TrimStart('/');

    private static FileGenerationMeta NormalizeTotals(FileGenerationMeta meta)
    {
        var total = meta.TokensTotal > 0
            ? meta.TokensTotal
            : meta.TokensIn + meta.TokensOut + meta.CacheReadTokens + meta.CacheCreationTokens;
        var duration = meta.DurationMs > 0 || meta.StartedAt == null || meta.EndedAt == null
            ? meta.DurationMs
            : (long)(meta.EndedAt.Value - meta.StartedAt.Value).TotalMilliseconds;
        return meta with { File = Normalize(meta.File), TokensTotal = total, DurationMs = duration };
    }
}
