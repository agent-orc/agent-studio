using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentRunner.RunTargets;

namespace AgentRunner;

public sealed record DeployHostContainerTarget(string Project, string EnvironmentId, string Container, string Artifact);

/// <summary>Reads Docker state and the last crash lines between deploy jobs, including preexisting containers.</summary>
public static partial class DeployHostContainerObserver
{
    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9_.-]{0,254}\z")]
    private static partial Regex ContainerName();

    public static async Task<int> ObserveAsync(HttpClient client, string hostId, CancellationToken ct,
        Func<string, IReadOnlyList<string>, CancellationToken, Task<ProcessResult>>? command = null)
    {
        using var targetsResponse = await client.GetAsync($"api/deploy/hosts/{Uri.EscapeDataString(hostId)}/containers", ct);
        if (!targetsResponse.IsSuccessStatusCode) return 0;
        var targets = await targetsResponse.Content.ReadFromJsonAsync<DeployHostContainerTarget[]>(ct) ?? [];
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
        foreach (var group in targets.Where(target => ContainerName().IsMatch(target.Container))
                     .GroupBy(target => (target.Project, target.EnvironmentId)))
        {
            var restarts = 0;
            var state = "running";
            var output = new List<string>();
            foreach (var target in group)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                var inspected = await Docker(["container", "inspect", "--format", "{{json .}}", target.Container], timeout.Token);
                if (!inspected.Success)
                {
                    state = "failed";
                    continue;
                }
                JsonDocument parsed;
                try { parsed = JsonDocument.Parse(inspected.StdOut); }
                catch (JsonException) { state = "failed"; continue; }
                using var document = parsed;
                var root = document.RootElement;
                var status = root.GetProperty("State").GetProperty("Status").GetString();
                var count = root.GetProperty("RestartCount").GetInt32();
                restarts += Math.Max(0, count);
                var started = root.GetProperty("State").GetProperty("StartedAt").GetString();
                var recentStart = DateTimeOffset.TryParse(started, out var at) && now - at.UtcDateTime < TimeSpan.FromMinutes(1);
                var crashLoop = count >= 3 && (status is "restarting" or "exited" || recentStart);
                if (crashLoop) state = "crash-loop";
                else if (status != "running" && state != "crash-loop") state = "unhealthy";
                if (count == 0 && status == "running") continue;
                var logs = await Docker(["logs", "--tail", "20", target.Container], timeout.Token);
                if (!logs.Success) continue;
                var environment = root.GetProperty("Config").GetProperty("Env").EnumerateArray()
                    .Select(item => item.GetString() ?? "")
                    .Select(item => item[(item.IndexOf('=') + 1)..])
                    .Where(value => value.Length >= 4);
                var known = environment.ToArray();
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
        using var report = await client.PostAsJsonAsync($"api/deploy/hosts/{Uri.EscapeDataString(hostId)}/observed-state",
            new RunTargetObservedReport(observations), ct);
        report.EnsureSuccessStatusCode();
        return observations.Count;
    }
}
