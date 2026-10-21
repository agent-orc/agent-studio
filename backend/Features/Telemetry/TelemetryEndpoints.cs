using AgentStudio.Security;

namespace AgentStudio.Telemetry;

public sealed record TelemetryIngestTokenRequest(string? Name, IReadOnlyList<string>? Signals, DateTime? ExpiresAt);

public sealed record TelemetryIngestTokenResponse(string Id, string Name, string Project, string Environment,
    IReadOnlyList<string> Signals, DateTime CreatedAt, string? CreatedBy, DateTime? ExpiresAt, DateTime? LastUsedAt,
    DateTime? RevokedAt)
{
    public static TelemetryIngestTokenResponse From(TelemetryIngestToken token)
        => new(token.Id, token.Name, token.Project, token.Environment, token.Signals, token.CreatedAt, token.CreatedBy,
            token.ExpiresAt, token.LastUsedAt, token.RevokedAt);
}

/// <summary>The plaintext token is returned once, with the OTLP endpoint and header to configure.</summary>
public sealed record TelemetryIngestTokenCreatedResponse(TelemetryIngestTokenResponse Token, string Secret,
    string OtlpEndpoint, string OtlpHeaders);

/// <summary>
/// OTLP/HTTP intake (IP-15) and the per-environment query and token API.
/// Intake: <c>POST /api/telemetry/otlp/v1/metrics</c> and <c>/v1/logs</c>, so an
/// SDK configured with <c>OTEL_EXPORTER_OTLP_ENDPOINT=&lt;studio&gt;/api/telemetry/otlp</c>
/// reaches them. The intake authenticates its own ingest token in every
/// security profile; the query and token routes are project routes and follow
/// the Studio session rules.
/// </summary>
public static class TelemetryEndpoints
{
    public const string OtlpBasePath = "/api/telemetry/otlp";

    public static void MapTelemetryEndpoints(this WebApplication app)
    {
        app.MapPost(OtlpBasePath + "/v1/metrics", (HttpContext context, TelemetryIntakeService intake)
            => Receive(context, intake, TelemetrySignals.Metrics));
        app.MapPost(OtlpBasePath + "/v1/logs", (HttpContext context, TelemetryIntakeService intake)
            => Receive(context, intake, TelemetrySignals.Logs));

        var environment = app.MapGroup("/api/projects/{projectName}/environments/{environmentId}/telemetry");

        environment.MapGet("/metrics/names", (string projectName, string environmentId,
            ITelemetryEnvironmentDirectory directory, TelemetryStore store) =>
            Resolve(projectName, environmentId, directory, requireDeclared: false) is { } key
                ? Results.Ok(store.MetricNames(key)) : NotFound());

        environment.MapGet("/metrics", (HttpContext context, string projectName, string environmentId,
            ITelemetryEnvironmentDirectory directory, TelemetryStore store, TelemetryOptions options, TimeProvider clock) =>
        {
            if (Resolve(projectName, environmentId, directory, requireDeclared: false) is not { } key) return NotFound();
            var query = context.Request.Query;
            var name = query["name"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(name)) return BadRequest("'name' is required.");
            var now = clock.GetUtcNow();
            if (TelemetryQueryParser.Range(query["from"], query["to"], now, out var error) is not { } range) return BadRequest(error!);
            long? step = null;
            if (query["step"].FirstOrDefault() is { Length: > 0 } stepText)
            {
                if (!long.TryParse(stepText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                    return BadRequest("'step' must be a whole number of seconds.");
                step = parsed;
            }
            if (MetricBucketPolicy.Plan(now, range.From, range.To, step, options, out error) is not { } plan) return BadRequest(error!);
            if (TelemetryQueryParser.Aggregation(query["aggregation"]) is not { } aggregation)
                return BadRequest("'aggregation' must be avg, sum, min, max, count, or last.");
            if (TelemetryQueryParser.Limit(query["maxSeries"], TelemetryQueryParser.DefaultMaxSeries, TelemetryQueryParser.MaxSeriesLimit) is not { } maxSeries)
                return BadRequest($"'maxSeries' must be 1 to {TelemetryQueryParser.MaxSeriesLimit}.");
            if (TelemetryQueryParser.Attributes(QueryPairs(context), out error) is not { } attributes) return BadRequest(error!);
            return Results.Ok(store.QueryMetrics(key, new MetricQuery(name.Trim(), attributes, plan, aggregation, maxSeries)));
        });

        environment.MapGet("/logs", (HttpContext context, string projectName, string environmentId,
            ITelemetryEnvironmentDirectory directory, TelemetryStore store, TimeProvider clock) =>
        {
            if (Resolve(projectName, environmentId, directory, requireDeclared: false) is not { } key) return NotFound();
            var query = context.Request.Query;
            if (TelemetryQueryParser.Range(query["from"], query["to"], clock.GetUtcNow(), out var error) is not { } range) return BadRequest(error!);
            int? severity = null;
            if (query["minSeverity"].FirstOrDefault() is { Length: > 0 } severityText)
            {
                severity = TelemetrySeverity.Parse(severityText);
                if (severity is null) return BadRequest("'minSeverity' must be 0 to 24 or trace, debug, info, warn, error, fatal.");
            }
            if (TelemetryQueryParser.Limit(query["limit"], TelemetryQueryParser.DefaultLogLimit, TelemetryQueryParser.MaxLogLimit) is not { } limit)
                return BadRequest($"'limit' must be 1 to {TelemetryQueryParser.MaxLogLimit}.");
            LogCursor? cursor = null;
            if (query["cursor"].FirstOrDefault() is { Length: > 0 } cursorText && (cursor = LogCursor.Decode(cursorText)) is null)
                return BadRequest("'cursor' is not a cursor returned by this API.");
            var text = query["text"].FirstOrDefault();
            if (text?.Length > 256) return BadRequest("'text' must be at most 256 characters.");
            if (TelemetryQueryParser.Attributes(QueryPairs(context), out error) is not { } attributes) return BadRequest(error!);
            return Results.Ok(store.QueryLogs(key, new LogQuery(TelemetryAdmissionPolicy.UnixNanos(range.From),
                TelemetryAdmissionPolicy.UnixNanos(range.To), severity, string.IsNullOrEmpty(text) ? null : text,
                attributes, limit, cursor)));
        });

        environment.MapGet("/tokens", (string projectName, string environmentId,
            ITelemetryEnvironmentDirectory directory, ITelemetryIngestCredentials credentials) =>
            Resolve(projectName, environmentId, directory, requireDeclared: false) is { } key
                ? Results.Ok(credentials.List(key.Project, key.Environment).Select(TelemetryIngestTokenResponse.From).ToList())
                : NotFound());

        environment.MapPost("/tokens", (HttpContext context, string projectName, string environmentId,
            TelemetryIngestTokenRequest? request, ITelemetryEnvironmentDirectory directory,
            ITelemetryIngestCredentials credentials) =>
        {
            if (Resolve(projectName, environmentId, directory, requireDeclared: true) is not { } key) return NotFound();
            try
            {
                var actor = (context.Items[AccessSecurityMiddleware.HumanPrincipalItem] as HumanPrincipal)?.User.Id;
                var (token, secret) = credentials.Create(key.Project, key.Environment, request?.Name, request?.Signals,
                    request?.ExpiresAt, actor);
                var endpoint = $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}{OtlpBasePath}";
                return Results.Ok(new TelemetryIngestTokenCreatedResponse(TelemetryIngestTokenResponse.From(token), secret,
                    endpoint, "Authorization=Bearer " + secret));
            }
            catch (SecurityOperationException ex)
            {
                return Results.Json(new { error = ex.Code, message = ex.Message }, statusCode: ex.Status);
            }
        });

        environment.MapDelete("/tokens/{tokenId}", (string projectName, string environmentId, string tokenId,
            ITelemetryEnvironmentDirectory directory, ITelemetryIngestCredentials credentials) =>
            Resolve(projectName, environmentId, directory, requireDeclared: false) is { } key
            && credentials.Revoke(key.Project, key.Environment, tokenId)
                ? Results.NoContent() : NotFound());
    }

    private static async Task Receive(HttpContext context, TelemetryIntakeService intake, string signal)
    {
        var outcome = await intake.ReceiveAsync(signal, context.Request.ContentType,
            context.Request.Headers.ContentEncoding.FirstOrDefault(), context.Request.Headers.Authorization.FirstOrDefault(),
            context.Request.ContentLength, context.Request.Body, context.RequestAborted);
        context.Response.StatusCode = outcome.StatusCode;
        context.Response.ContentType = outcome.ContentType;
        context.Response.ContentLength = outcome.Body.Length;
        context.Response.Headers.CacheControl = "no-store";
        if (outcome.RetryAfterSeconds is { } retry)
            context.Response.Headers.RetryAfter = retry.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await context.Response.Body.WriteAsync(outcome.Body, context.RequestAborted);
    }

    /// <summary>The environment key for a route, or null when the project or environment is unknown.</summary>
    private static TelemetryEnvironmentKey? Resolve(string project, string environment, ITelemetryEnvironmentDirectory directory,
        bool requireDeclared)
    {
        if (!TelemetryStore.IsValidEnvironmentId(environment)) return null;
        var declared = directory.Environments(project);
        if (declared is null) return null;
        if (requireDeclared && !declared.Contains(environment, StringComparer.Ordinal)) return null;
        return new TelemetryEnvironmentKey(project, environment);
    }

    private static IEnumerable<KeyValuePair<string, string?>> QueryPairs(HttpContext context)
        => context.Request.Query.SelectMany(pair => pair.Value.Select(value => KeyValuePair.Create(pair.Key, value)));

    private static IResult NotFound()
        => Results.Json(new { error = "environment-not-found", message = "The project or environment is not declared." }, statusCode: 404);

    private static IResult BadRequest(string message)
        => Results.Json(new { error = "invalid-query", message }, statusCode: 400);
}
