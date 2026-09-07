namespace AgentStudio.Git;

/// <summary>
/// Writes the git-state freshness stamp onto a response as headers.
///
/// <para>
/// Headers rather than payload fields, on every endpoint. <c>GET /api/tasks</c>
/// returns an array and <c>GET /api/git/inventory</c> a positional record, so
/// neither can carry a scalar without a wrapper. <c>GET /api/tasks/grouped</c>
/// looks like it could, but clients read that object as a lane map and iterate
/// its values as task arrays - the Explorer's project rows threw
/// <c>lane is not iterable</c> on a scalar sibling - so a field there is a
/// breaking change, not an additive one. One mechanism for all four, read once
/// by an HTTP interceptor on the client, so no call site changes either.
/// </para>
/// </summary>
public static class GitStateStampHeader
{
    /// <summary>Capture time of the oldest repository the response covers, ISO 8601 UTC.</summary>
    public const string AtHeader = "X-Git-State-At";

    /// <summary>Whether a known change has not been folded into that capture yet.</summary>
    public const string StaleHeader = "X-Git-State-Stale";

    public static void Apply(HttpContext context, GitStateStamp stamp)
    {
        if (context.Response.HasStarted) return;
        if (stamp.GitStateAt is { } at)
        {
            context.Response.Headers[AtHeader] = at.UtcDateTime.ToString(
                "o",
                System.Globalization.CultureInfo.InvariantCulture);
        }
        context.Response.Headers[StaleHeader] = stamp.Stale ? "true" : "false";
    }
}
