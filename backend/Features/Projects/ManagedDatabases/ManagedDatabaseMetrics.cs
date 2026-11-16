using AgentStudio.Telemetry;

namespace AgentStudio.Projects;

/// <summary>
/// Writes what the run host reports about a managed database into the
/// environment's operations intake (AGT-3878 on AGT-3227), next to the app's
/// own OTLP metrics: data directory size, client connections and their limit,
/// readiness, and the age and size of the newest dump. Studio is the producer,
/// so no ingest token is involved. A full intake queue drops the sample; the
/// next report brings a newer one. Names follow the OpenTelemetry Collector's
/// PostgreSQL receiver where one exists.
/// </summary>
public sealed class ManagedDatabaseMetrics(TelemetryIntakeQueue queue, TimeProvider clock, ILogger<ManagedDatabaseMetrics> logger)
{
    public const string VolumeSize = "agent_studio.managed_database.volume.size";
    public const string Backends = "postgresql.backends";
    public const string ConnectionMax = "postgresql.connection.max";
    public const string Ready = "agent_studio.managed_database.ready";
    public const string BackupAge = "agent_studio.managed_database.backup.age";
    public const string BackupSize = "agent_studio.managed_database.backup.size";

    /// <summary>A generous per-sample estimate for the intake's memory budget.</summary>
    private const long BytesPerSample = 256;

    public void Publish(IEnumerable<(string Project, ManagedDatabaseRecord Record)> measured)
    {
        foreach (var group in measured.GroupBy(item => (item.Project, item.Record.EnvironmentId)))
        {
            var samples = group.SelectMany(item => Samples(item.Record)).ToArray();
            if (samples.Length == 0) continue;
            var result = queue.TryEnqueue(new TelemetryIngestBatch(new(group.Key.Project, group.Key.EnvironmentId), samples, [],
                samples.Length * BytesPerSample));
            if (result != TelemetryEnqueueResult.Accepted)
                logger.LogInformation("managed-database metrics not queued project={Project} environment={Environment} result={Result}",
                    group.Key.Project, group.Key.EnvironmentId, result);
        }
    }

    public IReadOnlyList<MetricSample> Samples(ManagedDatabaseRecord record)
    {
        if (record.DeletedAt is not null) return [];
        var at = record.ObservedAt is { } observed ? new DateTimeOffset(DateTime.SpecifyKind(observed, DateTimeKind.Utc)) : clock.GetUtcNow();
        var time = TelemetryAdmissionPolicy.UnixNanos(at);
        var attributes = TelemetryAttributes.From(
        [
            KeyValuePair.Create("db.system.name", "postgresql"),
            KeyValuePair.Create("db.namespace", record.Name),
            KeyValuePair.Create("agent_studio.run_host", record.HostId),
        ], shared: true);
        MetricSample Gauge(string name, string unit, double value)
            => new(name, unit, MetricKind.Gauge, MetricTemporality.Unspecified, attributes, time, 0, value, 1, null, null);
        var samples = new List<MetricSample>();
        if (record.VolumeBytes is { } bytes) samples.Add(Gauge(VolumeSize, "By", bytes));
        if (record.Connections is { } connections) samples.Add(Gauge(Backends, "{connection}", connections));
        if (record.MaxConnections is { } maximum) samples.Add(Gauge(ConnectionMax, "{connection}", maximum));
        if (record.Ready is { } ready) samples.Add(Gauge(Ready, "1", ready ? 1 : 0));
        if (record.Backups.Count > 0)
        {
            var newest = record.Backups.MaxBy(item => item.TakenAt)!;
            samples.Add(Gauge(BackupAge, "s", Math.Max(0, (at.UtcDateTime - newest.TakenAt).TotalSeconds)));
            samples.Add(Gauge(BackupSize, "By", newest.Bytes));
        }
        return samples;
    }
}
