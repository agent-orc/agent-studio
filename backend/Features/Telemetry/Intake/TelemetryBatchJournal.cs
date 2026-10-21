using System.Text.Json;

namespace AgentStudio.Telemetry;

/// <summary>Durable, atomic intake handoff. A staged file survives a process exit until its store transaction commits.</summary>
internal sealed class TelemetryBatchJournal
{
    private readonly string _root;
    private readonly ITelemetryValueMasker _masker;
    private readonly ITelemetryDurability _durability;
    private long _nextOrder;

    public TelemetryBatchJournal(string root, ITelemetryValueMasker masker, ITelemetryDurability durability)
    {
        _root = Path.Combine(root, "pending");
        _masker = masker;
        _durability = durability;
        Directory.CreateDirectory(_root);
        _durability.FlushParentDirectory(_root);
        _durability.FlushParentDirectory(Path.GetDirectoryName(_root)!);
        foreach (var completed in Directory.EnumerateFiles(_root, "*.done")) File.Delete(completed);
        _nextOrder = Directory.EnumerateFiles(_root, "*.json")
            .Select(path => OrderOf(Path.GetFileNameWithoutExtension(path))).DefaultIfEmpty().Max();
    }

    public IEnumerable<TelemetryIngestBatch> Recover()
        => Directory.EnumerateFiles(_root, "*.json")
            .OrderBy(path => OrderOf(Path.GetFileNameWithoutExtension(path)))
            .ThenBy(path => path, StringComparer.Ordinal)
            .Select(path => Read(path));

    public TelemetryIngestBatch Stage(TelemetryIngestBatch batch)
    {
        var staged = batch with
        {
            Metrics = batch.Metrics.Select(item => item with
                { Attributes = item.Attributes.MapValues(_masker.Mask) }).ToArray(),
            Logs = batch.Logs.Select(item => item with
                { Body = _masker.Mask(item.Body), SeverityText = _masker.Mask(item.SeverityText),
                    EventName = item.EventName is null ? null : _masker.Mask(item.EventName),
                    Attributes = item.Attributes.MapValues(_masker.Mask) }).ToArray()
        };
        staged = staged with { StageId = $"{++_nextOrder:D20}-{Guid.NewGuid():N}" };
        var path = PathFor(staged.StageId);
        var temporary = path + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, ToWire(staged));
                stream.Flush(flushToDisk: true);
            }
            _durability.MoveStagedFile(temporary, path);
            return staged;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public void Complete(TelemetryIngestBatch batch)
    {
        if (batch.StageId is not null)
        {
            var staged = PathFor(batch.StageId);
            var completed = staged + ".done";
            // Persist the rename before the SQLite replay marker is removed. A
            // crash may leave a .done file, but it cannot replay that batch.
            if (File.Exists(staged)) _durability.MoveStagedFile(staged, completed);
            if (File.Exists(completed)) File.Delete(completed);
        }
    }

    private static long OrderOf(string stageId)
        => stageId.Length > 21 && stageId[20] == '-' && long.TryParse(stageId[..20], out var order)
            ? order : 0; // Legacy UUID stages have no recoverable intake order.

    private TelemetryIngestBatch Read(string path)
    {
        using var stream = File.OpenRead(path);
        var wire = JsonSerializer.Deserialize<BatchWire>(stream) ?? throw new InvalidDataException($"Empty telemetry batch: {path}");
        var id = Path.GetFileNameWithoutExtension(path);
        return new TelemetryIngestBatch(wire.Environment,
            wire.Metrics.Select(item => new MetricSample(item.Name, item.Unit, item.Kind, item.Temporality,
                TelemetryAttributes.ParseJson(item.Attributes), item.TimeUnixNano, item.StartTimeUnixNano,
                item.Value, item.Count, item.Min, item.Max, item.IsMonotonic)).ToList(),
            wire.Logs.Select(item => new TelemetryLogRecord(item.TimeUnixNano, item.SeverityNumber,
                item.SeverityText, item.Body, TelemetryAttributes.ParseJson(item.Attributes), item.TraceId,
                item.SpanId, item.EventName)).ToList(), wire.EstimatedBytes, wire.StoredBytes, id);
    }

    private static BatchWire ToWire(TelemetryIngestBatch batch)
        => new(batch.Environment,
            batch.Metrics.Select(item => new MetricWire(item.Name, item.Unit, item.Kind, item.Temporality,
                item.Attributes.Json, item.TimeUnixNano, item.StartTimeUnixNano, item.Value, item.Count,
                item.Min, item.Max, item.IsMonotonic)).ToArray(),
            batch.Logs.Select(item => new LogWire(item.TimeUnixNano, item.SeverityNumber, item.SeverityText,
                item.Body, item.Attributes.Json, item.TraceId, item.SpanId, item.EventName)).ToArray(),
            batch.EstimatedBytes, batch.StoredBytes);

    private string PathFor(string id) => Path.Combine(_root, id + ".json");

    private sealed record BatchWire(TelemetryEnvironmentKey Environment, MetricWire[] Metrics, LogWire[] Logs,
        long EstimatedBytes, long StoredBytes);
    private sealed record MetricWire(string Name, string Unit, MetricKind Kind, MetricTemporality Temporality,
        string Attributes, long TimeUnixNano, long StartTimeUnixNano, double Value, double Count,
        double? Min, double? Max, bool IsMonotonic);
    private sealed record LogWire(long TimeUnixNano, int SeverityNumber, string SeverityText, string Body,
        string Attributes, string? TraceId, string? SpanId, string? EventName);
}
