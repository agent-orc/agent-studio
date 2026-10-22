namespace AgentRunner;

/// <summary>
/// Host boundary for card previews. The run zone implementation builds the
/// exact source commit, routes its address, and captures a PNG after health.
/// No staging or production secret reference may enter this boundary.
/// </summary>
public interface IPreviewRunZone
{
    Task<PreviewRunZoneResult> DeployAsync(PreviewRunZoneJob job, CancellationToken cancellationToken);
    Task RemoveAsync(PreviewRunZoneJob job, CancellationToken cancellationToken);
}

public sealed record PreviewRunZoneJob(string Project, string CardKey, string SourceCommit,
    string SourceRepository, string SourceRef, string Address, string Template, string Destination,
    string? ArtifactPath, string? Dockerfile, IReadOnlyList<string> BuildCommands,
    IReadOnlyList<string> SecretRefs, int ContainerPort);

public sealed record PreviewRunZoneResult(bool Healthy, byte[]? ScreenshotPng);
