using System.Text.Json;

namespace AgentStudio.Search;

public static class GlobalSearchEndpoints
{
    private static readonly HashSet<string> AllowedDomains = new(StringComparer.OrdinalIgnoreCase) { "tasks", "commits", "files" };
    internal const int MinQueryLength = 2;

    private static readonly JsonSerializerOptions StreamJson =
        new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never };

    public static void MapGlobalSearchEndpoints(this WebApplication app)
    {
        app.MapGet("/api/search", (string? q, string? domains, int? limit, HttpContext context,
            GlobalSearchService search, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            var query = NormalizeQuery(q);
            var selected = ParseDomains(domains);
            if (query.Length < MinQueryLength)
                return Results.Ok(new GlobalSearchResponse(query, [], [], [], new Dictionary<string, string>(), 0));
            var response = search.Search(query, selected, limit ?? 20, context.RequestAborted);
            var filter = AccessFilter(context, projects);
            if (filter is null) return Results.Ok(response);
            return Results.Ok(response with
            {
                Tasks = response.Tasks.Where(filter).ToList(),
                Commits = response.Commits.Where(filter).ToList(),
                Files = response.Files.Where(filter).ToList(),
            });
        });

        // Streamed twin of /api/search. Same domains and limit, but each domain
        // is delivered as it completes: tasks answer from the warm index within
        // a frame, and every repository arrives with its own progress counter so
        // the palette can show what is still running instead of one spinner for
        // the whole search. Disconnecting cancels the request, which kills the
        // git children still walking.
        app.MapGet("/api/search/stream", async (string? q, string? domains, int? limit, HttpContext context,
            GlobalSearchService search, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            var query = NormalizeQuery(q);
            var selected = ParseDomains(domains);
            context.Response.Headers.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers["X-Accel-Buffering"] = "no";

            if (query.Length < MinQueryLength)
            {
                await WriteEventAsync(context, new("done", new GlobalSearchDoneFrame(0, new Dictionary<string, string>())));
                return;
            }

            var filter = AccessFilter(context, projects);
            try
            {
                await foreach (var frame in search.StreamAsync(query, selected, limit ?? 20, context.RequestAborted))
                    await WriteEventAsync(context, Authorize(frame, filter));
            }
            catch (OperationCanceledException ex) when (context.RequestAborted.IsCancellationRequested)
            {
                SilentCatch.Note(ex, "Global search stream: the operator kept typing or closed the palette.");
            }
        });
    }

    private static string NormalizeQuery(string? q) => q?.Trim() ?? "";

    private static HashSet<string> ParseDomains(string? domains) =>
        string.IsNullOrWhiteSpace(domains)
            ? new HashSet<string>(AllowedDomains, StringComparer.OrdinalIgnoreCase)
            : domains.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(AllowedDomains.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-project read filter for a human principal, or null when the
    /// caller is not access-scoped and sees everything.</summary>
    private static Func<GlobalSearchItem, bool>? AccessFilter(
        HttpContext context, AgentStudio.Registry.ProjectRegistry projects)
    {
        if (context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is not HumanPrincipal human) return null;
        return item => ProjectAccessAuthorization.Allows(human.User, item.ProjectName, projects);
    }

    /// <summary>
    /// Applies the same project-access filter the single-response endpoint
    /// applies. A streamed frame carries its items inline, so the filter runs
    /// per frame rather than once over the assembled response.
    /// </summary>
    private static GlobalSearchStreamEvent Authorize(
        GlobalSearchStreamEvent frame, Func<GlobalSearchItem, bool>? filter)
    {
        if (filter is null) return frame;
        return frame.Payload switch
        {
            GlobalSearchTasksFrame tasks =>
                frame with { Payload = tasks with { Items = tasks.Items.Where(filter).ToList() } },
            GlobalSearchRepositoryFrame repository => frame with
            {
                Payload = repository with
                {
                    Commits = repository.Commits.Where(filter).ToList(),
                    Files = repository.Files.Where(filter).ToList(),
                },
            },
            _ => frame,
        };
    }

    private static async Task WriteEventAsync(HttpContext context, GlobalSearchStreamEvent frame)
    {
        var data = JsonSerializer.Serialize(frame.Payload, frame.Payload.GetType(), StreamJson);
        await context.Response.WriteAsync($"event: {frame.Name}\ndata: {data}\n\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }
}
