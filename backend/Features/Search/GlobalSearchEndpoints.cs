using System.Text;
using System.Text.Json;
using AgentStudio.Registry;

namespace AgentStudio.Search;

public static class GlobalSearchEndpoints
{
    private static readonly HashSet<string> AllowedDomains = new(StringComparer.OrdinalIgnoreCase) { "tasks", "commits", "files" };

    private static readonly JsonSerializerOptions StreamJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static void MapGlobalSearchEndpoints(this WebApplication app)
    {
        app.MapGet("/api/search", (string? q, string? domains, int? limit, HttpContext context,
            GlobalSearchService search, ProjectRegistry projects) =>
        {
            var query = q?.Trim() ?? "";
            var selected = ParseDomains(domains);
            if (query.Length < 2)
                return Results.Ok(new GlobalSearchResponse(query, [], [], [], new Dictionary<string, string>(), 0));
            var response = search.Search(query, selected, limit ?? 20);
            if (context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is not HumanPrincipal human)
                return Results.Ok(response);
            bool Allowed(GlobalSearchItem item) => ProjectAccessAuthorization.Allows(human.User, item.ProjectName, projects);
            return Results.Ok(response with
            {
                Tasks = response.Tasks.Where(Allowed).ToList(),
                Commits = response.Commits.Where(Allowed).ToList(),
                Files = response.Files.Where(Allowed).ToList(),
            });
        });

        // Per-domain delivery. The palette cannot show progress over a single
        // synchronous body, and the operator should not wait for the slowest
        // repository before seeing task hits. Frames: start, chunk, progress,
        // error, done.
        app.MapGet("/api/search/stream", async (string? q, string? domains, int? limit, HttpContext context,
            GlobalSearchService search, ProjectRegistry projects, CancellationToken ct) =>
        {
            var query = q?.Trim() ?? "";
            var selected = ParseDomains(domains);

            context.Response.Headers.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers["X-Accel-Buffering"] = "no";

            var human = context.Items[AccessSecurityMiddleware.HumanPrincipalItem] as HumanPrincipal;

            async Task Emit(string name, object payload, CancellationToken token)
            {
                if (human is not null && payload is GlobalSearchChunk chunk)
                {
                    payload = chunk with
                    {
                        Items = chunk.Items
                            .Where(item => ProjectAccessAuthorization.Allows(human.User, item.ProjectName, projects))
                            .ToList(),
                    };
                }
                await WriteFrameAsync(context, name, JsonSerializer.Serialize(payload, StreamJson), token);
            }

            try
            {
                if (query.Length < 2)
                {
                    await Emit("done", new GlobalSearchSummary(0, new Dictionary<string, long>(), 0), ct);
                    return;
                }
                await search.StreamAsync(query, selected, limit ?? 20, Emit, ct);
            }
            catch (Exception __ex) when (__ex is OperationCanceledException or IOException)
            {
                // Expected: the palette aborts the previous request on every
                // keystroke and on Escape. A write to the already-closed socket
                // surfaces as IOException rather than cancellation, and neither
                // is worth a 500 on a response whose headers are long gone.
                SilentCatch.Note(__ex, "GlobalSearchEndpoints: client aborted the search stream");
            }
        });
    }

    private static HashSet<string> ParseDomains(string? domains) =>
        string.IsNullOrWhiteSpace(domains)
            ? new HashSet<string>(AllowedDomains, StringComparer.OrdinalIgnoreCase)
            : domains.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(AllowedDomains.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// One SSE frame. The payload is compact JSON, so it is always a single
    /// line and needs no multi-line <c>data:</c> continuation. Flushed per
    /// frame: buffering here would defeat the whole point of streaming.
    /// </summary>
    private static async Task WriteFrameAsync(HttpContext context, string evt, string data, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes($"event: {evt}\ndata: {data}\n\n");
        await context.Response.Body.WriteAsync(bytes, ct);
        await context.Response.Body.FlushAsync(ct);
    }
}
