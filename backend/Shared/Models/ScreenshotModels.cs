namespace AgentStudio.Shared;

/// <summary>
/// Wire shape for one screenshot file as surfaced by the per-job and
/// workspace screenshot listings. <see cref="LocalPath"/> is for an
/// "open in Explorer" affordance; the URL is the routable form the
/// frontend renders.
/// </summary>
public record TaskScreenshot
{
    public string JobId { get; init; } = "";
    public string JobTitle { get; init; } = "";
    public string ProjectName { get; init; } = "";
    public string WatchPath { get; init; } = "";

    public string FileName { get; init; } = "";
    /// <summary>Always begins with <c>results/</c>. Useful for captioning and protocol cross-reference.</summary>
    public string RelativePath { get; init; } = "";
    /// <summary>Routable URL that serves this file (sub-path aware).</summary>
    public string Url { get; init; } = "";
    /// <summary>Original file size and dimensions read from its bounded header.</summary>
    public long SizeBytes { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    /// <summary>Short human caption, typically the Playwright spec name or parent folder.</summary>
    public string Caption { get; init; } = "";
    /// <summary>One of <c>passed</c>, <c>failed</c>, <c>skipped</c>, <c>unknown</c>, or null when no harvest index applies.</summary>
    public string? Status { get; init; }
    /// <summary>
    /// Provenance label derived from the filename suffix: one of
    /// <see cref="ScreenshotSources"/> (<c>real</c> / <c>mocked</c> /
    /// <c>composite</c> / <c>pinned</c> / <c>unlabeled</c>). The UI
    /// renders it text-only next
    /// to the caption so the reviewer can tell a live-backend shot from a
    /// mocked-route e2e shot.
    /// </summary>
    public string Source { get; init; } = ScreenshotSources.Unlabeled;
    /// <summary>
    /// For a composite (<see cref="Source"/> == <c>composite</c>), the source
    /// of each stitched part (e.g. <c>["real", "mocked"]</c>). Empty for every
    /// other source label.
    /// </summary>
    public List<string> CompositeParts { get; init; } = [];
    /// <summary>Absolute on-disk path. The frontend offers a copy-to-clipboard / open-in-Explorer affordance off this.</summary>
    public string LocalPath { get; init; } = "";
    public DateTime TimestampUtc { get; init; }
}

/// <summary>
/// Wire shape for <c>GET /api/tasks/{id}/screenshots</c>.
/// </summary>
public record TaskScreenshotsResponse
{
    public string JobId { get; init; } = "";
    public List<TaskScreenshot> Screenshots { get; init; } = [];
}

/// <summary>
/// Wire shape for <c>GET /api/workspace/screenshots</c>. Items are
/// already ordered newest-first; the frontend re-buckets by hour for
/// the reel.
/// </summary>
public record WorkspaceScreenshotsResponse
{
    public int WindowHours { get; init; }
    public string? ProjectFilter { get; init; }
    public List<TaskScreenshot> Screenshots { get; init; } = [];
}
