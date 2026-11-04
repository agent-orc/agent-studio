using Microsoft.Extensions.Logging.Abstractions;

namespace AgentStudio.Security;

/// <summary>
/// The out-of-band kill switch on the control plane host (security model
/// SM-2), for when the Studio UI is unreachable:
/// <c>dotnet OrchestratorApi.dll --end-operator-access [--reason TEXT] [--wait-seconds N]</c>.
/// It first writes the latch, which refuses every operator session at once.
/// When the API is stopped (the host lock is free) it applies the kill itself
/// through the same coordinator as the Studio action, so the audit trail gets
/// the same <c>operator-kill.all</c> entry; the Owner notice follows when the
/// API starts. When the API runs, the API applies the latch within its poll
/// interval and the command waits for that confirmation.
/// </summary>
public static class OperatorKillSwitchCommand
{
    public const string Flag = "--end-operator-access";
    public const int ExitOk = 0;
    public const int ExitUsage = 64;
    public const int ExitOperationsServerPending = 2;
    public const int ExitNotConfirmed = 3;

    public static async Task<int> RunAsync(string[] args, TextWriter output, IConfiguration? configuration = null,
        TimeProvider? clock = null, CancellationToken ct = default)
    {
        string? reason = null;
        var waitSeconds = 30;
        var rest = new List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--reason" when index + 1 < args.Length:
                    reason = args[++index];
                    break;
                case "--wait-seconds" when index + 1 < args.Length && int.TryParse(args[index + 1], out var seconds) && seconds is >= 1 and <= 600:
                    waitSeconds = seconds;
                    index++;
                    break;
                case "--reason" or "--wait-seconds":
                    await output.WriteLineAsync($"Usage: {Flag} [--reason TEXT] [--wait-seconds 1-600] [--TaskRepository=PATH]");
                    return ExitUsage;
                default:
                    rest.Add(args[index]);
                    break;
            }
        }
        configuration ??= BuildConfiguration([.. rest]);
        clock ??= TimeProvider.System;
        var store = new AccessSecurityStore(configuration, NullLogger<AccessSecurityStore>.Instance, clock);
        var log = new OperatorKillSwitchLog(store.SecurityFolder);
        var request = OperatorKillSwitchLog.ReadRequest(store.OperatorKillRequestPath);
        if (request is null)
        {
            request = new OperatorKillRequest("request_" + Guid.NewGuid().ToString("N"), clock.GetUtcNow().UtcDateTime, reason);
            OperatorKillSwitchLog.WriteRequest(store.OperatorKillRequestPath, request);
        }
        await output.WriteLineAsync("Every operator session is refused from now on.");

        using (var hostLock = OperatorKillSwitchHostLock.TryAcquire(store.SecurityFolder))
        {
            if (hostLock is not null)
            {
                using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
                { Timeout = TimeSpan.FromSeconds(10) };
                var killSwitch = new OperatorKillSwitch(store, log,
                    new OperationsServerOperatorRevocation(http, OperationsServerRevocationOptions.From(configuration)),
                    new DeferredOperatorKillNotifier(), clock);
                var record = await killSwitch.ApplyOutOfBandRequestAsync(removeLatch: false, ct);
                await output.WriteLineAsync("The Studio API is stopped; the kill was applied on this host.");
                return await ReportAsync(record!, output);
            }
        }

        await output.WriteLineAsync($"The Studio API is running; waiting up to {waitSeconds} s for it to apply the kill.");
        var deadline = clock.GetUtcNow().AddSeconds(waitSeconds);
        while (clock.GetUtcNow() < deadline)
        {
            var applied = log.Read().Kills.FirstOrDefault(kill => kill.RequestId == request.Id);
            if (applied is not null && !store.OperatorKillRequested) return await ReportAsync(applied, output);
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }
        await output.WriteLineAsync("The API did not confirm in time. The latch stays and keeps refusing every operator session; "
            + "the API applies it on its next poll. Stop the API and run this command again to apply it here.");
        return ExitNotConfirmed;
    }

    /// <summary>The configuration the API reads: its JSON files, environment variables, and command-line overrides.</summary>
    public static IConfiguration BuildConfiguration(string[] args)
    {
        var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                          ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production";
        var basePath = File.Exists(Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json"))
            ? Directory.GetCurrentDirectory() : AppContext.BaseDirectory;
        return new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{environment}.json", optional: true)
            .AddJsonFile("appsettings.Local.json", optional: true)
            .AddEnvironmentVariables()
            .AddCommandLine(args)
            .Build();
    }

    private static async Task<int> ReportAsync(OperatorKillRecord record, TextWriter output)
    {
        await output.WriteLineAsync($"Ended all operator access: {record.Id}, {record.OperatorAgentIds.Count} operator agents, "
            + $"{record.SessionIds.Count} live sessions, {record.StandingGrantIds.Count} standing grants, "
            + $"{record.ApprovalIds.Count} approval requests. Audit event {OperatorKillEvents.All}.");
        await output.WriteLineAsync($"Operations Server principals: {record.OperationsServer.State}"
            + (record.OperationsServer.LastError is { } error ? $" ({error})" : ""));
        if (record.OperationsServer.State == OperatorKillPropagationStates.Revoked) return ExitOk;
        await output.WriteLineAsync("On the Operations Server host, run: operations-server revoke-operators <principals-file> <data-directory>");
        return record.OperationsServer.State == OperatorKillPropagationStates.Pending ? ExitOperationsServerPending : ExitOk;
    }
}
