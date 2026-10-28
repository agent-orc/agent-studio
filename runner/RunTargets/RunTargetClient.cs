using System.Net.Http.Json;

namespace AgentRunner.RunTargets;

/// <summary>The control plane as the run-target role sees it.</summary>
public interface IRunTargetControlPlane
{
    Task<RunTargetDesiredState> GetDesiredStateAsync(CancellationToken ct);
    Task PostObservedStateAsync(RunTargetObservedReport report, CancellationToken ct);
    Task<bool> ReportJobAsync(RunTargetJobReport report, CancellationToken ct);
}

/// <summary>
/// Outbound-only protocol: the host calls these three routes on Studio and
/// opens no listener. Studio cannot reach the host; it answers polls.
/// </summary>
public sealed class RunTargetClient(HttpClient http, string hostId) : IRunTargetControlPlane
{
    public static readonly IReadOnlyList<string> RouteTemplates =
    [
        "GET api/deploy/run-targets/{hostId}/desired-state",
        "POST api/deploy/run-targets/{hostId}/observed-state",
        "POST api/deploy/jobs/{project}/{jobId}/report",
    ];

    private string HostPath => "api/deploy/run-targets/" + Uri.EscapeDataString(hostId);

    public async Task<RunTargetDesiredState> GetDesiredStateAsync(CancellationToken ct)
    {
        using var response = await http.GetAsync(HostPath + "/desired-state", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<RunTargetDesiredState>(ct)
            ?? throw new InvalidDataException("Studio returned an empty desired state.");
    }

    public async Task PostObservedStateAsync(RunTargetObservedReport report, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(HostPath + "/observed-state", report, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <returns>False when Studio no longer accepts the job's lease.</returns>
    public async Task<bool> ReportJobAsync(RunTargetJobReport report, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(
            $"api/deploy/jobs/{Uri.EscapeDataString(report.Project)}/{Uri.EscapeDataString(report.JobId)}/report",
            new { report.LeaseToken, report.Healthy, report.Evidence }, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Conflict) return false;
        response.EnsureSuccessStatusCode();
        return true;
    }
}
