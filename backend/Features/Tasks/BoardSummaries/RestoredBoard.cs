using System.Globalization;

namespace AgentStudio.Tasks;

/// <summary>
/// The persisted board loaded once per process start (S5). Between process
/// start and the first task index publication, a board read answers from it
/// marked <c>restored</c> instead of waiting for the first workspace scan.
/// After that publication the index serves every read and this copy is
/// released.
/// </summary>
public sealed class RestoredBoard(BoardSummaryStore store, ILogger<RestoredBoard> logger)
{
    /// <summary>Cheap boot hint; validation and loading stay off the host startup path.</summary>
    public bool HasPersistedCandidate => store.DatabasePath is { } path && File.Exists(path);
    /// <summary>Response header naming the board state: <c>restored</c> or <c>ready</c>.</summary>
    public const string StateHeader = "X-Board-State";
    public const string Restored = "restored";
    public const string Ready = "ready";
    /// <summary>When the restored board was persisted (the oldest served project), ISO 8601.</summary>
    public const string PersistedAtHeader = "X-Board-Restored-At";
    /// <summary>The task index generation of the served board (the oldest served project when restored).</summary>
    public const string GenerationHeader = "X-Board-Index-Generation";

    private readonly Lock _gate = new();
    private IReadOnlyList<BoardSummaryProject>? _projects;
    private bool _released;

    /// <summary>The persisted projects, loaded on first use; empty once released or when none exist.</summary>
    public IReadOnlyList<BoardSummaryProject> Projects
    {
        get
        {
            lock (_gate)
            {
                if (_released) return [];
                if (_projects is null)
                {
                    _projects = store.Load();
                    logger.LogInformation("board-summaries-restored projects={Projects} cards={Cards}",
                        _projects.Count, _projects.Sum(project => project.Cards.Count));
                }
                return _projects;
            }
        }
    }

    /// <summary>Loads the persisted board ahead of the first read.</summary>
    public void Warm() => _ = Projects;

    /// <summary>Drops the restored copy once the index has published.</summary>
    public void Release()
    {
        lock (_gate)
        {
            _released = true;
            _projects = null;
        }
    }

    /// <summary>
    /// The restored answer of <c>GET /api/tasks/</c>, or null when nothing was
    /// persisted. The tag follows the bytes, like the live read's.
    /// </summary>
    public IResult? List(HttpContext context, Func<string, bool> projectVisible, Func<string, bool>? watchPath,
        bool includeFixtures, string scope)
    {
        var projects = Projects;
        if (projects.Count == 0) return null;
        var cards = BoardSummaryPolicy.Select(projects, projectVisible, watchPath, includeFixtures);
        return Answer(context, projects, cards, BoardSummaryPolicy.ComposeList(cards), scope);
    }

    /// <summary>The restored answer of <c>GET /api/tasks/grouped</c>, or null when nothing was persisted.</summary>
    public IResult? Grouped(HttpContext context, Func<string, bool> projectVisible, bool includeFixtures,
        bool legacyReviewLane, string scope)
    {
        var projects = Projects;
        if (projects.Count == 0) return null;
        var cards = BoardSummaryPolicy.Select(projects, projectVisible, watchPath: null, includeFixtures);
        var gitStateAt = BoardSummaryPolicy.GitStateAt(projects, cards);
        if (gitStateAt is { } at) context.Response.Headers["X-Git-State-At"] = at.ToString("O");
        context.Response.Headers["X-Git-State-Stale"] = "true";
        return Answer(context, projects, cards,
            BoardSummaryPolicy.ComposeGrouped(cards, legacyReviewLane, gitStateAt), scope);
    }

    private static IResult Answer(HttpContext context, IReadOnlyList<BoardSummaryProject> projects,
        IReadOnlyList<BoardSummaryCard> cards, byte[] body, string scope)
    {
        var represented = cards.Select(card => card.ProjectName).ToHashSet(StringComparer.Ordinal);
        var served = projects.Where(project => represented.Contains(project.ProjectName)).ToList();
        if (served.Count == 0) served = projects.ToList();
        context.Response.Headers[StateHeader] = Restored;
        context.Response.Headers[PersistedAtHeader] = served.Min(project => project.PersistedAt).ToString("O");
        context.Response.Headers[GenerationHeader] =
            served.Min(project => project.IndexGeneration).ToString(CultureInfo.InvariantCulture);
        var etag = BoardReadValidator.ContentETag(scope, body);
        return BoardReadValidator.NotModified(context, etag) ?? BoardReadValidator.Bytes(context, etag, body);
    }
}
