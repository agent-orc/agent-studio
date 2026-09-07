namespace AgentStudio.Search;

public static class GlobalSearchEndpoints
{
    private static readonly HashSet<string> AllowedDomains = new(StringComparer.OrdinalIgnoreCase) { "tasks", "commits", "files", "dossiers" };

    public static void MapGlobalSearchEndpoints(this WebApplication app)
    {
        app.MapGet("/api/search", (string? q, string? domains, int? limit, HttpContext context,
            GlobalSearchService search, AgentStudio.Registry.ProjectRegistry projects) =>
        {
            var query = q?.Trim() ?? "";
            var selected = string.IsNullOrWhiteSpace(domains)
                ? new HashSet<string>(AllowedDomains, StringComparer.OrdinalIgnoreCase)
                : domains.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(AllowedDomains.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (query.Length < 2)
                return Results.Ok(new GlobalSearchResponse(query, [], [], [], [], new Dictionary<string, string>(), 0));
            var response = search.Search(query, selected, limit ?? 20);
            if (context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is not HumanPrincipal human)
                return Results.Ok(response);
            bool Allowed(GlobalSearchItem item) => ProjectAccessAuthorization.Allows(human.User, item.ProjectName, projects);
            return Results.Ok(response with
            {
                Tasks = response.Tasks.Where(Allowed).ToList(),
                Commits = response.Commits.Where(Allowed).ToList(),
                Files = response.Files.Where(Allowed).ToList(),
                Dossiers = response.Dossiers.Where(Allowed).ToList(),
            });
        });
    }
}
