namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// The variables Studio sets on a workload whose environment enables telemetry
/// (M5). The control plane and the deploy host share these names.
/// </summary>
public static class DeployTelemetryVocabulary
{
    /// <summary>Environment-scoped secret that holds the ingest token Studio minted for deploys.</summary>
    public const string SecretName = "studio-telemetry-ingest";
    public const string Protocol = "http/protobuf";
    public const string EndpointVariable = "OTEL_EXPORTER_OTLP_ENDPOINT";
    public const string ProtocolVariable = "OTEL_EXPORTER_OTLP_PROTOCOL";
    public const string HeadersVariable = "OTEL_EXPORTER_OTLP_HEADERS";
    public const string ResourceAttributesVariable = "OTEL_RESOURCE_ATTRIBUTES";
    /// <summary>Resource attribute that carries the release id, so log search can filter by release.</summary>
    public const string ReleaseAttribute = "agent_studio.release.id";
    public const string EnvironmentAttribute = "deployment.environment.name";

    public static readonly IReadOnlyList<string> Variables =
        [EndpointVariable, ProtocolVariable, HeadersVariable, ResourceAttributesVariable];

    /// <summary>OTEL_RESOURCE_ATTRIBUTES text: comma-separated key=value pairs with percent-encoded values.</summary>
    public static string ResourceAttributes(string environmentId, string? releaseId)
    {
        var pairs = new List<string> { EnvironmentAttribute + "=" + Uri.EscapeDataString(environmentId) };
        if (!string.IsNullOrWhiteSpace(releaseId)) pairs.Add(ReleaseAttribute + "=" + Uri.EscapeDataString(releaseId));
        return string.Join(",", pairs);
    }
}
