namespace AgentStudio.Projects;

/// <summary>
/// Aggregate observations only. An intake store must omit personal identifiers,
/// expose endpoint paths without queries, and mask secrets before returning logs.
/// Missing measurements stay null; zero means an observed zero.
/// </summary>
public interface IOperationsTelemetryReader
{
    OperationsMetrics? ReadMetrics(string project, string environmentId, DateTimeOffset since, DateTimeOffset until);
    IReadOnlyList<OperationsLogLine>? SearchLogs(OperationsLogQuery query);
}

/// <param name="ActiveUsers">The most recent UTC daily bucket, deduplicated without exposing identities.</param>
public sealed record OperationsMetrics(double? ResponseP50Ms, double? ResponseP95Ms, double? ErrorRate,
    long? Restarts, long? Requests, long? Sessions, long? ActiveUsers, double? CpuCores,
    double? CpuReservedCores, long? MemoryBytes, long? MemoryReservedBytes, long? DiskBytes,
    long? DiskReservedBytes, long? NetworkBytes, IReadOnlyList<OperationsEndpointCount> TopEndpoints,
    IReadOnlyList<OperationsDailyUsage> DailyUsage, IReadOnlyList<OperationsMetricSample> Samples);

public sealed record OperationsEndpointCount(string Path, long Requests);
/// <summary>Daily counts, deduplicated within one UTC day without exposing user identifiers.</summary>
public sealed record OperationsDailyUsage(DateOnly Day, long Requests, long Sessions, long ActiveUsers);
public sealed record OperationsMetricSample(DateTimeOffset At, double? ResponseP95Ms, double? ErrorRate,
    long? Requests, double? CpuCores, long? MemoryBytes, long? DiskBytes);

public sealed record OperationsLogQuery(string Project, string EnvironmentId, DateTimeOffset Since,
    DateTimeOffset Until, string? Level, string? ReleaseId, string? Text, int Limit);

public sealed record OperationsLogLine(DateTimeOffset At, string Level, string Message, string? ReleaseId);

/// <summary>Temporary adapter until the OTLP intake store implements <see cref="IOperationsTelemetryReader"/>.</summary>
public sealed class UnavailableOperationsTelemetryReader : IOperationsTelemetryReader
{
    public OperationsMetrics? ReadMetrics(string project, string environmentId, DateTimeOffset since, DateTimeOffset until) => null;
    public IReadOnlyList<OperationsLogLine>? SearchLogs(OperationsLogQuery query) => null;
}
