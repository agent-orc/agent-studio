namespace AgentStudio.TaskServer.Contracts;

/// <summary>Metadata-only installation inventory. No secret or verifier belongs on this wire contract.</summary>
public sealed record CredentialInventoryItemDto(
    string Id,
    string Category,
    string HostId,
    string Kind,
    string Label,
    IReadOnlyList<string> Scopes,
    string? Repository,
    string? Permission,
    DateTime? ExpiresAt,
    DateTime? LastUseAt,
    string State,
    bool Required,
    string Detail,
    IReadOnlyList<string> AffectedCards,
    string? Issuer = null,
    string? Binding = null,
    string? StorageMode = null,
    string? RevocationReceipt = null,
    string? Holder = null,
    string? Target = null,
    DateTime? CreatedAt = null,
    int? RotationIntervalDays = null,
    DateTime? RotationDueAt = null,
    DateTime? RotationBasisAt = null,
    bool Refreshable = false,
    bool Active = true,
    string RotationPlanSource = "inventory-default",
    RunnerCredentialRenewalStatus? Renewal = null)
{
    public string LifetimeClass => CredentialLifecyclePolicy.Classify(this);
    public string Expiry => ExpiresAt?.ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? "none";
}

public sealed record CredentialInventoryDto(
    DateTime GeneratedAt,
    IReadOnlyList<CredentialInventoryItemDto> Items)
{
    public IReadOnlyList<CredentialLifecycleWarningDto> Warnings => CredentialLifecyclePolicy.Warnings(Items, GeneratedAt);
}
