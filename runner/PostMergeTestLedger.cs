using AgentStudio.OrchestrationDecisions;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>
/// RT-3: collects the per-test verdicts of one post-merge run (every attempt
/// with its result and duration), the focused runs and builds with their
/// durations, and the RT-2 classification hint. Facts only; the server applies
/// the triage policy.
/// </summary>
internal sealed class PostMergeTestLedger
{
    public const int MaximumTests = 256;
    public const int MaximumAttempts = 32;
    public const int MaximumFocusedRuns = 512;
    public const int MaximumCoveringFolders = 64;

    private sealed class Entry(string test, ProjectTestSuite suite, string command)
    {
        public string Test { get; } = test;
        public ProjectTestSuite Suite { get; } = suite;
        public string Command { get; set; } = command;
        public bool Quarantined { get; set; }
        public List<PostMergeTestAttempt> Attempts { get; } = [];
        public RedTestResult Repeat { get; set; }
        public IReadOnlyList<RedTestResult> Controls { get; set; } = [];
        public IReadOnlyList<RedTestBisectPoint> Points { get; set; } = [];
        public PostMergeCoveringFolders.Result? Covering { get; set; }
    }

    private readonly List<Entry> _entries = [];
    private readonly List<PostMergeTestRun> _runs = [];
    private string? _repeatSuite;
    private string? _attributionSuite;

    public bool HasTests => _entries.Count > 0;

    /// <summary>Records the initial failure of each observed test of one failed suite command.</summary>
    public void Initial(ProjectTestSuite suite, string command, IReadOnlyList<string> observed,
        IReadOnlyList<string> blocking, string sha)
    {
        _repeatSuite = suite.Id;
        foreach (var test in observed)
        {
            var entry = Find(suite.Id, test);
            if (entry is null) _entries.Add(entry = new(test, suite, command));
            entry.Command = command;
            entry.Quarantined = !blocking.Contains(test, StringComparer.Ordinal);
            entry.Attempts.Add(new(PostMergeRunPhases.Initial, PostMergeTestResults.Fail, null, sha));
        }
    }

    /// <summary>Records one repeat, control or bisect run, or a partial build (no tests).</summary>
    public void Record(PostMergeTestRun run)
    {
        _runs.Add(run);
        if (run.Tests.Count == 0) return;
        var suite = run.Phase is PostMergeRunPhases.Repeat or PostMergeRunPhases.Reference ? _repeatSuite : _attributionSuite;
        if (suite is null) return;
        foreach (var test in run.Tests)
        {
            if (Find(suite, test) is not { } entry) continue;
            var result = run.ResultOf(test);
            entry.Attempts.Add(new(run.Phase, result, run.DurationMs, run.Sha));
            if (run.Phase == PostMergeRunPhases.Repeat) entry.Repeat = Parse(result);
        }
    }

    /// <summary>Names the failed command whose tests the following control and bisect runs cover.</summary>
    public void BeginAttribution(string command)
        => _attributionSuite = _entries.FirstOrDefault(entry => entry.Command == command)?.Suite.Id;

    /// <summary>The protocol's control and point results, used for the classification hint.</summary>
    public void Attribution(IReadOnlyList<string> tests, IReadOnlyList<RedTestResult> controls,
        IReadOnlyList<RedTestBisectPoint> points)
    {
        if (_attributionSuite is null) return;
        foreach (var entry in _entries.Where(entry => entry.Suite.Id == _attributionSuite
                     && tests.Contains(entry.Test, StringComparer.Ordinal)))
        {
            entry.Controls = controls;
            entry.Points = points;
        }
    }

    /// <summary>Covering folders for every test that did not recover, read at the tip checkout.</summary>
    public async Task AddCoveringFoldersAsync(string checkout,
        Func<CancellationToken, Task<IReadOnlyList<string>>> trackedFiles, CancellationToken ct)
    {
        var pending = _entries.Where(entry => entry.Repeat != RedTestResult.Pass).ToArray();
        if (pending.Length == 0) return;
        var index = new PostMergeCoveringFolders.Index(await trackedFiles(ct), path =>
        {
            try { return File.ReadAllText(Path.Combine(checkout, path)); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
        });
        foreach (var entry in pending)
        {
            var project = PostMergeAttributor.TestProject(entry.Command);
            var directory = project is null ? null : Path.GetDirectoryName(project.Replace('\\', '/'))?.Replace('\\', '/');
            entry.Covering = index.For(entry.Test, directory);
        }
    }

    public IReadOnlyList<PostMergeTestVerdict> Verdicts() => _entries.Take(MaximumTests).Select(entry =>
        new PostMergeTestVerdict(entry.Test, entry.Suite.Id, entry.Suite.Category,
            entry.Attempts.Take(MaximumAttempts).ToArray(), HintName(Hint(entry)))
        {
            Quarantined = entry.Quarantined,
            TestFile = entry.Covering?.TestFile,
            CoveringFolders = entry.Covering?.Folders.Take(MaximumCoveringFolders).ToArray(),
            CoveringFallback = entry.Covering?.Fallback,
        }).ToArray();

    public IReadOnlyList<PostMergeFocusedRun>? FocusedRuns()
        => _runs.Count == 0 ? null : _runs.Take(MaximumFocusedRuns).Select(run => run.ToReceipt()).ToArray();

    public static string HintName(RedTestClassification classification) => classification switch
    {
        RedTestClassification.Flaky => PostMergeClassificationHints.Flaky,
        RedTestClassification.Environment => PostMergeClassificationHints.Environment,
        RedTestClassification.Regression => PostMergeClassificationHints.Regression,
        RedTestClassification.WrongAttribution => PostMergeClassificationHints.WrongAttribution,
        RedTestClassification.Ambiguous => PostMergeClassificationHints.Ambiguous,
        _ => PostMergeClassificationHints.Inconclusive,
    };

    private static RedTestClassification Hint(Entry entry)
        => RedTestTriagePolicy.ClassificationHint(new RedTestTriageFacts(entry.Test, entry.Suite.Id,
            entry.Suite.Category == ProjectTestSuiteCategories.Base ? RedTestCategory.Base : RedTestCategory.Routine)
        {
            EnvelopeCanary = TestEnvelope.CanaryFailed([entry.Test]) ? RedTestResult.Fail : RedTestResult.Pass,
            FirstResult = RedTestResult.Fail,
            SameShaRepeat = entry.Repeat,
            ReferenceEnvelopeRepeat = Parse(entry.Attempts.LastOrDefault(attempt => attempt.Phase == PostMergeRunPhases.Reference)?.Result ?? "unknown"),
            LastGreenControlResults = entry.Controls,
            BisectPoints = entry.Points,
        });

    private static RedTestResult Parse(string result) => result switch
    {
        PostMergeTestResults.Pass => RedTestResult.Pass,
        PostMergeTestResults.Fail => RedTestResult.Fail,
        _ => RedTestResult.Unknown,
    };

    private Entry? Find(string suite, string test)
        => _entries.FirstOrDefault(entry => entry.Suite.Id == suite && entry.Test == test);
}
