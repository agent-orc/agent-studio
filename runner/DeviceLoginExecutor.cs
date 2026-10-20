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
/// and the Claude setup token, and is never logged. The child gets a closed stdin, so it never
/// reads the daemon's. Success requires a changed credential file and a real provider probe.
/// Cancellation (Studio cancel, session timeout, runner shutdown) or a failed instruction
/// publish stops the process tree, and no Claude token is accepted afterwards.
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
        using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(15), _clock);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limit.Token);
        var info = new ProcessStartInfo(binary)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardInput = true,
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
        var closed = false;
        var publishFailed = false;
        Task publishing = Task.CompletedTask;
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
                if (!closed && !sent && url is not null && (claude || code is not null))
                {
                    sent = true;
                    instructions = new(url, code);
                    publishing = Task.Run(() => PublishAsync(instructions));
                }
            }
        }
        async Task PublishAsync(DeviceLoginInstructionsRequest instructions)
        {
            try
            {
                await publish(instructions, deadline.Token);
                published.TrySetResult(true);
            }
            catch (Exception ex)
            {
                // Nobody can see a code that was not published; stop the CLI now instead of
                // leaving it to wait for an authorization until the deadline.
                var stopNow = !deadline.IsCancellationRequested;
                if (stopNow) lock (gate) publishFailed = true;
                published.TrySetException(ex);
                if (stopNow) await deadline.CancelAsync();
            }
        }
        try
        {
            previousDigest = File.Exists(store)
                ? SHA256.HashData(await File.ReadAllBytesAsync(store, deadline.Token)) : null;
            if (!process.Start()) return new("failed", $"The {label} process could not start.");
            started = true;
            process.StandardInput.Close();
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
        catch (Exception ex)
        {
            if (started) Stop(process);
            lock (gate)
                if (publishFailed)
                    return new("failed", $"The {label} sign-in instructions could not be published.");
            return ex is OperationCanceledException
                ? new("failed", "The device sign-in timed out or was cancelled.")
                : new("failed", $"Device sign-in failed: {ex.GetType().Name}.");
        }
        finally
        {
            // Late output cannot start a publish, and one in flight must not outlive the token
            // source it reads.
            Task pending;
            lock (gate)
            {
                closed = true;
                pending = publishing;
            }
            await deadline.CancelAsync();
            await pending;
        }
    }

    /// <summary>
    /// Claude prints its setup token instead of writing a store. Only the probe children of this
    /// sign-in see the new token; the daemon's environment and token file change only after that
    /// probe succeeds, so the previous credential stays in force after a failed renewal and no
    /// concurrent run ever starts with an unverified token. A cancelled sign-in accepts nothing.
    /// </summary>
    private async Task<DeviceLoginCompletionRequest> AcceptClaudeTokenAsync(string store,
        byte[]? previousDigest, string? token, DateTimeOffset startedAt, CancellationToken ct)
    {
        if (token is null) return new("failed", "Claude did not return a setup token to this runner.");
        var content = Encoding.UTF8.GetBytes(token + "\n");
        if (previousDigest is not null && previousDigest.SequenceEqual(SHA256.HashData(content)))
            return new("failed", "Claude did not update this runner's credential.");
        bool verified;
        using (ProviderAuthEnvironment.UseCandidateClaudeToken(token))
            verified = probe is not null
                ? await probe(ct)
                : await ProbeAsync(binary, startedAt, ct);
        if (!verified) return new("failed", "Claude login completed, but the provider probe did not succeed.");
        ct.ThrowIfCancellationRequested();
        await WriteCredentialAsync(store, content, ct);
        Environment.SetEnvironmentVariable(ProviderAuthEnvironment.ClaudeCodeOAuthToken, token);
        return new("completed", "Claude login and a real provider request succeeded.");
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
        try
        {
            if (process.HasExited) return;
            process.Kill(entireProcessTree: true);
            process.WaitForExit(TimeSpan.FromSeconds(5));
        }
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
    /// <summary>Claim poll and running-session state poll interval.</summary>
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <summary>Bounds the completion report on its own, so it still goes out during shutdown.</summary>
    internal static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(10);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task RunAsync(CancellationToken ct)
    {
        RestoreClaudeToken();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var claim = await client.ClaimDeviceLoginAsync(ct);
                if (claim is not null) await ServeAsync(claim, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { log($"device-login poll failed: {ex.GetType().Name}"); }
            await Task.Delay(PollInterval, _clock, ct);
        }
    }

    private async Task ServeAsync(DeviceLoginClaimDto claim, CancellationToken ct)
    {
        Directory.CreateDirectory(stateDir);
        FileStream hostLock;
        try
        {
            hostLock = new FileStream(Path.Combine(stateDir, "device-login-" + claim.Provider + ".lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await CompleteAsync(claim.Handle,
                new("failed", "A provider sign-in is already running in this CLI home."));
            return;
        }
        using (hostLock)
        {
            var remaining = claim.ExpiresAt - _clock.GetUtcNow().UtcDateTime;
            using var expiry = new CancellationTokenSource(
                remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, _clock);
            using var session = CancellationTokenSource.CreateLinkedTokenSource(ct, expiry.Token);
            var watch = WatchAsync(claim.Handle, session);
            var executor = claim.Provider switch
            {
                "codex" => new DeviceLoginExecutor(codexBinary, clock: _clock),
                "claude" => new DeviceLoginExecutor(claudeBinary,
                    DeviceLoginExecutor.ClaudeTokenPath(stateDir), clock: _clock, provider: "claude"),
                _ => null,
            };
            DeviceLoginCompletionRequest result;
            try
            {
                result = executor is not null
                    ? await executor.RunAsync(
                        (instructions, token) => client.ReportDeviceLoginInstructionsAsync(
                            claim.Handle, instructions, token), session.Token)
                    : new DeviceLoginCompletionRequest("failed", "Unsupported device-login provider.");
            }
            finally
            {
                await session.CancelAsync();
                await watch;
            }
            await CompleteAsync(claim.Handle, result);
            log($"device-login provider={claim.Provider} result={result.Result}");
        }
    }

    /// <summary>
    /// Reads the session state back while the CLI runs. A Studio cancel, a Task Server timeout or
    /// a forgotten session cancels the sign-in, which stops the CLI before it can authorize the host.
    /// </summary>
    private async Task WatchAsync(string handle, CancellationTokenSource session)
    {
        try
        {
            while (true)
            {
                await Task.Delay(PollInterval, _clock, session.Token);
                string? state;
                try { state = await client.DeviceLoginStateAsync(handle, session.Token); }
                catch (Exception ex) when (ex is not OperationCanceledException || !session.IsCancellationRequested)
                {
                    log($"device-login state read failed: {ex.GetType().Name}");
                    continue;
                }
                if (state == "running") continue;
                log($"device-login session is {state ?? "gone"} on the Task Server; stopping the CLI");
                await session.CancelAsync();
                return;
            }
        }
        catch (OperationCanceledException) when (session.IsCancellationRequested)
        {
            return; // The sign-in ended; nothing left to watch.
        }
    }

    private async Task CompleteAsync(string handle, DeviceLoginCompletionRequest result)
    {
        using var timeout = new CancellationTokenSource(CompletionTimeout, _clock);
        try { await client.CompleteDeviceLoginAsync(handle, result, timeout.Token); }
        catch (TaskServerException ex) when (ex.StatusCode == 409)
        {
            log("device-login completion ignored: the session already ended on the Task Server");
        }
    }

    /// <summary>
    /// Runs before the first await, so the daemon's CLIs start with the accepted token. Precedence
    /// (AGT-3225): the <c>provider-auth/claude-token</c> file wins over a
    /// <c>CLAUDE_CODE_OAUTH_TOKEN</c> from runner.env. The file is written only after a verified
    /// Studio sign-in, so it is the newer operator decision; otherwise every restart would undo
    /// that sign-in. Deleting the file returns the runner to its runner.env token. Logs the active
    /// source, never the value.
    /// </summary>
    internal void RestoreClaudeToken()
    {
        var path = DeviceLoginExecutor.ClaudeTokenPath(stateDir);
        var fromEnvironment = !string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable(ProviderAuthEnvironment.ClaudeCodeOAuthToken));
        try
        {
            var stored = File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;
            if (stored.Length > 0)
            {
                Environment.SetEnvironmentVariable(ProviderAuthEnvironment.ClaudeCodeOAuthToken, stored);
                log("device-login claude token source=provider-auth-file"
                    + (fromEnvironment ? " (overrides runner.env)" : string.Empty));
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log($"device-login claude token restore failed: {ex.GetType().Name}");
        }
        log($"device-login claude token source={(fromEnvironment ? "runner-env" : "none")}");
    }
}
