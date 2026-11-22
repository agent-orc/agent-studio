using System.Net.Http.Headers;
using System.Net.Http.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>One coding host owns project discovery and T3 polling. Project state
/// and exact-tip run fencing remain on Studio; checkout locks also cover legacy timers.</summary>
public sealed class PostMergeHostScheduler(HttpClient http, string stateDirectory,
    int maxSlots, IReadOnlyList<string> stateDirectories, TimeProvider clock,
    Func<CoverageHostLoad> probeHost, Action<string> log)
{
    private string? _executingProject;
    public const int DefaultWindowMinutes = 30;
    public const int NightlyRunsPerDay = 1;

    internal static HttpClient CreateClient(RunnerOptions options, string clientId)
    {
        var http = new HttpClient(TaskServerClient.CreateServiceHandler(options))
        {
            BaseAddress = new Uri((options.PostMergeStateUrl ?? options.ServerUrl).TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromMinutes(2),
        };
        http.DefaultRequestHeaders.TryAddWithoutValidation("X-Principal-Consumer-Id", options.RunnerId);
        http.DefaultRequestHeaders.TryAddWithoutValidation("X-Client-Id", clientId);
        if (string.IsNullOrWhiteSpace(options.AuthTokenFile) && !string.IsNullOrWhiteSpace(options.AuthToken))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.AuthToken);
        return http;
    }

    public static async Task RunForHostAsync(RunnerOptions options, string clientId, Action<string> log, CancellationToken ct)
    {
        using var http = CreateClient(options, clientId);
        // Include sibling role state roots, as the existing scheduled worker does.
        var parent = Path.GetDirectoryName(Path.GetFullPath(options.StateDir))!;
        IReadOnlyList<string> StateDirectories()
        {
            var configured = (Environment.GetEnvironmentVariable("COVERAGE_RUN_STATE_DIRS") ?? "")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var siblings = Directory.Exists(parent) ? Directory.GetDirectories(parent) : [];
            var serviceParent = Path.GetDirectoryName(parent);
            var serviceStates = serviceParent is not null && Directory.Exists(serviceParent)
                ? Directory.GetDirectories(serviceParent).Select(directory =>
                    Path.Combine(directory, Path.GetFileName(options.StateDir))).Where(Directory.Exists)
                : [];
            return configured.Concat(siblings).Concat(serviceStates).Append(options.StateDir).Distinct().ToArray();
        }
        var scheduler = new PostMergeHostScheduler(http, options.StateDir,
            Math.Max(1, options.HostMaxParallelism), StateDirectories(), TimeProvider.System,
            () => CoverageRunCommand.ProbeHost(StateDirectories()), log);
        var execution = Task.CompletedTask;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (!IsDrained(options))
                    {
                        var projects = await scheduler.ObserveOnceAsync(ct);
                        if (execution.IsCompleted)
                        {
                            await execution;
                            execution = scheduler.ExecuteAsync(projects, ct);
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception exception)
                {
                    if (execution.IsCompleted) execution = Task.CompletedTask;
                    log($"post-merge discovery failed: {exception.GetType().Name}: {exception.Message}");
                }
                try { await Task.Delay(TimeSpan.FromMinutes(1), ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            }
        }
        finally
        {
            try { await execution; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* Host shutdown owns the cancellation. */ }
        }
    }

    internal static bool IsDrained(RunnerOptions options)
        => options.HostAdapter
            ? HostRoleDrainControl.ReadRequest(options.StateDir, "coding") is not null
            : CodingDrainGuard.ReadRequest(options.StateDir) is not null;

    public async Task TickAsync(CancellationToken ct)
        => await ExecuteAsync(await ObserveOnceAsync(ct), ct);

    public async Task<PostMergeProjectTarget[]> ObserveOnceAsync(CancellationToken ct)
    {
        var projects = await http.GetFromJsonAsync<PostMergeProjectTarget[]>("api/runner/post-merge/projects", ct) ?? [];
        foreach (var project in projects) await Run(project, "window", true, ct);
        return projects;
    }

    private async Task ExecuteAsync(IReadOnlyList<PostMergeProjectTarget> projects, CancellationToken ct)
    {
        foreach (var project in projects) await Run(project, "window", false, ct);
        foreach (var project in projects)
        {
            var now = clock.GetUtcNow();
            if (now.TimeOfDay < new TimeSpan(2, 15, 0) || now.TimeOfDay >= new TimeSpan(6, 15, 0)) continue;
            try
            {
                var state = await http.GetFromJsonAsync<PostMergeProjectState>(
                    $"api/runner/post-merge/projects/{Uri.EscapeDataString(project.Project)}", ct);
                if (state is not null && state.Runs.Count(run => run.Kind == "nightly"
                    && run.StartedAtUtc.UtcDateTime.Date == now.UtcDateTime.Date
                    && run.Status is "green" or "red") < NightlyRunsPerDay)
                    await Run(project, "nightly", false, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception exception) { log($"post-merge nightly project={project.ProjectId}: {exception.Message}"); }
        }
    }

    private async Task Run(PostMergeProjectTarget project, string mode, bool observeOnly, CancellationToken ct)
    {
        try
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(project.ProjectId, "^PROJ-[0-9]{3,}$"))
                throw new IOException("Post-merge target has an invalid stable project ID.");
            var checkout = Path.Combine(stateDirectory, "post-merge", project.ProjectId);
            var options = new PostMergeRunOptions(project.Project, checkout, http.BaseAddress!.ToString(),
                project.Branch, "origin", "", mode, PostMergeRunPlan.DefaultMaxMerges,
                TimeSpan.FromMinutes(DefaultWindowMinutes), maxSlots, stateDirectories, false) { RepositoryUrl = project.RepositoryUrl, RecordSkips = false };
            using var output = observeOnly ? TextWriter.Null
                : TextWriter.Synchronized(new HostLogWriter(line => log($"post-merge project={project.ProjectId}: {line}")));
            CoverageHostLoad Admission()
            {
                var host = probeHost();
                // This scheduler executes one project at a time. Its queue is
                // occupied even when test children have not raised host load yet.
                return observeOnly && Volatile.Read(ref _executingProject) is { } active && active != project.ProjectId
                    ? host with { ActiveSlots = Math.Max(maxSlots, host.ActiveSlots) } : host;
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(observeOnly ? TimeSpan.FromMinutes(2)
                : mode == "nightly" ? TimeSpan.FromHours(12) : TimeSpan.FromHours(3));
            if (!observeOnly) Volatile.Write(ref _executingProject, project.ProjectId);
            try
            {
                await new PostMergeRunCommand(options, output, clock, http, Admission).ExecuteAsync(deadline.Token, observeOnly);
            }
            finally { if (!observeOnly) Volatile.Write(ref _executingProject, null); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception exception) { log($"post-merge project={project.ProjectId} mode={mode}: {exception.GetType().Name}: {exception.Message}"); }
    }

    private sealed class HostLogWriter(Action<string> writeLine) : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public override void WriteLine(string? value) => writeLine(value ?? string.Empty);
    }

}
