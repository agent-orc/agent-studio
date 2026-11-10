using System.Globalization;
using System.Text.Json;
using AgentStudio.Registry;
using AgentStudio.Security;
using AgentStudio.Shared;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace AgentStudio.Operator;

/// <summary>
/// The operator decision stream and watcher lease (operator agent level
/// Dossier, OP-2, decision OA-D2 option B). Human sessions, API tokens and
/// operator agent sessions read the stream and the attention list; each
/// reader sees only events of the projects it may read. The routes publish facts; transition rules stay
/// in orchestration-decisions.
/// </summary>
public static class OperatorEndpoints
{
    private const int DefaultLimit = 200;
    private const int MaxLimit = 1000;

    public static void MapOperatorEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/operator");

        group.MapGet("/decisions", (HttpContext context, OperatorDecisionLog log, ProjectRegistry projects,
            string? after, int? limit) =>
        {
            if (!TryCursor(after, out var cursor))
                return Results.BadRequest(new { error = "invalid-cursor", message = "after must be a cursor from this stream." });
            var reader = ReaderFor(context, projects);
            var page = log.Read(cursor, Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit),
                item => OperatorDecisionPolicy.Shows(item, reader));
            return Results.Ok(new OperatorDecisionsResponse(page.Events, Format(page.Cursor), Format(page.Head),
                page.Oldest is { } oldest ? Format(oldest) : null, page.HasMore, page.Gap,
                (int)log.Retention.TotalHours));
        });

        // The attention list (AGT-3576): the facts the stream currently sees,
        // in the reader's project scope, as one current list instead of events.
        group.MapGet("/attention", async (HttpContext context, OperatorDecisionPublisher publisher,
            ProjectRegistry projects, CancellationToken ct) =>
            Results.Ok(OperatorAttention.Project(await publisher.CurrentFactsAsync(ct), ReaderFor(context, projects))));

        group.MapGet("/decisions/stream", async (HttpContext context, OperatorDecisionLog log,
            OperatorDecisionOptions options, ProjectRegistry projects, IOptions<JsonOptions> json,
            IHostApplicationLifetime lifetime, string? after) =>
        {
            // EventSource resends the last id it received as Last-Event-ID.
            var requested = after ?? context.Request.Headers["Last-Event-ID"].FirstOrDefault();
            if (!TryCursor(requested, out var cursor))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new { error = "invalid-cursor" }, context.RequestAborted);
                return;
            }
            // Without a cursor the stream starts at the head: only new events.
            if (string.IsNullOrWhiteSpace(requested)) cursor = log.Head;
            var reader = ReaderFor(context, projects);
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            // A stopping host ends open streams; clients reconnect with their last id.
            using var stream = CancellationTokenSource.CreateLinkedTokenSource(
                context.RequestAborted, lifetime.ApplicationStopping);
            var ct = stream.Token;
            try
            {
                await context.Response.WriteAsync("retry: 5000\n\n", ct);
                await context.Response.Body.FlushAsync(ct);
                var first = true;
                while (!ct.IsCancellationRequested)
                {
                    var page = log.Read(cursor, DefaultLimit, item => OperatorDecisionPolicy.Shows(item, reader));
                    if (first && page.Gap)
                        await WriteFrameAsync(context, null, "stream.gap", new
                        {
                            after = Format(cursor),
                            oldestCursor = page.Oldest is { } oldest ? Format(oldest) : null,
                            headCursor = Format(page.Head),
                        }, json.Value.SerializerOptions, ct);
                    first = false;
                    foreach (var item in page.Events)
                        await WriteFrameAsync(context, item.Cursor, item.Type, item, json.Value.SerializerOptions, ct);
                    cursor = page.Cursor;
                    if (page.HasMore) continue;
                    var head = page.Head;
                    await log.WaitForAppendAsync(head, options.StreamKeepAlive, ct);
                    if (log.Head == head)
                    {
                        await context.Response.WriteAsync(": keep-alive\n\n", ct);
                        await context.Response.Body.FlushAsync(ct);
                    }
                }
            }
            catch (OperationCanceledException exception) when (ct.IsCancellationRequested)
            {
                // The client disconnected; it resumes with the last id it received.
                SilentCatch.Note(exception, "operator decision stream closed by the client");
            }
        });

        var lease = group.MapGroup("/watcher/lease");

        lease.MapGet("", (WatcherLeaseService leases) =>
            leases.Current() is { } current
                ? Results.Ok(new { active = true, lease = leases.View(current) })
                : Results.Ok(new { active = false }));

        lease.MapPost("", (HttpContext context, WatcherLeaseService leases, WatcherLeaseRequest? request) =>
        {
            var result = leases.Acquire(Actor(context), request ?? new WatcherLeaseRequest());
            return result.Outcome == WatcherLeaseOutcome.Conflict
                ? Results.Conflict(Conflict("watcher-active", result.Lease, leases,
                    "Another watcher holds the lease. Send takeOver: true to replace it; the take-over is audited."))
                : Results.Ok(new
                {
                    outcome = result.Outcome == WatcherLeaseOutcome.TakenOver ? "taken-over" : "granted",
                    lease = leases.View(result.Lease!),
                    previous = result.Previous is { } previous ? leases.View(previous) : null,
                });
        });

        lease.MapPost("/{leaseId}/heartbeat", (string leaseId, HttpContext context, WatcherLeaseService leases) =>
        {
            var result = leases.Heartbeat(leaseId, Actor(context));
            return result.Outcome == WatcherLeaseOutcome.Conflict
                ? Results.Conflict(Conflict("watcher-lease-lost", result.Lease, leases,
                    "This lease is no longer held by the caller."))
                : Results.Ok(new { lease = leases.View(result.Lease!) });
        });

        lease.MapDelete("/{leaseId}", (string leaseId, HttpContext context, WatcherLeaseService leases) =>
            leases.Release(leaseId, Actor(context))
                ? Results.NoContent()
                : Results.NotFound(new { error = "watcher-lease-not-held" }));
    }

    /// <summary>The project scope of the caller; the local profile has no principal and reads everything.</summary>
    internal static OperatorDecisionReader ReaderFor(HttpContext context, ProjectRegistry projects)
    {
        if (context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is not HumanPrincipal human
            || ProjectAccessAuthorization.HasUnrestrictedProjectAccess(human.User))
            return OperatorDecisionReader.Everything;
        return new OperatorDecisionReader(false, project => ProjectAccessAuthorization.Allows(human.User, project, projects));
    }

    /// <summary>The audited actor: the token or operator session, the signed-in human, or the local client.</summary>
    internal static string Actor(HttpContext context)
    {
        if (context.Items[AccessSecurityMiddleware.ServiceAccountPrincipalItem] is ServiceAccountPrincipal account)
            return account.ActorId;
        if (context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is HumanPrincipal human)
            return TimelineActors.Human(human.User.Id);
        var client = context.Request.Headers["X-Client-Id"].FirstOrDefault();
        return string.IsNullOrWhiteSpace(client) ? "local-operator" : "client:" + client.Trim();
    }

    private static object Conflict(string error, WatcherLease? holder, WatcherLeaseService leases, string message) => new
    {
        error,
        message,
        holder = holder?.Actor,
        session = holder?.Session,
        lastHeartbeatUtc = holder?.LastHeartbeatUtc,
        lease = holder is null ? null : leases.View(holder),
    };

    private static async Task WriteFrameAsync(HttpContext context, string? id, string name, object payload,
        JsonSerializerOptions json, CancellationToken ct)
    {
        var frame = (id is null ? "" : $"id: {id}\n") + $"event: {name}\ndata: {JsonSerializer.Serialize(payload, json)}\n\n";
        await context.Response.WriteAsync(frame, ct);
        await context.Response.Body.FlushAsync(ct);
    }

    private static bool TryCursor(string? value, out long cursor)
    {
        cursor = 0;
        return string.IsNullOrWhiteSpace(value)
            || long.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out cursor);
    }

    private static string Format(long value) => value.ToString(CultureInfo.InvariantCulture);
}
