using System.Text;
using System.Text.Json;
using AgentStudio.Diagnostics;
using AgentStudio.Persistence;
using AgentStudio.Security;

namespace AgentStudio.Magazine;

/// <summary>
/// The article store of the magazines (MAG-1). Every scope is one folder
/// below <c>&lt;TaskRepository&gt;/.magazine</c> (see
/// <see cref="MagazineStorePathPolicy"/>), every article one folder named by
/// its slug:
/// <code>
/// .magazine/project-PROJ-002/weekly-digest/article.json          head: revision, flags
/// .magazine/project-PROJ-002/weekly-digest/revisions/000003.json  one immutable revision
/// </code>
/// An upsert writes the next revision file first and then swaps the head, both
/// through <see cref="IAtomicJsonFileWriter"/>. A crash between the two leaves
/// an unreferenced revision file that the next upsert overwrites; a referenced
/// revision is never rewritten, so <c>scope/slug@revision</c> stays a stable
/// citation for a later issue (PT-8).
/// </summary>
public sealed class MagazineStore
{
    public const string HeadFileName = "article.json";
    public const string RevisionFolderName = "revisions";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _root;
    private readonly IAtomicJsonFileWriter _writer;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();

    public MagazineStore(IConfiguration configuration, TimeProvider clock, IAtomicJsonFileWriter? writer = null)
        : this(MagazineStorePathPolicy.Root(configuration), clock, writer)
    {
    }

    public MagazineStore(string root, TimeProvider clock, IAtomicJsonFileWriter? writer = null)
    {
        _root = Path.GetFullPath(root);
        _clock = clock;
        _writer = writer ?? new AtomicJsonFileWriter();
    }

    public string Root => _root;

    /// <summary>The articles of a scope without bodies: pinned first, then the latest update first.</summary>
    public IReadOnlyList<MagazineArticleSummary> List(MagazineScope scope, bool includeArchived = false)
    {
        var folder = ScopeFolder(scope);
        if (!Directory.Exists(folder)) return [];
        var heads = new List<MagazineArticleHead>();
        foreach (var articleFolder in Directory.EnumerateDirectories(folder))
        {
            var slug = Path.GetFileName(articleFolder);
            if (!MagazineLimits.SlugPattern().IsMatch(slug)) continue;
            if (ReadHead(scope, slug) is { } head && (includeArchived || head.ArchivedAt is null)) heads.Add(head);
        }
        return heads
            .OrderByDescending(head => head.Pinned)
            .ThenByDescending(head => head.UpdatedAt)
            .ThenBy(head => head.Slug, StringComparer.Ordinal)
            .Select(MagazineArticleSummary.From)
            .ToList();
    }

    /// <summary>The article at its current revision, or at <paramref name="revision"/>; null when either is absent.</summary>
    public MagazineArticle? Get(MagazineScope scope, string slug, int? revision = null)
    {
        RequireSlug(slug);
        var head = ReadHead(scope, slug);
        if (head is null) return null;
        var number = revision ?? head.Revision;
        if (number < 1 || number > head.Revision) return null;
        var content = ReadRevision(scope, slug, number);
        return content is null ? null : View(head, content);
    }

    /// <summary>Every revision of an article, newest first; null when the article is absent.</summary>
    public IReadOnlyList<MagazineRevisionEntry>? History(MagazineScope scope, string slug)
    {
        RequireSlug(slug);
        var head = ReadHead(scope, slug);
        if (head is null) return null;
        var entries = new List<MagazineRevisionEntry>(head.Revision);
        for (var number = head.Revision; number >= 1; number--)
        {
            if (ReadRevision(scope, slug, number) is not { } content) continue;
            entries.Add(new MagazineRevisionEntry(content.Revision, content.At, content.Author, content.Title,
                content.Summary, content.Kind, Encoding.UTF8.GetByteCount(content.Body)));
        }
        return entries;
    }

    /// <summary>
    /// Creates the article or appends a revision to it. An existing article
    /// needs <paramref name="expectedRevision"/>: none answers 428
    /// <c>if-match-required</c>, a stale one 409 <c>revision-conflict</c>.
    /// Returns the article and whether it was created.
    /// </summary>
    public (MagazineArticle Article, bool Created) Upsert(
        MagazineScope scope, string slug, MagazineUpsertRequest request, string principal, int? expectedRevision)
    {
        RequireSlug(slug);
        var validated = Validate(request);
        lock (_gate)
        {
            var head = ReadHead(scope, slug);
            if (head is null && expectedRevision is > 0)
                throw new MagazineException(StatusCodes.Status409Conflict, "revision-conflict",
                    $"The article does not exist, so revision {expectedRevision} is stale.", 0);
            if (head is not null && expectedRevision is null)
                throw new MagazineException(StatusCodes.Status428PreconditionRequired, "if-match-required",
                    $"The article exists; send If-Match with its current revision {head.Revision}.", head.Revision);
            if (head is not null && expectedRevision != head.Revision)
                throw new MagazineException(StatusCodes.Status409Conflict, "revision-conflict",
                    $"Revision {expectedRevision} is stale; the current revision is {head.Revision}.", head.Revision);

            var now = _clock.GetUtcNow().UtcDateTime;
            var author = new MagazineAuthor(principal, validated.Persona);
            var content = new MagazineRevision
            {
                Revision = (head?.Revision ?? 0) + 1,
                At = now,
                Author = author,
                Title = validated.Title,
                Summary = validated.Summary,
                Kind = validated.Kind,
                Body = validated.Body,
                Sources = validated.Sources,
            };
            var next = new MagazineArticleHead
            {
                Scope = scope.ToString(),
                Slug = slug,
                Revision = content.Revision,
                CreatedAt = head?.CreatedAt ?? now,
                UpdatedAt = now,
                Title = content.Title,
                Summary = content.Summary,
                Kind = content.Kind,
                Authors = head is null || head.Authors.Contains(author) ? head?.Authors ?? [author] : [.. head.Authors, author],
                Sources = content.Sources,
                BodyBytes = validated.BodyBytes,
                Pinned = head?.Pinned ?? false,
                ArchivedAt = head?.ArchivedAt,
            };
            _writer.WriteJson(RevisionPath(scope, slug, content.Revision), content, Json);
            _writer.WriteJson(HeadPath(scope, slug), next, Json);
            return (View(next, content), head is null);
        }
    }

    /// <summary>Sets or clears <c>pinned</c>; a flag change appends no revision.</summary>
    public MagazineArticleSummary? SetPinned(MagazineScope scope, string slug, bool pinned)
        => ChangeHead(scope, slug, head => head with { Pinned = pinned });

    /// <summary>Archives the article now, or restores it; a flag change appends no revision.</summary>
    public MagazineArticleSummary? SetArchived(MagazineScope scope, string slug, bool archived)
        => ChangeHead(scope, slug, head => head with
        {
            ArchivedAt = archived ? head.ArchivedAt ?? _clock.GetUtcNow().UtcDateTime : null,
        });

    private MagazineArticleSummary? ChangeHead(MagazineScope scope, string slug, Func<MagazineArticleHead, MagazineArticleHead> change)
    {
        RequireSlug(slug);
        lock (_gate)
        {
            var head = ReadHead(scope, slug);
            if (head is null) return null;
            var next = change(head);
            if (next != head) _writer.WriteJson(HeadPath(scope, slug), next, Json);
            return MagazineArticleSummary.From(next);
        }
    }

    private sealed record ValidatedRequest(
        string Title, string Summary, string Body, int BodyBytes, string Kind, string? Persona,
        IReadOnlyList<MagazineSource> Sources);

    private static ValidatedRequest Validate(MagazineUpsertRequest request)
    {
        var body = request.Body ?? "";
        var bodyBytes = Encoding.UTF8.GetByteCount(body);
        if (bodyBytes > MagazineLimits.MaxBodyBytes)
            throw BodyTooLarge(bodyBytes);
        if (body.Contains('\0'))
            throw Invalid("magazine-body-invalid", "The body must be text without NUL characters.");

        var title = request.Title?.Trim() ?? "";
        if (title.Length == 0 || title.Length > MagazineLimits.MaxTitleLength || title.Contains('\n') || title.Contains('\r'))
            throw Invalid("magazine-title-invalid", $"The title is one line of 1 to {MagazineLimits.MaxTitleLength} characters.");

        var summary = (request.Summary ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (summary.Length > MagazineLimits.MaxSummaryLength)
            throw Invalid("magazine-summary-too-long", $"The summary is at most {MagazineLimits.MaxSummaryLength} characters.");
        if (summary.Contains("\n\n", StringComparison.Ordinal))
            throw Invalid("magazine-summary-paragraphs", "The summary is one short paragraph.");

        var kind = request.Kind?.Trim().ToLowerInvariant() ?? "";
        if (!MagazineLimits.Kinds.Contains(kind))
            throw Invalid("magazine-kind-invalid", $"The kind is one of {string.Join(", ", MagazineLimits.Kinds)}.");

        var persona = string.IsNullOrWhiteSpace(request.Persona) ? null : request.Persona.Trim();
        if (persona is not null && (persona.Length > MagazineLimits.MaxPersonaLength || persona.Contains('\n')))
            throw Invalid("magazine-persona-invalid", $"The persona is one line of at most {MagazineLimits.MaxPersonaLength} characters.");

        var sources = new List<MagazineSource>();
        foreach (var source in request.Sources ?? [])
        {
            var sourceKind = source?.Kind?.Trim().ToLowerInvariant() ?? "";
            var reference = source?.Ref?.Trim() ?? "";
            if (!MagazineLimits.SourceKinds.Contains(sourceKind) || reference.Length == 0
                || reference.Length > MagazineLimits.MaxSourceRefLength || reference.Any(char.IsControl))
                throw Invalid("magazine-source-invalid",
                    $"A source has a kind ({string.Join(", ", MagazineLimits.SourceKinds)}) and a reference of at most {MagazineLimits.MaxSourceRefLength} characters.");
            var normalized = new MagazineSource(sourceKind, reference);
            if (!sources.Contains(normalized)) sources.Add(normalized);
        }
        if (sources.Count > MagazineLimits.MaxSources)
            throw Invalid("magazine-sources-too-many", $"An article cites at most {MagazineLimits.MaxSources} sources.");

        // The store keeps text it may show to every reader of the scope; a
        // credential in it is refused rather than silently masked.
        foreach (var text in new[] { title, summary, body })
            if (CredentialRedactor.ContainsToken(text))
                throw Invalid("magazine-credential-in-content",
                    "The article contains a credential-shaped value. Remove it; if it is a real credential, revoke it.");

        return new ValidatedRequest(title, summary, body, bodyBytes, kind, persona, sources);
    }

    public static MagazineException BodyTooLarge(long bytes) => new(StatusCodes.Status413PayloadTooLarge,
        "magazine-body-too-large",
        $"The body has {bytes} bytes; an article body holds at most {MagazineLimits.MaxBodyBytes} bytes (256 KiB).");

    private static MagazineException Invalid(string code, string message) => new(StatusCodes.Status400BadRequest, code, message);

    private static void RequireSlug(string slug)
    {
        if (!MagazineLimits.SlugPattern().IsMatch(slug))
            throw Invalid("magazine-slug-invalid", "A slug has 1 to 80 lowercase letters, digits and inner hyphens.");
    }

    private static MagazineArticle View(MagazineArticleHead head, MagazineRevision content) => new(
        head.Scope, head.Slug, content.Revision, head.Revision, content.Title, content.Summary, content.Kind,
        content.Author, head.Authors, content.Sources, head.CreatedAt, head.UpdatedAt, content.At, head.Pinned,
        head.ArchivedAt, content.Body);

    private MagazineArticleHead? ReadHead(MagazineScope scope, string slug)
        => Read<MagazineArticleHead>(HeadPath(scope, slug));

    private MagazineRevision? ReadRevision(MagazineScope scope, string slug, int revision)
        => Read<MagazineRevision>(RevisionPath(scope, slug, revision));

    private static T? Read<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            // The folder vanished between the existence check and the read.
            SilentCatch.Note(exception, "MagazineStore: article file vanished during read");
            return null;
        }
    }

    private string ScopeFolder(MagazineScope scope) => Path.Combine(_root, scope.FolderName);

    private string ArticleFolder(MagazineScope scope, string slug) => Path.Combine(ScopeFolder(scope), slug);

    private string HeadPath(MagazineScope scope, string slug) => Path.Combine(ArticleFolder(scope, slug), HeadFileName);

    private string RevisionPath(MagazineScope scope, string slug, int revision)
        => Path.Combine(ArticleFolder(scope, slug), RevisionFolderName, revision.ToString("D6", System.Globalization.CultureInfo.InvariantCulture) + ".json");
}
