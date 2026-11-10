namespace AgentStudio.Runner;

/// <summary>
/// The one sentence that says what an open cause breaker holds cards behind
/// (AGT-3617). The board shows it on every held card ("Held behind DEMO-2:
/// review cannot read the test report") and on the cause card, so the owner
/// reads the cause instead of a failure-class token.
/// </summary>
public static class CauseBreakerWording
{
    /// <summary>
    /// Plain words for <paramref name="failureClass"/>
    /// (<c>ReviewInfra/&lt;classification&gt;</c>). An unknown class keeps its
    /// token, so a new classification still says something true.
    /// </summary>
    public static string Summary(string? failureClass)
    {
        var value = (failureClass ?? string.Empty).Trim();
        var classification = value.Contains('/') ? value[(value.LastIndexOf('/') + 1)..] : value;
        return classification switch
        {
            "TestReportUnavailable" => "review cannot read the test report",
            "ToolUnavailable" => "a review tool is unavailable",
            "PreparationFailed" => "review preparation fails",
            "ModelUnknownToCli" or "ModelConfigurationFault" => "the review model is misconfigured",
            "SnapshotUnavailable" => "review cannot read the delivery snapshot",
            "ArtifactTransferFailed" => "review cannot transfer its artifacts",
            "ExecutionTimeout" or "AspectTimeout" => "a review step times out",
            "CommandFailed" => "a review command fails",
            "LeaseExpired" => "the review lease expires",
            "ProviderUnauthorized" => "the review provider refuses its credentials",
            "AspectVerdictUnparseable" => "a review aspect returns no readable verdict",
            "" or "Unknown" => "review keeps failing for the same cause",
            _ => $"review keeps failing ({value})",
        };
    }
}
