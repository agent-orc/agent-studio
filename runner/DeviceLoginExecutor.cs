using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>
/// Runs an operator-initiated device sign-in in this service account's own CLI home. This is
/// the runner's only provider sign-in process boundary: <c>codex login --device-auth</c> and
/// <c>claude setup-token</c>. Child output is parsed for the verification URL, the one-time code
/// and the Claude setup token, and is never logged. Success requires a changed credential file
/// and a real provider probe.
/// </summary>
public sealed partial class DeviceLoginExecutor(
    string binary = "codex", string? credentialFile = null,
    Func<CancellationToken, Task<bool>>? probe = null, TimeProvider? clock = null,
    string provider = "codex")
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    [GeneratedRegex(@"https://[^\s<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"\b[A-Z0-9]{4,8}-[A-Z0-9]{4,8}\b")]
    private static partial Regex CodePattern();

    [GeneratedRegex(@"\bsk-ant-[A-Za-z0-9_-]{10,}\b")]
    private static partial Regex ClaudeTokenPattern();

    [GeneratedRegex(@"\x1B\[[0-9;]*[A-Za-z]")]
    private static partial Regex AnsiPattern();

    /// <summary>The Claude setup token this runner restores into its environment at start.</summary>
    public static string ClaudeTokenPath(string stateDir)
        => Path.Combine(stateDir, "provider-auth", "claude-token");

    public async Task<DeviceLoginCompletionRequest> RunAsync(
        Func<DeviceLoginInstructionsRequest, CancellationToken, Task> publish,
        CancellationToken cancellationToken)
    {
        if (provider is not ("codex" or "claude"))
            return new("failed", "Unsupported device-login provider.");
        var claude = provider == "claude";
        var label = claude ? "Claude" : "Codex";
        var store = credentialFile ?? (claude ? null : Path.Combine(
            Environment.GetEnvironmentVariable("CODEX_HOME")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"),
            "auth.json"));
        if (store is null) return new("failed", "No Claude credential file is configured for this runner.");
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
        string[] arguments = claude ? ["setup-token"] : ["login", "--device-auth"];
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        info.Environment["NO_BROWSER"] = "1";
        info.Environment["BROWSER"] = "false";
        using var process = new Process { StartInfo = info };
        var started = false;
        var gate = new object();
        string? url = null;
        string? code = null;
        string? token = null;
        var sent = false;
        var published = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Capture(string? line)
        {
            if (line is null) return;
            DeviceLoginInstructionsRequest? instructions = null;
            lock (gate)
            {
                url ??= SafeVerificationUrl(line, provider);
                if (!claude) code ??= DeviceCode(line);
                if (claude) token ??= ClaudeToken(line);
                if (!sent && url is not null && (claude || code is not null))
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
            if (!process.Start()) return new("failed", $"The {label} process could not start.");
            started = true;
            process.OutputDataReceived += (_, e) => Capture(e.Data);
            process.ErrorDataReceived += (_, e) => Capture(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(deadline.Token);
            process.WaitForExit(); // Drain redirected DataReceived callbacks before checking instructions.
            if (sent) await published.Task.WaitAsync(deadline.Token);
            if (process.ExitCode != 0 || !sent)
                return new("failed", $"{label} device sign-in failed before verification.");
            if (claude) return await AcceptClaudeTokenAsync(store, previousDigest, token, startedAt, deadline.Token);
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

    /// <summary>
    /// Claude prints its setup token instead of writing a store. The runner probes with the new
    /// token in its environment and replaces its token file only when that probe succeeds, so the
    /// previous credential stays in force after a failed renewal.
    /// </summary>
    private async Task<DeviceLoginCompletionRequest> AcceptClaudeTokenAsync(string store,
        byte[]? previousDigest, string? token, DateTimeOffset startedAt, CancellationToken ct)
    {
        if (token is null) return new("failed", "Claude did not return a setup token to this runner.");
        var content = Encoding.UTF8.GetBytes(token + "\n");
        if (previousDigest is not null && previousDigest.SequenceEqual(SHA256.HashData(content)))
            return new("failed", "Claude did not update this runner's credential.");
        var previousToken = Environment.GetEnvironmentVariable(ProviderAuthEnvironment.ClaudeCodeOAuthToken);
        var accepted = false;
        Environment.SetEnvironmentVariable(ProviderAuthEnvironment.ClaudeCodeOAuthToken, token);
        try
        {
            var verified = probe is not null
                ? await probe(ct)
                : await ProbeAsync(binary, startedAt, ct);
            if (!verified) return new("failed", "Claude login completed, but the provider probe did not succeed.");
            await WriteCredentialAsync(store, content, ct);
            accepted = true;
            return new("completed", "Claude login and a real provider request succeeded.");
        }
        finally
        {
            if (!accepted)
                Environment.SetEnvironmentVariable(ProviderAuthEnvironment.ClaudeCodeOAuthToken, previousToken);
        }
    }

    private static async Task WriteCredentialAsync(string path, byte[] content, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            await using (var stream = new FileStream(temporary, options))
                await stream.WriteAsync(content, ct);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal static string? SafeVerificationUrl(string line, string provider)
    {
        foreach (Match match in UrlPattern().Matches(AnsiPattern().Replace(line, string.Empty)))
        {
            var value = match.Value.TrimEnd('.', ',', ')', '"', '\'');
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0) continue;
            if ((provider == "codex" && uri.Host is "auth.openai.com" or "login.openai.com")
                || (provider == "claude" && uri.Host is "claude.ai" or "console.anthropic.com"))
                return value;
        }
        return null;
    }

    internal static string? DeviceCode(string line)
        => CodePattern().Match(AnsiPattern().Replace(line, string.Empty)).Value is { Length: > 0 } code
            ? code : null;

    internal static string? ClaudeToken(string line)
        => ClaudeTokenPattern().Match(AnsiPattern().Replace(line, string.Empty)).Value is { Length: > 0 } token
            ? token : null;

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

/// <summary>The runner's only device sign-in poller; it serves both providers for this host.</summary>
public sealed class DeviceLoginPoller(TaskServerClient client, Action<string> log,
    string stateDir, string codexBinary = "codex", string claudeBinary = "claude", TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    public async Task RunAsync(CancellationToken ct)
    {
        RestoreClaudeToken();
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
                        var executor = claim.Provider switch
                        {
                            "codex" => new DeviceLoginExecutor(codexBinary, clock: _clock),
                            "claude" => new DeviceLoginExecutor(claudeBinary,
                                DeviceLoginExecutor.ClaudeTokenPath(stateDir), clock: _clock, provider: "claude"),
                            _ => null,
                        };
                        var result = executor is not null
                            ? await executor.RunAsync(
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

    /// <summary>Runs before the first await, so the daemon's CLIs start with the accepted token.</summary>
    private void RestoreClaudeToken()
    {
        var path = DeviceLoginExecutor.ClaudeTokenPath(stateDir);
        try
        {
            if (File.Exists(path))
                Environment.SetEnvironmentVariable(ProviderAuthEnvironment.ClaudeCodeOAuthToken,
                    File.ReadAllText(path).Trim());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log($"device-login claude token restore failed: {ex.GetType().Name}");
        }
    }
}
