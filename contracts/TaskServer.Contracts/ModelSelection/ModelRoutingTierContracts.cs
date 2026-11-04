namespace AgentStudio.Pipeline;

// Moved from backend/Features/Pipeline/ModelRoutingPolicyRegistry.cs (MS-5):
// the shared model-selection planner checks correctness floors and
// provider-rejection siblings against these policy records.

public sealed record ModelRoutingVendorOverride
{
    public string Model { get; init; } = "";
    public string ThinkingLevel { get; init; } = "medium";
}

public sealed record ModelRoutingTier
{
    public string Id { get; init; } = "";
    public int Rank { get; init; }
    public string Model { get; init; } = "";
    public string ThinkingLevel { get; init; } = "medium";
    public int EstimatedSavingsPercent { get; init; }
    public Dictionary<string, ModelRoutingVendorOverride> VendorOverrides { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed record ProviderRejectionModelFallback
{
    public string CliType { get; init; } = "";
    public string FromModel { get; init; } = "";
    public string ToModel { get; init; } = "";
    public string Reason { get; init; } = "";
}
