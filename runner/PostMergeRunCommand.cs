using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

public sealed record PostMergeRunOptions(string Project, string Checkout, string ServerUrl,
    string Branch, string Remote, string TokenFile, string Mode, int MaxMerges,
    TimeSpan Window, int MaxSlots, IReadOnlyList<string> StateDirectories, bool DryRun)
{
    public string? RepositoryUrl { get; init; }
    public bool RecordSkips { get; init; } = true;

    public static PostMergeRunOptions Parse(IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string?> environment)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var dryRun = false;
        for (var index = 0; index < args.Count; index++)
        {
            if (args[index] == "--dry-run") { dryRun = true; continue; }
            if (args[index] is not ("--project" or "--checkout" or "--server" or "--branch"
                or "--remote" or "--token-file" or "--mode" or "--max-merges"
                or "--window-minutes" or "--free-slots") || ++index >= args.Count)
                throw new ArgumentException($"Unknown or incomplete post-merge option '{args[index - (index == args.Count ? 1 : 0)]}'.");
            if (!values.TryAdd(args[index - 1], args[index]))
                throw new ArgumentException($"{args[index - 1]} is given more than once.");
        }
        string? Get(string flag, string key) => values.GetValueOrDefault(flag)
            ?? environment.GetValueOrDefault(key);
        var project = Get("--project", "POST_MERGE_PROJECT")
            ?? throw new ArgumentException("Set POST_MERGE_PROJECT.");
        var checkout = Get("--checkout", "POST_MERGE_CHECKOUT")
            ?? throw new ArgumentException("Set POST_MERGE_CHECKOUT.");
        var server = Get("--server", "POST_MERGE_SERVER_URL")
            ?? throw new ArgumentException("Set POST_MERGE_SERVER_URL.");
        var tokenFile = Get("--token-file", "POST_MERGE_TOKEN_FILE")
            ?? (dryRun ? string.Empty : throw new ArgumentException("Set POST_MERGE_TOKEN_FILE."));
        var mode = Get("--mode", "POST_MERGE_MODE") ?? "window";
        if (mode is not ("window" or "nightly")) throw new ArgumentException("Mode must be window or nightly.");
        static int Positive(string? value, int fallback, string name)
            => value is null ? fallback : int.TryParse(value, out var parsed) && parsed > 0
                ? parsed : throw new ArgumentException($"{name} must be a positive integer.");
        return new(project, Path.GetFullPath(checkout), server, Get("--branch", "POST_MERGE_BRANCH") ?? "develop",
            Get("--remote", "POST_MERGE_REMOTE") ?? "origin", tokenFile, mode,
            Positive(Get("--max-merges", "POST_MERGE_MAX_MERGES"), PostMergeRunPlan.DefaultMaxMerges, "max-merges"),
            TimeSpan.FromMinutes(Positive(Get("--window-minutes", "POST_MERGE_WINDOW_MINUTES"), 30, "window-minutes")),
            Positive(Get("--free-slots", "POST_MERGE_FREE_SLOTS"), 1, "free-slots"),
            (Get("--state-dirs", "POST_MERGE_STATE_DIRS")
                ?? environment.GetValueOrDefault("COVERAGE_RUN_STATE_DIRS") ?? "")
                .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), dryRun);
    }
}

/// <summary>Scheduled, project-scoped post-merge worker. The server fences the run ID;
/// a host file lock prevents concurrent use of its dedicated checkout.</summary>
public sealed class PostMergeRunCommand
{
    private readonly PostMergeRunOptions _options;
    private readonly TextWriter _output;
    private readonly TimeProvider _clock;
    private readonly HttpClient _http;
    private readonly Func<CoverageHostLoad> _probeHost;
    private readonly RunnerOptions? _hostOptions;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public PostMergeRunCommand(PostMergeRunOptions options, TextWriter output, TimeProvider clock,
        HttpClient http, Func<CoverageHostLoad>? probeHost = null, RunnerOptions? hostOptions = null)
    {
        _options = options;
        _output = output;
        _clock = clock;
        _http = http;
        _hostOptions = hostOptions;
        _probeHost = probeHost ?? (() => CoverageRunCommand.ProbeHost(options.StateDirectories));
    }

    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter output,
        TextWriter error, CancellationToken ct)
    {
        PostMergeRunOptions options;
        try { options = PostMergeRunOptions.Parse(args,
            Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value)); }
        catch (ArgumentException ex) { error.WriteLine(ex.Message); return 2; }
        using var http = new HttpClient { BaseAddress = new Uri(options.ServerUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromMinutes(2) };
        if (!options.DryRun)
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
                (await File.ReadAllTextAsync(options.TokenFile, ct)).Trim());
        return await new PostMergeRunCommand(options, output, TimeProvider.System, http).ExecuteAsync(ct);
    }

    public async Task<int> ExecuteAsync(CancellationToken ct, bool observeOnly = false)
    {
        if (_options.DryRun)
        {
            _output.WriteLine($"post-merge dry-run project={_options.Project} mode={_options.Mode} checkout={_options.Checkout}");
            _output.WriteLine($"Would fetch {_options.Remote}/{_options.Branch}, pin FETCH_HEAD, and read the project state.");
            _output.WriteLine(_options.Mode == "window"
                ? $"Would close after {_options.MaxMerges} merges or {_options.Window.TotalMinutes:0} minutes; run the base suite and the affected-test union."
                : "Would run the full routine suite on the fetched tip across free slots.");
            var read = ProjectDefinitionReader.ReadWorkspace(_options.Checkout);
            if (read.Definition is null) return 1;
            _output.WriteLine("Window test commands are resolved from each fetched merge; this is the widest plan.");
            foreach (var command in PostMergeRunPlan.Build(read.Definition, _options.Mode, [],
                         _options.Mode == "window" ? PostMergeWindowSuiteModes.Routine : null))
                _output.WriteLine($"Would run: {command}");
            return 0;
        }

        using (var escalation = await _http.PostAsync(StateUrl() + "/escalation-check", null, ct))
            escalation.EnsureSuccessStatusCode();

        Directory.CreateDirectory(_options.Checkout);
        using var claim = ClaimCheckout();
        var readOnlyObservation = claim is null && observeOnly && _options.RepositoryUrl is not null;
        if (claim is null && !readOnlyObservation)
        { _output.WriteLine("post-merge checkout already claimed"); return 0; }
        var host = _probeHost();
        var admissionReason = host.ActiveSlots >= _options.MaxSlots
            || host.LoadPerCore is { } load && load > CoverageRunOptions.DefaultMaxLoadPerCore
            ? $"{host.ActiveSlots} occupied slots, load {host.LoadPerCore:0.00}" : null;
        if (readOnlyObservation) admissionReason = "post-merge checkout occupied";
        var freeSlots = Math.Max(0, _options.MaxSlots - host.ActiveSlots);
        if (!readOnlyObservation)
        {
            if (!Directory.Exists(Path.Combine(_options.Checkout, ".git"))
                && !File.Exists(Path.Combine(_options.Checkout, ".git")))
            {
                if (_options.RepositoryUrl is null)
                    throw new IOException("Post-merge checkout is not a git checkout.");
                // The checkout claim already occupies this directory. Initialize and fetch
                // in place so a failed first fetch is recoverable on the next poll.
                var initialized = await Git(ct, "init", "--quiet");
                if (!initialized.Success) throw new IOException(initialized.StdErr);
            }
            if (_options.RepositoryUrl is { } repository)
            {
                var remote = await Git(ct, "remote", "get-url", _options.Remote);
                var configured = remote.Success
                    ? await Git(ct, "remote", "set-url", _options.Remote, repository)
                    : await Git(ct, "remote", "add", _options.Remote, repository);
                if (!configured.Success) throw new IOException(configured.StdErr);
            }
            var dirty = await Git(ct, "status", "--porcelain", "--untracked-files=no");
            if (!dirty.Success) throw new IOException(dirty.StdErr);
            if (dirty.StdOut.Trim().Length > 0)
            {
                if (_options.RepositoryUrl is null)
                    throw new IOException("Post-merge checkout has tracked changes.");
                // The host owns this dedicated clone. Commands may have changed a
                // tracked generated file during the previous run; restore its base.
                var reset = await Git(ct, "reset", "--quiet", "--hard", "HEAD");
                if (!reset.Success) throw new IOException(reset.StdErr);
            }
        }
        var state = await _http.GetFromJsonAsync<PostMergeProjectState>(StateUrl(), Json, ct)
            ?? throw new IOException("Post-merge state was unavailable.");
        // An active suite owns the checkout and FETCH_HEAD. Observers only
        // update this separate ref and Git's object database, never its worktree.
        const string observationRef = "refs/agent-studio/post-merge-observed";
        var fetchedRef = readOnlyObservation ? observationRef : "FETCH_HEAD";
        var fetch = readOnlyObservation
            ? await Git(ct, "fetch", "--quiet", "--no-write-fetch-head", _options.RepositoryUrl!,
                $"+{_options.Branch}:{observationRef}")
            : await Git(ct, "fetch", "--quiet", _options.Remote, _options.Branch);
        if (!fetch.Success) throw new IOException(fetch.StdErr);
        var resolved = await Git(ct, "rev-parse", "--verify", fetchedRef + "^{commit}");
        if (!resolved.Success) throw new IOException(resolved.StdErr);
        var tip = resolved.StdOut.Trim();
        var now = _clock.GetUtcNow();
        var id = Guid.NewGuid().ToString("N");
        var nightlyChecked = _options.Mode == "nightly" && state.Runs.Any(run => run.Kind == "nightly"
            && run.Status == "green" && run.TipSha == tip
            && run.StartedAtUtc.UtcDateTime.Date == now.UtcDateTime.Date);
        if (string.Equals(tip, state.LastGreenSha, StringComparison.OrdinalIgnoreCase)
            && (_options.Mode == "window" || nightlyChecked))
        {
            await ObserveWindow(tip, null, now, state.WindowMinutes ?? (int)_options.Window.TotalMinutes, null, ct);
            if (observeOnly || admissionReason is not null || !_options.RecordSkips) return 0;
            var skipped = new PostMergeRunRecord(id, _options.Project, _options.Mode, tip,
                state.LastWindowTipSha, null, null, 0, [], [], "skipped", now, now,
                nightlyChecked ? "nightly routine suite already green on this tip today"
                    : "develop has not moved since the last green run");
            await Post(skipped, ct);
            _output.WriteLine($"post-merge skipped tip={tip}");
            return 0;
        }

        var from = observeOnly ? state.ActiveRun?.TipSha ?? state.LastWindowTipSha : state.LastWindowTipSha;
        IReadOnlyList<(string Sha, DateTimeOffset At)> merges = [];
        if (_options.Mode == "window")
        {
            // First observation starts a window at the current first-parent commit.
            var range = from is null ? fetchedRef : $"{from}..{tip}";
            var history = from is null
                ? await Git(ct, "log", "-1", "--first-parent", "--format=%H%x09%cI", range)
                : await Git(ct, "log", "--first-parent", "--format=%H%x09%cI", range);
            if (!history.Success) throw new IOException(history.StdErr);
            merges = history.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split('\t'))
                .Where(parts => parts.Length == 2 && DateTimeOffset.TryParse(parts[1], out _))
                .Select(parts => (parts[0], DateTimeOffset.Parse(parts[1])))
                .ToArray();
            var close = PostMergeRunPlan.Window(merges.Count,
                merges.Count == 0 ? null : merges.Min(merge => merge.At), now,
                state.WindowMaxMerges ?? _options.MaxMerges,
                state.WindowMinutes is { } minutes
                    ? TimeSpan.FromMinutes(minutes) : _options.Window);
            await ObserveWindow(tip, merges.Count == 0 ? null : merges.Min(merge => merge.At), now,
                state.WindowMinutes ?? (int)_options.Window.TotalMinutes, admissionReason, ct);
            if (!close.Close) { _output.WriteLine($"post-merge pending tip={tip}: {close.Reason}"); return 0; }
        }

        if (observeOnly || admissionReason is not null)
        {
            if (admissionReason is not null) _output.WriteLine($"post-merge waiting: {admissionReason}");
            return 0;
        }
        var checkout = await Git(ct, "checkout", "--quiet", "--detach", "--force", tip);
        if (!checkout.Success) throw new IOException(checkout.StdErr);
        var definition = ProjectDefinitionReader.ReadWorkspace(_options.Checkout).Definition
            ?? throw new IOException("Project execution definition is invalid at the fetched tip.");
        var changed = new List<string>();
        if (_options.Mode == "window")
        {
            // Select from each first-parent integration change. A later merge
            // may revert a file, but its earlier affected tests still belong
            // to this window's union.
            foreach (var merge in merges)
            {
                var diff = await Git(ct, "diff", "--name-only", merge.Sha + "^", merge.Sha);
                if (!diff.Success) throw new IOException(diff.StdErr);
                changed.AddRange(diff.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries));
            }
            changed = changed.Distinct(StringComparer.Ordinal).ToList();
        }
        var commands = PostMergeRunPlan.Build(definition, _options.Mode, changed, state.WindowSuites);
        var selection = ProjectTestSelectionPolicy.Select(definition, changed,
            _options.Mode == "nightly" || state.WindowSuites == PostMergeWindowSuiteModes.Routine);
        var opened = merges.Count == 0 ? (DateTimeOffset?)null : merges.Min(merge => merge.At);
        bool MayShard(ProjectTestSuite? suite, string command) => _options.Mode == "nightly" && suite is not null
            && freeSlots > 1 && command.Contains("dotnet test ", StringComparison.Ordinal)
            && command.Contains("--no-build", StringComparison.Ordinal);
        var planned = commands.Select(command => (Command: command,
                Suite: definition.TestSuites.FirstOrDefault(item => item.Command == command)))
            .Where(item => item.Suite is not null)
            .Select(item => (item.Suite!.Id, MayShard(item.Suite, item.Command) ? "auto" : "1")).ToArray();
        var started = new PostMergeRunRecord(id, _options.Project, _options.Mode, tip,
            from, opened, now, merges.Count, commands, [], "running", now, null,
            StartLoadPerCore: host.LoadPerCore, StartOccupiedSlots: host.ActiveSlots)
        {
            SuiteSelection = new(selection.Suites, selection.OmittedSuites, selection.Reasons),
            Envelope = await EnvelopeAsync(definition, commands, PostMergeEnvelopeProbe.ShardPlan(planned),
                host.LoadPerCore, null, ct),
        };
        await Post(started, ct);
        await using var sampler = new PostMergeLoadSampler(host.LoadPerCore, () => _probeHost().LoadPerCore);
        var verdicts = new List<PostMergeCommandVerdict>();
        var ledger = new PostMergeTestLedger();
        _recordRun = ledger.Record;
        var shardPlan = new List<(string Suite, string Shards)>();
        var quarantinedFailures = 0;
        foreach (var command in commands)
        {
            var verdictStart = verdicts.Count;
            var suite = definition.TestSuites.FirstOrDefault(item => item.Command == command);
            if (MayShard(suite, command))
            {
                var listed = await RunCommand(command + " --list-tests", definition.Environment, ct);
                var classes = listed.ExitCode == 0 ? PostMergeShards.Classes(listed.Output) : [];
                // T3 opts into AGT-3232 auto sharding without changing T1's
                // per-suite opt-in or forcing shards onto a busy host.
                var count = PostMergeShards.Count(suite! with
                { Shards = suite.Shards ?? ProjectTestSuite.AutoShards }, freeSlots, classes.Count);
                var shardCommands = PostMergeShards.Commands(command, classes, count,
                    Path.Combine(Path.GetTempPath(), "post-merge-" + id));
                shardPlan.Add((suite.Id, shardCommands.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                var results = await Task.WhenAll(shardCommands.Select(shard =>
                    TimedRun(shard, definition.Environment, ct)));
                verdicts.AddRange(results.Select((result, index) => Verdict(shardCommands[index], result)));
            }
            else
            {
                if (suite is not null) shardPlan.Add((suite.Id, "1"));
                verdicts.Add(Verdict(command, await TimedRun(command, definition.Environment, ct)));
            }
            if (suite is not null)
            {
                // Retry only this suite's new verdicts, including shards. Build and prepare failures
                // are not test failures and must remain blocking.
                for (var index = verdictStart; index < verdicts.Count; index++)
                {
                    if (verdicts[index].ExitCode == 0) continue;
                    var initial = verdicts[index];
                    var observed = initial.FailedTests ?? PostMergeAttributor.FailedTests(initial.OutputTail ?? "");
                    var blocking = PostMergeAttributor.BlockingTests(_options.Checkout, observed, _clock.GetUtcNow());
                    ledger.Initial(suite, initial.Command, observed, blocking, tip);
                    verdicts[index] = await PostMergeTestRetry.RepeatAsync(initial, _ => blocking,
                        retry => RunCommand(retry, definition.Environment, ct), ledger.Record, tip);
                    if (verdicts[index].ExitCode == 0) quarantinedFailures++;
                }
                for (var index = verdictStart; index < verdicts.Count; index++)
                    verdicts[index] = verdicts[index] with { SuiteId = suite.Id };
            }
            if (verdicts.Any(verdict => verdict.ExitCode != 0)) break;
        }
        var status = verdicts.All(v => v.ExitCode == 0) && verdicts.Count >= commands.Count
            ? "green" : "red";
        PostMergeFailureEvidence? failure = null;
        if (status == "red")
        {
            try { failure = await AttributeFailureAsync(state, definition, tip, verdicts, ledger, ct); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _output.WriteLine($"post-merge attribution unavailable: {exception.GetType().Name}: {exception.Message}");
                var failed = verdicts.First(verdict => verdict.ExitCode != 0);
                var excerpt = PostMergeAttributor.Excerpt(failed.OutputTail ?? "");
                var tests = failed.FailedTests ?? PostMergeAttributor.FailedTests(excerpt);
                failure = new(PostMergeAttributor.Fingerprint(tests, failed.Command, excerpt), tests,
                    excerpt, [], [], null, false, false);
            }
        }
        if (ledger.HasTests)
        {
            try { await ledger.AddCoveringFoldersAsync(_options.Checkout, ListTrackedFiles, ct); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _output.WriteLine($"post-merge covering folders unavailable: {exception.Message}");
            }
        }
        var envelope = await EnvelopeAsync(definition, commands, PostMergeEnvelopeProbe.ShardPlan(shardPlan),
            host.LoadPerCore, sampler.Peak, ct);
        await Post(started with { Status = status, Verdicts = verdicts, Failure = failure,
            SuiteSelection = new(selection.Suites, selection.OmittedSuites, selection.Reasons)
            {
                SuiteCommands = definition.TestSuites.ToDictionary(suite => suite.Id, suite => suite.Command),
                AttemptedSuites = definition.TestSuites.Where(suite => verdicts.Any(verdict => verdict.SuiteId == suite.Id))
                    .Select(suite => suite.Id).ToArray(),
            },
            Reason = quarantinedFailures > 0
                ? $"{quarantinedFailures} failing command(s) passed a same-SHA rerun or had only quarantined tests."
                : null,
            FinishedAtUtc = _clock.GetUtcNow(),
            TestVerdicts = ledger.HasTests ? ledger.Verdicts() : null,
            FocusedRuns = ledger.FocusedRuns(),
            Envelope = envelope }, ct);
        _output.WriteLine($"post-merge {status} tip={tip} commands={verdicts.Count}");
        return status == "green" ? 0 : 1;
    }

    private static PostMergeCommandVerdict Verdict(string command,
        (int ExitCode, bool TimedOut, string Output, long DurationMs) result)
        => new(command, result.ExitCode, result.TimedOut,
            result.ExitCode == 0 ? null : PostMergeAttributor.Excerpt(result.Output),
            result.ExitCode == 0 ? null : PostMergeAttributor.FailedTests(result.Output))
        { DurationMs = result.DurationMs };

    private async Task<(int ExitCode, bool TimedOut, string Output, long DurationMs)> TimedRun(string command,
        IReadOnlyDictionary<string, string> environment, CancellationToken ct)
    {
        var begun = System.Diagnostics.Stopwatch.GetTimestamp();
        var result = await RunCommand(command, environment, ct);
        return (result.ExitCode, result.TimedOut, result.Output,
            (long)System.Diagnostics.Stopwatch.GetElapsedTime(begun).TotalMilliseconds);
    }

    private async Task<IReadOnlyList<string>> ListTrackedFiles(CancellationToken ct)
    {
        var listed = await Git(ct, "ls-files");
        if (!listed.Success) throw new IOException(listed.StdErr);
        return listed.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private async Task<PostMergeFailureEvidence> AttributeFailureAsync(PostMergeProjectState state,
        ProjectExecutionDefinition definition, string tip,
        IReadOnlyList<PostMergeCommandVerdict> verdicts, PostMergeTestLedger ledger, CancellationToken ct)
    {
        var failed = verdicts.First(verdict => verdict.ExitCode != 0);
        var excerpt = PostMergeAttributor.Excerpt(failed.OutputTail ?? "");
        var observed = failed.FailedTests ?? PostMergeAttributor.FailedTests(excerpt);
        var blocking = PostMergeAttributor.BlockingTests(_options.Checkout, observed, _clock.GetUtcNow());
        var focused = PostMergeAttributor.FocusedCommand(failed.Command, blocking)
            ?? PostMergeAttributor.FocusedVitestCommand(failed.Command, blocking);
        var build = PostMergeAttributor.PartialBuildCommand(definition, failed.Command, blocking);
        var fingerprint = PostMergeAttributor.Fingerprint(blocking, failed.Command, excerpt);

        var range = state.LastGreenSha is null ? "FETCH_HEAD^..FETCH_HEAD" : state.LastGreenSha + ".." + tip;
        var history = await Git(ct, "log", "--first-parent", "--reverse", "--format=%H", range);
        if (!history.Success) throw new IOException(history.StdErr);
        var commits = history.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var listedProjects = await Git(ct, "ls-files", "*.csproj");
        if (!listedProjects.Success) throw new IOException(listedProjects.StdErr);
        var projectMap = new PostMergeProjectMap(_options.Checkout,
            listedProjects.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        var pathsByCommit = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var candidates = new List<(string Sha, IReadOnlyList<string> Paths)>();
        foreach (var commit in commits)
        {
            var diff = await Git(ct, "diff", "--name-only", commit + "^", commit);
            if (!diff.Success) throw new IOException(diff.StdErr);
            var paths = diff.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            pathsByCommit[commit] = paths;
            if (projectMap.CouldAffect(definition, paths, failed.Command))
                candidates.Add((commit, paths));
        }

        // RT-3 (RT-D9 B): two focused runs at the last green SHA, then two at every
        // bisect point, each after a build of the failing test project only.
        PostMergeAttributionEvidence? attribution = null;
        string? attributed = null;
        if (focused is not null && build is not null && blocking.Count > 0)
        {
            ledger.BeginAttribution(failed.Command);
            var protocol = await AttributionProtocolAsync(state.LastGreenSha, candidates.Select(c => c.Sha).ToArray(),
                tip, build, focused, blocking, definition.Environment, ct);
            attribution = protocol.Evidence;
            attributed = protocol.AttributedMerge;
            ledger.Attribution(blocking, protocol.Controls, protocol.Points);
        }

        var affected = attributed is null ? candidates.SelectMany(candidate => candidate.Paths)
            : candidates.First(candidate => candidate.Sha == attributed).Paths;
        var failingPaths = affected.Distinct(StringComparer.Ordinal).Take(128).ToArray();
        // RT-3: a later first-parent merge is coupled to the suspect only when it
        // changes a file the suspect changed (RT-2 file overlap).
        var laterDepends = attributed is not null && commits.SkipWhile(commit => commit != attributed)
            .Skip(1).Any(commit => HoldScopePolicy.LaterMergeIsCoupled(pathsByCommit[attributed], pathsByCommit[commit]));
        var clean = false;
        string? attributedTaskId = null;
        if (attributed is not null && !laterDepends)
        {
            var parents = await Git(ct, "rev-list", "--parents", "-n", "1", attributed);
            if (!parents.Success) throw new IOException(parents.StdErr);
            var merge = parents.StdOut.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 2;
            var args = merge ? new[] { "revert", "--no-commit", "-m", "1", attributed }
                : ["revert", "--no-commit", attributed];
            try { clean = (await Git(ct, args)).Success; }
            finally
            {
                if (!clean)
                {
                    var abort = await Git(CancellationToken.None, "revert", "--abort");
                    if (!abort.Success)
                        _output.WriteLine("post-merge revert probe had no active sequence to abort");
                }
                var reset = await Git(CancellationToken.None, "reset", "--hard", tip);
                if (!reset.Success) throw new IOException(reset.StdErr);
            }
        }
        if (attributed is not null)
        {
            var message = await Git(ct, "show", "-s", "--format=%B", attributed);
            if (!message.Success) throw new IOException(message.StdErr);
            attributedTaskId = PostMergeAttributor.TaskIdFromMergeMessage(message.StdOut);
        }
        return new(fingerprint, blocking, excerpt, failingPaths,
            candidates.Select(candidate => candidate.Sha).TakeLast(512).ToArray(),
            attributed, clean, laterDepends,
            AttributedTaskId: attributedTaskId, NoRelevantMerges: candidates.Count == 0)
        { Attribution = attribution };
    }

    private sealed record ProtocolResult(PostMergeAttributionEvidence Evidence, string? AttributedMerge,
        IReadOnlyList<RedTestResult> Controls, IReadOnlyList<RedTestBisectPoint> Points);

    /// <summary>
    /// The control at the last green SHA and the binary search over the
    /// candidates, two focused runs per point. The verdicts come from
    /// <see cref="BisectVerdictPolicy"/>: a failing control attributes no merge,
    /// mixed results stop the search, and a regression needs a failing point whose
    /// tested parent (or the control) passed twice. A build failure stops the
    /// search without attribution.
    /// </summary>
    private async Task<ProtocolResult> AttributionProtocolAsync(string? lastGreen, IReadOnlyList<string> candidates,
        string tip, string build, string focused, IReadOnlyList<string> tests,
        IReadOnlyDictionary<string, string> environment, CancellationToken ct)
    {
        static string Name(RedTestResult result) => result == RedTestResult.Pass ? PostMergeTestResults.Pass
            : result == RedTestResult.Fail ? PostMergeTestResults.Fail : PostMergeTestResults.Unknown;
        static string Hint(RedTestClassification classification) => PostMergeTestLedger.HintName(classification);
        if (lastGreen is null)
            return new(new(null, [], [], Hint(RedTestClassification.Inconclusive),
                "no last green SHA to run the control at") { BuildCommand = build }, null, [], []);
        var tested = new SortedDictionary<int, RedTestRunPair>();
        IReadOnlyList<RedTestResult> controls = [];
        string? stop = null;
        var buildFailed = false;
        try
        {
            var control = await PointRunsAsync(lastGreen, PostMergeRunPhases.ControlBuild, PostMergeRunPhases.Control,
                build, focused, tests, environment, ct);
            if (control is null)
            {
                stop = "the test project build failed at the last green SHA";
                buildFailed = true;
            }
            else
            {
                controls = [control.First, control.Second];
                if (controls.Contains(RedTestResult.Fail))
                    stop = "the failing tests also fail at the last green SHA";
                else
                {
                    var left = 0;
                    var right = candidates.Count - 1;
                    while (left <= right)
                    {
                        var middle = left + (right - left) / 2;
                        var runs = await PointRunsAsync(candidates[middle], PostMergeRunPhases.BisectBuild,
                            PostMergeRunPhases.Bisect, build, focused, tests, environment, ct);
                        if (runs is null)
                        {
                            stop = $"the test project build failed at {candidates[middle]}";
                            buildFailed = true;
                            break;
                        }
                        tested[middle] = runs;
                        var verdict = BisectVerdictPolicy.Verdict(runs);
                        if (verdict == RedTestPointVerdict.Mixed)
                        {
                            stop = $"mixed results at {candidates[middle]}";
                            break;
                        }
                        if (verdict == RedTestPointVerdict.Fail) right = middle - 1;
                        else left = middle + 1;
                    }
                }
            }
        }
        finally
        {
            var restored = await Git(CancellationToken.None, "checkout", "--quiet", "--detach", "--force", tip);
            if (!restored.Success) throw new IOException(restored.StdErr);
        }
        var controlPair = controls.Count == 2 ? new RedTestRunPair(controls[0], controls[1]) : null;
        var points = new List<RedTestBisectPoint>();
        foreach (var (index, runs) in tested)
        {
            // The parent is the nearest earlier tested candidate; binary search
            // tests the candidate before every boundary, or the control proves it.
            var parent = tested.Where(item => item.Key < index).Select(item => item.Value).LastOrDefault() ?? controlPair;
            if (parent is not null) points.Add(new(candidates[index], runs, parent));
        }
        var decision = buildFailed
            ? new RedTestBisectDecision(RedTestClassification.Inconclusive)
            : BisectVerdictPolicy.Decide(controls, points);
        if (stop is null && decision.AttributedMerge is null)
            stop = candidates.Count == 0 ? "no candidate merge can affect the failing test project"
                : "no tested point fails twice after a point that passes twice";
        var evidence = new PostMergeAttributionEvidence(lastGreen, controls.Select(Name).ToArray(),
            points.Select(point => new PostMergeBisectPointEvidence(point.Sha,
                [Name(point.Runs.First), Name(point.Runs.Second)],
                BisectVerdictPolicy.Verdict(point.Runs).ToString().ToLowerInvariant())
                { ParentResults = [Name(point.ParentRuns.First), Name(point.ParentRuns.Second)] }).ToArray(),
            Hint(decision.Classification), stop) { BuildCommand = build };
        return new(evidence, decision.AttributedMerge, controls, points);
    }

    /// <summary>Checks out one point, runs the partial build and the focused tests twice. Null when the build fails.</summary>
    private async Task<RedTestRunPair?> PointRunsAsync(string sha, string buildPhase, string runPhase, string build,
        string focused, IReadOnlyList<string> tests, IReadOnlyDictionary<string, string> environment, CancellationToken ct)
    {
        var switched = await Git(ct, "checkout", "--quiet", "--detach", "--force", sha);
        if (!switched.Success) throw new IOException(switched.StdErr);
        var built = await TimedRun(build, environment, ct);
        _recordRun?.Invoke(new(buildPhase, sha, [], built.ExitCode, built.TimedOut, "", built.DurationMs));
        if (built.ExitCode != 0 || built.TimedOut) return null;
        var results = new RedTestResult[2];
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var run = await TimedRun(focused, environment, ct);
            var record = new PostMergeTestRun(runPhase, sha, tests, run.ExitCode, run.TimedOut, run.Output, run.DurationMs);
            _recordRun?.Invoke(record);
            results[attempt] = record.Passed ? RedTestResult.Pass : RedTestResult.Fail;
        }
        return new(results[0], results[1]);
    }

    private Action<PostMergeTestRun>? _recordRun;

    private async Task<PostMergeEnvelopeFingerprint> EnvelopeAsync(ProjectExecutionDefinition definition,
        IReadOnlyList<string> commands, string shardPlan, double? startLoad, double? peakLoad, CancellationToken ct)
    {
        var declared = DeclaredEnvironment(definition.Environment);
        var (kind, cpuMax) = _hostOptions is not null && OperatingSystem.IsLinux()
            ? (PostMergeEnvelopeProbe.WorkerKind, _workerCpuMax)
            : PostMergeEnvelopeProbe.OwnCgroup();
        if (_sdkVersion is null && commands.Any(command => command.Contains("dotnet ", StringComparison.Ordinal)))
            _sdkVersion = await SdkVersionAsync(definition.Environment, ct) ?? "";
        return new(PostMergeEnvelopeProbe.AllowlistDigest(HostAllowlist().Where(name =>
                Environment.GetEnvironmentVariable(name) is not null)),
            PostMergeEnvelopeProbe.DeclaredEnvironmentDigest(declared), kind, cpuMax,
            PostMergeEnvelopeProbe.ProcessorCount(declared, cpuMax, Environment.ProcessorCount),
            string.IsNullOrEmpty(_sdkVersion) ? null : _sdkVersion, shardPlan, startLoad, peakLoad);
    }

    private string? _sdkVersion;
    private string? _workerCpuMax;

    private async Task<string?> SdkVersionAsync(IReadOnlyDictionary<string, string> environment, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var result = await ProcessRunner.RunAsync("dotnet", ["--version"], _options.Checkout,
                environment: ChildEnvironment(environment), clearEnvironment: true, ct: timeout.Token);
            var version = result.StdOut.Trim();
            return result.Success && version.Length is > 0 and <= 64 && !version.Contains('\n') ? version : null;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException
            or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private FileStream? ClaimCheckout()
    {
        try { return new FileStream(Path.Combine(_options.Checkout, ".post-merge-claim"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return null; }
    }

    /// <summary>Tool and OS variables a project command receives from the host; nothing else is inherited.</summary>
    private static IEnumerable<string> HostAllowlist()
    {
        string[] common =
        [
            "PATH", "HOME", "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT_ARM64",
            "LANG", "LC_ALL", "LC_CTYPE", "TMPDIR", "TMP", "TEMP",
            "SSL_CERT_FILE", "SSL_CERT_DIR", "PLAYWRIGHT_BROWSERS_PATH",
        ];
        return OperatingSystem.IsWindows()
            ? [.. common, "SystemRoot", "windir", "SystemDrive", "ComSpec", "PATHEXT", "USERPROFILE"]
            : common;
    }

    private static readonly string[] RemovedVariables =
        ["RUNNER_AUTH_TOKEN", "RUNNER_AUTH_TOKEN_FILE", "POST_MERGE_TOKEN_FILE", "POST_MERGE_SERVER_URL"];

    private static IReadOnlyDictionary<string, string> DeclaredEnvironment(IReadOnlyDictionary<string, string> values)
        => values.Where(pair => !RemovedVariables.Contains(pair.Key, StringComparer.Ordinal))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, string?> ChildEnvironment(IReadOnlyDictionary<string, string> values)
    {
        // Like review commands, project commands receive an explicit environment.
        // The host scheduler also holds runner, provider, and Git credentials.
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var name in HostAllowlist())
            if (Environment.GetEnvironmentVariable(name) is { } value) environment[name] = value;
        foreach (var (name, value) in values) environment[name] = value;
        foreach (var name in RemovedVariables) environment[name] = null;
        return environment;
    }

    internal async Task<(int ExitCode, bool TimedOut, string Output)> RunCommand(string command,
        IReadOnlyDictionary<string, string> environment, CancellationToken ct)
    {
        _output.WriteLine($"post-merge {command}");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromHours(2));
        using var envelope = PostMergeCommandEnvelope.Prepare(_hostOptions, line => _output.WriteLine(line));
        if (envelope is not null) _workerCpuMax ??= envelope.CpuMax;
        var launch = envelope?.Launch("sh", ["-c", command]) ?? new WorkerLaunch("sh", ["-c", command]);
        var childEnvironment = ChildEnvironment(environment);
        if (envelope is not null) childEnvironment = envelope.ChildEnvironment(childEnvironment);
        try
        {
            var result = await ProcessRunner.RunAsync(launch.FileName, launch.Arguments, _options.Checkout,
                environment: childEnvironment, clearEnvironment: true,
                onStdOut: line => _output.WriteLine(line),
                onStdErr: line => _output.WriteLine(line),
                isolateProcessGroup: OperatingSystem.IsLinux(), ct: timeout.Token);
            return (result.ExitCode, false, result.StdOut + "\n" + result.StdErr);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (124, true, "");
        }
    }

    private async Task ObserveWindow(string tip, DateTimeOffset? opened, DateTimeOffset now,
        int minutes, string? admissionReason, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(StateUrl() + "/window",
            new PostMergeWindowObservation(tip, opened, now, minutes, admissionReason), Json, ct);
        response.EnsureSuccessStatusCode();
    }

    private string StateUrl() => $"api/runner/post-merge/projects/{Uri.EscapeDataString(_options.Project)}";
    private async Task Post(PostMergeRunRecord run, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var response = await _http.PostAsJsonAsync(StateUrl() + "/runs", run, Json, ct);
                if (response.IsSuccessStatusCode) return;
                if ((int)response.StatusCode < 500 || attempt == 2)
                    response.EnsureSuccessStatusCode();
            }
            catch (HttpRequestException) when (attempt < 2 && !ct.IsCancellationRequested)
            {
                _output.WriteLine($"post-merge receipt retry {attempt + 1}/3");
            }
            await Task.Delay(TimeSpan.FromSeconds(attempt + 1), ct);
        }
        throw new HttpRequestException("Post-merge receipt was not accepted after three attempts.");
    }

    private Task<ProcessResult> Git(CancellationToken ct, params string[] arguments)
        => ProcessRunner.RunAsync("git", ["-C", _options.Checkout, .. arguments], ct: ct);
}
