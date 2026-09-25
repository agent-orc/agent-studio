using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Operations.Agent.Features.Execution;
using AgentStudio.Operations.Contracts;

namespace AgentStudio.Operations.Agent;

public sealed class OperationsAgentClient(HttpClient client, string agentId, string bootId, string spoolDirectory, TimeProvider clock)
{
    private const string Api = "api/operations/v1/";

    public async Task RegisterAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(spoolDirectory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(spoolDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var capability = new AgentCapability(agentId, bootId, "diagnostics", [], [],
            new Dictionary<string, int> { ["host.inspect"] = 1 }, 1, 1, 1);
        using var response = await client.PostAsJsonAsync(Api + "agents/register", capability, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task PollOnceAsync(CancellationToken ct)
    {
        // Replay persisted evidence before claiming any new work. A refusal leaves
        // the bounded spool intact for reconciliation instead of rerunning work.
        foreach (var file in Directory.EnumerateFiles(spoolDirectory, "*.json").Order(StringComparer.Ordinal).Take(128))
        {
            var report = JsonSerializer.Deserialize<AttemptReportRequest>(await File.ReadAllTextAsync(file, ct), OperationsProtocol.Json)
                         ?? throw new InvalidDataException("Agent result evidence is empty.");
            await ReportAsync(Path.GetFileNameWithoutExtension(file), report, ct);
            File.Delete(file);
        }
        using var response = await client.PostAsJsonAsync(Api + $"agents/{Uri.EscapeDataString(agentId)}/poll", new AgentPollRequest(bootId), ct);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent) return;
        var attempt = await response.Content.ReadFromJsonAsync<OperationAttempt>(ct);
        if (attempt is null) return;
        // Renew immediately before the executable boundary to observe cancellation
        // and recheck live Task Server authority, including after a lost poll reply.
        using var renewal = await client.PostAsJsonAsync(Api + $"attempts/{attempt.Id}/renew", new AttemptLeaseRequest(bootId, attempt.Fence), ct);
        renewal.EnsureSuccessStatusCode();
        attempt = await renewal.Content.ReadFromJsonAsync<OperationAttempt>(ct)
                  ?? throw new InvalidDataException("Agent lease response is empty.");
        var result = HostInspection.Execute(attempt, agentId, bootId, clock.GetUtcNow());
        var reportRequest = new AttemptReportRequest(bootId, attempt.Fence, result);
        if (!Guid.TryParseExact(attempt.Id, "N", out _)) throw new InvalidDataException("Agent attempt identity is invalid.");
        var target = Path.Combine(spoolDirectory, attempt.Id + ".json");
        if (Directory.EnumerateFiles(spoolDirectory).Take(128).Count() >= 128)
            throw new IOException("Agent evidence spool capacity reached.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(reportRequest, OperationsProtocol.Json);
        if (bytes.Length > 8192) throw new InvalidDataException("Agent evidence exceeds its output bound.");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Options = FileOptions.WriteThrough };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(target + ".tmp", options))
        {
            await stream.WriteAsync(bytes, ct);
            stream.Flush(flushToDisk: true);
        }
        File.Move(target + ".tmp", target);
        await ReportAsync(attempt.Id, reportRequest, ct);
        File.Delete(target);
    }

    private async Task ReportAsync(string id, AttemptReportRequest report, CancellationToken ct)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Agent spool identity is invalid.");
        using var response = await client.PostAsJsonAsync(Api + $"attempts/{id}/result", report, ct);
        response.EnsureSuccessStatusCode();
    }
}
