using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentStudio.Management;

public sealed record ClaudeSignInRequest(string SshTarget);

public sealed record ClaudeSignInStartResponse(
    string Handle,
    string State,
    string VerificationUrl,
    DateTime ExpiresAt);

public sealed record ClaudeSignInStatusResponse(
    string Handle,
    string State,
    string Detail,
    DateTime RequestedAt,
    DateTime ExpiresAt,
    DateTime? CompletedAt);

public sealed class ClaudeSignInException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

public sealed record ClaudeDeviceAuthTransportResult(
    int ExitCode,
    bool LoginStatusVerified,
    IReadOnlyList<string> RestartedServices);

public sealed class ClaudeDeviceAuthTransportSession(
    Task<ClaudeDeviceAuthTransportResult> completion,
    Action cancel)
{
    public Task<ClaudeDeviceAuthTransportResult> Completion { get; } = completion;
    public void Cancel() => cancel();
}

public interface IClaudeDeviceAuthTransport
{
    ClaudeDeviceAuthTransportSession Start(
        string sshTarget,
        Action<string> onOutput,
        CancellationToken cancellationToken);
}

/// <summary>
/// Owns bounded, in-memory Claude device-auth sessions, the Claude counterpart
/// of <see cref="CodexSignInCoordinator"/>. <c>claude setup-token</c> prints a
/// one-time browser verification URL and, once that browser flow completes,
/// its long-lived OAuth token. The remote script keeps the token on the host:
/// it lands straight in the shared provider-auth EnvironmentFile and never
/// appears in the streamed output this coordinator parses, in the returned
/// response, or in durable Studio state.
/// </summary>
public sealed partial class ClaudeSignInCoordinator(
    IClaudeDeviceAuthTransport transport,
    IProviderSignInAudit audit)
{
    internal static readonly TimeSpan SessionTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan InstructionTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan TerminalSessionRetention = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);
    private readonly object _startGate = new();

    public async Task<ClaudeSignInStartResponse> StartAsync(
        string hostId,
        ClaudeSignInRequest request,
        string actor,
        CancellationToken cancellationToken)
    {
        var validation = Validate(hostId, request);
        if (validation is not null)
            throw new ClaudeSignInException(400, "invalid-claude-sign-in-request", validation);

        var normalizedHost = hostId.Trim();
        var now = DateTime.UtcNow;
        var state = new SessionState(
            "claude_" + Guid.NewGuid().ToString("N"),
            normalizedHost,
            actor,
            now,
            now.Add(SessionTimeout));
        lock (_startGate)
        {
            foreach (var completed in _sessions.Where(pair => IsExpiredTerminal(pair.Value, now)).ToArray())
                _sessions.TryRemove(completed.Key, out _);
            if (_sessions.Values.Any(session => session.HostId == normalizedHost && IsPending(session)))
                throw new ClaudeSignInException(409, "claude-sign-in-active", "A Claude sign-in is already pending for this host.");
            if (!_sessions.TryAdd(state.Handle, state))
                throw new ClaudeSignInException(500, "claude-sign-in-handle-failed", "The Claude sign-in session could not be created.");
        }

        state.Timeout = new CancellationTokenSource(SessionTimeout);
        try
        {
            state.Transport = transport.Start(
                request.SshTarget.Trim(),
                line => CaptureInstructions(state, line),
                state.Timeout.Token);
            _ = ObserveCompletionAsync(state);

            await state.InstructionsReady.Task.WaitAsync(InstructionTimeout, cancellationToken);
            lock (state.Gate)
            {
                if (state.State != "pending" || state.VerificationUrl is null)
                    throw new ClaudeSignInException(502, "claude-device-auth-unavailable", state.Detail);
                return new ClaudeSignInStartResponse(
                    state.Handle,
                    state.State,
                    state.VerificationUrl,
                    state.ExpiresAt);
            }
        }
        catch (TimeoutException)
        {
            state.Transport?.Cancel();
            await CompleteAsync(state, "failed", "Claude did not provide a sign-in URL.", "failed");
            throw new ClaudeSignInException(
                502,
                "claude-device-auth-unavailable",
                "Claude did not provide a sign-in URL.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            state.Transport?.Cancel();
            await CompleteAsync(state, "failed", "The sign-in request was cancelled before instructions were returned.", "cancelled");
            throw;
        }
        catch (ClaudeSignInException)
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
            throw new ClaudeSignInException(
                502,
                "claude-device-auth-start-failed",
                "The SSH device-auth process could not be started.");
        }
    }

    public ClaudeSignInStatusResponse? Get(string hostId, string handle)
    {
        if (!_sessions.TryGetValue(handle, out var session)
            || !string.Equals(session.HostId, hostId, StringComparison.Ordinal)) return null;
        lock (session.Gate)
        {
            return new ClaudeSignInStatusResponse(
                session.Handle,
                session.State,
                session.Detail,
                session.RequestedAt,
                session.ExpiresAt,
                session.CompletedAt);
        }
    }

    internal static string? Validate(string hostId, ClaudeSignInRequest request)
    {
        if (string.IsNullOrWhiteSpace(hostId) || !RunnerIdPattern().IsMatch(hostId.Trim()))
            return "Host identity is required and may contain letters, numbers, dots, underscores, and hyphens.";
        if (request is null
            || string.IsNullOrWhiteSpace(request.SshTarget)
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
                && IsAnthropicSignInHost(uri.Host))
            {
                state.VerificationUrl = uri.ToString();
                state.InstructionsReady.TrySetResult();
            }
        }
    }

    private async Task ObserveCompletionAsync(SessionState state)
    {
        try
        {
            var result = await state.Transport!.Completion.ConfigureAwait(false);
            if (result.ExitCode == 0 && result.LoginStatusVerified)
            {
                var detail = result.RestartedServices.Count > 0
                    ? "Claude sign-in completed. Runner services restarted and a fresh provider probe is expected."
                    : "Claude sign-in completed. Waiting for the runner's next provider probe.";
                await CompleteAsync(state, "completed", detail, "completed").ConfigureAwait(false);
            }
            else
            {
                await CompleteAsync(
                    state,
                    "failed",
                    "Claude sign-in did not complete or login status could not be verified.",
                    "failed").ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (state.Timeout?.IsCancellationRequested == true)
        {
            await CompleteAsync(state, "failed", "Claude sign-in timed out after 15 minutes.", "timeout").ConfigureAwait(false);
        }
        catch (Exception)
        {
            await CompleteAsync(state, "failed", "The remote Claude sign-in process failed.", "failed").ConfigureAwait(false);
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
                "claude",
                actor,
                auditOutcome)).ConfigureAwait(false);
        }
    }

    private static bool IsAnthropicSignInHost(string host)
        => host.Equals("claude.ai", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".claude.ai", StringComparison.OrdinalIgnoreCase)
           || host.Equals("anthropic.com", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".anthropic.com", StringComparison.OrdinalIgnoreCase)
           || host.Equals("console.anthropic.com", StringComparison.OrdinalIgnoreCase);

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

    [GeneratedRegex(@"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~])")]
    private static partial Regex AnsiEscapePattern();

    private sealed class SessionState(
        string handle,
        string hostId,
        string actor,
        DateTime requestedAt,
        DateTime expiresAt)
    {
        public object Gate { get; } = new();
        public string Handle { get; } = handle;
        public string HostId { get; } = hostId;
        public string Actor { get; set; } = actor;
        public DateTime RequestedAt { get; } = requestedAt;
        public DateTime ExpiresAt { get; } = expiresAt;
        public string State { get; set; } = "pending";
        public string Detail { get; set; } = "Waiting for browser sign-in.";
        public string? VerificationUrl { get; set; }
        public DateTime? CompletedAt { get; set; }
        public CancellationTokenSource? Timeout { get; set; }
        public ClaudeDeviceAuthTransportSession? Transport { get; set; }
        public TaskCompletionSource InstructionsReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>
/// Runs <c>claude setup-token</c> as the runner user over the same SSH
/// boundary used by provider provisioning. The command's final line is the
/// long-lived OAuth token; the remote script redacts it out of the stream
/// this class parses and instead writes it directly into the shared
/// provider-auth EnvironmentFile on the host, so the token never crosses back
/// to Studio in any form.
/// </summary>
public sealed class SshClaudeDeviceAuthTransport : IClaudeDeviceAuthTransport
{
    public ClaudeDeviceAuthTransportSession Start(
        string sshTarget,
        Action<string> onOutput,
        CancellationToken cancellationToken)
    {
        var process = new Process { StartInfo = BuildStartInfo(sshTarget) };
        if (!process.Start()) throw new InvalidOperationException("SSH could not be started.");

        var completion = RunAsync(process, onOutput, cancellationToken);
        return new ClaudeDeviceAuthTransportSession(completion, () => TryKill(process));
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

    private static async Task<ClaudeDeviceAuthTransportResult> RunAsync(
        Process process,
        Action<string> onOutput,
        CancellationToken cancellationToken)
    {
        var safeMarkers = new ConcurrentBag<string>();
        using var registration = cancellationToken.Register(() => TryKill(process));
        try
        {
            await process.StandardInput.WriteAsync(RemoteScript.AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
            process.StandardInput.Close();

            var stdout = PumpAsync(process.StandardOutput, onOutput, safeMarkers, cancellationToken);
            var stderr = PumpAsync(process.StandardError, onOutput, safeMarkers, cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(stdout, stderr);
            var markers = safeMarkers.ToArray();
            return new ClaudeDeviceAuthTransportResult(
                process.ExitCode,
                markers.Contains("claude-login-status=verified", StringComparer.Ordinal),
                markers.Where(line => line.StartsWith("claude-probe-unit=", StringComparison.Ordinal))
                    .Select(line => line["claude-probe-unit=".Length..])
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
            if (line == "claude-login-status=verified"
                || line.StartsWith("claude-probe-unit=", StringComparison.Ordinal))
            {
                safeMarkers.Add(line);
                continue;
            }
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
            SilentCatch.Note(exception, "SshClaudeDeviceAuthTransport: bounded SSH process cleanup");
        }
    }

    // The remote script never lets the setup-token OAuth token itself reach
    // this process's stdout: it redacts every "sk-ant-..." occurrence in the
    // stream Studio parses and separately keeps the raw value only long enough
    // to install it into the shared provider-auth EnvironmentFile on the host.
    private const string RemoteScript = """
set -uo pipefail

if ! command -v claude >/dev/null 2>&1; then
  echo 'claude-login-status=binary-missing'
  exit 41
fi

out_tmp=$(mktemp)
trap 'rm -f "$out_tmp"' EXIT

timeout --signal=TERM --kill-after=5s 900s claude setup-token 2>&1 \
  | tee "$out_tmp" \
  | sed -E 's/sk-ant-[A-Za-z0-9_-]{10,}/[redacted]/g'
login_exit=${PIPESTATUS[0]}
if [[ "$login_exit" -ne 0 ]]; then
  echo 'claude-login-status=login-failed'
  exit "$login_exit"
fi

token=$(grep -Eo 'sk-ant-[A-Za-z0-9_-]{10,}' "$out_tmp" | tail -n1)
if [[ -z "$token" ]]; then
  echo 'claude-login-status=unverified'
  exit 42
fi

sudo -n getent group agent >/dev/null 2>&1 || sudo -n groupadd --system agent
sudo -n install -d -m 0750 -o root -g agent /etc/agent-runner

provider_auth_file=/etc/agent-runner/provider-auth.env
env_tmp=$(mktemp)
if sudo -n test -f "$provider_auth_file"; then
  sudo -n awk -F= '$1 != "CLAUDE_CODE_OAUTH_TOKEN" && $1 != "ANTHROPIC_API_KEY" { print }' \
    "$provider_auth_file" >"$env_tmp"
fi
printf 'CLAUDE_CODE_OAUTH_TOKEN=%s\n' "$token" >>"$env_tmp"
install_tmp=$(sudo -n mktemp /etc/agent-runner/.provider-auth.env.XXXXXX)
sudo -n install -m 0640 -o root -g agent "$env_tmp" "$install_tmp"
sudo -n mv -fT -- "$install_tmp" "$provider_auth_file"
rm -f "$env_tmp"

if ! sudo -n bash -c 'set -a; source /etc/agent-runner/provider-auth.env; set +a; claude auth status --text' >/dev/null 2>&1; then
  unset token
  echo 'claude-login-status=unverified'
  exit 43
fi
unset token

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
    printf 'claude-probe-unit=%s\n' "$unit"
  fi
done
echo 'claude-login-status=verified'
""";
}
