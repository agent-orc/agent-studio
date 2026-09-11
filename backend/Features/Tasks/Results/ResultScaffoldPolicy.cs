namespace AgentStudio.Tasks;

/// <summary>Pure overwrite guard for the application-owned result scaffold.</summary>
public static class ResultScaffoldPolicy
{
    public static bool ShouldWrite(string? existingContent, bool refreshOwnedScaffold)
    {
        if (existingContent is null) return true;
        return refreshOwnedScaffold
            && existingContent.TrimStart().StartsWith(
                TaskTransitionService.ResultScaffoldMarker,
                StringComparison.Ordinal);
    }
}
