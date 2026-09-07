using System.Text;
using System.Text.Json;

namespace AgentStudio.Search;

public static class GlobalSearchEndpoints
{
    private static readonly HashSet<string> AllowedDomains = new(StringComparer.OrdinalIgnoreCase) { "tasks", "commits", "files" };

    /// <summary>Matches the app-wide web defaults so stream payloads and
    /// <c>Results.Ok</c> payloads carry identical camelCase field names.</summary>
    private static readonly JsonSerializerOptions StreamJson = new(JsonSerializerDefaults.Web);

    public static void MapGlobalSearchEndpoints(this WebApplication app)
    {
        app.MapGet("/api/search", async (string? q, string? domains, int? limit, HttpContext context,
            GlobalSearchService search, AgentStudio.Registry.ProjectRegistry projects, CancellationToken ct) =>
        {
            var query = Normalize(q);
            var selected = SelectedDomains(domains);
            if (query.Length < 2)
                return Results.Ok(new GlobalSearchResponse(query, [], [], [], new Dictionary<string, string>(), 0));
            var response = await search.SearchAsync(query, selected, limit ?? 20, ct);
            var allowed = Filter(context, projects);
            if (allowed == null) return Results.Ok(response);
            return Results.Ok(response with
            {
                Tasks = response.Tasks.Where(allowed).ToList(),
                Commits = response.Commits.Where(allowed).ToList(),
                Files = response.Files.Where(allowed).ToList(),
            });
        });

        // Server-sent events variant of the same search. Same query contract, but
        // the task domain is delivered from the warm index before any repository
        // has been touched and each checkout is emitted as it finishes, so the
        // palette can show progress instead of one spinner for the whole sweep.
        // Closing the connection cancels the sweep.
        app.MapGet("/api/search/stream", async (string? q, string? domains, int? limit, HttpContext context,
            GlobalSearchService search, AgentStudio.Registry.ProjectRegistry projects, CancellationToken ct) =>
        {
            var query = Normalize(q);
            var selected = SelectedDomains(domains);
            var allowed = Filter(context, projects);

            context.Response.Headers.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers["X-Accel-Buffering"] = "no";

            try
            {
                if (query.Length >= 2)
                {
                    await foreach (var chunk in search.StreamAsync(query, selected, limit ?? 20, ct))
                    {
                        var visible = allowed == null ? chunk : chunk with { Items = chunk.Items.Where(allowed).ToList() };
                        await WriteSseAsync(context, visible.Domain, JsonSerializer.Serialize(visible, StreamJson), ct);
                    }
                }
                await WriteSseAsync(context, "done", "{}", ct);
            }
            catch (OperationCanceledException ex)
            {
                // The palette aborts the previous request on every keystroke, so a
                // cancelled stream is the normal case, not a failure.
                SilentCatch.Note(ex, "GlobalSearchEndpoints: search stream cancelled by the client");
            }
        });
    }

    private static string Normalize(string? q) => q?.Trim() ?? "";

    private static HashSet<string> SelectedDomains(string? domains) => string.IsNullOrWhiteSpace(domains)
        ? new HashSet<string>(AllowedDomains, StringComparer.OrdinalIgnoreCase)
        : domains.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(AllowedDomains.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Project-access predicate for a human caller, or null when the
    /// request carries no human principal and every result is visible.</summary>
    private static Func<GlobalSearchItem, bool>? Filter(HttpContext context, AgentStudio.Registry.ProjectRegistry projects)
    {
        if (context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is not HumanPrincipal human) return null;
        return item => ProjectAccessAuthorization.Allows(human.User, item.ProjectName, projects);
    }

    private static async Task WriteSseAsync(HttpContext context, string evt, string data, CancellationToken ct)
    {
        // One event equals one single-line JSON payload, so the client parser
        // never has to reassemble a multi-line data block.
        var payload = $"event: {evt}\ndata: {data.Replace("\r", "").Replace("\n", " ")}\n\n";
        await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(payload), ct);
        await context.Response.Body.FlushAsync(ct);
    }
}
