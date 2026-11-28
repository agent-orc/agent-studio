namespace AgentStudio.Pipeline;

/// <summary>Checks whether a delivery inherits the tested inputs of an earlier publication.</summary>
public static class IntegrationPublicationGatePolicy
{
    public const int MaxFirstParentSteps = 4096;
    public sealed record FirstParentCommit(string Sha, string ParentSha);

    /// <summary>Every step from the delivery back to the receipt must preserve its parent's tested inputs.</summary>
    public static IReadOnlyList<string>? InheritedReceipt(string delivery, string receipt,
        Func<string, string?> neutralParent)
    {
        var commits = new List<string>();
        for (var sha = delivery; !string.Equals(sha, receipt, StringComparison.OrdinalIgnoreCase);)
        {
            if (neutralParent(sha) is not { } parent) return null;
            commits.Add(sha);
            sha = parent;
        }
        return commits;
    }
}
