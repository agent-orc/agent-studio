namespace AgentStudio.Tasks;

/// <summary>
/// Decides how the <c>results/</c> route answers for an HTML artifact. The
/// document renders inline only when a Studio page frames it: the browser
/// reports a frame destination from the Studio origin in its Fetch Metadata
/// headers, which page script cannot set. Every other request, including a
/// top-level tab and a client that sends no Fetch Metadata, receives the file
/// as a download, so the artifact never opens as a page on the Studio origin.
/// </summary>
internal static class ResultHtmlDispositionPolicy
{
    public const string FetchDestHeader = "Sec-Fetch-Dest";
    public const string FetchSiteHeader = "Sec-Fetch-Site";

    /// <summary>True when the request comes from a Studio frame and may render inline.</summary>
    public static bool ServesInline(string? fetchDest, string? fetchSite)
    {
        var framed = string.Equals(fetchDest, "iframe", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(fetchDest, "frame", StringComparison.OrdinalIgnoreCase);
        return framed && string.Equals(fetchSite, "same-origin", StringComparison.OrdinalIgnoreCase);
    }
}
