using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentRunner.RunTargets;

namespace AgentRunner;

public sealed record DeployHostContainerTarget(string Project, string EnvironmentId, string Container, string Artifact);

/// <summary>
/// Reads Docker state and the last crash lines between deploy jobs, including preexisting containers.
/// Observation is best effort: an unreadable target list, unexpected Docker JSON or a refused report
/// is written to stderr and never fails the job-less poll. Only cancellation of the poll propagates.
/// </summary>
public static partial class DeployHostContainerObserver
{
    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9_.-]{0,254}\z")]
    private static partial Regex ContainerName();

    private sealed record Inspection(string Status, int RestartCount, string? StartedAt, string[] EnvironmentValues);

    public static async Task<int> ObserveAsync(HttpClient client, string hostId, CancellationToken ct,
        Func<string, IReadOnlyList<string>, CancellationToken, Task<ProcessResult>>? command = null)
    {
        var host = Uri.EscapeDataString(hostId);
        DeployHostContainerTarget[] targets;
        try
        {
            using var targetsResponse = await client.GetAsync($"api/deploy/hosts/{host}/containers", ct);
            if (!targetsResponse.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"Container observation for host '{hostId}' skipped: target list returned {(int)targetsResponse.StatusCode}.");
                return 0;
            }
            targets = await targetsResponse.Content.ReadFromJsonAsync<DeployHostContainerTarget[]>(ct) ?? [];
        }
        catch (Exception ex) when (IsTransportFailure(ex, ct))
        {
            Console.Error.WriteLine($"Container observation for host '{hostId}' skipped: target list unreadable ({ex.GetType().Name}).");
            return 0;
        }

        var now = DateTime.UtcNow;
        var run = command ?? ((string file, IReadOnlyList<string> args, CancellationToken token)
            => ProcessRunner.RunAsync(file, args, ct: token));
        async Task<ProcessResult> Docker(IReadOnlyList<string> args, CancellationToken token)
        {
            try { return await run("docker", args, token); }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception
                or InvalidOperationException || ex is OperationCanceledException && !ct.IsCancellationRequested)
            { return new ProcessResult(1, "", ""); }
        }
        var observations = new List<RunTargetObservation>();
        foreach (var group in targets.Where(IsObservable).GroupBy(target => (target.Project, target.EnvironmentId)))
        {
            var restarts = 0;
            var state = "running";
            var output = new List<string>();
            foreach (var target in group)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                var inspected = await Docker(["container", "inspect", "--format", "{{json .}}", target.Container], timeout.Token);
                var inspection = inspected.Success ? ReadInspection(inspected.StdOut) : null;
                if (inspection is null)
                {
                    state = "failed";
                    continue;
                }
                var (status, count, started, known) = inspection;
                restarts += count;
                var recentStart = DateTimeOffset.TryParse(started, out var at) && now - at.UtcDateTime < TimeSpan.FromMinutes(1);
                var crashLoop = count >= 3 && (status is "restarting" or "exited" || recentStart);
                if (crashLoop) state = "crash-loop";
                else if (status != "running" && state != "crash-loop") state = "unhealthy";
                if (count == 0 && status == "running") continue;
                var logs = await Docker(["logs", "--tail", "20", target.Container], timeout.Token);
                if (!logs.Success) continue;
                var stdout = RunTargetCrashOutput.Sanitize(logs.StdOut, known);
                var stderr = RunTargetCrashOutput.Sanitize(logs.StdErr, known);
                output.AddRange(stdout.TakeLast(stderr.Count == 0 ? 20 : 10));
                output.AddRange(stderr.TakeLast(stdout.Count == 0 ? 20 : 10));
            }
            var latest = group.Last();
            observations.Add(new(group.Key.Project, group.Key.EnvironmentId, state, latest.Artifact, null,
                restarts, now, [], output.TakeLast(RunTargetCrashOutput.MaxLines).ToArray()));
        }
        if (observations.Count == 0) return 0;
        try
        {
            using var report = await client.PostAsJsonAsync($"api/deploy/hosts/{host}/observed-state",
                new RunTargetObservedReport(observations), ct);
            if (!report.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"Container observation for host '{hostId}' was not accepted: {(int)report.StatusCode}.");
                return 0;
            }
        }
        catch (Exception ex) when (IsTransportFailure(ex, ct))
        {
            Console.Error.WriteLine($"Container observation for host '{hostId}' could not be sent ({ex.GetType().Name}).");
            return 0;
        }
        return observations.Count;
    }

    private static bool IsObservable(DeployHostContainerTarget? target)
        => target is { Project: not null, EnvironmentId: not null, Artifact: not null, Container: not null }
           && ContainerName().IsMatch(target.Container);

    private static bool IsTransportFailure(Exception ex, CancellationToken ct)
        => ex is HttpRequestException or JsonException or NotSupportedException
           || ex is OperationCanceledException && !ct.IsCancellationRequested;

    /// <summary>Reads the fields the observer needs from <c>docker container inspect</c>; null for any other shape.</summary>
    private static Inspection? ReadInspection(string json)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException) { return null; }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("State", out var state) || state.ValueKind != JsonValueKind.Object
                || !state.TryGetProperty("Status", out var status) || status.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("RestartCount", out var restartCount)
                || restartCount.ValueKind != JsonValueKind.Number
                || !restartCount.TryGetInt32(out var count))
                return null;
            var started = state.TryGetProperty("StartedAt", out var startedAt) && startedAt.ValueKind == JsonValueKind.String
                ? startedAt.GetString()
                : null;
            string[] environment = [];
            if (root.TryGetProperty("Config", out var config) && config.ValueKind == JsonValueKind.Object
                && config.TryGetProperty("Env", out var env) && env.ValueKind == JsonValueKind.Array)
                environment = env.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString() ?? "")
                    .Select(item => item[(item.IndexOf('=') + 1)..])
                    .Where(value => value.Length >= 4)
                    .ToArray();
            return new Inspection(status.GetString() ?? "", Math.Max(0, count), started, environment);
        }
    }
}
