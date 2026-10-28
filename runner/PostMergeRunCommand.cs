using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

public sealed record PostMergeRunOptions(string Project, string Checkout, string ServerUrl,
    string Branch, string Remote, string TokenFile, string Mode, int MaxMerges,
    TimeSpan Window, int MaxSlots, IReadOnlyList<string> StateDirectories, bool DryRun)
{
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
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public PostMergeRunCommand(PostMergeRunOptions options, TextWriter output, TimeProvider clock,
        HttpClient http, Func<CoverageHostLoad>? probeHost = null)
    {
        _options = options;
        _output = output;
        _clock = clock;
        _http = http;
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

    public async Task<int> ExecuteAsync(CancellationToken ct)
    {
        if (_options.DryRun)
        {
            _output.WriteLine($"post-merge dry-run project={_options.Project} mode={_options.Mode} checkout={_options.Checkout}");
            _output.WriteLine($"Would fetch {_options.Remote}/{_options.Branch}, pin FETCH_HEAD, and read the project state.");
            _output.WriteLine(_options.Mode == "window"
                ? $"Would close after {_options.MaxMerges} merges or {_options.Window.TotalMinutes:0} minutes; run the affected-test union."
                : "Would run the full routine suite on the fetched tip across free slots.");
            var read = ProjectDefinitionReader.ReadWorkspace(_options.Checkout);
            if (read.Definition is null) return 1;
            _output.WriteLine("Window test commands are resolved from each fetched merge; this is the full fallback plan.");
            foreach (var command in PostMergeRunPlan.Build(read.Definition, _options.Mode,
                         _options.Mode == "window" ? [".agent-studio/project.yml"] : []))
                _output.WriteLine($"Would run: {command}");
            return 0;
        }

        using (var escalation = await _http.PostAsync(StateUrl() + "/escalation-check", null, ct))
            escalation.EnsureSuccessStatusCode();

        Directory.CreateDirectory(_options.Checkout);
        using var claim = ClaimCheckout();
        if (claim is null) { _output.WriteLine("post-merge checkout already claimed"); return 0; }
        var host = _probeHost();
        if (host.ActiveSlots >= _options.MaxSlots
            || host.LoadPerCore is { } load && load > CoverageRunOptions.DefaultMaxLoadPerCore)
        {
            _output.WriteLine($"post-merge waiting: {host.ActiveSlots} occupied slots, load {host.LoadPerCore:0.00}");
            return 0;
        }
        var freeSlots = _options.MaxSlots - host.ActiveSlots;
        if (!Directory.Exists(Path.Combine(_options.Checkout, ".git"))
            && !File.Exists(Path.Combine(_options.Checkout, ".git")))
            throw new IOException("Post-merge checkout is not a git checkout.");
        var dirty = await Git(ct, "status", "--porcelain", "--untracked-files=no");
        if (!dirty.Success || dirty.StdOut.Trim().Length > 0)
            throw new IOException("Post-merge checkout has tracked changes.");
        var state = await _http.GetFromJsonAsync<PostMergeProjectState>(StateUrl(), Json, ct)
            ?? throw new IOException("Post-merge state was unavailable.");
        var fetch = await Git(ct, "fetch", "--quiet", _options.Remote, _options.Branch);
        if (!fetch.Success) throw new IOException(fetch.StdErr);
        var resolved = await Git(ct, "rev-parse", "--verify", "FETCH_HEAD^{commit}");
        if (!resolved.Success) throw new IOException(resolved.StdErr);
        var tip = resolved.StdOut.Trim();
        var now = _clock.GetUtcNow();
        var id = Guid.NewGuid().ToString("N");
        if (string.Equals(tip, state.LastGreenSha, StringComparison.OrdinalIgnoreCase))
        {
            var skipped = new PostMergeRunRecord(id, _options.Project, _options.Mode, tip,
                state.LastWindowTipSha, null, null, 0, [], [], "skipped", now, now,
                "develop has not moved since the last green run");
            await Post(skipped, ct);
            _output.WriteLine($"post-merge skipped tip={tip}");
            return 0;
        }

        var from = state.LastWindowTipSha;
        IReadOnlyList<(string Sha, DateTimeOffset At)> merges = [];
        if (_options.Mode == "window")
        {
            // First observation starts a window at the current first-parent commit.
            var range = from is null ? "FETCH_HEAD^..FETCH_HEAD" : $"{from}..{tip}";
            var history = await Git(ct, "log", "--first-parent", "--format=%H%x09%cI", range);
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
            if (!close.Close) { _output.WriteLine($"post-merge pending tip={tip}: {close.Reason}"); return 0; }
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
        var commands = PostMergeRunPlan.Build(definition, _options.Mode, changed);
        var opened = merges.Count == 0 ? (DateTimeOffset?)null : merges.Min(merge => merge.At);
        var started = new PostMergeRunRecord(id, _options.Project, _options.Mode, tip,
            from, opened, now, merges.Count, commands, [], "running", now, null);
        await Post(started, ct);
        var verdicts = new List<PostMergeCommandVerdict>();
        var quarantinedFailures = 0;
        foreach (var command in commands)
        {
            var suite = definition.TestSuites.FirstOrDefault(item => item.Command == command);
            if (_options.Mode == "nightly" && suite is not null
                && freeSlots > 1 && command.Contains("dotnet test ", StringComparison.Ordinal)
                && command.Contains("--no-build", StringComparison.Ordinal))
            {
                var listed = await RunCommand(command + " --list-tests", definition.Environment, ct);
                var classes = listed.ExitCode == 0 ? PostMergeShards.Classes(listed.Output) : [];
                // T3 opts into AGT-3232 auto sharding without changing T1's
                // per-suite opt-in or forcing shards onto a busy host.
                var count = PostMergeShards.Count(suite with
                { Shards = suite.Shards ?? ProjectTestSuite.AutoShards }, freeSlots, classes.Count);
                var shardCommands = PostMergeShards.Commands(command, classes, count,
                    Path.Combine(Path.GetTempPath(), "post-merge-" + id));
                var results = await Task.WhenAll(shardCommands.Select(shard =>
                    RunCommand(shard, definition.Environment, ct)));
                verdicts.AddRange(results.Select((result, index) =>
                    new PostMergeCommandVerdict(shardCommands[index], result.ExitCode, result.TimedOut,
                        result.ExitCode == 0 ? null : PostMergeAttributor.Excerpt(result.Output),
                        result.ExitCode == 0 ? null : PostMergeAttributor.FailedTests(result.Output))));
            }
            else
            {
                var result = await RunCommand(command, definition.Environment, ct);
                verdicts.Add(new(command, result.ExitCode, result.TimedOut,
                    result.ExitCode == 0 ? null : PostMergeAttributor.Excerpt(result.Output),
                    result.ExitCode == 0 ? null : PostMergeAttributor.FailedTests(result.Output)));
            }
            foreach (var failingVerdict in verdicts.Where(verdict => verdict.ExitCode != 0).ToArray())
            {
                if (!definition.TestSuites.Any(suite => failingVerdict.Command == suite.Command
                        || failingVerdict.Command.StartsWith(suite.Command + " ", StringComparison.Ordinal)
                        || suite.Command.Contains("dotnet test ", StringComparison.Ordinal)
                        && failingVerdict.Command.Contains(suite.Command.Split(" --filter", StringSplitOptions.None)[0],
                            StringComparison.Ordinal)))
                    continue;
                var observed = failingVerdict.FailedTests ?? PostMergeAttributor.FailedTests(failingVerdict.OutputTail ?? "");
                var blocking = PostMergeAttributor.BlockingTests(_options.Checkout, observed, _clock.GetUtcNow());
                var focused = PostMergeAttributor.FocusedCommand(failingVerdict.Command, blocking);
                var knownFlake = observed.Count > 0 && blocking.Count == 0;
                if (!knownFlake && focused is not null)
                    knownFlake = (await RunCommand(focused, definition.Environment, ct)).ExitCode == 0;
                if (!knownFlake) continue;
                verdicts[verdicts.IndexOf(failingVerdict)] = failingVerdict with { ExitCode = 0 };
                quarantinedFailures++;
            }
            if (verdicts.Any(verdict => verdict.ExitCode != 0)) break;
        }
        var status = verdicts.All(v => v.ExitCode == 0) && verdicts.Count >= commands.Count
            ? "green" : "red";
        PostMergeFailureEvidence? failure = null;
        if (status == "red")
        {
            try { failure = await AttributeFailureAsync(state, definition, tip, verdicts, ct); }
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
        await Post(started with { Status = status, Verdicts = verdicts, Failure = failure,
            Reason = quarantinedFailures > 0
                ? $"{quarantinedFailures} failing command(s) passed a same-SHA rerun or had only quarantined tests."
                : null,
            FinishedAtUtc = _clock.GetUtcNow() }, ct);
        _output.WriteLine($"post-merge {status} tip={tip} commands={verdicts.Count}");
        return status == "green" ? 0 : 1;
    }

    private async Task<PostMergeFailureEvidence> AttributeFailureAsync(PostMergeProjectState state,
        ProjectExecutionDefinition definition, string tip,
        IReadOnlyList<PostMergeCommandVerdict> verdicts, CancellationToken ct)
    {
        var failed = verdicts.First(verdict => verdict.ExitCode != 0);
        var excerpt = PostMergeAttributor.Excerpt(failed.OutputTail ?? "");
        var observed = failed.FailedTests ?? PostMergeAttributor.FailedTests(excerpt);
        var blocking = PostMergeAttributor.BlockingTests(_options.Checkout, observed, _clock.GetUtcNow());
        var focused = PostMergeAttributor.FocusedCommand(failed.Command, blocking);
        var fingerprint = PostMergeAttributor.Fingerprint(blocking, failed.Command, excerpt);

        var range = state.LastGreenSha is null ? "FETCH_HEAD^..FETCH_HEAD" : state.LastGreenSha + ".." + tip;
        var history = await Git(ct, "log", "--first-parent", "--reverse", "--format=%H", range);
        if (!history.Success) throw new IOException(history.StdErr);
        var commits = history.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var listedProjects = await Git(ct, "ls-files", "*.csproj");
        if (!listedProjects.Success) throw new IOException(listedProjects.StdErr);
        var projectMap = new PostMergeProjectMap(_options.Checkout,
            listedProjects.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        var candidates = new List<(string Sha, IReadOnlyList<string> Paths)>();
        foreach (var commit in commits)
        {
            var diff = await Git(ct, "diff", "--name-only", commit + "^", commit);
            if (!diff.Success) throw new IOException(diff.StdErr);
            var paths = diff.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (projectMap.CouldAffect(definition, paths, failed.Command))
                candidates.Add((commit, paths));
        }

        string? attributed = null;
        if (focused is not null && candidates.Count > 0)
        {
            var left = 0;
            var right = candidates.Count - 1;
            try
            {
                // One point for a sole suspect; binary search when paths leave
                // multiple first-parent suspects in the range.
                while (left <= right)
                {
                    var middle = left + (right - left) / 2;
                    var point = candidates[middle].Sha;
                    var switched = await Git(ct, "checkout", "--quiet", "--detach", "--force", point);
                    if (!switched.Success) throw new IOException(switched.StdErr);
                    var buildCommands = new List<string>();
                    if (!string.IsNullOrWhiteSpace(definition.Commands.Prepare))
                        buildCommands.Add(definition.Commands.Prepare);
                    buildCommands.AddRange(definition.Commands.Build);
                    var built = true;
                    foreach (var command in buildCommands)
                    {
                        if ((await RunCommand(command, definition.Environment, ct)).ExitCode == 0) continue;
                        built = false;
                        break;
                    }
                    if (!built) { attributed = null; break; }
                    var test = await RunCommand(focused, definition.Environment, ct);
                    if (test.ExitCode == 0) left = middle + 1;
                    else { attributed = point; right = middle - 1; }
                }
            }
            finally
            {
                var restored = await Git(CancellationToken.None, "checkout", "--quiet", "--detach", "--force", tip);
                if (!restored.Success) throw new IOException(restored.StdErr);
            }
        }

        var affected = attributed is null ? candidates.SelectMany(candidate => candidate.Paths)
            : candidates.First(candidate => candidate.Sha == attributed).Paths;
        var failingPaths = affected.Distinct(StringComparer.Ordinal).Take(128).ToArray();
        // A later first-parent integration may depend on the suspect through
        // another file. Without a proof of independence, use fix forward.
        var laterDepends = attributed is not null && commits.SkipWhile(commit => commit != attributed)
            .Skip(1).Any();
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
            AttributedTaskId: attributedTaskId);
    }

    private FileStream? ClaimCheckout()
    {
        try { return new FileStream(Path.Combine(_options.Checkout, ".post-merge-claim"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return null; }
    }

    private IReadOnlyDictionary<string, string?> ChildEnvironment(IReadOnlyDictionary<string, string> values)
    {
        var environment = values.ToDictionary(pair => pair.Key, pair => (string?)pair.Value);
        environment["POST_MERGE_TOKEN_FILE"] = null;
        environment["POST_MERGE_SERVER_URL"] = null;
        return environment;
    }

    private async Task<(int ExitCode, bool TimedOut, string Output)> RunCommand(string command,
        IReadOnlyDictionary<string, string> environment, CancellationToken ct)
    {
        _output.WriteLine($"post-merge {command}");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromHours(2));
        try
        {
            var result = await ProcessRunner.RunAsync("sh", ["-c", command], _options.Checkout,
                environment: ChildEnvironment(environment),
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
