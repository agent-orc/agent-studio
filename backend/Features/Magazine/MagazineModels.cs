using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AgentStudio.Magazine;

/// <summary>The three scope kinds of a magazine (MAG-1).</summary>
public enum MagazineScopeKind
{
    Installation,
    Workspace,
    Project,
}

/// <summary>
/// One magazine: the installation's, a workspace's, or a project's. The wire
/// form is <c>installation</c>, <c>workspace:&lt;id&gt;</c> or
/// <c>project:&lt;id&gt;</c>; <see cref="Id"/> is null for the installation.
/// </summary>
public sealed partial record MagazineScope(MagazineScopeKind Kind, string? Id)
{
    public const string InstallationName = "installation";
    public const string WorkspacePrefix = "workspace:";
    public const string ProjectPrefix = "project:";

    public static readonly MagazineScope Installation = new(MagazineScopeKind.Installation, null);

    /// <summary>Ids become one folder name, so they keep to a portable character set.</summary>
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    public static bool TryParse(string? value, out MagazineScope scope)
    {
        scope = Installation;
        var text = value?.Trim();
        if (string.Equals(text, InstallationName, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var (prefix, kind) in new[] { (WorkspacePrefix, MagazineScopeKind.Workspace), (ProjectPrefix, MagazineScopeKind.Project) })
        {
            if (text is null || !text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var id = text[prefix.Length..];
            if (!IdPattern().IsMatch(id) || id.Contains("..", StringComparison.Ordinal)) return false;
            scope = new MagazineScope(kind, id);
            return true;
        }
        return false;
    }

    /// <summary>The same scope with its id replaced by the registry's canonical id.</summary>
    public MagazineScope WithId(string id) => this with { Id = id };

    public override string ToString() => Kind switch
    {
        MagazineScopeKind.Workspace => WorkspacePrefix + Id,
        MagazineScopeKind.Project => ProjectPrefix + Id,
        _ => InstallationName,
    };

    /// <summary>The scope's folder below the magazine root.</summary>
    internal string FolderName => Kind switch
    {
        MagazineScopeKind.Workspace => "workspace-" + Id,
        MagazineScopeKind.Project => "project-" + Id,
        _ => InstallationName,
    };
}

public static partial class MagazineLimits
{
    /// <summary>Largest Markdown body, counted in UTF-8 bytes.</summary>
    public const int MaxBodyBytes = 256 * 1024;
    public const int MaxTitleLength = 200;
    /// <summary>One short paragraph.</summary>
    public const int MaxSummaryLength = 600;
    public const int MaxSources = 100;
    public const int MaxSourceRefLength = 300;
    public const int MaxPersonaLength = 80;

    public static readonly IReadOnlyList<string> Kinds = ["digest", "decision-brief", "report", "note"];
    public static readonly IReadOnlyList<string> SourceKinds = ["card", "dossier", "batch"];

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,78}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    public static partial Regex SlugPattern();
}

/// <summary>A source an article is drawn from: a card key, a Dossier path or a batch id.</summary>
public sealed record MagazineSource(string Kind, string Ref);

/// <summary>Who wrote a revision: the authenticated principal and an optional persona name.</summary>
public sealed record MagazineAuthor(string Principal, string? Persona, string? PersonaId = null, string? RoleId = null);

/// <summary>Body of <c>PUT /api/magazine/{scope}/{slug}</c>.</summary>
public sealed record MagazineUpsertRequest(
    string? Title,
    string? Summary,
    string? Body,
    string? Kind,
    string? Persona,
    IReadOnlyList<MagazineSource>? Sources,
    string? PersonaId = null,
    string? RoleId = null);

/// <summary>Body of the pin and archive routes; an absent value sets the flag.</summary>
public sealed record MagazineFlagRequest(bool? Pinned, bool? Archived);

/// <summary>
/// One immutable revision. It is written once and never rewritten, so a
/// later issue (PT-8) can cite <c>scope/slug@revision</c> and still read the
/// exact text it quoted.
/// </summary>
public sealed record MagazineRevision
{
    public required int Revision { get; init; }
    public required DateTime At { get; init; }
    public required MagazineAuthor Author { get; init; }
    public required string Title { get; init; }
    public required string Summary { get; init; }
    public required string Kind { get; init; }
    public required string Body { get; init; }
    public IReadOnlyList<MagazineSource> Sources { get; init; } = [];
}

/// <summary>
/// The mutable head of an article: its current revision and its flags. The
/// content of every revision lives in its own revision file.
/// </summary>
public sealed record MagazineArticleHead
{
    public int SchemaVersion { get; init; } = 2;
    public required string Scope { get; init; }
    public required string Slug { get; init; }
    public required int Revision { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required DateTime UpdatedAt { get; init; }
    public required string Title { get; init; }
    public required string Summary { get; init; }
    public required string Kind { get; init; }
    public IReadOnlyList<MagazineAuthor> Authors { get; init; } = [];
    public IReadOnlyList<MagazineSource> Sources { get; init; } = [];
    public int BodyBytes { get; init; }
    public bool Pinned { get; init; }
    public DateTime? ArchivedAt { get; init; }
}

/// <summary>An article in a list: everything but the body.</summary>
public sealed record MagazineArticleSummary(
    string Scope,
    string Slug,
    int Revision,
    string Title,
    string Summary,
    string Kind,
    IReadOnlyList<MagazineAuthor> Authors,
    IReadOnlyList<MagazineSource> Sources,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    bool Pinned,
    DateTime? ArchivedAt,
    int BodyBytes)
{
    public static MagazineArticleSummary From(MagazineArticleHead head) => new(
        head.Scope, head.Slug, head.Revision, head.Title, head.Summary, head.Kind, head.Authors, head.Sources,
        head.CreatedAt, head.UpdatedAt, head.Pinned, head.ArchivedAt, head.BodyBytes);
}

/// <summary>
/// One article with its body. <see cref="Revision"/> is the revision shown;
/// <see cref="CurrentRevision"/> is the head, so a reader of an earlier
/// revision sees that it is not the latest. <see cref="BodyFormat"/> is
/// always <c>markdown</c>: the body is text and is never served as HTML.
/// </summary>
public sealed record MagazineArticle(
    string Scope,
    string Slug,
    int Revision,
    int CurrentRevision,
    string Title,
    string Summary,
    string Kind,
    MagazineAuthor Author,
    IReadOnlyList<MagazineAuthor> Authors,
    IReadOnlyList<MagazineSource> Sources,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime RevisionAt,
    bool Pinned,
    DateTime? ArchivedAt,
    string Body)
{
    [JsonPropertyOrder(-1)]
    public string BodyFormat => "markdown";
}

/// <summary>One entry of an article's history.</summary>
public sealed record MagazineRevisionEntry(
    int Revision,
    DateTime At,
    MagazineAuthor Author,
    string Title,
    string Summary,
    string Kind,
    int BodyBytes);

/// <summary>A refused magazine operation with a stable error code and HTTP status.</summary>
public sealed class MagazineException(int status, string code, string message, int? currentRevision = null)
    : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public int? CurrentRevision { get; } = currentRevision;
}
