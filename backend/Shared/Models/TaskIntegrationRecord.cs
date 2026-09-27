using System.Text.Json.Serialization;

namespace AgentStudio.Shared;

/// <summary>
/// Append-only application-owned integration bookkeeping for one task. Live
/// acceptance continues to use pipeline and timeline facts; these records add
/// a durable classification for historical cards whose acceptance predates
/// that recording contract.
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

    [JsonPropertyName("integrationTipSha")]
    public string? IntegrationTipSha { get; init; }

    [JsonPropertyName("commitShas")]
    public List<string> CommitShas { get; init; } = [];

    [JsonPropertyName("fenceRefs")]
    public List<string> FenceRefs { get; init; } = [];

    [JsonPropertyName("evidence")]
    public string Evidence { get; init; } = "";
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

    public static readonly string[] All =
    [
        IntegratedVerified,
        IntegratedHistorical,
        NoCodeExpected,
        NoAttributionLegacy,
        ContentOnFence,
        GenuinelyMissing,
    ];

    public static bool IsOperatorVisible(string? classification)
        => string.Equals(classification, ContentOnFence, StringComparison.Ordinal)
           || string.Equals(classification, GenuinelyMissing, StringComparison.Ordinal);
}
