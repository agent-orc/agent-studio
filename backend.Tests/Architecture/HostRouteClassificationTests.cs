using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.Security;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests.Architecture;

/// <summary>
/// Every route a host calls through its Task Server client is classified for
/// the monolith in the networked profile (AGT-3366). A route either maps to a
/// runner scope here, or exists only on the standalone Task Server, where the
/// monolith answers a runner credential with 404 so the host pauses that loop
/// instead of logging a refusal every poll. The few routes outside both lists
/// say why. Adding a host route without classifying it fails this test.
/// </summary>
[Trait("Category", "Guard")]
public sealed class HostRouteClassificationTests : IDisposable
{
    /// <summary>The host's Task Server client and the work-container calls it hands its bearer to.</summary>
    private static readonly string[] Sources =
    [
        "runner/TaskServerClient.cs",
        "runner/SourceFacts/TaskServerClient.SourceFacts.cs",
        "runner/WorkContainers/WorkContainerCredentialSource.cs",
        "runner/WorkContainers/WorkContainerCommand.cs",
    ];

    private enum Monolith
    {
        /// <summary>Mapped here; a runner credential needs a named runner scope.</summary>
        RunnerScope,
        /// <summary>
    /// Mapped only on the standalone Task Server; a runner credential gets 404,
    /// or 405 where the monolith maps the path for another method only.
    /// </summary>
        TaskServerOnly,
        /// <summary>Health probe, open to every caller.</summary>
        Anonymous,
        /// <summary>The runner reads its own client identity without a scope.</summary>
        RunnerSelfRead,
        /// <summary>Open self-registration of the local profile; the networked profile answers 404.</summary>
        LocalProfileOnly,
    }

    private sealed record HostRoute(string Methods, string Template, Monolith Monolith);

    private static readonly HostRoute[] Classified =
    [
        new("GET", "/healthz", Monolith.Anonymous),
        new("POST", "/api/clients/register", Monolith.LocalProfileOnly),
        new("GET", "/api/clients/{}", Monolith.RunnerSelfRead),
        new("POST", "/api/clients/{}/runner-git-capability", Monolith.RunnerScope),
        new("POST", "/api/runner/artifacts", Monolith.RunnerScope),
        new("GET", "/api/runner/artifacts/limits", Monolith.RunnerScope),
        new("POST", "/api/runner/artifacts/outcome", Monolith.RunnerScope),
        new("POST", "/api/runner/claim", Monolith.RunnerScope),
        new("POST", "/api/runner/completion", Monolith.RunnerScope),
        new("POST", "/api/runner/events", Monolith.RunnerScope),
        new("POST", "/api/runner/lease/acquire", Monolith.RunnerScope),
        new("POST", "/api/runner/lease/release", Monolith.RunnerScope),
        new("POST", "/api/runner/lease/renew", Monolith.RunnerScope),
        new("POST", "/api/runner/lease/{}/revoked-reference", Monolith.RunnerScope),
        new("POST", "/api/runner/logs", Monolith.RunnerScope),
        new("POST", "/api/runner/orchestrator-decisions/claim", Monolith.RunnerScope),
        new("POST", "/api/runner/orchestrator-decisions/complete", Monolith.RunnerScope),
        new("GET", "/api/runner/post-merge/projects/{}", Monolith.RunnerScope),
        new("POST", "/api/runner/project-chat/claim", Monolith.RunnerScope),
        new("POST", "/api/runner/project-chat/complete", Monolith.RunnerScope),
        new("POST", "/api/runner/project-chat/renew", Monolith.RunnerScope),
        new("GET", "/api/system/about", Monolith.RunnerScope),
        new("GET", "/api/tasks/{}/attachments/{}", Monolith.RunnerScope),
        new("GET", "/api/tasks/{}/files/{}", Monolith.RunnerScope),
        new("GET", "/api/v1/artifact-limits", Monolith.TaskServerOnly),
        new("GET,POST", "/api/v1/failure-fingerprints", Monolith.TaskServerOnly),
        new("POST", "/api/v1/gates/attempts/{}/containment", Monolith.RunnerScope),
        new("POST", "/api/v1/gates/attempts/{}/phase", Monolith.RunnerScope),
        new("POST", "/api/v1/gates/attempts/{}/renew", Monolith.RunnerScope),
        new("POST", "/api/v1/gates/attempts/{}/report", Monolith.RunnerScope),
        new("POST", "/api/v1/gates/claims", Monolith.RunnerScope),
        new("GET", "/api/v1/gates/subjects/{}", Monolith.RunnerScope),
        new("GET", "/api/v1/hosts/{}/runtime-capacity", Monolith.TaskServerOnly),
        new("POST", "/api/v1/integrations/claims", Monolith.TaskServerOnly),
        new("POST", "/api/v1/integrations/{}/lease/renew", Monolith.TaskServerOnly),
        new("POST", "/api/v1/integrations/{}/report", Monolith.TaskServerOnly),
        new("POST", "/api/v1/post-work/attempts/{}/renew", Monolith.TaskServerOnly),
        new("POST", "/api/v1/post-work/attempts/{}/report", Monolith.TaskServerOnly),
        new("POST", "/api/v1/post-work/claims", Monolith.TaskServerOnly),
        new("GET", "/api/v1/projects", Monolith.RunnerScope),
        new("GET", "/api/v1/projects/{}/repository", Monolith.TaskServerOnly),
        new("GET", "/api/v1/projects/{}/tasks/{}", Monolith.RunnerScope),
        new("POST", "/api/v1/protocol/compatibility", Monolith.RunnerScope),
        new("GET", "/api/v1/reviews/attempts/{}", Monolith.RunnerScope),
        new("POST", "/api/v1/reviews/attempts/{}/cleanup", Monolith.RunnerScope),
        new("POST", "/api/v1/reviews/attempts/{}/lease/renew", Monolith.RunnerScope),
        new("POST", "/api/v1/reviews/attempts/{}/reclaim", Monolith.RunnerScope),
        new("POST", "/api/v1/reviews/attempts/{}/report", Monolith.RunnerScope),
        new("GET", "/api/v1/runner/tasks/{}/attachments/{}", Monolith.TaskServerOnly),
        new("PUT", "/api/v1/runners/{}", Monolith.RunnerScope),
        new("PUT", "/api/v1/runners/{}/capabilities", Monolith.RunnerScope),
        new("POST", "/api/v1/runners/{}/capability-failures", Monolith.RunnerScope),
        new("POST", "/api/v1/runners/{}/claims", Monolith.TaskServerOnly),
        new("GET", "/api/v1/runners/{}/cli-update", Monolith.TaskServerOnly),
        new("POST", "/api/v1/runners/{}/cli-update/result", Monolith.TaskServerOnly),
        new("POST", "/api/v1/runners/{}/device-login/claim", Monolith.TaskServerOnly),
        new("GET", "/api/v1/runners/{}/device-login/{}", Monolith.TaskServerOnly),
        new("POST", "/api/v1/runners/{}/device-login/{}/completion", Monolith.TaskServerOnly),
        new("POST", "/api/v1/runners/{}/device-login/{}/instructions", Monolith.TaskServerOnly),
        new("POST", "/api/v1/runners/{}/git-setups/claim", Monolith.TaskServerOnly),
        new("POST", "/api/v1/runners/{}/git-setups/{}/key", Monolith.TaskServerOnly),
        new("POST", "/api/v1/runners/{}/git-setups/{}/{}", Monolith.TaskServerOnly),
        new("PUT", "/api/v1/runners/{}/outbox-status", Monolith.RunnerScope),
        new("POST", "/api/v1/runners/{}/project-probes/{}", Monolith.TaskServerOnly),
        new("POST", "/api/v1/runners/{}/provider-canary/{}", Monolith.TaskServerOnly),
        new("GET", "/api/v1/runners/{}/provider-comparison", Monolith.TaskServerOnly),
        new("POST", "/api/v1/runners/{}/quota/claim", Monolith.RunnerScope),
        new("PUT", "/api/v1/runners/{}/quota/report", Monolith.RunnerScope),
        new("POST", "/api/v1/runners/{}/reports", Monolith.TaskServerOnly),
        new("POST", "/api/v1/runners/{}/repository-credentials", Monolith.RunnerScope),
        new("POST", "/api/v1/runners/{}/review-claims", Monolith.RunnerScope),
        new("POST", "/api/v1/runs/{}/artifacts", Monolith.TaskServerOnly),
        new("GET", "/api/v1/runs/{}/artifacts/{}/content", Monolith.TaskServerOnly),
        new("POST", "/api/v1/runs/{}/completion", Monolith.TaskServerOnly),
        new("POST", "/api/v1/runs/{}/credentials/{}", Monolith.TaskServerOnly),
        new("POST", "/api/v1/runs/{}/events", Monolith.TaskServerOnly),
        new("POST", "/api/v1/runs/{}/executor-receipt", Monolith.TaskServerOnly),
        new("POST", "/api/v1/runs/{}/lease/release", Monolith.TaskServerOnly),
        new("POST", "/api/v1/runs/{}/lease/renew", Monolith.TaskServerOnly),
        new("POST", "/api/v1/runs/{}/post-steps/{}/claim", Monolith.TaskServerOnly),
        new("POST", "/api/v1/runs/{}/post-steps/{}/complete", Monolith.TaskServerOnly),
        new("POST", "/api/v1/runs/{}/reconcile", Monolith.TaskServerOnly),
        new("POST", "/api/v1/runs/{}/result-finalization", Monolith.TaskServerOnly),
        new("POST", "/api/v1/runs/{}/result-handoff", Monolith.TaskServerOnly),
        new("POST", "/api/v1/runs/{}/revoked-reference", Monolith.TaskServerOnly),
        new("PUT", "/api/v1/source-facts/{}/blames", Monolith.TaskServerOnly),
        new("GET,POST", "/api/v1/source-facts/{}/demands", Monolith.TaskServerOnly),
        new("POST", "/api/v1/source-facts/{}/demands/{}/complete", Monolith.TaskServerOnly),
        new("GET,PUT", "/api/v1/source-facts/{}/diffs", Monolith.TaskServerOnly),
        new("PUT", "/api/v1/source-facts/{}/path-histories", Monolith.TaskServerOnly),
        new("POST", "/api/v1/source-facts/{}/producer/lease", Monolith.TaskServerOnly),
        new("PUT", "/api/v1/source-facts/{}/reachability", Monolith.TaskServerOnly),
        new("POST", "/api/v1/source-facts/{}/review-material/reads", Monolith.TaskServerOnly),
        new("POST", "/api/v1/work-permits/{}/accept", Monolith.TaskServerOnly),
    ];

    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "host-routes-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Every_host_route_is_classified_for_the_monolith()
    {
        var called = HostRouteTemplates();
        var classified = Classified.Select(route => route.Template).ToHashSet(StringComparer.Ordinal);

        var unclassified = called.Where(template => !classified.Contains(template)).ToList();
        Assert.True(unclassified.Count == 0,
            "Host routes without a monolith classification (add each to Classified in this test, and map a runner scope "
            + "in AccessSecurityMiddleware or mark it TaskServerOnly):\n  " + string.Join("\n  ", unclassified));
        var stale = classified.Where(template => !called.Contains(template)).Order(StringComparer.Ordinal).ToList();
        Assert.True(stale.Count == 0, "Classified routes the host no longer calls:\n  " + string.Join("\n  ", stale));
        Assert.Equal(Classified.Length, classified.Count);
    }

    [Fact]
    public async Task Each_host_route_answers_the_host_or_says_it_is_not_mapped()
    {
        await using var factory = Factory();
        var store = factory.Services.GetRequiredService<AccessSecurityStore>();
        var probe = factory.Services.GetRequiredService<EndpointProbe>();
        // Holds a scope no host route needs: a scoped route refuses it before any handler runs.
        var unscoped = Enroll(store, "host-route-unscoped", [RunnerScopes.Deploy]);
        // The host's own scopes, for routes no endpoint here can answer.
        var host = Enroll(store, "host-route-host", null);
        using var http = factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://studio.test"), HandleCookies = false });

        var problems = new List<string>();
        foreach (var route in Classified)
        foreach (var method in route.Methods.Split(','))
        {
            var label = $"{method} {route.Template} ({route.Monolith})";
            switch (route.Monolith)
            {
                case Monolith.RunnerScope:
                {
                    var answer = await SendAsync(http, probe, method, SamplePath(route.Template, unscoped.Id), unscoped.Secret);
                    if (answer.Endpoint is null)
                        problems.Add($"{label}: the monolith maps no endpoint");
                    else if ((answer.Status, answer.Error) != (403, "runner-scope-denied"))
                        problems.Add($"{label}: a runner without the scope got {answer.Status} {answer.Error}, not 403 runner-scope-denied");
                    break;
                }
                case Monolith.TaskServerOnly:
                {
                    var path = SamplePath(route.Template, host.Id);
                    var answer = await SendAsync(http, probe, method, path, host.Secret);
                    if (answer.Endpoint is not null)
                        problems.Add($"{label}: the monolith maps {answer.Endpoint}; classify it RunnerScope");
                    // 405 when the monolith maps the path for another method only.
                    else if (answer.Status != 405
                             && (answer.Status != 404 || answer.Error is not (null or RouteAbsenceContract.ErrorCode)))
                        problems.Add($"{label}: the host got {answer.Status} {answer.Error}, not 404 or 405");
                    var anonymous = await SendAsync(http, probe, method, path, null);
                    if (anonymous.Status != 401)
                        problems.Add($"{label}: an anonymous caller got {anonymous.Status}, not 401");
                    break;
                }
                case Monolith.Anonymous:
                {
                    var answer = await SendAsync(http, probe, method, SamplePath(route.Template, host.Id), null);
                    if (answer.Endpoint is null || answer.Status is 401 or 403 or 404)
                        problems.Add($"{label}: an anonymous caller got {answer.Status} {answer.Error}");
                    break;
                }
                case Monolith.RunnerSelfRead:
                {
                    var answer = await SendAsync(http, probe, method, SamplePath(route.Template, unscoped.Id), unscoped.Secret);
                    if (answer.Endpoint is null || answer.Status is 401 or 403)
                        problems.Add($"{label}: the runner got {answer.Status} {answer.Error} for its own identity");
                    break;
                }
                case Monolith.LocalProfileOnly:
                {
                    var answer = await SendAsync(http, probe, method, SamplePath(route.Template, host.Id), host.Secret);
                    if ((answer.Status, answer.Error) != (404, "registration-disabled"))
                        problems.Add($"{label}: the host got {answer.Status} {answer.Error}, not 404 registration-disabled");
                    break;
                }
            }
        }

        Assert.True(problems.Count == 0, "Host routes the networked monolith answers wrongly:\n  " + string.Join("\n  ", problems));
    }

    /// <summary>
    /// The route templates the host sources build: each URL literal, joined
    /// with the literals and expressions concatenated to it. An interpolation
    /// hole or a non-literal operand becomes <c>{}</c>; a query is dropped.
    /// </summary>
    internal static IReadOnlySet<string> HostRouteTemplates()
    {
        var templates = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var relative in Sources)
        {
            var source = File.ReadAllText(Path.Combine(RepoRoot(), relative));
            // Helpers such as SourceFactsRoot(id) => $"/api/v1/source-facts/{id}" are route prefixes.
            var helpers = HelperPrefix.Matches(source)
                .ToDictionary(match => match.Groups["name"].Value, match => match);
            foreach (Match literal in UrlLiteral.Matches(source))
            {
                if (helpers.Values.Any(helper => helper.Groups["url"].Index == literal.Groups["url"].Index)) continue;
                templates.Add(Normalize(literal.Groups["url"].Value + Concatenated(source, literal.Index + literal.Length)));
            }
            foreach (var (name, helper) in helpers)
            foreach (Match call in new Regex($@"\b{name}\([^()]*\)").Matches(source))
            {
                if (call.Index == helper.Index) continue;
                var tail = Concatenated(source, call.Index + call.Length);
                if (tail.Length > 0) templates.Add(Normalize(helper.Groups["url"].Value + tail));
            }
        }
        return templates;
    }

    private static readonly Regex UrlLiteral = new(@"\$?@?""(?<url>(?:/?api/|/healthz)[^""]*)""");
    private static readonly Regex HelperPrefix = new(@"(?<name>\w+)\([^()]*\)\s*=>\s*\$?""(?<url>/?api/[^""]*)""");
    private static readonly Regex StringOperand = new(@"\G\s*\+\s*\$?@?""(?<text>[^""]*)""");
    private static readonly Regex ExpressionOperand = new(@"\G\s*\+\s*(?<expression>[\w.]+(?:\([^()]*(?:\([^()]*\)[^()]*)*\))?)");

    /// <summary>The <c>+ operand</c> chain after <paramref name="index"/>.</summary>
    private static string Concatenated(string source, int index)
    {
        var tail = new System.Text.StringBuilder();
        while (true)
        {
            if (StringOperand.Match(source, index) is { Success: true } text)
            {
                tail.Append(text.Groups["text"].Value);
                index = text.Index + text.Length;
            }
            else if (ExpressionOperand.Match(source, index) is { Success: true } expression)
            {
                tail.Append("{}");
                index = expression.Index + expression.Length;
            }
            else
                return tail.ToString();
        }
    }

    private static string Normalize(string url)
    {
        var template = Regex.Replace(url, @"\{[^{}]*\}", "{}");
        var query = template.IndexOf('?');
        if (query >= 0) template = template[..query];
        return template.StartsWith('/') ? template : "/" + template;
    }

    private static string SamplePath(string template, string runnerId) => template.Replace("{}", runnerId);

    private sealed record Answer(int Status, string? Error, string? Endpoint);

    private static async Task<Answer> SendAsync(HttpClient http, EndpointProbe probe, string method, string path, string? bearer)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (bearer is not null) request.Headers.Authorization = new("Bearer", bearer);
        if (method is "POST" or "PUT") request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        probe.Last = null;
        using var response = await http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        string? error = null;
        if (body.StartsWith('{'))
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var code) && code.ValueKind == JsonValueKind.String)
                error = code.GetString();
        }
        var endpoint = probe.Last is { } matched && !RoutePermissionCatalog.IsMethodRejection(matched)
            ? matched.DisplayName : null;
        return new Answer((int)response.StatusCode, error, endpoint);
    }

    private static (string Id, string Secret) Enroll(AccessSecurityStore store, string name, IReadOnlyList<string>? scopes)
    {
        var enrolled = store.EnrollRunner(store.CreateEnrollment(new RunnerEnrollmentRequest(name, scopes, null, null)).Code);
        return (enrolled.Runner.Id, enrolled.Secret);
    }

    private WebApplicationFactory<Program> Factory()
        => new TestHostFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["Security:Profile"] = SecurityProfiles.Networked,
                ["AllowedHosts"] = "studio.test",
            }));
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<EndpointProbe>();
                services.AddSingleton<IStartupFilter, EndpointProbeStartupFilter>();
            });
        });

    /// <summary>The endpoint routing selected for the last request; routing leaves it on the context.</summary>
    private sealed class EndpointProbe
    {
        public volatile Endpoint? Last;
    }

    private sealed class EndpointProbeStartupFilter(EndpointProbe probe) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                await nextMiddleware(context);
                probe.Last = context.GetEndpoint();
            });
            next(app);
        };
    }

    private static string RepoRoot([CallerFilePath] string sourceFile = "")
    {
        foreach (var start in new[] { Path.GetDirectoryName(sourceFile), AppContext.BaseDirectory })
        {
            var current = start;
            while (!string.IsNullOrEmpty(current))
            {
                if (File.Exists(Path.Combine(current, "agent-taskboard.sln"))) return current;
                current = Path.GetDirectoryName(current);
            }
        }
        throw new InvalidOperationException("Repository root with agent-taskboard.sln not found.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspace)) Directory.Delete(_workspace, true);
    }
}
