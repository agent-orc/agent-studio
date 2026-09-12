
using AgentStudio.Security;

namespace AgentStudio.Docs;

/// <summary>
/// Project-level docs surface (prototype): security archive +
/// architecture decision browser + a read-only wiki view over the
/// project's <c>docs/</c> tree. See <see cref="ProjectDocsService"/>
/// for storage layout and resolution rules.
/// </summary>
public static class ProjectDocsEndpoints
{
    public static void MapProjectDocsEndpoints(this WebApplication app)
    {
        // ---- Security ----

        app.MapGet("/api/projects/{projectName}/security", (string projectName, ProjectDocsService docs) =>
        {
            var ov = docs.GetSecurityOverview(projectName);
            return ov == null
                ? Results.NotFound(new { error = $"Unknown project '{projectName}'" })
                : Results.Ok(ov);
        });

        app.MapGet("/api/projects/{projectName}/security/files/{**relPath}", (string projectName, string relPath, ProjectDocsService docs) =>
        {
            var content = docs.ReadSecurityFile(projectName, relPath);
            if (content == null) return Results.NotFound(new { error = "File not found or path rejected" });
            return Results.Ok(new { relPath, content });
        });

        app.MapPut("/api/projects/{projectName}/security/files/{**relPath}", async (string projectName, string relPath, HttpRequest req, ProjectDocsService docs) =>
        {
            using var reader = new StreamReader(req.Body);
            var json = await reader.ReadToEndAsync();
            string? content = null;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("content", out var c))
                    content = c.GetString();
            }
            catch
            {
                return Results.BadRequest(new { error = "Invalid JSON body" });
            }
            if (content == null) return Results.BadRequest(new { error = "content field required" });
            var ok = docs.WriteSecurityFile(projectName, relPath, content);
            return ok ? Results.Ok(new { relPath, saved = true })
                      : Results.BadRequest(new { error = "Write rejected (path unsafe or project unknown)" });
        });

        app.MapPut("/api/projects/{projectName}/security/meta", (string projectName, SecurityMeta meta, ProjectDocsService docs) =>
        {
            var ok = docs.WriteSecurityMeta(projectName, meta);
            return ok ? Results.Ok(new { saved = true })
                      : Results.NotFound(new { error = $"Unknown project '{projectName}'" });
        });

        // ---- Wiki (docs/ tree) ----

        app.MapGet("/api/projects/{projectName}/wiki", (string projectName, ProjectDocsService docs) =>
        {
            var ov = docs.GetWikiOverview(projectName);
            return ov == null
                ? Results.NotFound(new { error = $"Unknown project '{projectName}'" })
                : Results.Ok(ov);
        });

        // Repository-owned style-guide family. The same applicability result
        // is consumed by intake prompt enrichment, so the Wiki never advertises
        // a guide that the coding run cannot discover.
        app.MapGet("/api/projects/{projectName}/style-guides", (string projectName, ProjectStyleGuideService guides, bool refresh = false) =>
        {
            var catalogue = guides.GetCatalogue(projectName, refresh);
            return catalogue == null
                ? Results.NotFound(new { error = $"Unknown project '{projectName}'" })
                : Results.Ok(catalogue);
        });

        // Repository-owned experiment Dossiers. The catalogue is discovered by
        // scanning docs/ recursively for workbench.json descriptors (post-2026-07
        // migration the workbench folders are theme-distributed, e.g. under
        // operations/ and quality/); HTML is returned as data and is never
        // executed by the backend origin.
        app.MapGet("/api/projects/{projectName}/workbenches", (string projectName, bool? history, ProjectDocsService docs) =>
        {
            var catalogue = docs.GetWikiWorkbenchCatalogue(projectName, history == true);
            return catalogue == null
                ? Results.NotFound(new { error = $"Unknown project '{projectName}'" })
                : Results.Ok(catalogue);
        });

        // Shared workspace-wide/project-scoped queue. Project filtering is a
        // query because both UI variants intentionally consume the identical
        // projection and ordering policy. Scoped network users only receive
        // projects assigned to their account.
        app.MapGet("/api/workbenches", (string? project, HttpContext context,
            WorkbenchCatalogueService workbenches,
            ProjectDocsService docs,
            AgentStudio.Registry.ProjectRegistry projects) =>
        {
            var requested = string.IsNullOrWhiteSpace(project)
                ? null
                : projects.FindByIdOrDisplayName(project)?.DisplayName
                    ?? projects.FindByShortCode(project)?.DisplayName
                    ?? project;
            var names = requested == null
                ? workbenches.ListProjectNames()
                : [requested];
            if (context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is HumanPrincipal human)
            {
                names = names
                    .Where(name => ProjectAccessAuthorization.Allows(human.User, name, projects))
                    .ToList();
            }
            return Results.Ok(docs.GetWikiWorkbenchOverview(names, requested));
        });

        app.MapGet("/api/projects/{projectName}/workbenches/{id}", (string projectName, string id, ProjectDocsService docs) =>
        {
            var item = docs.GetWikiWorkbenchCatalogue(projectName, includeHistory: true)?.Items
                .FirstOrDefault(candidate => candidate.Id == id);
            if (item is { Valid: false })
                return Results.UnprocessableEntity(new { error = item.Error ?? "Dossier descriptor is invalid." });
            var document = docs.ReadWikiWorkbench(projectName, id);
            return document == null
                ? Results.NotFound(new { error = "Dossier is not available in the selected Wiki source." })
                : Results.Ok(document);
        });

        app.MapGet("/api/projects/{projectName}/workbenches/{key}/references",
            (string projectName, string key, WorkbenchCatalogueService workbenches) =>
            {
                var references = workbenches.References(projectName, key);
                return references == null
                    ? Results.NotFound(new { error = "Document reference key not found" })
                    : Results.Ok(references);
            });

        // The Workbench Decision gate (AGT-2375). Prepare validates against
        // the exact revision/fingerprint and writes nothing; confirm is the
        // single durable write and lands in the Dossier's own workbench.json.
        app.MapPost("/api/projects/{projectName}/workbenches/{id}/decisions/prepare",
            (string projectName, string id, PrepareWorkbenchDecisionRequest body,
                WorkbenchDecisionService decisions) =>
                WorkbenchDecisionHttpResult(decisions.Prepare(projectName, id, body)));

        app.MapPost("/api/projects/{projectName}/workbenches/{id}/decisions/confirm",
            (string projectName, string id, ConfirmWorkbenchDecisionRequest body,
                WorkbenchDecisionService decisions, ProjectDocsService docs) =>
            {
                var result = decisions.Confirm(projectName, id, body);
                // The descriptor is a docs/ file, so the warm wiki cache must
                // not keep serving the pre-decision bytes.
                if (result.Success) docs.InvalidateWikiContent(projectName);
                return WorkbenchDecisionHttpResult(result);
            });

        app.MapPost("/api/projects/{projectName}/workbenches/{id}/document",
            (string projectName, string id, DocumentWorkbenchRequest body,
                WorkbenchLifecycleService lifecycle, ProjectDocsService docs) =>
            {
                var result = lifecycle.Document(projectName, id, body);
                if (result.Success) docs.InvalidateWikiContent(projectName);
                return WorkbenchLifecycleHttpResult(result);
            });

        app.MapPut("/api/projects/{projectName}/workbenches/{id}/review",
            (string projectName, string id, RecordWorkbenchReviewRequest body,
                WorkbenchReviewService reviews, ProjectDocsService docs) =>
            {
                var result = reviews.Record(projectName, id, body);
                if (result.Success) docs.InvalidateWikiContent(projectName);
                return result.Success ? Results.Ok(result)
                    : result.ErrorCode == "not-found" ? Results.NotFound(result)
                    : result.ErrorCode == "stale-revision" ? Results.Conflict(result)
                    : Results.BadRequest(result);
            });

        // The physical docs/ folder hierarchy (folders + .md/.html/.json files)
        // that backs the wiki navigation tree. No git is touched here, and a warm
        // cache serves it without opening a file (AGT-2013); the ETag lets a
        // frontend reload skip the payload entirely with a 304. Per-doc commit
        // metadata is still fetched lazily via /history.
        app.MapGet("/api/projects/{projectName}/wiki/tree", (string projectName, ProjectDocsService docs, HttpContext http) =>
        {
            var res = docs.GetWikiTreeResult(projectName);
            return res == null
                ? Results.NotFound(new { error = $"Unknown project '{projectName}'" })
                : ConditionalOk(http, res.ETag, res.Tree);
        });

        // Recently-edited wiki pages (page / git author / timestamp), newest
        // first, for the dashboard landing surface. Touches git (one log walk),
        // memoized on the wiki branch HEAD so a warm request skips the walk
        // (AGT-2013); `limit` is clamped server-side. Sits before the /files
        // catch-all for path precedence.
        app.MapGet("/api/projects/{projectName}/wiki/recent", (string projectName, ProjectDocsService docs, GitService git, HttpContext http, int? limit) =>
        {
            var n = Math.Clamp(limit ?? 12, 1, 50);
            var res = docs.GetWikiRecentEditsResult(projectName, git, n);
            return res == null
                ? Results.NotFound(new { error = $"Unknown project '{projectName}'" })
                : ConditionalOk(http, res.ETag, res.Edits);
        });

        // The generated wiki Pulse landing view: change feed + inbox + drift,
        // PULSE-2 warnings/live docs work, and maintenance-run summaries. It is
        // composed server-side so the landing
        // surface costs two git walks instead of the tree + recent + per-doc
        // history fan-out. Sits before the /files catch-all for path precedence.
        app.MapGet("/api/projects/{projectName}/wiki/pulse", (string projectName, ProjectDocsService docs, GitService git, int? feedLimit) =>
        {
            var pulse = docs.GetWikiPulse(projectName, git, feedLimit ?? 12);
            return pulse == null
                ? Results.NotFound(new { error = $"Unknown project '{projectName}'" })
                : Results.Ok(pulse with
                {
                    Lifecycle = ProjectDocsService.MergeWorkbenchLifecycle(pulse.Lifecycle, pulse.Workbenches),
                });
        });

        // One directory level of the wiki for the folder-overview surface:
        // direct children (folders first, then pages, each alphabetical) with
        // sniffed titles, plain-text summaries, and folder child counts. An
        // empty relPath lists the wiki root. Sits before the /files catch-all
        // for path precedence, like its sibling routes.
        app.MapGet("/api/projects/{projectName}/wiki/folder/{**relPath}", (string projectName, string? relPath, ProjectDocsService docs, GitService git) =>
        {
            var folder = docs.GetWikiFolder(projectName, relPath, git);
            return folder == null
                ? Results.NotFound(new { error = "Folder not found or path rejected" })
                : Results.Ok(folder);
        });

        // Lexical wiki search (BM25 over title/headings/body) with an optional
        // fail-open semantic query-expansion layer (semantic=true). The limit
        // is clamped server-side; a blank query is a 400, an unknown project a
        // 404. Sits before the /files catch-all for path precedence.
        app.MapGet("/api/projects/{projectName}/wiki/search", async (string projectName, string? q, bool? semantic, int? limit, WikiSearchService search, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(q))
                return Results.BadRequest(new { error = "q is required" });
            var res = await search.SearchAsync(projectName, q.Trim(), semantic == true, Math.Clamp(limit ?? 20, 1, 50), ct);
            return res == null
                ? Results.NotFound(new { error = $"Unknown project '{projectName}'" })
                : Results.Ok(res);
        });

        // Curated wiki home sections from docs/app/config/home.json. Missing or
        // malformed file degrades to empty sections; configured links are kept
        // and annotated with an exists flag instead of being dropped. Sits
        // before the /files catch-all for path precedence.
        app.MapGet("/api/projects/{projectName}/wiki/home", (string projectName, ProjectDocsService docs) =>
        {
            var home = docs.GetWikiHome(projectName);
            return home == null
                ? Results.NotFound(new { error = $"Unknown project '{projectName}'" })
                : Results.Ok(home);
        });

        // Shared, versioned Wiki Overview curation. This is intentionally
        // separate from operator-local stars: pins mutate home.json and are
        // visible to everyone, including agents that use the Overview.
        app.MapPut("/api/projects/{projectName}/wiki/home/pins/{**relPath}",
            (string projectName, string relPath, WikiHomePinRequest body,
                ProjectDocsService docs, GitService git) =>
            {
                var rel = Normalize(relPath);
                if (rel == null) return Results.BadRequest(new { error = "relPath is required" });
                var result = docs.SetWikiHomePin(
                    projectName, rel, body.Pinned, body.SectionTitle, body.Label, body.Note);
                if (!result.Success) return Results.BadRequest(new { error = result.Error });
                return CommitWikiChange(
                    docs, git, projectName, result.FullPath!,
                    body.Pinned ? $"wiki: pin {rel} to home" : $"wiki: unpin {rel} from home");
            });

        app.MapGet("/api/projects/{projectName}/wiki/files/{**relPath}", (string projectName, string relPath, ProjectDocsService docs) =>
        {
            var result = docs.ReadWikiFileResult(projectName, relPath);
            return result.File == null
                ? Results.NotFound(new { error = result.Error })
                : Results.Ok(result.File);
        });

        app.MapPut("/api/projects/{projectName}/wiki/files/{**relPath}", (string projectName, string relPath, WikiSaveRequest body, ProjectDocsService docs, GitService git) =>
        {
            var rel = Normalize(relPath);
            if (rel == null) return Results.BadRequest(new { error = "relPath is required" });
            if (body.Content == null) return Results.BadRequest(new { error = "content field required" });

            var result = docs.WriteWikiFile(projectName, rel, body.Content);
            if (!result.Success) return Results.BadRequest(new { error = result.Error });

            var repoRoot = git.ResolveRepoRootForProject(projectName);
            if (string.IsNullOrWhiteSpace(repoRoot))
                return Results.BadRequest(new { error = "Repository not found" });

            var branch = git.GetStatusForRepoRoot(repoRoot).Branch;
            if (!result.Changed)
                return Results.Ok(new { relPath = rel, saved = true, changed = false, sha = (string?)null, branch });

            var repoRel = Path.GetRelativePath(repoRoot, result.FullPath!).Replace('\\', '/');
            var commit = git.CommitPaths(repoRoot, $"wiki: update {rel}", new[] { repoRel });
            if (!commit.Success) return Results.BadRequest(new { error = commit.Error, branch });
            docs.InvalidateWikiContent(projectName);
            return Results.Ok(new { relPath = rel, saved = true, changed = true, sha = commit.Sha, branch });
        });

        // Page lifecycle metadata. Archive changes classification only: the
        // source page remains readable, linkable, and recoverable.
        app.MapPut("/api/projects/{projectName}/wiki/classification/{**relPath}",
            (string projectName, string relPath, WikiClassificationRequest body,
                ProjectDocsService docs, WikiCompanionStore companions, GitService git,
                WorkbenchCatalogueService workbenches) =>
            {
                var rel = Normalize(relPath);
                if (rel == null) return Results.BadRequest(new { error = "relPath is required" });
                // Generic Wiki classification writes a .meta.json sidecar, which
                // a Dossier's visibility does not read (AGT-2375). Canonical
                // Dossiers archive only through their decision endpoint.
                if (workbenches.OwnsCanonicalPath(projectName, rel))
                    return Results.Conflict(new
                    {
                        error = "Canonical Dossiers must use the explicit decision endpoint; Wiki classification cannot archive them.",
                    });
                var status = body.Status?.Trim().ToLowerInvariant();
                if (status is not ("archived" or "aktuell"))
                    return Results.BadRequest(new { error = "status must be 'archived' or 'aktuell'" });
                var result = docs.SetWikiClassificationStatus(projectName, rel, status, companions);
                if (!result.Success) return Results.BadRequest(new { error = result.Error });
                return CommitWikiChange(
                    docs, git, projectName, result.FullPath!,
                    $"wiki: classify {rel} as {status}");
            });

        // Serves images/diagrams referenced from wiki docs so relative
        // `![](images/foo.png)` paths render in place. Markdown-only docs go
        // through /wiki/files; binary assets stream here with a content type.
        app.MapGet("/api/projects/{projectName}/wiki/assets/{**relPath}", (string projectName, string relPath, ProjectDocsService docs) =>
        {
            var asset = docs.ReadWikiAsset(projectName, relPath);
            return asset == null
                ? Results.NotFound(new { error = "Asset not found or type not allowed" })
                : Results.File(asset.Value.Path, asset.Value.ContentType);
        });

        // Per-doc provenance + history: which model last touched it (frontmatter
        // `model:` wins, else the latest commit's Co-authored-by trailer), plus
        // the file's git log (when / why / who, newest first). Memoized on HEAD
        // so a re-open costs no git spawn (AGT-2013); the ETag lets a reload 304.
        // `history/` sits before the catch-all so it isn't swallowed by
        // /wiki/files/{**relPath}.
        app.MapGet("/api/projects/{projectName}/wiki/history/{**relPath}", (string projectName, string relPath, ProjectDocsService docs, GitService git, HttpContext http) =>
        {
            var res = docs.GetWikiHistory(projectName, relPath, git);
            return res == null
                ? Results.NotFound(new { error = "File not found or path rejected" })
                : ConditionalOk(http, res.ETag, res.History);
        });

        // Content of a wiki doc as it existed at an earlier commit, so the
        // history panel can preview an old revision. The bytes are content-
        // addressed, so the read is cached permanently and the ETag is the sha
        // (AGT-2013). Sits before the /files catch-all for the same precedence
        // reason as /history.
        app.MapGet("/api/projects/{projectName}/wiki/revisions/{sha}/{**relPath}", (string projectName, string sha, string relPath, ProjectDocsService docs, GitService git, HttpContext http) =>
        {
            var res = docs.GetWikiRevision(projectName, sha, relPath, git);
            return res == null
                ? Results.NotFound(new { error = "Revision not found or path rejected" })
                : ConditionalOk(http, res.ETag, res.Revision);
        });

        // ---- Wiki mutations (commit-backed create / move / delete) ----

        // Create a new wiki page (.md/.html/.json). The file is written to disk
        // then committed into the project repo so it shows up in git history.
        app.MapPost("/api/projects/{projectName}/wiki/pages", (string projectName, WikiCreatePageRequest body, ProjectDocsService docs, GitService git) =>
        {
            var rel = Normalize(body.RelPath);
            if (rel == null) return Results.BadRequest(new { error = "relPath is required" });
            var result = docs.CreateWikiPage(projectName, rel, body.Content);
            if (!result.Success) return Results.BadRequest(new { error = result.Error });
            return CommitWikiChange(docs, git, projectName, result.FullPath!, $"wiki: create {rel}", result.ExtraPaths);
        });

        app.MapPost("/api/projects/{projectName}/wiki/folders", (string projectName, WikiCreateFolderRequest body, ProjectDocsService docs, GitService git) =>
        {
            var rel = Normalize(body.RelPath);
            if (rel == null) return Results.BadRequest(new { error = "relPath is required" });
            var result = docs.CreateWikiFolder(projectName, rel);
            if (!result.Success) return Results.BadRequest(new { error = result.Error });
            return CommitWikiChange(docs, git, projectName, result.FullPath!, $"wiki: create folder {rel}");
        });

        // Move/rename a wiki node (file or folder) via git mv + commit.
        app.MapPost("/api/projects/{projectName}/wiki/move", (string projectName, WikiMoveRequest body, ProjectDocsService docs, GitService git) =>
        {
            if (docs.WikiWriteBlockReason(projectName) is { } blocked)
                return Results.Conflict(new { error = blocked });
            var from = Normalize(body.FromRelPath);
            var to = Normalize(body.ToRelPath);
            if (from == null || to == null) return Results.BadRequest(new { error = "fromRelPath and toRelPath are required" });

            var fromFull = docs.ResolveWikiNodeFullPath(projectName, from);
            var toFull = docs.ResolveWikiNodeFullPath(projectName, to);
            var repoRoot = git.ResolveRepoRootForProject(projectName);
            if (fromFull == null || toFull == null || string.IsNullOrWhiteSpace(repoRoot))
                return Results.BadRequest(new { error = "Invalid path or repository not found" });

            var fromRepoRel = Path.GetRelativePath(repoRoot, fromFull).Replace('\\', '/');
            var toRepoRel = Path.GetRelativePath(repoRoot, toFull).Replace('\\', '/');
            var commit = git.MoveAndCommit(repoRoot, fromRepoRel, toRepoRel, $"wiki: move {from} -> {to}");
            if (!commit.Success) return Results.BadRequest(new { error = commit.Error });
            docs.InvalidateWikiContent(projectName);
            return Results.Ok(new { from, to, sha = commit.Sha });
        });

        // Persist the sibling display order of category folders (consumed by the
        // wiki tree and the folder overview). Stored beside the other wiki
        // metadata in docs/app/config/wiki-order.json and committed like every other wiki
        // mutation; folders missing from the list sort behind alphabetically.
        app.MapPut("/api/projects/{projectName}/wiki/folder-order", (string projectName, WikiFolderOrderRequest body, ProjectDocsService docs, GitService git) =>
        {
            if (body.OrderedNames == null)
                return Results.BadRequest(new { error = "orderedNames field required" });
            var parent = Normalize(body.ParentRelPath) ?? string.Empty;
            var result = docs.SetWikiFolderOrder(projectName, parent, body.OrderedNames);
            if (!result.Success) return Results.BadRequest(new { error = result.Error });
            return CommitWikiChange(docs, git, projectName, result.FullPath!,
                $"wiki: reorder categories under {(parent.Length == 0 ? "root" : parent)}");
        });

        // Persist the sibling display order of documents through the same
        // config and commit-backed mutation shape as category ordering.
        app.MapPut("/api/projects/{projectName}/wiki/file-order", (string projectName, WikiFileOrderRequest body, ProjectDocsService docs, GitService git) =>
        {
            if (body.OrderedNames == null)
                return Results.BadRequest(new { error = "orderedNames field required" });
            var parent = Normalize(body.ParentRelPath) ?? string.Empty;
            var result = docs.SetWikiFileOrder(projectName, parent, body.OrderedNames);
            if (!result.Success) return Results.BadRequest(new { error = result.Error });
            return CommitWikiChange(docs, git, projectName, result.FullPath!,
                $"wiki: reorder documents under {(parent.Length == 0 ? "root" : parent)}");
        });

        // Delete a wiki node (file or folder) via git rm + commit.
        app.MapDelete("/api/projects/{projectName}/wiki/files/{**relPath}", (string projectName, string relPath, ProjectDocsService docs, GitService git) =>
        {
            if (docs.WikiWriteBlockReason(projectName) is { } blocked)
                return Results.Conflict(new { error = blocked });
            var rel = Normalize(relPath);
            if (rel == null) return Results.BadRequest(new { error = "relPath is required" });
            var full = docs.ResolveWikiNodeFullPath(projectName, rel);
            var repoRoot = git.ResolveRepoRootForProject(projectName);
            if (full == null || string.IsNullOrWhiteSpace(repoRoot))
                return Results.BadRequest(new { error = "Invalid path or repository not found" });

            var repoRel = Path.GetRelativePath(repoRoot, full).Replace('\\', '/');
            var commit = git.RemoveAndCommit(repoRoot, repoRel, $"wiki: delete {rel}");
            if (!commit.Success) return Results.BadRequest(new { error = commit.Error });
            docs.InvalidateWikiContent(projectName);
            return Results.Ok(new { relPath = rel, sha = commit.Sha });
        });

        // ---- Architecture decisions ----

        app.MapGet("/api/projects/{projectName}/architecture", (string projectName, ProjectDocsService docs) =>
        {
            var ov = docs.GetArchitectureOverview(projectName);
            return ov == null
                ? Results.NotFound(new { error = $"Unknown project '{projectName}'" })
                : Results.Ok(ov);
        });

        app.MapGet("/api/projects/{projectName}/architecture/decisions/{id}", (string projectName, string id, ProjectDocsService docs) =>
        {
            var d = docs.GetArchitectureDecision(projectName, id);
            return d == null ? Results.NotFound(new { error = "Decision not found" }) : Results.Ok(d);
        });
    }

    /// <summary>
    /// Emits an <c>ETag</c> + <c>Cache-Control: no-cache</c> response, honouring a
    /// matching <c>If-None-Match</c> with <c>304 Not Modified</c> so a frontend
    /// reload of an unchanged wiki payload skips the body entirely (AGT-2013).
    /// The ETag is a strong validator derived from the docs signature / HEAD sha /
    /// commit sha, so a match provably means the client already holds the current
    /// version. <c>no-cache</c> tells the browser to store the response but always
    /// revalidate, which is what turns the next reload into a conditional GET.
    /// </summary>
    internal static IResult ConditionalOk(HttpContext http, string etag, object payload)
    {
        http.Response.Headers.ETag = etag;
        http.Response.Headers.CacheControl = "no-cache";

        foreach (var candidate in http.Request.Headers.IfNoneMatch)
        {
            if (candidate == "*" || candidate == etag)
                return Results.StatusCode(StatusCodes.Status304NotModified);
        }
        return Results.Ok(payload);
    }

    private static IResult WorkbenchDecisionHttpResult(WorkbenchDecisionResult result)
    {
        if (result.Success) return Results.Ok(result);
        return result.ErrorCode switch
        {
            "not-canonical" => Results.NotFound(result),
            "stale-revision" or "dirty-descriptor" or "operation-id-conflict"
                or "already-settled" => Results.Conflict(result),
            "validation" => Results.BadRequest(result),
            _ => Results.Json(result, statusCode: StatusCodes.Status500InternalServerError),
        };
    }

    internal static IResult WorkbenchLifecycleHttpResult(DocumentWorkbenchResult result)
    {
        if (result.Success) return Results.Ok(result);
        return result.ErrorCode switch
        {
            "not-canonical" => Results.NotFound(result),
            "stale-revision" or "dirty-descriptor" or "invalid-transition"
                or "references-not-terminal" => Results.Conflict(result),
            "validation" => Results.BadRequest(result),
            _ => Results.Json(result, statusCode: StatusCodes.Status500InternalServerError),
        };
    }

    /// <summary>Trims and forward-slashes a client path; null when blank.</summary>
    private static string? Normalize(string? relPath)
    {
        if (string.IsNullOrWhiteSpace(relPath)) return null;
        return relPath.Replace('\\', '/').Trim().TrimStart('/');
    }

    /// <summary>
    /// Commits a freshly created wiki file/folder into the project repo and maps
    /// the git outcome to an HTTP result. Resolving the repo root or a failed
    /// commit both surface as a 400 so the UI can show the reason.
    /// </summary>
    private static IResult CommitWikiChange(
        ProjectDocsService docs,
        GitService git,
        string projectName,
        string fullPath,
        string message,
        IReadOnlyList<string>? extraPaths = null)
    {
        var repoRoot = git.ResolveRepoRootForProject(projectName);
        if (string.IsNullOrWhiteSpace(repoRoot))
            return Results.BadRequest(new { error = "Repository not found" });

        var repoRel = Path.GetRelativePath(repoRoot, fullPath).Replace('\\', '/');
        var paths = new List<string> { repoRel };
        if (extraPaths != null)
            foreach (var extra in extraPaths)
                paths.Add(Path.GetRelativePath(repoRoot, extra).Replace('\\', '/'));
        var commit = git.CommitPaths(repoRoot, message, paths);
        if (!commit.Success) return Results.BadRequest(new { error = commit.Error });
        docs.InvalidateWikiContent(projectName);
        return Results.Ok(new { relPath = repoRel, sha = commit.Sha });
    }
}

public record WikiCreatePageRequest(string RelPath, string? Content);
public record WikiCreateFolderRequest(string RelPath);
public record WikiMoveRequest(string FromRelPath, string ToRelPath);
public record WikiFolderOrderRequest(string? ParentRelPath, List<string>? OrderedNames);
public record WikiFileOrderRequest(string? ParentRelPath, List<string>? OrderedNames);
public record WikiSaveRequest(string? Content);
public record WikiClassificationRequest(string? Status);
public record WikiHomePinRequest(
    bool Pinned,
    string? SectionTitle,
    string? Label,
    string? Note);
