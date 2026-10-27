using System.Reflection;
using AgentStudio.Git;
using AgentStudio.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests.Architecture;

/// <summary>
/// Static census of every registered GET route template (AGT-3321, Git facts
/// S1, step 3). The routes come from endpoint metadata of a real host in both
/// route profiles; each handler is traced through the backend's IL call graph
/// to <see cref="GitProcessBoundary"/>, the only Git start. The census file
/// <c>Fixtures/git-read-route-census.tsv</c> records, per route:
/// <list type="bullet">
///   <item><c>git</c>: <c>reachable</c> (a static path to Git exists),
///   <c>free</c> (none) or <c>opaque</c> (no handler method to trace).</item>
///   <item><c>status</c>: <c>frozen</c> (a manifest exception, measured by
///   <see cref="GitReadSpawnCensusTests"/>), <c>measured-zero</c> (reachable,
///   but the focused tests prove zero spawns on their scenarios),
///   <c>gap</c> (reachable and not exercised: a coverage gap with a named
///   owner, never a passing route) or <c>-</c>.</item>
///   <item><c>owner</c> and <c>removal</c> for frozen and gap routes.</item>
/// </list>
/// A new GET route, a route whose reachability changed, or a reachable route
/// without an owner fails here. Set <c>GIT_READ_CENSUS_WRITE=1</c> to rewrite
/// the file: it keeps reviewed owners and marks new reachable routes
/// <c>gap UNASSIGNED</c>, which still fails until someone names the owner. The
/// census never edits the runtime exception manifest.
/// <c>GIT_READ_CENSUS_REPORT</c> names a file for each route's static path to Git.
/// </summary>
[Trait("Category", "Guard")]
public sealed class GitReadRouteCensusTests : IDisposable
{
    internal const string CensusPath = "backend.Tests/Architecture/Fixtures/git-read-route-census.tsv";
    private const string Unassigned = "UNASSIGNED";

    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "git-read-census-" + Guid.NewGuid().ToString("N"));
    private static readonly Lazy<IlCallGraph> Graph = new(() => new IlCallGraph(typeof(GitProcessBoundary).Assembly));

    [Fact]
    public void Every_registered_get_template_matches_the_reviewed_census()
    {
        var computed = ComputeCensus();
        if (Environment.GetEnvironmentVariable("GIT_READ_CENSUS_REPORT") is { Length: > 0 } report)
            File.WriteAllText(report, System.Text.Json.JsonSerializer.Serialize(computed.Select(route =>
                new { route = route.Route, git = route.Git, via = route.Via })));
        var reviewed = ReadCensus();
        if (Environment.GetEnvironmentVariable("GIT_READ_CENSUS_WRITE") == "1")
        {
            WriteCensus(computed.Select(route => reviewed.TryGetValue(route.Route, out var known) && known.Git == route.Git
                ? known
                : route with
                {
                    Status = route.Git == "reachable" ? "gap" : "-",
                    Owner = route.Git == "reachable" ? Unassigned : "-",
                    Removal = route.Git == "reachable" ? Unassigned : "-",
                }).ToList());
            reviewed = ReadCensus();
        }

        var problems = new List<string>();
        foreach (var route in computed)
        {
            if (!reviewed.TryGetValue(route.Route, out var known))
                problems.Add($"new GET route {route.Route} (git={route.Git}) is not in the census");
            else if (known.Git != route.Git)
                problems.Add($"{route.Route}: census says git={known.Git}, the call graph says {route.Git} via {route.Via}");
        }
        foreach (var stale in reviewed.Keys.Except(computed.Select(route => route.Route), StringComparer.Ordinal))
            problems.Add($"{stale} is in the census but no longer registered");

        Assert.True(problems.Count == 0,
            $"GET route census drifted; update {CensusPath} (GIT_READ_CENSUS_WRITE=1 keeps reviewed owners):\n  "
            + string.Join("\n  ", problems));
    }

    [Fact]
    public void Every_git_reachable_route_is_frozen_measured_or_an_owned_gap()
    {
        var problems = new List<string>();
        foreach (var route in ReadCensus().Values)
        {
            var exception = GitReadSpawnExceptionManifest.Find(HttpMethods.Get, route.Route);
            switch (route.Status)
            {
                case "frozen" when exception is null:
                    problems.Add($"{route.Route}: census says frozen, the manifest has no exception");
                    break;
                case "frozen" when exception.Owner != route.Owner || exception.RemovalSlice != route.Removal:
                    problems.Add($"{route.Route}: census owner/removal differ from the manifest");
                    break;
                case "frozen" or "measured-zero" when !GitReadSpawnScenarios.Routes.Contains(route.Route)
                    && !(route.Status == "frozen" && GitReadSpawnScenarios.ExistingTestEvidence.ContainsKey(route.Route)):
                    problems.Add($"{route.Route}: status {route.Status} without a measured scenario in GitReadSpawnScenarios");
                    break;
                case "gap" when route.Owner is Unassigned or "-" || route.Removal is Unassigned or "-":
                    problems.Add($"{route.Route}: coverage gap without a named owner and removal slice");
                    break;
                case "-" when route.Git == "reachable":
                    problems.Add($"{route.Route}: reachable route without a status");
                    break;
            }
            if (exception is not null && route.Status != "frozen")
                problems.Add($"{route.Route}: has a manifest exception but census status {route.Status}");
        }
        foreach (var entry in GitReadSpawnExceptionManifest.Entries)
        {
            if (!ReadCensus().ContainsKey(entry.RouteTemplate))
                problems.Add($"manifest exception {entry.Method} {entry.RouteTemplate} is not a registered GET route");
        }

        Assert.True(problems.Count == 0, "Git read census classification is inconsistent:\n  " + string.Join("\n  ", problems));
    }

    /// <summary>
    /// Pipeline members that may still reach Git, with the reviewed reason and
    /// owner. Their spawns land in the read scope of the route they guard.
    /// </summary>
    private static readonly Dictionary<string, string> PipelineGitDebt = new(StringComparer.Ordinal)
    {
        ["AccessSecurityMiddleware.InvokeAsync"] =
            "Environment-scoped permission targets resolve the project's environment declarations through "
            + "GitService.ResolveRepoRootForProject (one cached rev-parse --show-toplevel per repository). "
            + "Owner: access security; removal: resolve repository roots from the Git fact store (S2).",
    };

    [Fact]
    public void No_middleware_or_endpoint_filter_reaches_git()
    {
        var assembly = typeof(GitProcessBoundary).Assembly;
        var graph = Graph.Value;
        var paths = graph.PathsTo(Sinks());
        var pipeline = assembly.GetTypes()
            .Where(type => type.Name.EndsWith("Middleware", StringComparison.Ordinal)
                || typeof(IEndpointFilter).IsAssignableFrom(type))
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                | BindingFlags.DeclaredOnly))
            .Where(method => method.Name is "Invoke" or "InvokeAsync")
            .ToList();

        Assert.NotEmpty(pipeline);
        var offenders = pipeline
            .Where(method => !PipelineGitDebt.ContainsKey(method.DeclaringType!.Name + "." + method.Name))
            .Select(method => graph.Path(method, paths))
            .Where(path => path.Count > 0)
            .Select(path => string.Join(" -> ", path.Select(IlCallGraph.Describe)))
            .ToList();
        Assert.True(offenders.Count == 0,
            "Middleware and endpoint filters run on every request and must not start Git:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void Call_graph_finds_the_known_git_reads_and_clears_the_board_read()
    {
        var census = ComputeCensus().ToDictionary(route => route.Route, StringComparer.Ordinal);

        Assert.Equal("reachable", census["/api/tasks/{jobId}/files/{**path}"].Git);
        Assert.Equal("reachable", census["/api/git/hygiene"].Git);
        Assert.Contains("GitProcessBoundary", census["/api/tasks/{jobId}/files/{**path}"].Via, StringComparison.Ordinal);
        // AGT-3323 (Git facts S3): the card reads it migrated join published
        // facts and have no static path to Git at all.
        foreach (var route in new[]
                 {
                     "/api/tasks/{jobId}/provenance", "/api/tasks/{jobId}/delivery-claim", "/api/git/integration",
                     "/api/tasks/{jobId}/commits", "/api/tasks/{jobId}/commits/files", "/api/tasks/{jobId}/commits/diff",
                     "/api/tasks/{jobId}/commit", "/api/tasks/{jobId}/commits/{sha}/files",
                     "/api/tasks/{jobId}/runs/{index:int}/commits", "/api/tasks/{jobId}/runs/{index:int}/files",
                 })
            Assert.Equal("free", census[route].Git);
    }

    internal sealed record CensusRoute(string Route, string Git, string Status, string Owner, string Removal, string Via);

    internal IReadOnlyList<CensusRoute> ComputeCensus()
    {
        var graph = Graph.Value;
        var paths = graph.PathsTo(Sinks());
        var endpoints = new Dictionary<string, RouteEndpoint>(StringComparer.Ordinal);
        foreach (var taskServer in new string?[] { null, "http://127.0.0.1:5071" })
        {
            using var factory = Factory(taskServer);
            foreach (var endpoint in factory.Services.GetServices<EndpointDataSource>()
                         .SelectMany(source => source.Endpoints).OfType<RouteEndpoint>())
            {
                if (!IsGet(endpoint) || endpoint.Metadata.GetMetadata<HubMetadata>() is not null) continue;
                endpoints.TryAdd(GitReadRouteKey.Template(endpoint), endpoint);
            }
        }

        return endpoints
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair =>
            {
                var handler = Handler(pair.Value);
                if (handler is null || !graph.Contains(handler))
                    return new CensusRoute(pair.Key, "opaque", "-", "-", "-", "");
                var path = graph.Path(handler, paths);
                return new CensusRoute(pair.Key, path.Count > 0 ? "reachable" : "free", "-", "-", "-",
                    string.Join(" -> ", path.Select(IlCallGraph.Describe)));
            })
            .ToList();
    }

    internal static IEnumerable<MethodBase> Sinks() => typeof(GitProcessBoundary)
        .GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly)
        .Where(method => method.Name is "Run" or "RunAsync" or "RunBinary" or "Begin");

    private static MethodBase? Handler(RouteEndpoint endpoint)
        => endpoint.Metadata.OfType<MethodInfo>().FirstOrDefault()
           ?? endpoint.RequestDelegate?.Method;

    private static bool IsGet(RouteEndpoint endpoint)
        => endpoint.Metadata.GetMetadata<IHttpMethodMetadata>() is not { } methods
           || methods.HttpMethods.Count == 0
           || methods.HttpMethods.Contains(HttpMethods.Get, StringComparer.OrdinalIgnoreCase);

    internal static Dictionary<string, CensusRoute> ReadCensus()
    {
        var path = Path.Combine(CSharpSourceScanner.RepoRoot(), CensusPath);
        var routes = new Dictionary<string, CensusRoute>(StringComparer.Ordinal);
        if (!File.Exists(path)) return routes;
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var fields = line.Split('\t');
            routes[fields[0]] = new CensusRoute(fields[0], fields[1], fields[2], fields[3], fields[4], "");
        }
        return routes;
    }

    private static void WriteCensus(IReadOnlyList<CensusRoute> routes)
    {
        var path = Path.Combine(CSharpSourceScanner.RepoRoot(), CensusPath);
        var lines = new List<string>
        {
            "# GET route census for the Git read guard (AGT-3321). Generated by GitReadRouteCensusTests;",
            "# owners and removal slices are reviewed by hand. Columns: route, git, status, owner, removal.",
        };
        lines.AddRange(routes.OrderBy(route => route.Route, StringComparer.Ordinal)
            .Select(route => string.Join('\t', route.Route, route.Git, route.Status, route.Owner, route.Removal)));
        File.WriteAllText(path, string.Join('\n', lines) + "\n");
    }

    private WebApplicationFactory<Program> Factory(string? taskServer)
    {
        var factory = new TestHostFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["Security:Profile"] = SecurityProfiles.Local,
                ["AllowedHosts"] = "*",
                ["TaskServer:BaseUrl"] = taskServer,
            }));
        });
        _ = factory.Server;
        return factory;
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspace)) Directory.Delete(_workspace, true);
    }
}
