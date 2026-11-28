using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>Pulls project-scoped custody before observing or running an after-merge window.</summary>
public sealed class PostMergeHostScheduler(HttpClient http, string stateDirectory,
    Func<int> maxSlots, IReadOnlyList<string> stateDirectories, TimeProvider clock,
    Func<CoverageHostLoad> probeHost, Action<string> log, RunnerOptions? hostOptions = null)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);
    private readonly Dictionary<PostMergeProjectTarget, DateTimeOffset> _deniedRepositories = [];
    internal static readonly IReadOnlyDictionary<string, string?> GitEnvironment = new Dictionary<string, string?>() { ["GIT_TERMINAL_PROMPT"] = "0" };

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

    public static async Task RunForHostAsync(RunnerOptions options, TaskServerClient client,
        Func<int> occupiedSlots, Action<string> log, CancellationToken ct)
    {
        using var http = CreateClient(options, client.ClientId);
        http.DefaultRequestHeaders.Add(PostMergeClaimProtocol.InstanceHeader, client.RunnerInstanceId);
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
            () => client.HostMaxParallelism, StateDirectories(), TimeProvider.System, () =>
            {
                var load = CoverageRunCommand.ProbeHost(StateDirectories());
                return load with { ActiveSlots = Math.Max(occupiedSlots(), load.ActiveSlots) };
            }, log, options);
        while (!ct.IsCancellationRequested && !IsDrained(options))
        {
            try
            {
                await scheduler.TickAsync(ct);
                await Task.Delay(PollInterval, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
            {
                log($"post-merge discovery failed: {exception.GetType().Name}: {exception.Message}");
                try { await Task.Delay(PollInterval, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            }
        }
    }

    internal static bool IsDrained(RunnerOptions options)
        => options.HostAdapter
            ? HostRoleDrainControl.ReadRequest(options.StateDir, "coding") is not null
            : CodingDrainGuard.ReadRequest(options.StateDir) is not null;

    public async Task TickAsync(CancellationToken ct)
    {
        if (hostOptions is { } options && (!options.PostMergeEnabled || IsDrained(options))) return;
        var projects = await http.GetFromJsonAsync<PostMergeProjectTarget[]>("api/runner/post-merge/projects", ct) ?? [];
        foreach (var project in projects)
        {
            if (hostOptions is { } host && IsDrained(host)) break;
            try { await TryRunAsync(project, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                log($"post-merge project={project.ProjectId}: {exception.GetType().Name}: {exception.Message}");
            }
        }
    }

    private async Task TryRunAsync(PostMergeProjectTarget project, CancellationToken ct)
    {
        var load = probeHost();
        if (!System.Text.RegularExpressions.Regex.IsMatch(project.ProjectId, "^PROJ-[0-9]{3,}$"))
            throw new IOException("Post-merge target has an invalid stable project ID.");
        var tip = await ProbeRepositoryAsync(project, ct);
        if (tip is null || hostOptions is { } host && IsDrained(host)) return;

        var checkout = Path.Combine(stateDirectory, "post-merge", project.ProjectId);
        using var output = TextWriter.Synchronized(new HostLogWriter(line => log($"post-merge project={project.ProjectId}: {line}")));
        PostMergeRunCommand Command(string mode, TextWriter writer)
            => new(new PostMergeRunOptions(project.Project, checkout, http.BaseAddress!.ToString(),
                project.Branch, "origin", "", mode, PostMergeRunPlan.DefaultMaxMerges,
                TimeSpan.FromMinutes(DefaultWindowMinutes), maxSlots(), stateDirectories, false)
            {
                RepositoryUrl = project.RepositoryUrl, RecordSkips = false, UseGateCapacity = true,
                StateProjectId = project.ProjectId,
            }, writer, clock, http, probeHost, hostOptions);

        var worker = Command("window", output);
        using var response = await http.PostAsJsonAsync(StateUrl(project.ProjectId) + "/claim",
            new PostMergeClaimRequest(project.RepositoryUrl, project.Branch, tip, load.ActiveSlots,
                load.LoadPerCore, worker.RecoveryOwner()), ct);
        if (response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound) return;
        response.EnsureSuccessStatusCode();
        var grant = await response.Content.ReadFromJsonAsync<PostMergeGrant>(ct)
            ?? throw new IOException("Post-merge claim response was empty.");

        http.DefaultRequestHeaders.Add(PostMergeClaimProtocol.GrantHeader, grant.Id);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var renew = RenewAsync(project.ProjectId, lifetime);
        try
        {
            if (hostOptions is not null && IsDrained(hostOptions)) return;
            await ExecuteAsync(worker, "window", lifetime.Token);
            var now = clock.GetUtcNow();
            if (hostOptions is not null && IsDrained(hostOptions)
                || now.TimeOfDay < new TimeSpan(2, 15, 0) || now.TimeOfDay >= new TimeSpan(6, 15, 0)) return;
            var state = await http.GetFromJsonAsync<PostMergeProjectState>(StateUrl(project.ProjectId), lifetime.Token);
            if (state is not null && state.Runs.Count(run => run.Kind == "nightly"
                && run.StartedAtUtc.UtcDateTime.Date == now.UtcDateTime.Date
                && run.Status is "green" or "red") < NightlyRunsPerDay)
                await ExecuteAsync(Command("nightly", output), "nightly", lifetime.Token);
        }
        finally
        {
            lifetime.Cancel();
            try { await renew; }
            finally
            {
                try
                {
                    using var releaseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    using var released = await http.PostAsync(StateUrl(project.ProjectId) + "/grant?release=true", null, releaseTimeout.Token);
                    released.EnsureSuccessStatusCode();
                }
                catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
                { log($"post-merge project={project.ProjectId}: grant release failed, waiting for expiry"); }
                finally { http.DefaultRequestHeaders.Remove(PostMergeClaimProtocol.GrantHeader); }
            }
        }

        async Task ExecuteAsync(PostMergeRunCommand command, string mode, CancellationToken token)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(mode == "nightly" ? TimeSpan.FromHours(12) : TimeSpan.FromHours(3));
            var observation = ObserveActiveAsync(deadline.Token);
            try { await command.ExecuteAsync(deadline.Token, placementOnly: grant.PlacementOnly); }
            finally
            {
                deadline.Cancel();
                await observation;
            }
        }

        async Task ObserveActiveAsync(CancellationToken token)
        {
            try
            {
                while (true)
                {
                    await Task.Delay(PollInterval, clock, token);
                    await Command("window", TextWriter.Null).ExecuteAsync(token, observeOnly: true);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception exception)
            {
                log($"post-merge project={project.ProjectId}: active window observation failed: {exception.GetType().Name}");
            }
        }
    }

    private static string StateUrl(string projectId) => $"api/runner/post-merge/projects/{Uri.EscapeDataString(projectId)}";

    private async Task RenewAsync(string projectId, CancellationTokenSource lifetime)
    {
        try
        {
            while (true)
            {
                await Task.Delay(PollInterval, clock, lifetime.Token);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(20));
                using var response = await http.PostAsync(StateUrl(projectId) + "/grant", null, deadline.Token);
                response.EnsureSuccessStatusCode();
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch
        {
            lifetime.Cancel();
            throw;
        }
    }

    private async Task<string?> ProbeRepositoryAsync(PostMergeProjectTarget project, CancellationToken ct)
    {
        var knownDenied = _deniedRepositories.TryGetValue(project, out var retryAt);
        if (knownDenied && clock.GetUtcNow() < retryAt) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var probe = await ProcessRunner.RunAsync("git",
                ["ls-remote", "--exit-code", "--refs", "--", project.RepositoryUrl, "refs/heads/" + project.Branch],
                environment: GitEnvironment, ct: timeout.Token);
            var tip = probe.StdOut.Split('\t', 2)[0];
            if (probe.Success && tip.Length == 40 && tip.All(Uri.IsHexDigit))
            {
                _deniedRepositories.Remove(project);
                return tip;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        _deniedRepositories[project] = clock.GetUtcNow().AddMinutes(5);
        if (!knownDenied) log($"post-merge project={project.ProjectId}: repository access unavailable for the integration branch");
        return null;
    }

    private sealed class HostLogWriter(Action<string> writeLine) : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public override void WriteLine(string? value) => writeLine(value ?? string.Empty);
    }
}
