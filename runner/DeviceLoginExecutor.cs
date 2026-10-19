using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>Runs a device login in this service account's own CLI home.</summary>
public sealed partial class DeviceLoginExecutor(
    string binary = "codex", string? credentialFile = null,
    Func<CancellationToken, Task<bool>>? probe = null, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    [GeneratedRegex(@"https://(?:auth|login)\.openai\.com/[^\s<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"\b[A-Z0-9]{4,8}-[A-Z0-9]{4,8}\b")]
    private static partial Regex CodePattern();

    public async Task<DeviceLoginCompletionRequest> RunAsync(
        Func<DeviceLoginInstructionsRequest, CancellationToken, Task> publish,
        CancellationToken cancellationToken)
    {
        var store = credentialFile ?? Path.Combine(
            Environment.GetEnvironmentVariable("CODEX_HOME")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"),
            "auth.json");
        byte[]? previousDigest = null;
        var startedAt = _clock.GetUtcNow();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(15));
        var info = new ProcessStartInfo(binary)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add("login");
        info.ArgumentList.Add("--device-auth");
        using var process = new Process { StartInfo = info };
        var started = false;
        var gate = new object();
        string? url = null;
        string? code = null;
        var sent = false;
        var published = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Capture(string? line)
        {
            if (line is null) return;
            DeviceLoginInstructionsRequest? instructions = null;
            lock (gate)
            {
                url ??= UrlPattern().Match(line).Value is { Length: > 0 } foundUrl ? foundUrl : null;
                code ??= CodePattern().Match(line).Value is { Length: > 0 } foundCode ? foundCode : null;
                if (!sent && url is not null && code is not null)
                {
                    sent = true;
                    instructions = new(url, code);
                }
            }
            if (instructions is not null) _ = PublishAsync(instructions);
        }
        async Task PublishAsync(DeviceLoginInstructionsRequest instructions)
        {
            try
            {
                await publish(instructions, deadline.Token);
                published.TrySetResult(true);
            }
            catch (Exception ex) { published.TrySetException(ex); }
        }
        try
        {
            previousDigest = File.Exists(store)
                ? SHA256.HashData(await File.ReadAllBytesAsync(store, deadline.Token)) : null;
            if (!process.Start()) return new("failed", "The Codex process could not start.");
            started = true;
            process.OutputDataReceived += (_, e) => Capture(e.Data);
            process.ErrorDataReceived += (_, e) => Capture(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(deadline.Token);
            process.WaitForExit(); // Drain redirected DataReceived callbacks before checking instructions.
            if (sent) await published.Task.WaitAsync(deadline.Token);
            if (process.ExitCode != 0 || !sent)
                return new("failed", "Codex device sign-in failed before verification.");
            if (!File.Exists(store))
                return new("failed", "Codex did not create a credential in this runner's CLI home.");
            var newDigest = SHA256.HashData(await File.ReadAllBytesAsync(store, deadline.Token));
            if (previousDigest is not null && previousDigest.SequenceEqual(newDigest))
                return new("failed", "Codex did not update this runner's credential.");
            var verified = probe is not null
                ? await probe(deadline.Token)
                : await ProbeAsync(binary, startedAt, deadline.Token);
            return verified
                ? new("completed", "Codex login and a real provider request succeeded.")
                : new("failed", "Codex login completed, but the provider probe did not succeed.");
        }
        catch (OperationCanceledException)
        {
            if (started) Stop(process);
            return new("failed", "The device sign-in timed out or was cancelled.");
        }
        catch (Exception ex)
        {
            if (started) Stop(process);
            return new("failed", $"Device sign-in failed: {ex.GetType().Name}.");
        }
    }

    private static void Stop(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { return; } // Exit raced the stop request.
    }

    private static async Task<bool> ProbeAsync(string binary, DateTimeOffset startedAt, CancellationToken ct)
    {
        var result = await ProviderAuthProbe.Shared.RefreshAsync(binary, ct);
        return result.IsReady && result.Outcome == ProviderProbeOutcome.Healthy
            && result.LastRealSuccessAt >= startedAt;
    }
}

public sealed class DeviceLoginPoller(TaskServerClient client, Action<string> log,
    string stateDir, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var claim = await client.ClaimDeviceLoginAsync(ct);
                if (claim is not null)
                {
                    Directory.CreateDirectory(stateDir);
                    FileStream? hostLock = null;
                    try
                    {
                        hostLock = new FileStream(Path.Combine(stateDir, "device-login-" + claim.Provider + ".lock"),
                            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        await client.CompleteDeviceLoginAsync(claim.Handle,
                            new("failed", "A provider sign-in is already running in this CLI home."), ct);
                        continue;
                    }
                    using (hostLock)
                    {
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        var remaining = claim.ExpiresAt - _clock.GetUtcNow().UtcDateTime;
                        deadline.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
                        var result = claim.Provider == "codex"
                            ? await new DeviceLoginExecutor().RunAsync(
                                (instructions, token) => client.ReportDeviceLoginInstructionsAsync(
                                    claim.Handle, instructions, token), deadline.Token)
                            : new DeviceLoginCompletionRequest("failed", "Unsupported device-login provider.");
                        await client.CompleteDeviceLoginAsync(claim.Handle, result, ct);
                        log($"device-login provider={claim.Provider} result={result.Result}");
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { log($"device-login poll failed: {ex.GetType().Name}"); }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }
}
