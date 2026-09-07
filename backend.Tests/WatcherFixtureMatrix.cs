using System.Text.Json;

namespace AgentStudio.Tests;

/// <summary>
/// One replayable finding from the 2026-09-06 evening, as recorded in the
/// Watcher dossier section 10.1.
/// </summary>
public sealed record WatcherFixture
{
    public required string Id { get; init; }
    public int DossierRow { get; init; }
    public required string Finding { get; init; }
    public required string ExpectedClass { get; init; }
    public required string ExpectedRule { get; init; }

    /// <summary>Cards the operator wrote by hand for this finding that evening.</summary>
    public IReadOnlyList<string> ManualTickets { get; init; } = [];

    public string? Note { get; init; }

    /// <summary>The normalized signals the Task Server already held.</summary>
    public required WatcherSweepInput Signals { get; init; }
}

/// <summary>
/// Loader for <c>testdata/watcher/fixtures-2026-09-06.json</c>. The matrix is
/// data, not code, so a new finding is added by editing JSON and the detectors
/// stay the only place that interprets it.
/// </summary>
public static class WatcherFixtureMatrix
{
    private sealed record MatrixFile
    {
        public DateTime NowUtc { get; init; }
        public string Note { get; init; } = string.Empty;
        public List<WatcherFixture> Fixtures { get; init; } = [];
    }

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private static readonly Lazy<MatrixFile> Loaded = new(Read);

    /// <summary>The single clock the whole matrix is replayed against.</summary>
    public static DateTime NowUtc => Loaded.Value.NowUtc;

    public static IReadOnlyList<WatcherFixture> All => Loaded.Value.Fixtures;

    public static WatcherFixture ById(string id)
        => All.SingleOrDefault(fixture => fixture.Id == id)
           ?? throw new InvalidOperationException($"Watcher fixture '{id}' is not in the matrix.");

    /// <summary>
    /// Every fixture's signals merged into one sweep, which is what a live
    /// five-minute sweep would have seen that evening.
    /// </summary>
    public static WatcherSweepInput MergedSweep(DateTime? nowUtc = null) => new()
    {
        NowUtc = nowUtc ?? NowUtc,
        Probes = All.SelectMany(fixture => fixture.Signals.Probes).ToList(),
        CapabilitySnapshots = All.SelectMany(fixture => fixture.Signals.CapabilitySnapshots).ToList(),
        Completions = All.SelectMany(fixture => fixture.Signals.Completions).ToList(),
        ReviewAttempts = All.SelectMany(fixture => fixture.Signals.ReviewAttempts).ToList(),
        IntegrationFailures = All.SelectMany(fixture => fixture.Signals.IntegrationFailures).ToList(),
        Projections = All.SelectMany(fixture => fixture.Signals.Projections).ToList(),
        ValidationErrors = All.SelectMany(fixture => fixture.Signals.ValidationErrors).ToList(),
        ToolVersions = All.SelectMany(fixture => fixture.Signals.ToolVersions).ToList(),
        DependentFailures = All.SelectMany(fixture => fixture.Signals.DependentFailures).ToList(),
    };

    /// <summary>One fixture replayed on its own, so a failure names the finding it broke.</summary>
    public static WatcherSweepInput Sweep(this WatcherFixture fixture, DateTime? nowUtc = null)
        => fixture.Signals with { NowUtc = nowUtc ?? NowUtc };

    public static string Path()
        => System.IO.Path.Combine(RepoRoot(), "testdata", "watcher", "fixtures-2026-09-06.json");

    private static MatrixFile Read()
    {
        var json = File.ReadAllText(Path());
        return JsonSerializer.Deserialize<MatrixFile>(json, Options)
               ?? throw new InvalidOperationException("The Watcher fixture matrix is empty.");
    }

    private static string RepoRoot()
    {
        var current = AppContext.BaseDirectory;
        while (current != null)
        {
            if (File.Exists(System.IO.Path.Combine(current, "agent-taskboard.sln"))) return current;
            current = System.IO.Path.GetDirectoryName(current);
        }
        throw new InvalidOperationException("agent-taskboard.sln not found above the test base directory.");
    }
}
