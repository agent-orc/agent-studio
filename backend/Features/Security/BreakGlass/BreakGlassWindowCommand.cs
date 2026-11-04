using Microsoft.Extensions.Logging.Abstractions;

namespace AgentStudio.Security;

/// <summary>Host-only opening path while Studio is stopped. Read both secret factors from stdin, never command arguments.</summary>
public static class BreakGlassWindowCommand
{
    public const string Flag = "--open-break-glass";
    public const string RecordFlag = "--record-break-glass";

    public static async Task<int> RunAsync(string[] args, TextReader input, TextWriter output,
        IConfiguration? configuration = null, TimeProvider? clock = null, CancellationToken ct = default)
    {
        string? owner = null, project = null, sessions = null;
        var hours = 2;
        var configArgs = new List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--owner" when index + 1 < args.Length: owner = args[++index]; break;
                case "--project" when index + 1 < args.Length: project = args[++index]; break;
                case "--sessions" when index + 1 < args.Length: sessions = args[++index]; break;
                case "--hours" when index + 1 < args.Length && int.TryParse(args[index + 1], out var parsed):
                    hours = parsed; index++; break;
                default: configArgs.Add(args[index]); break;
            }
        }
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(sessions))
        {
            await output.WriteLineAsync("Usage: --open-break-glass --owner NAME --project PROJECT --sessions ID,ID [--hours 1|2]. Enter sealed credential and authenticator code on stdin.");
            return 64;
        }
        configuration ??= OperatorKillSwitchCommand.BuildConfiguration([.. configArgs]);
        clock ??= TimeProvider.System;
        var sinkOptions = ControlPlaneAuditSinkOptions.From(configuration);
        if (!sinkOptions.Configured)
        {
            await output.WriteLineAsync("An off-host audit sink must be configured before opening a host break-glass window.");
            return 2;
        }
        var store = new AccessSecurityStore(configuration, NullLogger<AccessSecurityStore>.Instance, clock);
        using var hostLock = OperatorKillSwitchHostLock.TryAcquire(store.SecurityFolder);
        if (hostLock is null)
        {
            await output.WriteLineAsync("The Studio API is running. Open the window in Settings > Access.");
            return 2;
        }
        await output.WriteLineAsync("Enter the sealed credential, then the current authenticator code on separate lines:");
        var credential = await ReadSecretAsync(input, ct);
        var factor = await ReadSecretAsync(input, ct);
        try
        {
            var window = store.OpenBreakGlassWindowOffline(owner, factor, credential,
                sessions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), hours, project);
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
                { Timeout = TimeSpan.FromSeconds(15) };
            var sink = new HttpsControlPlaneAuditSink(http, sinkOptions);
            var monitor = new ControlPlaneAuditMonitor(store.ControlPlaneAudit, sinkOptions, clock);
            var forwarder = new AuditSinkForwarder(store.ControlPlaneAudit, monitor, sink, clock, configuration);
            await forwarder.ForwardPendingAsync(ct);
            if (store.ControlPlaneAudit.ReadForwardPosition().Sequence < store.ControlPlaneAudit.HeadSequence)
            {
                await output.WriteLineAsync($"Window {window.Id} opened and journalled, but the sink has not acknowledged its entry. Restart Studio to retry forwarding before using the window.");
                return 2;
            }
            await output.WriteLineAsync($"Window {window.Id} opened until {window.ExpiresAt:O}. The host journal and off-host sink hold its opening entry.");
            return 0;
        }
        catch (SecurityOperationException exception)
        {
            await output.WriteLineAsync(exception.Message);
            return 3;
        }
    }

    public static async Task<int> RecordAsync(string[] args, TextReader input, TextWriter output,
        IConfiguration? configuration = null, TimeProvider? clock = null, CancellationToken ct = default)
    {
        string? owner = null, window = null, operation = null, target = null, outcome = null;
        var configArgs = new List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--owner" when index + 1 < args.Length: owner = args[++index]; break;
                case "--window" when index + 1 < args.Length: window = args[++index]; break;
                case "--operation" when index + 1 < args.Length: operation = args[++index]; break;
                case "--target" when index + 1 < args.Length: target = args[++index]; break;
                case "--outcome" when index + 1 < args.Length: outcome = args[++index]; break;
                default: configArgs.Add(args[index]); break;
            }
        }
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(window)
            || string.IsNullOrWhiteSpace(operation) || string.IsNullOrWhiteSpace(outcome))
        {
            await output.WriteLineAsync("Usage: --record-break-glass --owner NAME --window ID --operation NAME --target NAME --outcome RESULT. Enter the current authenticator code on stdin.");
            return 64;
        }
        configuration ??= OperatorKillSwitchCommand.BuildConfiguration([.. configArgs]);
        clock ??= TimeProvider.System;
        var sinkOptions = ControlPlaneAuditSinkOptions.From(configuration);
        if (!sinkOptions.Configured) { await output.WriteLineAsync("An off-host audit sink is required."); return 2; }
        var store = new AccessSecurityStore(configuration, NullLogger<AccessSecurityStore>.Instance, clock);
        using var hostLock = OperatorKillSwitchHostLock.TryAcquire(store.SecurityFolder);
        if (hostLock is null) { await output.WriteLineAsync("The Studio API is running. Record the action through Studio."); return 2; }
        await output.WriteLineAsync("Enter the current authenticator code:");
        var factor = await ReadSecretAsync(input, ct);
        try
        {
            store.AppendOfflineBreakGlassEntry(window, owner, factor, operation, target, outcome);
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
                { Timeout = TimeSpan.FromSeconds(15) };
            var sink = new HttpsControlPlaneAuditSink(http, sinkOptions);
            var monitor = new ControlPlaneAuditMonitor(store.ControlPlaneAudit, sinkOptions, clock);
            var forwarder = new AuditSinkForwarder(store.ControlPlaneAudit, monitor, sink, clock, configuration);
            await forwarder.ForwardPendingAsync(ct);
            if (store.ControlPlaneAudit.ReadForwardPosition().Sequence < store.ControlPlaneAudit.HeadSequence)
            {
                await output.WriteLineAsync("The host journal has the entry, but the sink has not acknowledged it. Restart Studio to retry forwarding.");
                return 2;
            }
            await output.WriteLineAsync("The host journal and off-host sink received the entry.");
            return 0;
        }
        catch (SecurityOperationException exception)
        {
            await output.WriteLineAsync(exception.Message);
            return 3;
        }
    }

    private static async Task<string?> ReadSecretAsync(TextReader input, CancellationToken ct)
    {
        if (!ReferenceEquals(input, Console.In) || Console.IsInputRedirected) return await input.ReadLineAsync(ct);
        var value = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return value.ToString(); }
            if (key.Key == ConsoleKey.Backspace) { if (value.Length > 0) value.Length--; continue; }
            if (!char.IsControl(key.KeyChar) && value.Length < 256) value.Append(key.KeyChar);
        }
    }
}
