namespace AgentStudio.Telemetry;

/// <summary>What the writer committed for one environment, after masking.</summary>
public sealed record TelemetryExportBatch(
    TelemetryEnvironmentKey Environment, IReadOnlyList<MetricSample> Metrics, IReadOnlyList<TelemetryLogRecord> Logs);

/// <summary>
/// Export hook for external systems (Prometheus, Loki, another OTLP endpoint;
/// Dossier integrierte-plattform IP-D14 option A). The writer calls every
/// registered sink after the built-in store committed a batch. A failing sink
/// is logged and never affects storage or intake. No sink is registered yet;
/// concrete exporters are separate work.
/// </summary>
public interface ITelemetryExportSink
{
    string Name { get; }

    ValueTask ExportAsync(TelemetryExportBatch batch, CancellationToken cancellationToken);
}
