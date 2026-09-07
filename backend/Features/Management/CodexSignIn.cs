using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using AgentStudio.Bus;

namespace AgentStudio.Management;

public sealed record ProviderSignInRequest(string Provider, string SshTarget);

public sealed record ProviderSignInStartResponse(
    string Handle,
    string State,
    string Provider,
    string VerificationUrl,
    string? UserCode,
    DateTime ExpiresAt);

public sealed record ProviderSignInStatusResponse(
    string Handle,
    string State,
    string Provider,
    string Detail,
    DateTime RequestedAt,
    DateTime ExpiresAt,
    DateTime? CompletedAt);

public sealed class ProviderSignInException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

public sealed record ProviderDeviceAuthTransportResult(
    int ExitCode,
    bool LoginStatusVerified,
    IReadOnlyList<string> RestartedServices);

public sealed class ProviderDeviceAuthTransportSession(
    Task<ProviderDeviceAuthTransportResult> completion,
    Action cancel)
{
    public Task<ProviderDeviceAuthTransportResult> Completion { get; } = completion;
    public void Cancel() => cancel();
}

public interface IProviderDeviceAuthTransport
{
    ProviderDeviceAuthTransportSession Start(
        string provider,
        string sshTarget,
        Action<string> onOutput,
        CancellationToken cancellationToken);
}

public sealed record ProviderSignInAuditEvent(
    string Host,
    string Provider,
    string Actor,
    string Outcome);

public interface IProviderSignInAudit
{
    Task WriteAsync(ProviderSignInAuditEvent evt, CancellationToken cancellationToken = default);
}

public sealed class ProviderSignInOperatorFeed(AgentMessageBusBridge bus) : IProviderSignInAudit
{
    public Task WriteAsync(ProviderSignInAuditEvent evt, CancellationToken cancellationToken = default)
        => bus.EmitProviderSignInAsync(evt.Host, evt.Provider, evt.Actor, evt.Outcome, cancellationToken);
}

/// <summary>
/// Owns bounded, in-memory provider browser-auth sessions. The CLI writes its
/// credential only into the remote runner user's native store. Studio retains
/// the one-time browser instructions only while the process is pending and
/// never writes the transcript, code, or credential to durable state.
/// </summary>
public sealed partial class ProviderSignInCoordinator(
    IProviderDeviceAuthTransport transport,
    IProviderSignInAudit audit)
{
    internal static readonly TimeSpan SessionTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan InstructionTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan TerminalSessionRetention = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);
    private readonly object _startGate = new();

    public async Task<ProviderSignInStartResponse> StartAsync(
        string hostId,
        ProviderSignInRequest request,
        string actor,
        CancellationToken cancellationToken)
    {
        var validation = Validate(hostId, request);
        if (validation is not null)
            throw new ProviderSignInException(400, "invalid-provider-sign-in-request", validation);

        var normalizedHost = hostId.Trim();
        var provider = request.Provider.Trim().ToLowerInvariant();
        var now = DateTime.UtcNow;
        var state = new SessionState(
            provider + "_" + Guid.NewGuid().ToString("N"),
            normalizedHost,
            provider,
            actor,
            now,
            now.Add(SessionTimeout));
        lock (_startGate)
        {
            foreach (var completed in _sessions.Where(pair => IsExpiredTerminal(pair.Value, now)).ToArray())
                _sessions.TryRemove(completed.Key, out _);
            if (_sessions.Values.Any(session => session.HostId == normalizedHost
                                                && session.Provider == provider
                                                && IsPending(session)))
                throw new ProviderSignInException(409, "provider-sign-in-active", $"A {ProviderLabel(provider)} sign-in is already pending for this host.");
            if (!_sessions.TryAdd(state.Handle, state))
                throw new ProviderSignInException(500, "provider-sign-in-handle-failed", "The provider sign-in session could not be created.");
        }

        state.Timeout = new CancellationTokenSource(SessionTimeout);
        try
        {
            state.Transport = transport.Start(
                provider,
                request.SshTarget.Trim(),
                line => CaptureInstructions(state, line),
                state.Timeout.Token);
            _ = ObserveCompletionAsync(state);

            await state.InstructionsReady.Task.WaitAsync(InstructionTimeout, cancellationToken);
            lock (state.Gate)
            {
                if (state.State != "pending" || state.VerificationUrl is null)
                    throw new ProviderSignInException(502, "provider-browser-auth-unavailable", state.Detail);
                return new ProviderSignInStartResponse(
                    state.Handle,
                    state.State,
                    state.Provider,
                    state.VerificationUrl,
                    state.UserCode,
                    state.ExpiresAt);
            }
        }
        catch (TimeoutException)
        {
            state.Transport?.Cancel();
            await CompleteAsync(state, "failed", $"{ProviderLabel(provider)} did not provide browser sign-in instructions.", "failed");
            throw new ProviderSignInException(
                502,
                "provider-browser-auth-unavailable",
                $"{ProviderLabel(provider)} did not provide browser sign-in instructions.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            state.Transport?.Cancel();
            await CompleteAsync(state, "failed", "The sign-in request was cancelled before instructions were returned.", "cancelled");
            throw;
        }
        catch (ProviderSignInException)
        {
            throw;
        }
        catch (Exception)
        {
            state.Transport?.Cancel();
            await CompleteAsync(state, "failed", "The SSH device-auth process could not be started.", "failed");
            if (state.Transport is null)
            {
                state.Timeout?.Dispose();
                state.Timeout = null;
            }
            throw new ProviderSignInException(
                502,
                "provider-browser-auth-start-failed",
                "The SSH browser-auth process could not be started.");
        }
    }

    public ProviderSignInStatusResponse? Get(string hostId, string handle)
    {
        if (!_sessions.TryGetValue(handle, out var session)
            || !string.Equals(session.HostId, hostId, StringComparison.Ordinal)) return null;
        lock (session.Gate)
        {
            return new ProviderSignInStatusResponse(
                session.Handle,
                session.State,
                session.Provider,
                session.Detail,
                session.RequestedAt,
                session.ExpiresAt,
                session.CompletedAt);
        }
    }

    internal static string? Validate(string hostId, ProviderSignInRequest request)
    {
        if (string.IsNullOrWhiteSpace(hostId) || !RunnerIdPattern().IsMatch(hostId.Trim()))
            return "Host identity is required and may contain letters, numbers, dots, underscores, and hyphens.";
        if (request is null
            || string.IsNullOrWhiteSpace(request.Provider)
            || request.Provider.Trim().ToLowerInvariant() is not ("claude" or "codex"))
            return "Provider must be claude or codex.";
        if (string.IsNullOrWhiteSpace(request.SshTarget)
            || !SshTargetPattern().IsMatch(request.SshTarget.Trim()))
            return "SSH target must be a configured alias or user@host without shell characters.";
        return null;
    }

    private static void CaptureInstructions(SessionState state, string rawLine)
    {
        if (string.IsNullOrWhiteSpace(rawLine)) return;
        var line = AnsiEscapePattern().Replace(rawLine, string.Empty).Trim();
        lock (state.Gate)
        {
            if (state.State != "pending") return;
            var urlMatch = HttpsUrlPattern().Match(line);
            if (urlMatch.Success
                && Uri.TryCreate(urlMatch.Value.TrimEnd('.', ',', ')', ']'), UriKind.Absolute, out var uri)
                && (state.Provider == "codex"
                    ? IsOpenAiSignInHost(uri.Host)
                    : IsAnthropicSignInHost(uri.Host)))
                state.VerificationUrl = uri.ToString();

            if (!line.Contains("http", StringComparison.OrdinalIgnoreCase))
            {
                var codeMatch = UserCodePattern().Match(line.ToUpperInvariant());
                if (codeMatch.Success) state.UserCode = codeMatch.Value;
            }

            if (state.VerificationUrl is not null
                && (state.Provider == "claude" || state.UserCode is not null))
                state.InstructionsReady.TrySetResult();
        }
    }

    private async Task ObserveCompletionAsync(SessionState state)
    {
        try
        {
            var result = await state.Transport!.Completion.ConfigureAwait(false);
            if (result.ExitCode == 0 && result.LoginStatusVerified)
            {
                var provider = ProviderLabel(state.Provider);
                var detail = result.RestartedServices.Count > 0
                    ? $"{provider} sign-in completed. Runner services restarted and a fresh provider probe is expected."
                    : $"{provider} sign-in completed. Waiting for the runner's next provider probe.";
                await CompleteAsync(state, "completed", detail, "completed").ConfigureAwait(false);
            }
            else
            {
                await CompleteAsync(
                    state,
                    "failed",
                    $"{ProviderLabel(state.Provider)} sign-in did not complete or login status could not be verified.",
                    "failed").ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (state.Timeout?.IsCancellationRequested == true)
        {
            await CompleteAsync(state, "failed", $"{ProviderLabel(state.Provider)} sign-in timed out after 15 minutes.", "timeout").ConfigureAwait(false);
        }
        catch (Exception)
        {
            await CompleteAsync(state, "failed", $"The remote {ProviderLabel(state.Provider)} sign-in process failed.", "failed").ConfigureAwait(false);
        }
        finally
        {
            state.Timeout?.Dispose();
            state.Timeout = null;
        }
    }

    private async Task CompleteAsync(SessionState state, string terminalState, string detail, string auditOutcome)
    {
        var writeAudit = false;
        var actor = "unknown";
        lock (state.Gate)
        {
            if (state.State != "pending") return;
            state.State = terminalState;
            state.Detail = detail;
            state.CompletedAt = DateTime.UtcNow;
            state.VerificationUrl = null;
            state.UserCode = null;
            state.Transport = null;
            state.InstructionsReady.TrySetResult();
            actor = state.Actor;
            state.Actor = "";
            writeAudit = true;
        }
        if (writeAudit)
        {
            await audit.WriteAsync(new ProviderSignInAuditEvent(
                state.HostId,
                state.Provider,
                actor,
                auditOutcome)).ConfigureAwait(false);
        }
    }

    private static bool IsOpenAiSignInHost(string host)
        => host.Equals("openai.com", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".openai.com", StringComparison.OrdinalIgnoreCase)
           || host.Equals("chatgpt.com", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".chatgpt.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsAnthropicSignInHost(string host)
        => host.Equals("anthropic.com", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".anthropic.com", StringComparison.OrdinalIgnoreCase)
           || host.Equals("claude.ai", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".claude.ai", StringComparison.OrdinalIgnoreCase);

    private static string ProviderLabel(string provider)
        => provider == "claude" ? "Claude" : "Codex";

    private static bool IsPending(SessionState state)
    {
        lock (state.Gate) return state.State == "pending";
    }

    private static bool IsExpiredTerminal(SessionState state, DateTime now)
    {
        lock (state.Gate)
            return state.CompletedAt is { } completedAt && now - completedAt >= TerminalSessionRetention;
    }

    [GeneratedRegex(@"^([A-Za-z0-9][A-Za-z0-9._-]*@)?[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex SshTargetPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex RunnerIdPattern();

    [GeneratedRegex(@"https://[^\s<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex HttpsUrlPattern();

    [GeneratedRegex(@"\b[A-Z0-9]{4,8}(?:-[A-Z0-9]{4,8})+\b")]
    private static partial Regex UserCodePattern();

    [GeneratedRegex(@"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~])")]
    private static partial Regex AnsiEscapePattern();

    private sealed class SessionState(
        string handle,
        string hostId,
        string provider,
        string actor,
        DateTime requestedAt,
        DateTime expiresAt)
    {
        public object Gate { get; } = new();
        public string Handle { get; } = handle;
        public string HostId { get; } = hostId;
        public string Provider { get; } = provider;
        public string Actor { get; set; } = actor;
        public DateTime RequestedAt { get; } = requestedAt;
        public DateTime ExpiresAt { get; } = expiresAt;
        public string State { get; set; } = "pending";
        public string Detail { get; set; } = "Waiting for browser sign-in.";
        public string? VerificationUrl { get; set; }
        public string? UserCode { get; set; }
        public DateTime? CompletedAt { get; set; }
        public CancellationTokenSource? Timeout { get; set; }
        public ProviderDeviceAuthTransportSession? Transport { get; set; }
        public TaskCompletionSource InstructionsReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>
/// Runs the fixed device-auth script through the same local SSH boundary used
/// by provider provisioning. Output is streamed to the coordinator parser and
/// is never logged or persisted by Studio.
/// </summary>
public sealed class SshProviderDeviceAuthTransport : IProviderDeviceAuthTransport
{
    public ProviderDeviceAuthTransportSession Start(
        string provider,
        string sshTarget,
        Action<string> onOutput,
        CancellationToken cancellationToken)
    {
        var process = new Process { StartInfo = BuildStartInfo(sshTarget) };
        if (!process.Start()) throw new InvalidOperationException("SSH could not be started.");

        var completion = RunAsync(process, provider, onOutput, cancellationToken);
        return new ProviderDeviceAuthTransportSession(completion, () => TryKill(process));
    }

    internal static ProcessStartInfo BuildStartInfo(string sshTarget)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "ssh",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add("BatchMode=yes");
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add("ConnectTimeout=10");
        startInfo.ArgumentList.Add("-T");
        startInfo.ArgumentList.Add(sshTarget);
        startInfo.ArgumentList.Add("bash");
        startInfo.ArgumentList.Add("-s");
        return startInfo;
    }

    private static async Task<ProviderDeviceAuthTransportResult> RunAsync(
        Process process,
        string provider,
        Action<string> onOutput,
        CancellationToken cancellationToken)
    {
        var safeMarkers = new ConcurrentBag<string>();
        using var registration = cancellationToken.Register(() => TryKill(process));
        try
        {
            await process.StandardInput.WriteAsync(RemoteScript(provider).AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
            process.StandardInput.Close();

            var stdout = PumpAsync(process.StandardOutput, onOutput, safeMarkers, cancellationToken);
            var stderr = PumpAsync(process.StandardError, onOutput, safeMarkers, cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(stdout, stderr);
            var markers = safeMarkers.ToArray();
            return new ProviderDeviceAuthTransportResult(
                process.ExitCode,
                markers.Contains("provider-login-status=verified", StringComparer.Ordinal),
                markers.Where(line => line.StartsWith("provider-probe-unit=", StringComparison.Ordinal))
                    .Select(line => line["provider-probe-unit=".Length..])
                    .Where(unit => unit.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray());
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        finally
        {
            process.Dispose();
        }
    }

    private static async Task PumpAsync(
        StreamReader reader,
        Action<string> onOutput,
        ConcurrentBag<string> safeMarkers,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line == "provider-login-status=verified"
                || line.StartsWith("provider-probe-unit=", StringComparison.Ordinal))
                safeMarkers.Add(line);
            onOutput(line);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception exception)
        {
            SilentCatch.Note(exception, "SshProviderDeviceAuthTransport: bounded SSH process cleanup");
        }
    }

    private static string RemoteScript(string provider)
        => provider == "claude" ? ClaudeRemoteScript : CodexRemoteScript;

    private const string CodexRemoteScript = """
set -uo pipefail

if ! command -v codex >/dev/null 2>&1; then
  echo 'codex-login-status=binary-missing'
  exit 41
fi

timeout --signal=TERM --kill-after=5s 900s codex login --device-auth
login_exit=$?
if [[ "$login_exit" -ne 0 ]]; then
  echo 'codex-login-status=login-failed'
  exit "$login_exit"
fi

if ! codex login status >/dev/null 2>&1; then
  echo 'codex-login-status=unverified'
  exit 42
fi
echo 'provider-login-status=verified'

units=()
if sudo -n systemctl cat agent-host.service >/dev/null 2>&1; then
  units+=(agent-host.service)
elif sudo -n systemctl cat agent-runner.service >/dev/null 2>&1; then
  units+=(agent-runner.service)
fi
if sudo -n systemctl cat agent-runner-review.service >/dev/null 2>&1; then
  units+=(agent-runner-review.service)
fi
for unit in "${units[@]}"; do
  if sudo -n systemctl restart "$unit"; then
    printf 'provider-probe-unit=%s\n' "$unit"
  fi
done
""";

    private const string ClaudeRemoteScript = """
set -uo pipefail

if ! command -v claude >/dev/null 2>&1; then
  echo 'provider-login-status=binary-missing'
  exit 41
fi

timeout --signal=TERM --kill-after=5s 900s claude auth login --claudeai
login_exit=$?
if [[ "$login_exit" -ne 0 ]]; then
  echo 'provider-login-status=login-failed'
  exit "$login_exit"
fi

if ! claude auth status --text >/dev/null 2>&1; then
  echo 'provider-login-status=unverified'
  exit 42
fi
echo 'provider-login-status=verified'

units=()
if sudo -n systemctl cat agent-host.service >/dev/null 2>&1; then
  units+=(agent-host.service)
elif sudo -n systemctl cat agent-runner.service >/dev/null 2>&1; then
  units+=(agent-runner.service)
fi
if sudo -n systemctl cat agent-runner-review.service >/dev/null 2>&1; then
  units+=(agent-runner-review.service)
fi
for unit in "${units[@]}"; do
  if sudo -n systemctl restart "$unit"; then
    printf 'provider-probe-unit=%s\n' "$unit"
  fi
done
""";
}
