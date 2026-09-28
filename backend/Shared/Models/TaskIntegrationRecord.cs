using System.Text.Json.Serialization;

namespace AgentStudio.Shared;

/// <summary>
/// Append-only application-owned integration bookkeeping for one task. Live
/// acceptance continues to use pipeline and timeline facts. Historical rows
/// classify older cards; curated mappings bind a current source SHA to a
/// published integration SHA for rewritten deliveries.
/// </summary>
public sealed record TaskIntegrationRecord
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;

    [JsonPropertyName("classification")]
    public string Classification { get; init; } = IntegrationRecordClasses.GenuinelyMissing;

    [JsonPropertyName("recordedAtUtc")]
    public DateTime RecordedAtUtc { get; init; }

    [JsonPropertyName("acceptedAtUtc")]
    public DateTime? AcceptedAtUtc { get; init; }

    [JsonPropertyName("integrationBranch")]
    public string? IntegrationBranch { get; init; }

    [JsonPropertyName("commitShas")]
    public List<string> CommitShas { get; init; } = [];

    [JsonPropertyName("fenceRefs")]
    public List<string> FenceRefs { get; init; } = [];

    [JsonPropertyName("evidence")]
    public string Evidence { get; init; } = "";

    /// <summary>Immutable result or attributed commit represented by a curated merge.</summary>
    [JsonPropertyName("sourceSha")]
    public string? SourceSha { get; init; }

    /// <summary>Commit on the published integration branch that contains that source.</summary>
    [JsonPropertyName("integrationSha")]
    public string? IntegrationSha { get; init; }

    [JsonPropertyName("deliveryEpoch")]
    public string? DeliveryEpoch { get; init; }
}

/// <summary>Durable classifications produced by the historical integration sweep.</summary>
public static class IntegrationRecordClasses
{
    public const string IntegratedVerified = "integrated-verified";
    public const string IntegratedHistorical = "integrated-historical";
    public const string NoCodeExpected = "no-code-expected";
    public const string NoAttributionLegacy = "no-attribution-legacy";
    public const string ContentOnFence = "content-on-fence";
    public const string GenuinelyMissing = "genuinely-missing";
    public const string CuratedMapping = "curated-mapping";

    public static readonly string[] All =
    [
        IntegratedVerified,
        IntegratedHistorical,
        NoCodeExpected,
        NoAttributionLegacy,
        ContentOnFence,
        GenuinelyMissing,
        CuratedMapping,
    ];

    public static bool IsOperatorVisible(string? classification)
        => string.Equals(classification, ContentOnFence, StringComparison.Ordinal)
           || string.Equals(classification, GenuinelyMissing, StringComparison.Ordinal);
}
