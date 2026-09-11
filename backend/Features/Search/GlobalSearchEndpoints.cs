using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace AgentStudio.Search;

public static class GlobalSearchEndpoints
{
    private static readonly HashSet<string> AllowedDomains = new(StringComparer.OrdinalIgnoreCase) { "tasks", "dossiers", "wiki", "commits", "files" };

    public static void MapGlobalSearchEndpoints(this WebApplication app)
    {
        app.MapGet("/api/search", (string? q, string? domains, int? limit, HttpContext context,
            GlobalSearchService search, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            var query = q?.Trim() ?? "";
            var selected = ParseDomains(domains);
            if (query.Length < 2)
                return Results.Ok(new GlobalSearchResponse(query, [], [], [], [], [], new Dictionary<string, string>(), 0));
            var response = search.Search(query, selected, limit ?? 20);
            var allowed = AccessFilter(context, projects);
            if (allowed == null)
                return Results.Ok(response);
            return Results.Ok(response with
            {
                Tasks = response.Tasks.Where(allowed).ToList(),
                Dossiers = response.Dossiers.Where(allowed).ToList(),
                Wiki = response.Wiki.Where(allowed).ToList(),
                Commits = response.Commits.Where(allowed).ToList(),
                Files = response.Files.Where(allowed).ToList(),
            });
        });

        // AGT-2758: the dev-seat half of the split /api/search. Task results
        // moved to the standalone Task Server's durable board index
        // (GET /api/v1/studio/search); this route keeps the git-backed
        // domains - commits, files, dossiers, and wiki all read the project's
        // local checkout - on an explicit dev-seat boundary. "tasks" is not
        // an accepted domain here even if requested; it always empties out.
        app.MapGet("/api/search/repository", (string? q, string? domains, int? limit, HttpContext context,
            GlobalSearchService search, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            var query = q?.Trim() ?? "";
            var selected = ParseDomains(domains);
            selected.Remove("tasks");
            if (query.Length < 2)
                return Results.Ok(new GlobalSearchResponse(query, [], [], [], [], [], new Dictionary<string, string>(), 0));
            var response = search.Search(query, selected, limit ?? 20) with { Tasks = [] };
            var allowed = AccessFilter(context, projects);
            if (allowed == null)
                return Results.Ok(response);
            return Results.Ok(response with
            {
                Dossiers = response.Dossiers.Where(allowed).ToList(),
                Wiki = response.Wiki.Where(allowed).ToList(),
                Commits = response.Commits.Where(allowed).ToList(),
                Files = response.Files.Where(allowed).ToList(),
            });
        });

        // Per-domain delivery. The palette renders task matches as soon as the
        // first frame lands and fills the git domains in as each repository
        // answers, so one slow checkout no longer holds the whole result set.
        // Disconnecting (a new keystroke, Escape, closing the palette) cancels
        // RequestAborted, which stops the fan-out server-side.
        app.MapGet("/api/search/stream", async (string? q, string? domains, int? limit, HttpContext context,
            GlobalSearchService search, AgentStudio.Registry.ProjectRegistry projects,
            IOptions<JsonOptions> jsonOptions) =>
        {
            var query = q?.Trim() ?? "";
            var selected = ParseDomains(domains);
            var json = jsonOptions.Value.SerializerOptions;

            context.Response.Headers.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers["X-Accel-Buffering"] = "no";

            if (query.Length < 2)
            {
                // Same frame record as a real completion so the client parses
                // one shape, not two.
                await WriteEventAsync(context, "done", new GlobalSearchDoneFrame(0, 0, 0, 0), json);
                return;
            }

            var allowed = AccessFilter(context, projects);
            var ct = context.RequestAborted;
            try
            {
                await foreach (var frame in search.StreamAsync(query, selected, limit ?? 20, ct))
                    await WriteEventAsync(context, frame.Event, Authorize(frame, allowed), json);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Client went away mid-search. Nothing to report: the stream is
                // already gone and the fan-out observed the same token.
                return;
            }
        });
    }

    private static HashSet<string> ParseDomains(string? domains) =>
        string.IsNullOrWhiteSpace(domains)
            ? new HashSet<string>(AllowedDomains, StringComparer.OrdinalIgnoreCase)
            : domains.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(AllowedDomains.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Project-access predicate for the calling principal, or null when the
    /// caller is not a human principal and every project is visible.
    /// </summary>
    internal static Func<GlobalSearchItem, bool>? AccessFilter(HttpContext context, AgentStudio.Registry.ProjectRegistry projects)
    {
        if (context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is not HumanPrincipal human) return null;
        return item => ProjectAccessAuthorization.Allows(human.User, item.ProjectName, projects);
    }

    /// <summary>Applies the caller's project access to a frame's item arrays.</summary>
    internal static object Authorize(GlobalSearchStreamEvent frame, Func<GlobalSearchItem, bool>? allowed) =>
        allowed == null
            ? frame.Payload
            : frame.Payload switch
            {
                GlobalSearchTasksFrame tasks => tasks with { Items = tasks.Items.Where(allowed).ToList() },
                GlobalSearchDossiersFrame dossiers => dossiers with { Items = dossiers.Items.Where(allowed).ToList() },
                GlobalSearchWikiFrame wiki => wiki with { Items = wiki.Items.Where(allowed).ToList() },
                GlobalSearchRepositoryFrame repository => repository with
                {
                    Commits = repository.Commits.Where(allowed).ToList(),
                    Files = repository.Files.Where(allowed).ToList(),
                },
                var payload => payload,
            };

    private static async Task WriteEventAsync(HttpContext context, string name, object payload, JsonSerializerOptions json)
    {
        await context.Response.WriteAsync($"event: {name}\n", context.RequestAborted);
        await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(payload, json)}\n\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }
}
