using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

public sealed record CoverageRunOptions(
    string Checkout,
    string Branch,
    string Remote,
    string HistoryPath,
    IReadOnlyList<string> StateDirectories,
    double MaxLoadPerCore,
    int MaxActiveSlots,
    string? QualityStudioBaseUrl,
    string? QualityStudioRepositoryId,
    string? QualityStudioApiToken,
    string? QualityStudioClientId,
    bool DryRun,
    bool Force)
{
    public const string DefaultBranch = "develop";
    public const string DefaultHistoryPath = "/var/lib/agent-runner/coverage-runs.jsonl";
    public const double DefaultMaxLoadPerCore = 0.5;

    public bool QualityStudioConfigured =>
        !string.IsNullOrWhiteSpace(QualityStudioBaseUrl) && !string.IsNullOrWhiteSpace(QualityStudioRepositoryId);

    /// <summary>
    /// Command-line values win over the environment file the systemd unit
    /// reads (<c>/etc/agent-runner/coverage-run.env</c>).
    /// </summary>
    public static CoverageRunOptions Parse(IReadOnlyList<string> args, IReadOnlyDictionary<string, string?> environment)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var stateDirectories = new List<string>();
        bool dryRun = false, force = false;
        for (var index = 0; index < args.Count; index++)
        {
            var arg = args[index];
            switch (arg)
            {
                case "--dry-run": dryRun = true; continue;
                case "--force": force = true; continue;
                case "--checkout" or "--branch" or "--remote" or "--history" or "--state-dir"
                    or "--max-load-per-core" or "--max-active-slots":
                    if (index + 1 >= args.Count) throw new ArgumentException($"{arg} needs a value.");
                    var value = args[++index];
                    if (arg == "--state-dir") stateDirectories.Add(value);
                    else if (!values.TryAdd(arg, value)) throw new ArgumentException($"{arg} is given more than once.");
                    continue;
                default:
                    throw new ArgumentException($"coverage-run does not accept '{arg}'.");
            }
        }

        string? Env(string key) => environment.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
        string? Value(string flag, string key) => values.GetValueOrDefault(flag) ?? Env(key);

        var checkout = Value("--checkout", "COVERAGE_RUN_CHECKOUT")
                       ?? throw new ArgumentException("Set --checkout or COVERAGE_RUN_CHECKOUT to the coverage checkout registered in Quality Studio.");
        if (stateDirectories.Count == 0)
        {
            var configured = Env("COVERAGE_RUN_STATE_DIRS") ?? Env("RUNNER_STATE_DIR");
            if (configured is not null)
                stateDirectories.AddRange(configured.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        var maxLoad = Value("--max-load-per-core", "COVERAGE_RUN_MAX_LOAD_PER_CORE");
        var maxSlots = Value("--max-active-slots", "COVERAGE_RUN_MAX_ACTIVE_SLOTS");
        return new(
            Path.GetFullPath(checkout),
            Value("--branch", "COVERAGE_RUN_BRANCH") ?? DefaultBranch,
            Value("--remote", "COVERAGE_RUN_REMOTE") ?? "origin",
            Value("--history", "COVERAGE_RUN_HISTORY") ?? DefaultHistoryPath,
            stateDirectories,
            maxLoad is null ? DefaultMaxLoadPerCore : ParseNonNegative(maxLoad, "--max-load-per-core"),
            maxSlots is null ? 0 : (int)ParseNonNegative(maxSlots, "--max-active-slots"),
            Env("QUALITY_STUDIO_BASE_URL"),
            Env("QUALITY_STUDIO_REPO_ID"),
            Env("QUALITY_STUDIO_API_TOKEN"),
            Env("QUALITY_STUDIO_CLIENT_ID"),
            dryRun,
            force);
    }

    private static double ParseNonNegative(string value, string name)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0
            ? parsed
            : throw new ArgumentException($"{name} must be a non-negative number.");
}

public sealed record CoverageRunStepRecord(string Id, string Kind, int ExitCode, double DurationSeconds, bool TimedOut);

/// <summary>One line of the coverage run history and the <c>coverage/coverage-run.json</c> summary.</summary>
public sealed record CoverageRunRecord(
    string Status,
    string? Commit,
    string Branch,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    double DurationSeconds,
    string? Reason,
    IReadOnlyList<CoverageRunStepRecord> Steps,
    int Reports,
    string Scan)
{
    public const string Complete = "complete";

    public int FailedSuites => Steps.Count(step => step.Kind == CoverageRunPlanner.SuiteKind && step.ExitCode != 0);

    public string SummaryLine()
    {
        var suites = Steps.Count(step => step.Kind == CoverageRunPlanner.SuiteKind);
        var reason = Reason is null ? string.Empty : $" reason=\"{Reason}\"";
        return string.Create(CultureInfo.InvariantCulture,
            $"coverage-run status={Status} commit={Commit ?? "unknown"} branch={Branch} duration={DurationSeconds:0}s suites={suites} failedSuites={FailedSuites} reports={Reports} scan={Scan}{reason}");
    }
}

/// <summary>
/// <c>agent-host coverage-run [--dry-run] [--force]</c> (AGT-3074). Started by
/// <c>agent-coverage-run.timer</c> on a runner host: it yields to claimed work
/// and host load, detaches the dedicated coverage checkout at the integration
/// branch tip, runs prepare, build and the <c>release</c>-category suites one at
/// a time under <c>nice</c>, and asks Quality Studio's coverage sensor to ingest
/// the reports. The sensor tags the snapshot with the checkout's HEAD, which is
/// the measured commit because nothing moves HEAD between the run and the scan.
/// </summary>
public sealed class CoverageRunCommand
{
    public const string ReportPaths = "coverage/dotnet/**/*.cobertura.xml;coverage/frontend/lcov.info";
    private const string SummaryRelativePath = "coverage/coverage-run.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly CoverageRunOptions _options;
    private readonly TextWriter _output;
    private readonly TimeProvider _clock;
    private readonly Func<CoverageHostLoad> _probeHost;
    private readonly HttpMessageHandler? _qualityStudioHandler;

    public CoverageRunCommand(
        CoverageRunOptions options,
        TextWriter output,
        TimeProvider clock,
        Func<CoverageHostLoad>? probeHost = null,
        HttpMessageHandler? qualityStudioHandler = null)
    {
        _options = options;
        _output = output;
        _clock = clock;
        _probeHost = probeHost ?? (() => ProbeHost(options.StateDirectories));
        _qualityStudioHandler = qualityStudioHandler;
    }

    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        CoverageRunOptions options;
        try
        {
            var environment = System.Environment.GetEnvironmentVariables()
                .Cast<System.Collections.DictionaryEntry>()
                .ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value, StringComparer.Ordinal);
            options = CoverageRunOptions.Parse(args, environment);
        }
        catch (ArgumentException ex)
        {
            error.WriteLine($"error: {ex.Message}");
            error.WriteLine("usage: agent-host coverage-run [--dry-run] [--force] [--checkout <path>] [--branch <name>] "
                            + "[--remote <name>] [--history <file>] [--state-dir <dir>]... "
                            + "[--max-load-per-core <n>] [--max-active-slots <n>]");
            return 2;
        }
        return await new CoverageRunCommand(options, output, TimeProvider.System).ExecuteAsync(ct);
    }

    public async Task<int> ExecuteAsync(CancellationToken ct)
    {
        if (_options.DryRun) return await DryRunAsync(ct);

        var startedAt = _clock.GetUtcNow();
        var host = CoverageRunAdmission.ForHost(_probeHost(), _options.MaxLoadPerCore, _options.MaxActiveSlots);
        if (host.Verdict != CoverageRunVerdict.Run)
            return Finish(Record("skipped-busy", null, startedAt, host.Reason, [], 0, "not-run"), exitCode: 0);

        if (!Directory.Exists(Path.Combine(_options.Checkout, ".git")) && !File.Exists(Path.Combine(_options.Checkout, ".git")))
            return Finish(Record("checkout-missing", null, startedAt, $"{_options.Checkout} is not a git checkout", [], 0, "not-run"), 1);
        // The checkout is dedicated to this run. Tracked changes mean someone works in it; never discard them.
        var dirty = await GitAsync(ct, "status", "--porcelain", "--untracked-files=no");
        if (!dirty.Success || dirty.StdOut.Trim().Length > 0)
            return Finish(Record("checkout-dirty", null, startedAt, "the coverage checkout has tracked changes", [], 0, "not-run"), 1);

        var fetch = await GitAsync(ct, "fetch", "--quiet", _options.Remote, _options.Branch);
        var resolved = fetch.Success ? await GitAsync(ct, "rev-parse", "--verify", "FETCH_HEAD^{commit}") : fetch;
        if (!resolved.Success)
            return Finish(Record("fetch-failed", null, startedAt, FirstLine(resolved.StdErr), [], 0, "not-run"), 1);
        var commit = resolved.StdOut.Trim();

        var change = CoverageRunAdmission.ForCommit(commit, LastMeasuredCommit(), _options.Force);
        if (change.Verdict != CoverageRunVerdict.Run)
            return Finish(Record("skipped-unchanged", commit, startedAt, change.Reason, [], 0, "not-run"), 0);

        var checkout = await GitAsync(ct, "checkout", "--quiet", "--detach", "--force", commit);
        if (!checkout.Success)
            return Finish(Record("checkout-failed", commit, startedAt, FirstLine(checkout.StdErr), [], 0, "not-run"), 1);
        // Reports from an earlier commit must not be ingested under this one.
        var coverageRoot = Path.Combine(_options.Checkout, "coverage");
        if (Directory.Exists(coverageRoot)) Directory.Delete(coverageRoot, recursive: true);

        var read = ProjectDefinitionReader.ReadWorkspace(_options.Checkout);
        if (read.Definition is null)
            return Finish(Record("definition-invalid", commit, startedAt,
                string.Join("; ", read.Issues.Select(issue => $"{issue.Path}: {issue.Message}")), [], 0, "not-run"), 1);
        var plan = CoverageRunPlanner.Build(read.Definition);
        if (plan.Suites.Count == 0)
            return Finish(Record("no-release-suites", commit, startedAt,
                $"no testSuites with category '{CoverageRunPlanner.ReleaseCategory}' in {ProjectPreparationPaths.Definition}", [], 0, "not-run"), 1);

        _output.WriteLine($"coverage-run start commit={commit} branch={_options.Branch} host={host.Reason} change={change.Reason}");
        var steps = new List<CoverageRunStepRecord>();
        foreach (var step in plan.Steps)
        {
            var result = await RunStepAsync(step, plan.Environment, ct);
            steps.Add(result);
            // A red suite still writes coverage; a red prepare or build leaves nothing worth measuring.
            if (step.Kind != CoverageRunPlanner.SuiteKind && result.ExitCode != 0)
                return Finish(Record("setup-failed", commit, startedAt, $"{step.Id} exited {result.ExitCode}", steps, 0, "not-run"), 1);
        }

        var reports = CountReports(_options.Checkout);
        var scan = reports == 0 ? "no-reports" : await ScanAsync(ct);
        var status = scan is "ingested" or "not-configured" ? CoverageRunRecord.Complete : scan;
        var record = Record(status, commit, startedAt, null, steps, reports, scan);
        await File.WriteAllTextAsync(Path.Combine(_options.Checkout, SummaryRelativePath),
            JsonSerializer.Serialize(record, Json), ct);
        return Finish(record, status == CoverageRunRecord.Complete && record.FailedSuites == 0 ? 0 : 1);
    }

    private async Task<int> DryRunAsync(CancellationToken ct)
    {
        _output.WriteLine($"coverage-run dry-run checkout={_options.Checkout} branch={_options.Branch} remote={_options.Remote}");
        var host = CoverageRunAdmission.ForHost(_probeHost(), _options.MaxLoadPerCore, _options.MaxActiveSlots);
        _output.WriteLine($"host: {(host.Verdict == CoverageRunVerdict.Run ? "would run" : "would skip")} ({host.Reason})");
        var known = Directory.Exists(_options.Checkout)
            ? await GitAsync(ct, "rev-parse", "--verify", "--quiet", $"refs/remotes/{_options.Remote}/{_options.Branch}^{{commit}}")
            : null;
        var commit = known is { Success: true } ? known.StdOut.Trim() : "<fetched tip>";
        _output.WriteLine($"last measured commit: {LastMeasuredCommit() ?? "none"} (history {_options.HistoryPath})");
        _output.WriteLine($"git -C {_options.Checkout} fetch --quiet {_options.Remote} {_options.Branch}");
        _output.WriteLine($"git -C {_options.Checkout} checkout --quiet --detach --force {commit}");
        _output.WriteLine($"rm -rf {Path.Combine(_options.Checkout, "coverage")}");

        var read = ProjectDefinitionReader.ReadWorkspace(_options.Checkout);
        if (read.Definition is null)
        {
            _output.WriteLine($"error: {ProjectPreparationPaths.Definition} unreadable in the current checkout: "
                              + string.Join("; ", read.Issues.Select(issue => issue.Message)));
            return 1;
        }
        var plan = CoverageRunPlanner.Build(read.Definition);
        _output.WriteLine($"# steps from {ProjectPreparationPaths.Definition} of the current checkout; the run reads them after checkout");
        foreach (var step in plan.Steps)
            _output.WriteLine($"[{step.Id}] {step.Display}  # timeout {step.Timeout.TotalMinutes:0} min");
        _output.WriteLine(_options.QualityStudioConfigured
            ? $"POST {ScanUri()}  # coverage reports: {ReportPaths}"
            : "# Quality Studio scan not configured: set QUALITY_STUDIO_BASE_URL and QUALITY_STUDIO_REPO_ID");
        return plan.Suites.Count == 0 ? 1 : 0;
    }

    private async Task<CoverageRunStepRecord> RunStepAsync(
        CoverageRunStep step,
        IReadOnlyDictionary<string, string> definitionEnvironment,
        CancellationToken ct)
    {
        _output.WriteLine($"[{step.Id}] {step.Display}");
        var environment = definitionEnvironment.ToDictionary(pair => pair.Key, pair => (string?)pair.Value, StringComparer.Ordinal);
        // .agent-studio/prepare requires the browser cache path; Playwright's own default on Linux.
        if (string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH"))
            && !environment.ContainsKey("PLAYWRIGHT_BROWSERS_PATH"))
            environment["PLAYWRIGHT_BROWSERS_PATH"] = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".cache", "ms-playwright");
        // Test code from the measured commit never sees the Quality Studio credentials.
        environment["QUALITY_STUDIO_API_TOKEN"] = null;
        environment["QUALITY_STUDIO_CLIENT_ID"] = null;
        var started = _clock.GetTimestamp();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(step.Timeout);
        int exitCode;
        var timedOut = false;
        try
        {
            var arguments = step.Arguments;
            var result = await ProcessRunner.RunAsync(arguments[0], [.. arguments.Skip(1)], _options.Checkout,
                onStdOut: line => _output.WriteLine($"[{step.Id}] {line}"),
                onStdErr: line => _output.WriteLine($"[{step.Id}] {line}"),
                environment: environment,
                isolateProcessGroup: OperatingSystem.IsLinux(),
                ct: timeout.Token);
            exitCode = result.ExitCode;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            exitCode = 124;
            timedOut = true;
        }
        var seconds = Math.Round(_clock.GetElapsedTime(started).TotalSeconds, 1);
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[{step.Id}] exit={exitCode} duration={seconds:0.0}s{(timedOut ? " timed-out" : string.Empty)}"));
        return new(step.Id, step.Kind, exitCode, seconds, timedOut);
    }

    private async Task<string> ScanAsync(CancellationToken ct)
    {
        if (!_options.QualityStudioConfigured) return "not-configured";
        try
        {
            using var http = _qualityStudioHandler is null
                ? new HttpClient()
                : new HttpClient(_qualityStudioHandler, disposeHandler: false);
            http.Timeout = TimeSpan.FromMinutes(5);
            using var request = new HttpRequestMessage(HttpMethod.Post, ScanUri());
            if (!string.IsNullOrWhiteSpace(_options.QualityStudioApiToken))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.QualityStudioApiToken);
            if (!string.IsNullOrWhiteSpace(_options.QualityStudioClientId))
                request.Headers.Add("X-Client-Id", _options.QualityStudioClientId);
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _output.WriteLine($"coverage-run scan: Quality Studio returned {(int)response.StatusCode}");
                return "scan-failed";
            }
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var available = body.RootElement.TryGetProperty("available", out var flag) && flag.ValueKind == JsonValueKind.True;
            var reason = body.RootElement.TryGetProperty("unavailableReason", out var text) && text.ValueKind == JsonValueKind.String
                ? text.GetString()
                : null;
            if (reason is not null) _output.WriteLine($"coverage-run scan: {reason}");
            return available && reason is null ? "ingested" : "scan-failed";
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _output.WriteLine($"coverage-run scan: {ex.GetType().Name}: {ex.Message}");
            return "scan-failed";
        }
    }

    private Uri ScanUri()
    {
        var baseUrl = _options.QualityStudioBaseUrl!;
        var root = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
        return new Uri(root, $"api/repos/{Uri.EscapeDataString(_options.QualityStudioRepositoryId!)}/sensors/coverage/scan");
    }

    private CoverageRunRecord Record(
        string status, string? commit, DateTimeOffset startedAt, string? reason,
        IReadOnlyList<CoverageRunStepRecord> steps, int reports, string scan)
    {
        var finishedAt = _clock.GetUtcNow();
        return new(status, commit, _options.Branch, startedAt, finishedAt,
            Math.Round((finishedAt - startedAt).TotalSeconds, 1), reason, steps, reports, scan);
    }

    private int Finish(CoverageRunRecord record, int exitCode)
    {
        _output.WriteLine(record.SummaryLine());
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(_options.HistoryPath));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.AppendAllText(_options.HistoryPath, JsonSerializer.Serialize(record, Json) + "\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _output.WriteLine($"coverage-run history not written to {_options.HistoryPath}: {ex.Message}");
        }
        return exitCode;
    }

    /// <summary>The commit of the newest completed run, or <c>null</c> without one.</summary>
    public string? LastMeasuredCommit()
    {
        if (!File.Exists(_options.HistoryPath)) return null;
        string? last = null;
        foreach (var line in File.ReadLines(_options.HistoryPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var record = JsonSerializer.Deserialize<CoverageRunRecord>(line, Json);
                if (record is { Status: CoverageRunRecord.Complete, Commit: { Length: > 0 } commit }) last = commit;
            }
            catch (JsonException)
            {
                // A torn line from an interrupted append is not a measurement.
            }
        }
        return last;
    }

    public static int CountReports(string checkout)
    {
        var dotnet = Path.Combine(checkout, "coverage", "dotnet");
        var cobertura = Directory.Exists(dotnet)
            ? Directory.EnumerateFiles(dotnet, "*.cobertura.xml", SearchOption.AllDirectories).Count()
            : 0;
        return cobertura + (File.Exists(Path.Combine(checkout, "coverage", "frontend", "lcov.info")) ? 1 : 0);
    }

    /// <summary>
    /// Active slots are the persisted coding (<c>*.slot.json</c>), review
    /// (<c>reviews/*.review-slot.json</c>) and gate (<c>gate-claim.json</c>)
    /// claims under each runner state directory.
    /// </summary>
    public static CoverageHostLoad ProbeHost(IReadOnlyList<string> stateDirectories)
    {
        var slots = 0;
        foreach (var directory in stateDirectories.Where(Directory.Exists))
        {
            try
            {
                slots += Directory.EnumerateFiles(directory, "*.slot.json").Count();
                var reviews = Path.Combine(directory, "reviews");
                if (Directory.Exists(reviews)) slots += Directory.EnumerateFiles(reviews, "*.review-slot.json").Count();
                if (File.Exists(Path.Combine(directory, "gate-claim.json"))) slots++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A directory the run cannot read cannot prove the host idle.
                slots++;
            }
        }
        return new(ReadLoadPerCore(), slots);
    }

    private static double? ReadLoadPerCore()
    {
        try
        {
            if (!OperatingSystem.IsLinux()) return null;
            var first = File.ReadAllText("/proc/loadavg").Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            // The load average counts the whole host, so divide by the host's online CPUs:
            // Environment.ProcessorCount follows this process's cgroup CPU quota instead.
            var cores = OnlineCpuCount(File.ReadAllText("/sys/devices/system/cpu/online")) ?? System.Environment.ProcessorCount;
            return cores <= 0 ? null : double.Parse(first, CultureInfo.InvariantCulture) / cores;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>Counts a kernel CPU list such as <c>0-11</c> or <c>0-3,6,8-9</c>.</summary>
    public static int? OnlineCpuCount(string cpuList)
    {
        var count = 0;
        foreach (var range in cpuList.Trim().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bounds = range.Split('-');
            if (!int.TryParse(bounds[0], NumberStyles.None, CultureInfo.InvariantCulture, out var low)) return null;
            var high = low;
            if (bounds.Length == 2 && !int.TryParse(bounds[1], NumberStyles.None, CultureInfo.InvariantCulture, out high)) return null;
            if (bounds.Length > 2 || high < low) return null;
            count += high - low + 1;
        }
        return count == 0 ? null : count;
    }

    private Task<ProcessResult> GitAsync(CancellationToken ct, params string[] arguments)
        => ProcessRunner.RunAsync("git", ["-C", _options.Checkout, .. arguments], ct: ct);

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return string.IsNullOrEmpty(line) ? "git failed without output" : line;
    }
}
