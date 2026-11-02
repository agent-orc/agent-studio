namespace AgentStudio.TaskServer.Contracts;

/// <summary>Shared wire ceilings for review report submission.</summary>
public static class ReviewReportLimits
{
    public const long MaxRequestBodyBytes = 25L * 1024 * 1024;
    public const long TargetRequestBytes = 24L * 1024 * 1024;
    public const int MaxEmbeddedArtifactBytes = 8 * 1024 * 1024;
}
