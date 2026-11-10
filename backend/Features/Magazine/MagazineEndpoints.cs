using System.Text.Json;
using AgentStudio.Operator;
using AgentStudio.Registry;
using AgentStudio.Security;
using Microsoft.AspNetCore.Http.Features;

namespace AgentStudio.Magazine;

/// <summary>
/// The magazine API (MAG-1): summaries the agents keep up to date, one
/// magazine for the installation, one per workspace and one per project.
/// Bodies are Markdown text served inside JSON; Studio never renders them
/// as HTML on the server. Every write is audited on the operator stream.
/// </summary>
public static class MagazineEndpoints
{
    /// <summary>
    /// Largest PUT request. A 256 KiB body can grow when JSON escapes it;
    /// anything larger is refused before it is read.
    /// </summary>
    public const long MaxRequestBytes = 2L * 1024 * 1024;

    public static void MapMagazineEndpoints(this WebApplication app)
    {
        app.MapGet("/api/magazine", (HttpContext context, string? scope, bool? includeArchived,
            MagazineStore store, ProjectRegistry projects, WorkspaceRegistry workspaces) =>
        {
            var resolved = MagazineAccess.Resolve(context, scope, IamPermissions.MagazineRead, projects, workspaces);
            if (resolved.Refusal is { } refusal) return refusal;
            return Results.Ok(new
            {
                scope = resolved.Scope!.ToString(),
                articles = store.List(resolved.Scope, includeArchived == true),
            });
        }).RequirePermission(IamPermissions.MagazineRead);

        app.MapGet("/api/magazine/{scope}/{slug}", (HttpContext context, string scope, string slug, int? revision,
            MagazineStore store, ProjectRegistry projects, WorkspaceRegistry workspaces) =>
        {
            var resolved = MagazineAccess.Resolve(context, scope, IamPermissions.MagazineRead, projects, workspaces);
            if (resolved.Refusal is { } refusal) return refusal;
            return Guarded(() =>
            {
                var article = store.Get(resolved.Scope!, slug, revision);
                if (article is null) return NotFound(revision);
                SetEtag(context, article.CurrentRevision);
                return Results.Ok(article);
            });
        }).RequirePermission(IamPermissions.MagazineRead);

        app.MapGet("/api/magazine/{scope}/{slug}/history", (HttpContext context, string scope, string slug,
            MagazineStore store, ProjectRegistry projects, WorkspaceRegistry workspaces) =>
        {
            var resolved = MagazineAccess.Resolve(context, scope, IamPermissions.MagazineRead, projects, workspaces);
            if (resolved.Refusal is { } refusal) return refusal;
            return Guarded(() =>
            {
                var revisions = store.History(resolved.Scope!, slug);
                if (revisions is null) return NotFound(null);
                SetEtag(context, revisions[0].Revision);
                return Results.Ok(new
                {
                    scope = resolved.Scope!.ToString(), slug, currentRevision = revisions[0].Revision, revisions,
                });
            });
        }).RequirePermission(IamPermissions.MagazineRead);

        app.MapPut("/api/magazine/{scope}/{slug}", async (HttpContext context, string scope, string slug,
            MagazineStore store, ProjectRegistry projects, WorkspaceRegistry workspaces, ControlPlaneAuditLog audit,
            CancellationToken ct) =>
        {
            var resolved = MagazineAccess.Resolve(context, scope, IamPermissions.MagazineWrite, projects, workspaces);
            if (resolved.Refusal is { } refusal) return refusal;
            if (!TryParseIfMatch(context.Request.Headers.IfMatch.ToString(), out var expected))
                return Error(StatusCodes.Status400BadRequest, "if-match-invalid",
                    "If-Match carries the article's current revision, for example \"3\".");
            var (request, readRefusal) = await ReadUpsertAsync(context, ct);
            if (readRefusal is not null) return readRefusal;
            var actor = OperatorEndpoints.Actor(context);
            return Guarded(() =>
            {
                var (article, created) = store.Upsert(resolved.Scope!, slug, request!, actor, expected);
                audit.Append(new ControlPlaneAuditRecord(ControlPlaneAuditStreams.Operator, actor,
                    "magazine.article.upsert", created ? "created" : "updated", Target(article),
                    Details: new
                    {
                        article.Scope, article.Slug, article.Revision, article.Kind,
                        bodyBytes = System.Text.Encoding.UTF8.GetByteCount(article.Body),
                    }));
                SetEtag(context, article.Revision);
                return created
                    ? Results.Created($"/api/magazine/{Uri.EscapeDataString(article.Scope)}/{article.Slug}", article)
                    : Results.Ok(article);
            });
        }).RequirePermission(IamPermissions.MagazineWrite);

        app.MapPost("/api/magazine/{scope}/{slug}/pin", (HttpContext context, string scope, string slug,
            MagazineFlagRequest? request, MagazineStore store, ProjectRegistry projects, WorkspaceRegistry workspaces,
            ControlPlaneAuditLog audit) =>
        {
            var resolved = MagazineAccess.Resolve(context, scope, IamPermissions.MagazineWrite, projects, workspaces);
            if (resolved.Refusal is { } refusal) return refusal;
            var pinned = request?.Pinned ?? true;
            return Guarded(() => Flag(context, audit, store.SetPinned(resolved.Scope!, slug, pinned),
                pinned ? "magazine.article.pin" : "magazine.article.unpin"));
        }).RequirePermission(IamPermissions.MagazineWrite);

        app.MapPost("/api/magazine/{scope}/{slug}/archive", (HttpContext context, string scope, string slug,
            MagazineFlagRequest? request, MagazineStore store, ProjectRegistry projects, WorkspaceRegistry workspaces,
            ControlPlaneAuditLog audit) =>
        {
            var resolved = MagazineAccess.Resolve(context, scope, IamPermissions.MagazineWrite, projects, workspaces);
            if (resolved.Refusal is { } refusal) return refusal;
            var archived = request?.Archived ?? true;
            return Guarded(() => Flag(context, audit, store.SetArchived(resolved.Scope!, slug, archived),
                archived ? "magazine.article.archive" : "magazine.article.restore"));
        }).RequirePermission(IamPermissions.MagazineWrite);
    }

    private static IResult Flag(HttpContext context, ControlPlaneAuditLog audit, MagazineArticleSummary? article, string operation)
    {
        if (article is null) return NotFound(null);
        audit.Append(new ControlPlaneAuditRecord(ControlPlaneAuditStreams.Operator, OperatorEndpoints.Actor(context),
            operation, "success", $"{article.Scope}/{article.Slug}",
            Details: new { article.Scope, article.Slug, article.Pinned, article.ArchivedAt }));
        SetEtag(context, article.Revision);
        return Results.Ok(article);
    }

    private static async Task<(MagazineUpsertRequest? Request, IResult? Refusal)> ReadUpsertAsync(
        HttpContext context, CancellationToken ct)
    {
        if (context.Request.ContentLength is > MaxRequestBytes)
            return (null, Error(StatusCodes.Status413PayloadTooLarge, "magazine-body-too-large",
                $"The request has {context.Request.ContentLength} bytes; an article body holds at most {MagazineLimits.MaxBodyBytes} bytes (256 KiB)."));
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = MaxRequestBytes;
        try
        {
            var request = await context.Request.ReadFromJsonAsync<MagazineUpsertRequest>(ct);
            return request is null
                ? (null, Error(StatusCodes.Status400BadRequest, "magazine-request-invalid", "The request body is a JSON article."))
                : (request, null);
        }
        catch (JsonException)
        {
            return (null, Error(StatusCodes.Status400BadRequest, "magazine-request-invalid", "The request body is not a valid JSON article."));
        }
        catch (InvalidOperationException)
        {
            return (null, Error(StatusCodes.Status415UnsupportedMediaType, "magazine-request-invalid", "Send the article as application/json."));
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return (null, Error(StatusCodes.Status413PayloadTooLarge, "magazine-body-too-large",
                $"An article body holds at most {MagazineLimits.MaxBodyBytes} bytes (256 KiB)."));
        }
    }

    /// <summary>
    /// Reads <c>If-Match</c>: absent is null, otherwise one revision number,
    /// quoted or bare, optionally weak (<c>W/"3"</c>).
    /// </summary>
    internal static bool TryParseIfMatch(string? header, out int? revision)
    {
        revision = null;
        var text = header?.Trim();
        if (string.IsNullOrEmpty(text)) return true;
        if (text.StartsWith("W/", StringComparison.Ordinal)) text = text[2..];
        text = text.Trim('"');
        if (int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value)
            && value >= 0)
        {
            revision = value;
            return true;
        }
        return false;
    }

    private static IResult Guarded(Func<IResult> action)
    {
        try
        {
            return action();
        }
        catch (MagazineException exception)
        {
            return Results.Json(new
            {
                error = exception.Code, message = exception.Message, currentRevision = exception.CurrentRevision,
            }, statusCode: exception.Status);
        }
    }

    private static string Target(MagazineArticle article) => $"{article.Scope}/{article.Slug}@{article.Revision}";

    private static void SetEtag(HttpContext context, int revision)
        => context.Response.Headers.ETag = $"\"{revision.ToString(System.Globalization.CultureInfo.InvariantCulture)}\"";

    private static IResult NotFound(int? revision) => revision is null
        ? Error(StatusCodes.Status404NotFound, "magazine-article-not-found", "No article has this slug in this magazine.")
        : Error(StatusCodes.Status404NotFound, "magazine-revision-not-found", $"The article has no revision {revision}.");

    internal static IResult Error(int status, string code, string message)
        => Results.Json(new { error = code, message }, statusCode: status);
}

/// <summary>
/// Who reads and writes which magazine. The access middleware already
/// checked the route permission and, for <c>project:</c> routes, the project;
/// this repeats the project check and decides the installation and workspace
/// magazines, which the middleware defers here:
/// <list type="bullet">
/// <item>a project's magazine needs the permission in that project;</item>
/// <item>a workspace's magazine is readable where one of its projects is,
/// and writable only with a workspace binding;</item>
/// <item>the installation's magazine needs a workspace binding.</item>
/// </list>
/// Without a captured <see cref="RequestAccess"/> (local profile) the local
/// operator reads and writes every scope.
/// </summary>
public static class MagazineAccess
{
    public sealed record Resolution(MagazineScope? Scope, IResult? Refusal);

    public static Resolution Resolve(HttpContext context, string? scopeText, string permission,
        ProjectRegistry projects, WorkspaceRegistry workspaces)
    {
        if (!MagazineScope.TryParse(scopeText, out var scope))
            return Refuse(StatusCodes.Status400BadRequest, "magazine-scope-invalid",
                "The scope is installation, workspace:<id> or project:<id>.");
        var access = context.Items[AccessSecurityMiddleware.RequestAccessItem] as RequestAccess;
        switch (scope.Kind)
        {
            case MagazineScopeKind.Project:
            {
                var project = projects.FindById(scope.Id!) ?? projects.FindByShortCode(scope.Id);
                if (project is null) return Refuse(StatusCodes.Status404NotFound, "magazine-scope-not-found", "No project has this id.");
                if (access is not null && !access.AllowsProject(permission, project.Id)) return Forbidden(permission);
                return new Resolution(scope.WithId(project.Id), null);
            }
            case MagazineScopeKind.Workspace:
            {
                workspaces.EnsureLoaded();
                var workspace = workspaces.Find(scope.Id!);
                if (workspace is null) return Refuse(StatusCodes.Status404NotFound, "magazine-scope-not-found", "No workspace has this id.");
                if (access is not null && !access.Allows(permission, PermissionTarget.WorkspaceWide)
                    && (permission != IamPermissions.MagazineRead
                        || !projects.List().Any(project => !project.Archived
                            && string.Equals(project.WorkspaceId, workspace.Id, StringComparison.OrdinalIgnoreCase)
                            && access.AllowsProject(permission, project.Id))))
                    return Forbidden(permission);
                return new Resolution(scope.WithId(workspace.Id), null);
            }
            default:
                if (access is not null && !access.Allows(permission, PermissionTarget.WorkspaceWide)) return Forbidden(permission);
                return new Resolution(MagazineScope.Installation, null);
        }
    }

    private static Resolution Forbidden(string permission) => Refuse(StatusCodes.Status403Forbidden, "magazine-scope-denied",
        $"No role binding grants '{permission}' for this magazine.");

    private static Resolution Refuse(int status, string code, string message)
        => new(null, MagazineEndpoints.Error(status, code, message));
}
