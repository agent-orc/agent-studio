namespace AgentStudio.Pipeline;

/// <summary>Decides when an unpublished merge can no longer fence a new batch.</summary>
public static class PendingCodePushPolicy
{
    public static bool IsResolved(bool originContainsApproved, bool originIsAncestorOfApproved,
        bool hasOrigin, bool cardOnIntegrationPath, bool mergePassed,
        string? currentApprovedSha, string approvedSha)
        => originContainsApproved
           || !cardOnIntegrationPath
           || !mergePassed
           || !string.Equals(currentApprovedSha, approvedSha,
               StringComparison.OrdinalIgnoreCase)
           || hasOrigin && !originIsAncestorOfApproved;
}
