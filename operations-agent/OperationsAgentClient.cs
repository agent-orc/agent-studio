using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Operations.Agent.Features.Execution;
using AgentStudio.Operations.Contracts;

namespace AgentStudio.Operations.Agent;

public sealed class OperationsAgentClient(HttpClient client, string agentId, string bootId, string spoolDirectory, TimeProvider clock,
    RestartableServices? services = null, Func<ProcessStartInfo, TimeSpan, CancellationToken, Task<ToolRun>>? run = null)
{
    private readonly RestartableServices _services = services ?? RestartableServices.Empty;

    private const string Api = "api/operations/v1/";

    public async Task RegisterAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(spoolDirectory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(spoolDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        // Results from an earlier process generation must be reported before
        // the new boot asks to replace that generation's registration.
        await ReplaySpoolAsync(ct);
        var operations = new Dictionary<string, int> { [OperationCatalogue.HostInspect.Id] = OperationCatalogue.HostInspect.Version };
        // AGT-3207: only an agent with a local allowlist offers restarts.
        if (_services.Services.Count > 0)
            operations[OperationCatalogue.ServiceRestart.Id] = OperationCatalogue.ServiceRestart.Version;
        var capability = new AgentCapability(agentId, bootId, "diagnostics", [], [], operations, 1, 1, 1);
        using var response = await client.PostAsJsonAsync(Api + "agents/register", capability, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task PollOnceAsync(CancellationToken ct)
    {
        await ReplaySpoolAsync(ct);
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
        if (!Guid.TryParseExact(attempt.Id, "N", out _)) throw new InvalidDataException("Agent attempt identity is invalid.");
        var restartMarker = Path.Combine(spoolDirectory, attempt.Id + ".restart-started");
        if (attempt.Command.OperationId == OperationCatalogue.ServiceRestart.Id)
        {
            if (File.Exists(restartMarker))
                throw new IOException("Restart attempt has an unresolved durable execution marker.");
            var started = clock.GetUtcNow();
            var uncertain = new AttemptReportRequest(bootId, attempt.Fence,
                new OperationResult("failed", attempt.Command.InputDigest, "restart-outcome-unknown",
                    JsonSerializer.SerializeToElement(new { }), [], true, started, started));
            await WriteDurableAsync(restartMarker, JsonSerializer.SerializeToUtf8Bytes(uncertain, OperationsProtocol.Json), ct);
        }
        var result = attempt.Command.OperationId == OperationCatalogue.ServiceRestart.Id
            ? await ServiceRestart.ExecuteAsync(attempt, agentId, bootId, _services, run ?? ServiceRestart.RunAsync, clock, ct)
            : HostInspection.Execute(attempt, agentId, bootId, clock.GetUtcNow());
        var reportRequest = new AttemptReportRequest(bootId, attempt.Fence, result);
        var target = Path.Combine(spoolDirectory, attempt.Id + ".json");
        if (Directory.EnumerateFiles(spoolDirectory).Take(128).Count() >= 128)
            throw new IOException("Agent evidence spool capacity reached.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(reportRequest, OperationsProtocol.Json);
        if (bytes.Length > 8192) throw new InvalidDataException("Agent evidence exceeds its output bound.");
        await WriteDurableAsync(target + ".tmp", bytes, ct);
        File.Move(target + ".tmp", target);
        await ReportAsync(attempt.Id, reportRequest, ct);
        File.Delete(target);
        if (File.Exists(restartMarker)) File.Delete(restartMarker);
    }

    private static async Task WriteDurableAsync(string path, byte[] bytes, CancellationToken ct)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Options = FileOptions.WriteThrough };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var stream = new FileStream(path, options);
        await stream.WriteAsync(bytes, ct);
        stream.Flush(flushToDisk: true);
    }

    private async Task ReplaySpoolAsync(CancellationToken ct)
    {
        await RecoverInterruptedWritesAsync(ct);
        // Replay persisted evidence before claiming any new work. A refusal leaves
        // the bounded spool intact for reconciliation instead of rerunning work.
        foreach (var file in Directory.EnumerateFiles(spoolDirectory, "*.json").Order(StringComparer.Ordinal).Take(128))
        {
            var report = JsonSerializer.Deserialize<AttemptReportRequest>(await File.ReadAllTextAsync(file, ct), OperationsProtocol.Json)
                         ?? throw new InvalidDataException("Agent result evidence is empty.");
            await ReportAsync(Path.GetFileNameWithoutExtension(file), report, ct);
            File.Delete(file);
            var marker = Path.ChangeExtension(file, ".restart-started");
            if (File.Exists(marker)) File.Delete(marker);
        }
        // A side effect may have completed before its result could be spooled.
        // Report uncertainty and never recreate the service on redispatch.
        foreach (var marker in Directory.EnumerateFiles(spoolDirectory, "*.restart-started").Order(StringComparer.Ordinal).Take(128))
        {
            var report = JsonSerializer.Deserialize<AttemptReportRequest>(await File.ReadAllTextAsync(marker, ct), OperationsProtocol.Json)
                         ?? throw new InvalidDataException("Restart execution marker is empty.");
            await ReportAsync(Path.GetFileNameWithoutExtension(marker), report, ct);
            File.Delete(marker);
        }
    }

    private async Task RecoverInterruptedWritesAsync(CancellationToken ct)
    {
        // A crash can leave a complete report behind its temporary name. Promote
        // it before replay so the accepted result is not executed a second time.
        foreach (var temporary in Directory.EnumerateFiles(spoolDirectory, "*.json.tmp").Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.ChangeExtension(temporary, null);
            if (File.Exists(target))
            {
                File.Delete(temporary);
                continue;
            }

            var id = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(temporary));
            if (!Guid.TryParseExact(id, "N", out _) || new FileInfo(temporary).Length is <= 0 or > 8192)
            {
                File.Delete(temporary);
                continue;
            }

            try
            {
                var report = JsonSerializer.Deserialize<AttemptReportRequest>(
                    await File.ReadAllTextAsync(temporary, ct), OperationsProtocol.Json);
                if (report is null || string.IsNullOrWhiteSpace(report.BootId) || report.Result is null)
                {
                    File.Delete(temporary);
                    continue;
                }
            }
            catch (JsonException)
            {
                // A partial write has no durable result. The read-only assignment
                // can be replayed by the server after this incomplete file is removed.
                File.Delete(temporary);
                continue;
            }

            File.Move(temporary, target);
        }
    }

    private async Task ReportAsync(string id, AttemptReportRequest report, CancellationToken ct)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Agent spool identity is invalid.");
        using var response = await client.PostAsJsonAsync(Api + $"attempts/{id}/result", report, ct);
        response.EnsureSuccessStatusCode();
    }
}
