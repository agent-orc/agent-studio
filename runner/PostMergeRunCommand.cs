using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

public sealed record PostMergeRunOptions(string Project, string Checkout, string ServerUrl,
    string Branch, string Remote, string TokenFile, string Mode, int MaxMerges,
    TimeSpan Window, int MaxSlots, IReadOnlyList<string> StateDirectories, bool DryRun)
{
    public string? RepositoryUrl { get; init; }
    public string? StateProjectId { get; init; }
    public bool RecordSkips { get; init; } = true;
    public bool UseGateCapacity { get; init; }
    public bool GateExecution { get; init; }
    public string? PinnedTipSha { get; init; }
    public string? RunId { get; init; }

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
                .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), dryRun) { UseGateCapacity = true };
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
    internal Func<CancellationToken, Task>? BeforeTests { get; init; }
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string NoTestDefinition = "no valid test definition at the fetched tip";

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

        Directory.CreateDirectory(_options.Checkout);
        using var claim = ClaimCheckout();
        var readOnlyObservation = claim is null && observeOnly && _options.RepositoryUrl is not null;
        if (claim is null && !readOnlyObservation)
        { _output.WriteLine("post-merge checkout already claimed"); return 0; }
        if (claim is not null && File.Exists(JournalPath))
        {
            // Observers must leave the interrupted workspace and its evidence untouched.
            if (observeOnly || claim is null) return 0;
            _journal = ReadJournal();
            if (_options.StateProjectId is not null && await WasSupersededAsync(ct))
            {
                ArchiveJournal();
                _journal = null;
            }
            else
            {
                if (_journal.Terminal is not null)
                {
                    await DeliverTerminalAsync(ct);
                    return 0;
                }
                await RecoverAsync(ct);
                await ReapInterruptedAsync(ct);
                if (_journal.InFlight || _journal.WorkspaceDigest is null || _journal.WorkspaceDigest != CheckpointDigest())
                {
                    await InterruptAsync("host interrupted with an incomplete command or changed workspace", ct);
                    return 1;
                }
                return await ExecuteOwnedAsync(ct);
            }
        }
        using (var escalation = await _http.PostAsync(StateUrl() + "/escalation-check", null, ct))
            escalation.EnsureSuccessStatusCode();
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
        // The gate transport already authenticated and materialized the pinned commit.
        var fetch = readOnlyObservation
            ? await Git(ct, "fetch", "--quiet", "--no-write-fetch-head", _options.RepositoryUrl!,
                $"+{_options.Branch}:{observationRef}")
            : _options.GateExecution && _options.PinnedTipSha is { } pinned
                ? await Git(ct, "update-ref", "FETCH_HEAD", pinned)
                : await Git(ct, "fetch", "--quiet", _options.Remote, _options.PinnedTipSha ?? _options.Branch);
        if (!fetch.Success) throw new IOException(fetch.StdErr);
        var resolved = await Git(ct, "rev-parse", "--verify", fetchedRef + "^{commit}");
        if (!resolved.Success) throw new IOException(resolved.StdErr);
        var tip = resolved.StdOut.Trim();
        if (state.ClosedWindow is { } closedWindow && closedWindow.TipSha == tip && closedWindow.Reason == NoTestDefinition)
        {
            _output.WriteLine($"post-merge closed tip={tip}: {closedWindow.Reason}");
            return 0;
        }
        var now = _clock.GetUtcNow();
        var id = _options.RunId ?? Guid.NewGuid().ToString("N");
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
                    : "develop has not moved since the last green run")
            { ExecutionHost = _hostOptions?.Hostname ?? Environment.MachineName };
            await Post(skipped, ct);
            _output.WriteLine($"post-merge skipped tip={tip}");
            return 0;
        }

        DateTimeOffset? windowOpened = null;
        var windowMinutes = state.WindowMinutes ?? (int)_options.Window.TotalMinutes;
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
            windowOpened = merges.Count == 0 ? null : merges.Min(merge => merge.At);
            var close = PostMergeRunPlan.Window(merges.Count,
                merges.Count == 0 ? null : merges.Min(merge => merge.At), now,
                state.WindowMaxMerges ?? _options.MaxMerges,
                state.WindowMinutes is { } minutes
                    ? TimeSpan.FromMinutes(minutes) : _options.Window);
            await ObserveWindow(tip, merges.Count == 0 ? null : merges.Min(merge => merge.At), now,
                state.WindowMinutes ?? (int)_options.Window.TotalMinutes, admissionReason, ct);
            if (!close.Close) { _output.WriteLine($"post-merge pending tip={tip}: {close.Reason}"); return 0; }
        }

        if (!observeOnly && _options.UseGateCapacity)
        {
            using var placement = await _http.PostAsJsonAsync(StateUrl() + "/placement?kind=" + _options.Mode,
                new PostMergeWindowObservation(tip, windowOpened, now, windowMinutes, admissionReason), Json, ct);
            placement.EnsureSuccessStatusCode();
            var destination = await placement.Content.ReadFromJsonAsync<PostMergePlacement>(Json, ct);
            if (destination?.Executor == "gate")
            {
                if (_options.Mode == "window") await ObserveWindow(tip, windowOpened, now, windowMinutes,
                    destination.Reason ?? "waiting for gate capacity", ct);
                return 0;
            }
            if (destination?.Executor == "closed") return 0;
        }
        var promoted = _options.Mode == "window"
            && PostMergeSchedulingPolicy.DeadlineReached(windowOpened, windowMinutes, now);
        if (observeOnly || !_options.GateExecution && !promoted && admissionReason is not null)
        {
            if (admissionReason is not null) _output.WriteLine($"post-merge waiting: {admissionReason}");
            return 0;
        }
        var checkout = await Git(ct, "checkout", "--quiet", "--detach", "--force", tip);
        if (!checkout.Success) throw new IOException(checkout.StdErr);
        var definition = ProjectDefinitionReader.ReadWorkspace(_options.Checkout).Definition;
        if (definition is null || definition.TestSuites.Count == 0 && definition.Commands.Test.Count == 0)
        {
            using var closed = await _http.PostAsJsonAsync(StateUrl() + "/close-window",
                new PostMergeWindowClosure(tip, now, NoTestDefinition), Json, ct);
            closed.EnsureSuccessStatusCode();
            return 0;
        }
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
            ExecutionHost = _hostOptions?.Hostname ?? Environment.MachineName,
            ExecutionPlane = _options.GateExecution ? "gate" : "run",
            SuiteSelection = new(selection.Suites, selection.OmittedSuites, selection.Reasons),
            Envelope = await EnvelopeAsync(definition, commands, PostMergeEnvelopeProbe.ShardPlan(planned),
                host.LoadPerCore, null, ct),
        };
        _journal = new(started, state, definition, freeSlots, Guid.NewGuid().ToString("N"))
        { WorkspaceDigest = CheckpointDigest() };
        SaveJournal();
        await Post(started, ct);
        SaveJournal();
        return await ExecuteOwnedAsync(ct);
    }

    internal async Task<int> ExecuteOwnedAsync(CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var renewal = RenewLoopAsync(lifetime);
        try { return await ExecutePlanAsync(lifetime.Token); }
        catch (PostMergeCheckpointException exception)
        {
            await InterruptAsync(exception.Message, lifetime.Token);
            return 1;
        }
        finally
        {
            lifetime.Cancel();
            try { await renewal; }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }
    }

    private async Task<int> ExecutePlanAsync(CancellationToken ct)
    {
        var started = _journal!.Started;
        var state = _journal.State;
        var definition = _journal.Definition;
        var commands = started.Commands;
        var tip = started.TipSha;
        var id = started.Id;
        var freeSlots = _journal.FreeSlots;
        var selection = started.SuiteSelection!;
        var host = new CoverageHostLoad(started.StartLoadPerCore, started.StartOccupiedSlots ?? 0);
        _testHome = Path.Combine(_options.Checkout, ".post-merge-work", _journal.Owner, "home");
        _testTemp = Path.Combine(_options.Checkout, ".post-merge-work", _journal.Owner, "temp");
        Directory.CreateDirectory(_testHome);
        Directory.CreateDirectory(_testTemp);
        bool MayShard(ProjectTestSuite? suite, string command) => started.Kind == "nightly" && suite is not null
            && freeSlots > 1 && command.Contains("dotnet test ", StringComparison.Ordinal)
            && command.Contains("--no-build", StringComparison.Ordinal);

        await using var sampler = new PostMergeLoadSampler(host.LoadPerCore, () => _probeHost().LoadPerCore);
        var verdicts = new List<PostMergeCommandVerdict>();
        var ledger = new PostMergeTestLedger();
        var savedFocused = _journal.Checkpoint?.FocusedRuns ?? [];
        var focusedOrdinal = 0;
        _recordRun = run =>
        {
            if (focusedOrdinal < savedFocused.Count)
            {
                var saved = savedFocused[focusedOrdinal];
                if (run.ToReceipt() with { DurationMs = saved.DurationMs } != saved)
                    throw new PostMergeCheckpointException("Post-merge focused checkpoint inputs changed.");
                run = run with { DurationMs = saved.DurationMs };
            }
            focusedOrdinal++;
            ledger.Record(run);
            _journal!.Checkpoint = started with { Verdicts = verdicts.ToArray(),
                TestVerdicts = ledger.HasTests ? ledger.Verdicts() : null, FocusedRuns = ledger.FocusedRuns() };
            SaveJournal();
        };
        var shardPlan = new List<(string Suite, string Shards)>();
        var quarantinedFailures = 0;
        // A TAP failure in lint uses the same coverage identity on later passing receipts.
        var lintChecks = definition.Commands.Lint.Except(definition.TestSuites.Select(item => item.Command))
            .Where(commands.Contains).ToDictionary(command => command, command =>
                new ProjectTestSuite("lint-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(command)))[..16],
                    ProjectTestSuiteCategories.Base, 0, command));
        var testsStarted = false;
        foreach (var command in commands)
        {
            if (!testsStarted && (definition.TestSuites.Any(suite => suite.Command == command)
                || definition.Commands.Test.Contains(command)))
            {
                if (BeforeTests is not null) await BeforeTests(ct);
                testsStarted = true;
            }
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
                var result = await TimedRun(command, definition.Environment, ct);
                var lint = lintChecks.GetValueOrDefault(command);
                var verdict = Verdict(command, result) with { SuiteId = lint?.Id };
                if (lint is not null && result.ExitCode != 0)
                {
                    var initial = new PostMergeTap(result.Output);
                    if (initial.HasFailures)
                    {
                        var repeat = await TimedRun(command, definition.Environment, ct);
                        verdict = LintRetryVerdict(verdict, lint, initial.Compare(new(repeat.Output), _options.Checkout), repeat, tip, ledger, _recordRun);
                    }
                }
                verdicts.Add(verdict);
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
                    var blocking = TestQuarantinePolicy.Partition(observed,
                        TestQuarantinePolicy.ReadUnion(_options.Checkout, state.Quarantines, _clock.GetUtcNow()).Entries,
                        DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime)).Blocking;
                    ledger.Initial(suite, initial.Command, observed, blocking, tip);
                    _journal!.Checkpoint = started with { Verdicts = verdicts.ToArray(),
                        TestVerdicts = ledger.Verdicts(), FocusedRuns = ledger.FocusedRuns() };
                    SaveJournal();
                    verdicts[index] = await PostMergeTestRetry.RepeatAsync(initial, _ => blocking,
                        retry => RunCommand(retry, definition.Environment, ct), _recordRun, tip,
                        referenceRun: retry => RunReferenceCommand(retry, definition.Environment, ct));
                    if (verdicts[index].ExitCode == 0) quarantinedFailures++;
                }
                for (var index = verdictStart; index < verdicts.Count; index++)
                    verdicts[index] = verdicts[index] with { SuiteId = suite.Id };
            }
            _journal!.Checkpoint = started with { Verdicts = verdicts.ToArray(),
                TestVerdicts = ledger.HasTests ? ledger.Verdicts() : null, FocusedRuns = ledger.FocusedRuns() };
            _journal.WorkspaceDigest = CheckpointDigest();
            SaveJournal();
            if (AfterCheckpoint is not null) await AfterCheckpoint(ct);
            if (verdicts.Any(verdict => verdict.ExitCode != 0)) break;
        }
        var status = verdicts.All(v => v.ExitCode == 0) && verdicts.Count >= commands.Count
            ? "green" : "red";
        if (_canaryFailed && RedTestTriagePolicy.ClassificationHint(new(TestEnvelope.CanaryName, "backend-base", RedTestCategory.Base)
            { EnvelopeCanary = RedTestResult.Fail }) == RedTestClassification.Environment) status = "inconclusive";
        PostMergeFailureEvidence? failure = null;
        if (status == "red" && !verdicts.Any(verdict => TestEnvelope.CanaryFailed(verdict.FailedTests ?? [])))
        {
            try { failure = await AttributeFailureAsync(state, definition, tip, verdicts, ledger, ct); }
            catch (Exception exception) when (exception is not (OperationCanceledException or PostMergeCheckpointException))
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
        _journal!.Terminal = started with { Status = status, Verdicts = verdicts, Failure = failure,
            SuiteSelection = new([.. selection.SelectedSuites, .. lintChecks.Values.Select(lint => lint.Id)], selection.OmittedSuites, selection.Reasons)
            {
                SuiteCommands = definition.TestSuites.Concat(lintChecks.Values).ToDictionary(suite => suite.Id, suite => suite.Command),
                AttemptedSuites = definition.TestSuites.Concat(lintChecks.Values).Where(suite => verdicts.Any(verdict => verdict.SuiteId == suite.Id))
                    .Select(suite => suite.Id).ToArray(),
            },
            Reason = quarantinedFailures > 0
                ? $"{quarantinedFailures} failing command(s) passed a same-SHA rerun or had only quarantined tests."
                : null,
            FinishedAtUtc = _clock.GetUtcNow(),
            TestVerdicts = ledger.HasTests ? ledger.Verdicts() : null,
            FocusedRuns = ledger.FocusedRuns(),
            Envelope = envelope,
            ReferenceEnvelope = _referenceEnvelope,
            FingerprintMatchesReference = _referenceEnvelope is null ? null : _lastEnvelope is { } actual
                && TestEnvelope.MatchesReference(actual, _referenceEnvelope),
            EnvelopeCanaryPassed = _canaryFailed ? false : _canaryObserved ? true : null };
        SaveJournal();
        if (AfterTerminalPrepared is not null) await AfterTerminalPrepared(ct);
        await DeliverTerminalAsync(ct);
        _output.WriteLine($"post-merge {status} tip={tip} commands={verdicts.Count}");
        return status == "green" ? 0 : 1;
    }

    internal Func<CancellationToken, Task>? AfterCheckpoint { get; init; }
    internal Func<CancellationToken, Task>? AfterTerminalPrepared { get; init; }
    internal Func<string, CancellationToken, Task<(int ExitCode, bool TimedOut, string Output)>>? CommandExecutor { get; init; }
    private readonly object _journalGate = new();
    private PostMergeHostJournal? _journal;
    private int _commandOrdinal;
    private int _runningCommands;
    private bool _terminalDelivered;
    private string JournalPath => Path.Combine(_options.Checkout, ".post-merge-journal.json");

    internal string? RecoveryOwner() => File.Exists(JournalPath) ? ReadJournal().Owner : null;

    internal PostMergeHostJournal ReadJournal()
        => JsonSerializer.Deserialize<PostMergeHostJournal>(File.ReadAllText(JournalPath), Json)
            ?? throw new IOException("Post-merge journal is empty.");

    private void SaveJournal()
    {
        lock (_journalGate)
        {
            if (_terminalDelivered) return;
            var temporary = JournalPath + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, _journal, Json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, JournalPath, overwrite: true);
        }
    }

    // Include ignored build outputs: a clean Git status cannot validate a completed build.
    // Record symlinks without traversing outside the dedicated checkout.
    internal string WorkspaceDigest()
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string value) => digest.AppendData(Encoding.UTF8.GetBytes(value + "\0"));
        void Walk(string directory)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileName(path);
                if (name == ".git" || directory == _options.Checkout
                    && name.StartsWith(".post-merge-", StringComparison.Ordinal) && name != ".post-merge-work") continue;
                Add(Path.GetRelativePath(_options.Checkout, path));
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    var link = new FileInfo(path);
                    Add(link.LinkTarget ?? "");
                    var target = link.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
                    if (target is null || !Path.GetFullPath(target).StartsWith(
                        Path.GetFullPath(_options.Checkout) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                        throw new IOException("An external symlink prevents post-merge checkpoint reuse.");
                    continue;
                }
                if (!OperatingSystem.IsWindows()) Add(File.GetUnixFileMode(path).ToString());
                if ((attributes & FileAttributes.Directory) != 0) { Walk(path); continue; }
                using var file = File.OpenRead(path);
                digest.AppendData(SHA256.HashData(file));
            }
        }
        var head = Path.Combine(_options.Checkout, ".git", "HEAD");
        if (File.Exists(head)) Add(File.ReadAllText(head));
        Walk(_options.Checkout);
        return Convert.ToHexString(digest.GetHashAndReset());
    }

    private string? CheckpointDigest()
    {
        try { return WorkspaceDigest(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _output.WriteLine($"post-merge checkpoint cannot be reused: {exception.Message}");
            return null;
        }
    }

    private async Task ReapInterruptedAsync(CancellationToken ct)
    {
        if (!_journal!.InFlight) return;
        if (CommandExecutor is not null) return;
        if (!OperatingSystem.IsLinux())
            throw new IOException("Interrupted post-merge command requires confirmed process cleanup before recovery on this host.");
        await WorktreeProcessReaper.ReapAsync(_options.Checkout, line => _output.WriteLine(line), ct);
    }

    private async Task RecoverAsync(CancellationToken ct)
    {
        if (_journal!.Fence == 0)
        {
            await Post(_journal.Started, ct);
            SaveJournal();
        }
        _journal.RecoveryId ??= Guid.NewGuid().ToString("N");
        SaveJournal();
        var expiry = await RenewAsync(recover: true, ct);
        if (expiry <= _clock.GetUtcNow())
        {
            // A lost acknowledgement may be replayed after that generation's lease expired.
            _journal.RecoveryId = Guid.NewGuid().ToString("N");
            SaveJournal();
            await RenewAsync(recover: true, ct);
        }
        _journal.RecoveryId = null;
        SaveJournal();
    }

    private async Task<DateTimeOffset> RenewAsync(bool recover, CancellationToken ct)
    {
        var journal = _journal!;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Post, StateUrl() + "/runs")
        { Content = JsonContent.Create(journal.Started, options: Json) };
        request.Headers.Add("X-Post-Merge-Owner", journal.Owner);
        request.Headers.Add("X-Post-Merge-Fence", journal.Fence.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add("X-Post-Merge-Operation", recover ? "recover" : "renew");
        if (journal.RecoveryId is not null) request.Headers.Add("X-Post-Merge-Recovery-Id", journal.RecoveryId);
        using var response = await _http.SendAsync(request, deadline.Token);
        response.EnsureSuccessStatusCode();
        var custody = await response.Content.ReadFromJsonAsync<HostCustody>(Json, ct)
            ?? throw new IOException("Post-merge custody response was empty.");
        journal.Fence = custody.Fence;
        SaveJournal();
        return custody.ExpiresAtUtc;
    }

    private async Task RenewLoopAsync(CancellationTokenSource lifetime)
    {
        if (_journal!.Fence == 0) return;
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), _clock, lifetime.Token);
                await RenewAsync(recover: false, lifetime.Token);
            }
        }
        catch (Exception) when (!lifetime.IsCancellationRequested)
        {
            lifetime.Cancel();
            throw;
        }
    }

    private async Task<bool> WasSupersededAsync(CancellationToken ct)
    {
        var state = await _http.GetFromJsonAsync<PostMergeProjectState>(StateUrl(), Json, ct)
            ?? throw new IOException("Post-merge state was unavailable during recovery.");
        var journal = _journal!;
        if (state.ActiveRun?.Id == journal.Started.Id) return false;
        var recorded = state.Runs.FirstOrDefault(run => run.Id == journal.Started.Id);
        return recorded is not null
            ? journal.Terminal is null || recorded.Status != journal.Terminal.Status || recorded.Reason != journal.Terminal.Reason
            : journal.Fence != 0;
    }

    private void ArchiveJournal()
    {
        var directory = Path.Combine(_options.Checkout, ".post-merge-evidence");
        Directory.CreateDirectory(directory);
        File.Move(JournalPath, Path.Combine(directory, _journal!.Owner + ".json"), overwrite: true);
    }

    private async Task DeliverTerminalAsync(CancellationToken ct)
    {
        await Post(_journal!.Terminal!, ct);
        lock (_journalGate)
        {
            if (_journal.Terminal!.Status == "inconclusive")
                ArchiveJournal();
            else File.Delete(JournalPath);
            _terminalDelivered = true;
        }
        try
        {
            var workspace = Path.Combine(_options.Checkout, ".post-merge-work", _journal.Owner);
            if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { _output.WriteLine($"post-merge envelope cleanup unavailable: {exception.Message}"); }
    }

    private async Task InterruptAsync(string reason, CancellationToken ct)
    {
        var checkpoint = _journal!.Checkpoint ?? _journal.Started;
        var retained = checkpoint.Verdicts.ToList();
        foreach (var command in _journal.Commands.OfType<PostMergeHostCommand>())
            if (!retained.Any(verdict => verdict.Command == command.Command))
                retained.Add(Verdict(command.Command, (command.ExitCode, command.TimedOut, command.Output, command.DurationMs)));
        _journal.Terminal = checkpoint with
        { Status = "inconclusive", FinishedAtUtc = _clock.GetUtcNow(), Reason = reason,
            Verdicts = retained.Take(64).ToArray() };
        SaveJournal();
        await DeliverTerminalAsync(ct);
    }

    // Reconstructed-service tests enter at the durable execution boundary.
    internal async Task<int> ExecuteJournalAsync(PostMergeHostJournal? journal, CancellationToken ct)
    {
        Directory.CreateDirectory(_options.Checkout);
        using var claim = ClaimCheckout() ?? throw new IOException("Post-merge checkout already claimed.");
        _journal = journal ?? ReadJournal();
        if (journal is not null) SaveJournal();
        if (_journal.Terminal is not null) { await DeliverTerminalAsync(ct); return 0; }
        if (journal is null)
        {
            await RecoverAsync(ct);
            await ReapInterruptedAsync(ct);
        }
        if (journal is null && (_journal.InFlight || _journal.WorkspaceDigest is null || _journal.WorkspaceDigest != CheckpointDigest()))
        { await InterruptAsync("host interrupted with an incomplete command or changed workspace", ct); return 1; }
        return await ExecuteOwnedAsync(ct);
    }

    private sealed record HostCustody(long Fence, DateTimeOffset ExpiresAtUtc);
    private sealed class PostMergeCheckpointException(string message) : IOException(message);

    private static PostMergeCommandVerdict Verdict(string command,
        (int ExitCode, bool TimedOut, string Output, long DurationMs) result)
        => new(command, result.ExitCode, result.TimedOut,
            result.ExitCode == 0 ? null : PostMergeAttributor.Excerpt(result.Output),
            result.ExitCode == 0 ? null : PostMergeAttributor.FailedTests(result.Output))
        { DurationMs = result.DurationMs,
            FailureMessages = result.ExitCode == 0 ? null : PostMergeAttributor.FailedTests(result.Output)
                .Take(64).ToDictionary(test => test, test =>
                {
                    var error = TestFailureMessages.FirstError(result.Output, test);
                    return error[..Math.Min(error.Length, 2000)];
                }, StringComparer.Ordinal) };

    private static PostMergeCommandVerdict LintRetryVerdict(PostMergeCommandVerdict initialVerdict,
        ProjectTestSuite lint, IReadOnlyList<PostMergeTap.Test> observations,
        (int ExitCode, bool TimedOut, string Output, long DurationMs) repeat, string tip,
        PostMergeTestLedger ledger, Action<PostMergeTestRun> record)
    {
        // Give retry failures priority within the receipt's 64-test limit.
        var evidence = observations.OrderBy(test => test.Repeat switch
            {
                PostMergeTestResults.Fail => 0,
                PostMergeTestResults.Unknown => 1,
                _ => 2,
            }).Take(64).ToDictionary(test => test.Name);
        var tests = evidence.Keys.ToArray();
        var failed = tests.Where(test => evidence[test].Repeat != PostMergeTestResults.Pass).ToArray();
        ledger.Initial(lint, lint.Command, tests, tests, tip, test => evidence[test].Initial);
        record(new(PostMergeRunPhases.Repeat, tip, tests, repeat.ExitCode, repeat.TimedOut, repeat.Output, repeat.DurationMs)
            { ReportedResults = tests.ToDictionary(test => test, test => evidence[test].Repeat) });

        return initialVerdict with
        {
            ExitCode = repeat.ExitCode == 0 && failed.Length > 0 ? initialVerdict.ExitCode : repeat.ExitCode,
            TimedOut = repeat.TimedOut,
            Retried = true,
            OutputTail = repeat.ExitCode != 0 ? PostMergeAttributor.Excerpt(repeat.Output) : initialVerdict.OutputTail,
            FailedTests = failed,
            FlakyTests = tests.Where(test => evidence[test].Repeat == PostMergeTestResults.Pass).ToArray(),
            FailureMessages = tests.ToDictionary(test => test,
                test => evidence[test].Error ?? "Not recorded in receipt."),
        };
    }

    private async Task<(int ExitCode, bool TimedOut, string Output, long DurationMs)> TimedRun(string command,
        IReadOnlyDictionary<string, string> environment, CancellationToken ct)
    {
        var duration = 0L;
        var result = await RunCommand(command, environment, ct, duration: value => duration = value);
        return (result.ExitCode, result.TimedOut, result.Output, duration);
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
        var blocking = TestQuarantinePolicy.Partition(observed,
                        TestQuarantinePolicy.ReadUnion(_options.Checkout, state.Quarantines, _clock.GetUtcNow()).Entries,
                        DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime)).Blocking;
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
        var reach = new PostMergeCoveringFolders.Index(await ListTrackedFiles(ct), path =>
        {
            try { return File.ReadAllText(Path.Combine(_options.Checkout, path)); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _output.WriteLine($"post-merge test reach unavailable for {path}: {exception.Message}");
                return null;
            }
        });
        var testProject = PostMergeAttributor.TestProject(failed.Command);
        var testDirectory = testProject is null ? null : Path.GetDirectoryName(testProject.Replace('\\', '/'));
        var scopes = blocking.Select(test => reach.For(test, testDirectory)).ToArray();
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
        // Keep the measured RT control/bisect protocol independent of the static reach heuristic.
        // A proven attribution wins even when source-name matching misses its dependency.
        var suspects = candidates.Where(candidate => candidate.Sha == attributed
            || PostMergeAttributor.TouchesFailure(candidate.Paths, scopes)).Select(candidate => candidate.Sha).ToArray();
        return new(fingerprint, blocking, excerpt, failingPaths,
            suspects.TakeLast(512).ToArray(),
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

    private Task<PostMergeEnvelopeFingerprint> EnvelopeAsync(ProjectExecutionDefinition definition,
        IReadOnlyList<string> commands, string shardPlan, double? startLoad, double? peakLoad, CancellationToken ct)
    {
        using var fallback = new TestCommandEnvelope(definition.Environment);
        return Task.FromResult((_lastEnvelope ?? fallback.Fingerprint(definition.Environment, shardPlan))
            with { ShardPlan = shardPlan, SdkVersion = string.IsNullOrEmpty(_sdkVersion) ? null : _sdkVersion,
                StartLoadPerCore = startLoad, PeakLoadPerCore = peakLoad });
    }

    private string? _sdkVersion;
    private PostMergeEnvelopeFingerprint? _lastEnvelope;
    internal PostMergeEnvelopeFingerprint? LastCommandEnvelope => _lastEnvelope;
    private bool _canaryFailed;
    private bool _canaryObserved;
    private string? _testHome;
    private string? _testTemp;
    private PostMergeEnvelopeFingerprint? _referenceEnvelope;
    private static readonly SemaphoreSlim ReferenceToken = new(1, 1);

    private async Task<(int ExitCode, bool TimedOut, string Output)> RunReferenceCommand(string command,
        IReadOnlyDictionary<string, string> environment, CancellationToken ct)
    {
        await ReferenceToken.WaitAsync(ct);
        try
        {
            var result = await RunCommand(command, environment, ct, TestEnvelope.Reference);
            _referenceEnvelope = _lastEnvelope! with { ShardPlan = TestEnvelope.ReferenceShardPlan };
            return result;
        }
        finally { ReferenceToken.Release(); }
    }

    private FileStream? ClaimCheckout()
    {
        try { return new FileStream(Path.Combine(_options.Checkout, ".post-merge-claim"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return null; }
    }

    internal async Task<(int ExitCode, bool TimedOut, string Output)> RunCommand(string command,
        IReadOnlyDictionary<string, string> environment, CancellationToken ct, TestEnvelopeDefinition? shape = null, Action<long>? duration = null)
    {
        ct.ThrowIfCancellationRequested();
        var begun = System.Diagnostics.Stopwatch.GetTimestamp();
        var ordinal = Interlocked.Increment(ref _commandOrdinal) - 1;
        _output.WriteLine($"post-merge {command}");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromHours(2));
        using var envelope = WorkerCgroup.PrepareTestCommand(environment, line => _output.WriteLine(line), _testHome, _testTemp, shape);
        if (CommandExecutor is null) await envelope.ProbeSdkAsync(_options.Checkout, ct);
        _lastEnvelope = envelope.Fingerprint(environment, "command=1");
        _sdkVersion ??= _lastEnvelope.SdkVersion;
        var sourceSha = _journal?.Started.TipSha;
        if (_journal is not null && CommandExecutor is null)
        {
            var source = await Git(ct, "rev-parse", "--verify", "HEAD^{commit}");
            if (!source.Success) throw new IOException(source.StdErr);
            sourceSha = source.StdOut.Trim();
        }
        var input = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { command, sourceSha, environment, shape, Envelope = _lastEnvelope }, Json))));
        if (_journal is not null)
        {
            lock (_journalGate)
            {
                if (ordinal < _journal.Commands.Count && _journal.Commands[ordinal] is { } cached)
                {
                    if (cached.Input != input) throw new PostMergeCheckpointException("Post-merge checkpoint inputs changed.");
                    _canaryObserved |= cached.CanaryObserved;
                    _canaryFailed |= cached.CanaryFailed;
                    duration?.Invoke(cached.DurationMs);
                    return (cached.ExitCode, cached.TimedOut, cached.Output);
                }
                _runningCommands++;
                _journal.InFlight = true;
                SaveJournal();
            }
        }
        void Checkpoint(int exit, bool timedOut, string text)
        {
            var elapsed = (long)System.Diagnostics.Stopwatch.GetElapsedTime(begun).TotalMilliseconds;
            duration?.Invoke(elapsed);
            if (_journal is null) return;
            lock (_journalGate)
            {
                while (_journal.Commands.Count <= ordinal) _journal.Commands.Add(null);
                _journal.Commands[ordinal] = new(command, input, sourceSha, exit, timedOut, text, elapsed, _canaryObserved, _canaryFailed);
                _runningCommands--;
                // Flush the result before hashing potentially large generated outputs.
                SaveJournal();
                if (_runningCommands == 0)
                {
                    _journal.WorkspaceDigest = CheckpointDigest();
                    _journal.InFlight = false;
                    SaveJournal();
                }
            }
        }
        if (CommandExecutor is not null)
        {
            var simulated = await CommandExecutor(command, ct);
            Checkpoint(simulated.ExitCode, simulated.TimedOut, simulated.Output);
            return simulated;
        }
        var launch = envelope.Launch("sh", ["-c", command]);
        var childEnvironment = envelope.Environment;
        try
        {
            var result = await ProcessRunner.RunAsync(launch.FileName, launch.Arguments, _options.Checkout,
                environment: childEnvironment, clearEnvironment: true,
                onStdOut: line => _output.WriteLine(line),
                onStdErr: line => _output.WriteLine(line),
                isolateProcessGroup: OperatingSystem.IsLinux(), ct: timeout.Token);
            _lastEnvelope = envelope.Fingerprint(environment, "command=1");
            var output = result.StdOut + "\n" + result.StdErr;
            var canaryFailure = TestEnvelope.CanaryFailed(PostMergeAttributor.FailedTests(output));
            if (result.ExitCode == 0 && command.Contains(TestEnvelope.CanaryName, StringComparison.Ordinal) || canaryFailure)
                _canaryObserved = true;
            if (result.ExitCode != 0 && (canaryFailure || output.Contains("test envelope attach failed", StringComparison.Ordinal)))
                _canaryFailed = true;
            Checkpoint(result.ExitCode, false, output);
            return (result.ExitCode, false, output);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Checkpoint(124, true, "");
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

    internal Func<TimeSpan, CancellationToken, Task> ReceiptDelay { get; init; } = Task.Delay;

    private string StateUrl() => $"api/runner/post-merge/projects/{Uri.EscapeDataString(_options.StateProjectId ?? _options.Project)}";
    private async Task Post(PostMergeRunRecord run, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, StateUrl() + "/runs")
                { Content = JsonContent.Create(run, options: Json) };
                if (_journal is not null)
                {
                    request.Headers.Add("X-Post-Merge-Owner", _journal.Owner);
                    request.Headers.Add("X-Post-Merge-Fence", _journal.Fence.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                using var response = await _http.SendAsync(request, ct);
                if (response.IsSuccessStatusCode)
                {
                    if (_journal is not null && response.Headers.TryGetValues("X-Post-Merge-Fence", out var values)
                        && long.TryParse(values.Single(), out var fence)) _journal.Fence = fence;
                    return;
                }
                if ((int)response.StatusCode < 500 || attempt == 2)
                    response.EnsureSuccessStatusCode();
            }
            catch (HttpRequestException exception) when (attempt < 2 && !ct.IsCancellationRequested
                && (exception.StatusCode is null || (int)exception.StatusCode >= 500))
            {
                _output.WriteLine($"post-merge receipt retry {attempt + 1}/3");
            }
            await ReceiptDelay(TimeSpan.FromSeconds(attempt + 1), ct);
        }
        throw new HttpRequestException("Post-merge receipt was not accepted after three attempts.");
    }

    private Task<ProcessResult> Git(CancellationToken ct, params string[] arguments)
        => ProcessRunner.RunAsync("git", ["-C", _options.Checkout, .. arguments],
            environment: _options.StateProjectId is null ? null : PostMergeHostScheduler.GitEnvironment, ct: ct);
}

internal sealed record PostMergeHostJournal(PostMergeRunRecord Started, PostMergeProjectState State,
    ProjectExecutionDefinition Definition, int FreeSlots, string Owner)
{
    public long Fence { get; set; }
    public string? RecoveryId { get; set; }
    public bool InFlight { get; set; }
    public string? WorkspaceDigest { get; set; }
    public List<PostMergeHostCommand?> Commands { get; init; } = [];
    public PostMergeRunRecord? Checkpoint { get; set; }
    public PostMergeRunRecord? Terminal { get; set; }
}

internal sealed record PostMergeHostCommand(string Command, string Input, string? SourceSha, int ExitCode, bool TimedOut,
    string Output, long DurationMs, bool CanaryObserved, bool CanaryFailed);
