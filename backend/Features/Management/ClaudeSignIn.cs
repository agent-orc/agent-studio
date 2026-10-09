using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentStudio.Management;

public sealed record ClaudeSignInRequest(string SshTarget, string? IdempotencyKey = null,
    string Mode = "environment");

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

    ClaudeDeviceAuthTransportSession StartNative(
        string sshTarget, Action<string> onOutput, CancellationToken cancellationToken)
        => Start(sshTarget, onOutput, cancellationToken);

    ClaudeDeviceAuthTransportSession StartFenced(string sshTarget, string operationId,
        string expectedGeneration, bool native, Action<string> onOutput, CancellationToken cancellationToken)
        => native ? StartNative(sshTarget, onOutput, cancellationToken)
            : Start(sshTarget, onOutput, cancellationToken);
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
    IProviderSignInAudit audit,
    IProviderRenewalJournal? journal = null)
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
        string handle;
        AgentStudio.TaskServer.Contracts.ProviderRenewalReceiptDto? reservation = null;
        try
        {
            reservation = journal is null ? null : await journal.BeginAsync(normalizedHost,
                request.Mode == "native" ? "R2" : "R1", actor,
                request.IdempotencyKey, cancellationToken);
            handle = reservation?.OperationId ?? "claude_" + Guid.NewGuid().ToString("N");
        }
        catch (Exception) when (journal is not null)
        {
            throw new ClaudeSignInException(409, "claude-renewal-unavailable",
                "The durable host renewal could not be reserved. Check the credential binding or resume its current operation.");
        }
        if (reservation is not null && reservation.Step != "requested")
        {
            if (_sessions.TryGetValue(handle, out var existing))
            {
                await existing.InstructionsReady.Task.WaitAsync(InstructionTimeout, cancellationToken);
                lock (existing.Gate)
                {
                    if (existing.State == "pending" && existing.VerificationUrl is not null)
                        return new(existing.Handle, existing.State, existing.VerificationUrl, existing.ExpiresAt);
                }
            }
            throw new ClaudeSignInException(409, "claude-renewal-resume-required",
                "The durable renewal already started. Read its status before beginning another login.");
        }
        var state = new SessionState(
            handle,
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
            if (journal is not null)
                await journal.AdvanceAsync(state.Handle, "preflight", cancellationToken);
            state.Transport = reservation is not null
                ? transport.StartFenced(request.SshTarget.Trim(), reservation.OperationId,
                    reservation.ExpectedGeneration, request.Mode == "native",
                    line => CaptureInstructions(state, line), state.Timeout.Token)
                : request.Mode == "native"
                    ? transport.StartNative(request.SshTarget.Trim(),
                        line => CaptureInstructions(state, line), state.Timeout.Token)
                    : transport.Start(request.SshTarget.Trim(),
                        line => CaptureInstructions(state, line), state.Timeout.Token);
            _ = ObserveCompletionAsync(state);

            await state.InstructionsReady.Task.WaitAsync(InstructionTimeout, cancellationToken);
            if (journal is not null)
                await journal.AdvanceAsync(state.Handle, "awaiting-human", cancellationToken);
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

    public async Task<ClaudeSignInStatusResponse?> GetAsync(
        string hostId, string handle, CancellationToken ct)
    {
        if (journal is null) return Get(hostId, handle);
        var receipt = await journal.GetAsync(handle, ct);
        if (receipt is null || receipt.HostId != hostId || receipt.Method is not ("R1" or "R2")) return null;
        if (receipt.Step == "installed") receipt = await journal.TryVerifyAsync(receipt, ct);
        if (receipt.Step == "complete")
        {
            if (_sessions.TryGetValue(handle, out var state))
                await CompleteAsync(state, "completed", "Claude renewal passed both runner real-request checks.", "completed");
            return new(handle, "completed", "Claude renewal passed both runner real-request checks.",
                receipt.UpdatedAt, receipt.Deadline, receipt.UpdatedAt);
        }
        if (receipt.Step is "cancelled" or "failed" or "recovery-required")
            return new(handle, "failed", receipt.Step == "recovery-required"
                ? "Host renewal needs recovery before another login can start."
                : "Host renewal ended before verification.", receipt.UpdatedAt,
                receipt.Deadline, receipt.UpdatedAt);
        return Get(hostId, handle) ?? new(handle, "pending",
            "Host renewal is awaiting a generation-matched real request from both runner units.",
            receipt.UpdatedAt, receipt.Deadline, null);
    }

    public async Task<ClaudeSignInStatusResponse?> CancelAsync(
        string hostId, string handle, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(handle, out var state) || state.HostId != hostId)
            return await GetAsync(hostId, handle, ct);
        state.Transport?.Cancel();
        if (journal is not null) await TryMarkRecoveryAsync(handle);
        await CompleteAsync(state, "failed", "Claude sign-in was cancelled; host recovery may be required.", "cancelled");
        return await GetAsync(hostId, handle, ct);
    }

    internal static string? Validate(string hostId, ClaudeSignInRequest request)
    {
        if (string.IsNullOrWhiteSpace(hostId) || !RunnerIdPattern().IsMatch(hostId.Trim()))
            return "Host identity is required and may contain letters, numbers, dots, underscores, and hyphens.";
        if (request is null
            || string.IsNullOrWhiteSpace(request.SshTarget)
            || !SshTargetPattern().IsMatch(request.SshTarget.Trim()))
            return "SSH target must be a configured alias or user@host without shell characters.";
        if (request.Mode is not ("environment" or "native"))
            return "Claude sign-in mode must be environment or native.";
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
                if (journal is not null)
                {
                    await journal.AdvanceAsync(state.Handle, "staged", CancellationToken.None);
                    await journal.AdvanceAsync(state.Handle, "installed", CancellationToken.None);
                    lock (state.Gate)
                        state.Detail = "Claude installed the host credential. Waiting for real requests from both runner units.";
                    return;
                }
                var detail = result.RestartedServices.Count > 0
                    ? "Claude sign-in completed. Runner services restarted and a fresh provider probe is expected."
                    : "Claude sign-in completed. Waiting for the runner's next provider probe.";
                await CompleteAsync(state, "completed", detail, "completed").ConfigureAwait(false);
            }
            else
            {
                if (journal is not null)
                    await journal.AdvanceAsync(state.Handle, "recovery-required", CancellationToken.None);
                await CompleteAsync(
                    state,
                    "failed",
                    "Claude sign-in did not complete or login status could not be verified.",
                    "failed").ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (state.Timeout?.IsCancellationRequested == true)
        {
            if (journal is not null)
                await TryMarkRecoveryAsync(state.Handle);
            await CompleteAsync(state, "failed", "Claude sign-in timed out after 15 minutes.", "timeout").ConfigureAwait(false);
        }
        catch (Exception)
        {
            if (journal is not null)
                await TryMarkRecoveryAsync(state.Handle);
            await CompleteAsync(state, "failed", "The remote Claude sign-in process failed.", "failed").ConfigureAwait(false);
        }
        finally
        {
            state.Timeout?.Dispose();
            state.Timeout = null;
        }
    }

    private async Task TryMarkRecoveryAsync(string handle)
    {
        try { await journal!.AdvanceAsync(handle, "recovery-required", CancellationToken.None); }
        catch (Exception exception)
        {
            SilentCatch.Note(exception, "Claude renewal recovery receipt unavailable; binding remains fenced");
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
        => StartScript(sshTarget, onOutput, cancellationToken, FencedScript(RemoteScript, "", ""));

    public ClaudeDeviceAuthTransportSession StartNative(
        string sshTarget, Action<string> onOutput, CancellationToken cancellationToken)
        => StartScript(sshTarget, onOutput, cancellationToken, FencedScript(NativeScript, "", ""));

    public ClaudeDeviceAuthTransportSession StartFenced(string sshTarget, string operationId,
        string expectedGeneration, bool native, Action<string> onOutput, CancellationToken cancellationToken)
        => StartScript(sshTarget, onOutput, cancellationToken,
            FencedScript(native ? NativeScript : RemoteScript, operationId, expectedGeneration));

    internal static string BuildFencedScriptForTest(string operationId, string expectedGeneration, bool native)
        => FencedScript(native ? NativeScript : RemoteScript, operationId, expectedGeneration);

    private static string FencedScript(string script, string operationId, string expectedGeneration)
    {
        static bool Safe(string value) => value.Length <= 128 &&
            value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' or ':');
        if (!Safe(operationId) || !Safe(expectedGeneration))
            throw new ArgumentException("Renewal fence identifiers are invalid.");
        return script.Replace("__OPERATION_ID__", operationId, StringComparison.Ordinal)
            .Replace("__EXPECTED_GENERATION__", expectedGeneration, StringComparison.Ordinal);
    }

    private static ClaudeDeviceAuthTransportSession StartScript(
        string sshTarget, Action<string> onOutput, CancellationToken cancellationToken, string script)
    {
        var process = new Process { StartInfo = BuildStartInfo(sshTarget) };
        if (!process.Start()) throw new InvalidOperationException("SSH could not be started.");

        var completion = RunAsync(process, onOutput, cancellationToken, script);
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
        CancellationToken cancellationToken,
        string script)
    {
        var safeMarkers = new ConcurrentBag<string>();
        using var registration = cancellationToken.Register(() => TryKill(process));
        try
        {
            await process.StandardInput.WriteAsync(script.AsMemory(), cancellationToken);
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
operation_id='__OPERATION_ID__'
expected_generation='__EXPECTED_GENERATION__'

umask 077
mkdir -p "$HOME/.claude"
exec 9>"$HOME/.claude/.agent-studio-renewal.lock"
flock -n 9 || { echo 'claude-login-status=binding-busy'; exit 73; }
if [[ -n "$expected_generation" ]]; then
  current_generation=absent
  if sudo -n test -f /etc/agent-runner/provider-auth.env; then
    current_generation=$(sudo -n awk -F= '$1 == "AGENT_STUDIO_CLAUDE_AUTH_GENERATION" { print $2 }' /etc/agent-runner/provider-auth.env)
    [[ -n "$current_generation" ]] || current_generation=unknown
  fi
  [[ "$current_generation" == "$expected_generation" ]] || {
    echo 'claude-login-status=stale-generation'; exit 46;
  }
fi
if pgrep -u "$(id -u)" -x claude >/dev/null 2>&1; then
  echo 'claude-login-status=workers-busy'
  exit 73
fi
if [[ -n "$operation_id" ]]; then
  receipt_dir="$HOME/.local/state/agent-studio/provider-renewal"
  mkdir -p "$receipt_dir"
  chmod 0700 "$receipt_dir"
  receipt_file="$receipt_dir/$operation_id"
  [[ ! -e "$receipt_file" ]] || { echo 'claude-login-status=recovery-required'; exit 74; }
  printf 'started\n' >"$receipt_file"
fi

if ! command -v claude >/dev/null 2>&1; then
  echo 'claude-login-status=binary-missing'
  exit 41
fi

out_tmp=$(mktemp)
env_tmp=$(mktemp)
provider_auth_file=/etc/agent-runner/provider-auth.env
rollback_file=
install_committed=0
had_prior_file=0
units=()
if sudo -n systemctl cat agent-host.service >/dev/null 2>&1; then
  units+=(agent-host.service)
elif sudo -n systemctl cat agent-runner.service >/dev/null 2>&1; then
  units+=(agent-runner.service)
fi
if sudo -n systemctl cat agent-runner-review.service >/dev/null 2>&1; then
  units+=(agent-runner-review.service)
fi
rollback_environment_on_failure() {
  result=$?
  trap - EXIT
  trap '' HUP INT TERM
  rm -f "$out_tmp" "$env_tmp"
  if (( result == 0 )); then
    [[ -z "$rollback_file" ]] || sudo -n rm -f -- "$rollback_file"
    exit 0
  fi
  if (( ! install_committed )); then
    [[ -z "$rollback_file" ]] || sudo -n rm -f -- "$rollback_file"
    exit "$result"
  fi
  rollback_ok=1
  if (( had_prior_file )); then
    restore_tmp=$(sudo -n mktemp /etc/agent-runner/.provider-auth.restore.XXXXXX) || rollback_ok=0
    if (( rollback_ok )) && ! sudo -n install -m 0640 -o root -g agent "$rollback_file" "$restore_tmp"; then rollback_ok=0; fi
    if (( rollback_ok )) && ! sudo -n mv -fT -- "$restore_tmp" "$provider_auth_file"; then rollback_ok=0; fi
    [[ -z "${restore_tmp:-}" ]] || sudo -n rm -f -- "$restore_tmp"
  else
    sudo -n rm -f -- "$provider_auth_file" || rollback_ok=0
  fi
  if (( rollback_ok )); then
    for unit in "${units[@]}"; do
      if [[ "$unit" == agent-runner-review.service ]]; then
        sudo -n /usr/local/sbin/agent-runner-deploy restart-review || rollback_ok=0
      else
        sudo -n systemctl restart "$unit" || rollback_ok=0
      fi
      pid=$(sudo -n systemctl show --property=MainPID --value "$unit")
      [[ "$pid" =~ ^[1-9][0-9]*$ ]] || rollback_ok=0
      if (( rollback_ok )) && ! sudo -n bash -c '
        set -a; source /etc/agent-runner/provider-auth.env; set +a
        oauth=${CLAUDE_CODE_OAUTH_TOKEN:-}; api=${ANTHROPIC_API_KEY:-}
        [[ -n "$oauth" || -n "$api" ]] || exit 1
        seen_oauth=0; seen_api=0
        while IFS= read -r -d "" entry; do
          [[ -z "$oauth" || "$entry" != "CLAUDE_CODE_OAUTH_TOKEN=$oauth" ]] || seen_oauth=1
          [[ -z "$api" || "$entry" != "ANTHROPIC_API_KEY=$api" ]] || seen_api=1
        done <"/proc/$1/environ"
        [[ ( -z "$oauth" || "$seen_oauth" == 1 ) && ( -z "$api" || "$seen_api" == 1 ) ]]
      ' bash "$pid"; then rollback_ok=0; fi
    done
  fi
  if (( ! rollback_ok )); then
    for unit in "${units[@]}"; do sudo -n systemctl stop "$unit" || true; done
    [[ -z "${receipt_file:-}" ]] || printf 'recovery-required\n' >"$receipt_file"
    echo 'claude-login-status=recovery-required'
  else
    echo 'claude-login-status=rollback-restored'
    [[ -z "$rollback_file" ]] || sudo -n rm -f -- "$rollback_file"
  fi
  exit "$result"
}
trap rollback_environment_on_failure EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

timeout --signal=TERM --kill-after=5s 900s claude setup-token 2>&1 \
  | tee "$out_tmp" \
  | grep -Eo 'https://(claude.ai|console.anthropic.com)/[^[:space:]<>]+'
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
sudo -n install -d -m 0750 -o root -g agent /etc/agent-runner || exit 74

generation="renewal_$(tr -d '-' </proc/sys/kernel/random/uuid)"
rollback_file="/etc/agent-runner/.provider-auth.rollback.${operation_id:-$generation}"
if sudo -n test -f "$provider_auth_file"; then
  sudo -n install -m 0600 -o root -g root "$provider_auth_file" "$rollback_file" || exit 74
  had_prior_file=1
  sudo -n awk -F= '$1 != "CLAUDE_CODE_OAUTH_TOKEN" && $1 != "ANTHROPIC_API_KEY" && $1 != "AGENT_STUDIO_CLAUDE_AUTH_GENERATION" { print }' \
    "$provider_auth_file" >"$env_tmp" || exit 74
fi
printf 'CLAUDE_CODE_OAUTH_TOKEN=%s\n' "$token" >>"$env_tmp" || exit 74
printf 'AGENT_STUDIO_CLAUDE_AUTH_GENERATION=%s\n' "$generation" >>"$env_tmp" || exit 74
install_tmp=$(sudo -n mktemp /etc/agent-runner/.provider-auth.env.XXXXXX) || exit 74
sudo -n install -m 0640 -o root -g agent "$env_tmp" "$install_tmp" || exit 74
install_committed=1
sudo -n mv -fT -- "$install_tmp" "$provider_auth_file" || exit 74

if ! sudo -n bash -c 'set -a; source /etc/agent-runner/provider-auth.env; set +a; claude auth status --text' >/dev/null 2>&1; then
  unset token
  echo 'claude-login-status=unverified'
  exit 43
fi
unset token

for unit in "${units[@]}"; do
  if [[ "$unit" == agent-runner-review.service ]]; then
    sudo -n /usr/local/sbin/agent-runner-deploy restart-review || exit 44
  else
    sudo -n systemctl restart "$unit" || exit 44
  fi
  pid=$(sudo -n systemctl show --property=MainPID --value "$unit")
  [[ "$pid" =~ ^[1-9][0-9]*$ ]] || exit 44
  if ! sudo -n bash -c 'set -a; source /etc/agent-runner/provider-auth.env; set +a; while IFS= read -r -d "" entry; do [[ "$entry" == "CLAUDE_CODE_OAUTH_TOKEN=$CLAUDE_CODE_OAUTH_TOKEN" ]] && exit 0; done <"/proc/$1/environ"; exit 1' bash "$pid"; then
    exit 44
  fi
  printf 'claude-probe-unit=%s\n' "$unit"
done
echo 'claude-login-status=verified'
""";

    // Native login keeps the CLI's refreshable store on this host. Existing
    // environment mode must be removed by the owned host configuration flow
    // before starting; this adapter never guesses which value to discard.
    private const string NativeScript = """
set -uo pipefail
operation_id='__OPERATION_ID__'
expected_generation='__EXPECTED_GENERATION__'
umask 077
mkdir -p "$HOME/.claude"
exec 9>"$HOME/.claude/.agent-studio-renewal.lock"
flock -n 9 || { echo 'claude-login-status=binding-busy'; exit 73; }
current_generation=absent
previous_store_digest=absent
if [[ -f "$HOME/.claude/.credentials.json" ]]; then
  current_generation="native-cli-store:$(date -r "$HOME/.claude/.credentials.json" +%s%3N)"
  previous_store_digest=$(sha256sum "$HOME/.claude/.credentials.json" | cut -d ' ' -f1) || {
    echo 'claude-login-status=recovery-required'; exit 74;
  }
fi
if [[ -n "$expected_generation" ]]; then
  [[ "$current_generation" == "$expected_generation" ]] || {
    echo 'claude-login-status=stale-generation'; exit 46;
  }
fi
if pgrep -u "$(id -u)" -x claude >/dev/null 2>&1; then
  echo 'claude-login-status=workers-busy'
  exit 73
fi
if [[ -n "$operation_id" ]]; then
  receipt_dir="$HOME/.local/state/agent-studio/provider-renewal"
  mkdir -p "$receipt_dir"
  chmod 0700 "$receipt_dir"
  receipt_file="$receipt_dir/$operation_id"
  [[ ! -e "$receipt_file" ]] || { echo 'claude-login-status=recovery-required'; exit 74; }
  printf 'started\n' >"$receipt_file"
fi
for unit in agent-host.service agent-runner.service agent-runner-review.service; do
  sudo -n systemctl cat "$unit" >/dev/null 2>&1 || continue
  pid=$(sudo -n systemctl show --property=MainPID --value "$unit")
  [[ "$pid" =~ ^[1-9][0-9]*$ ]] || continue
  if sudo -n bash -c 'while IFS= read -r -d "" entry; do case "$entry" in CLAUDE_CODE_OAUTH_TOKEN=*|ANTHROPIC_API_KEY=*) exit 0;; esac; done <"/proc/$1/environ"; exit 1' bash "$pid"; then
    echo 'claude-login-status=environment-mode-active'
    exit 45
  fi
done
credential_store="$HOME/.claude/.credentials.json"
rollback_store=$(mktemp "$HOME/.claude/.agent-studio-rollback.XXXXXXXX") || {
  echo 'claude-login-status=recovery-required'; exit 74;
}
had_prior_store=0
if [[ -f "$credential_store" ]]; then
  if ! cp -p -- "$credential_store" "$rollback_store" || ! chmod 0600 "$rollback_store"; then
    rm -f "$rollback_store"
    echo 'claude-login-status=recovery-required'; exit 74
  fi
  had_prior_store=1
fi
out_tmp=$(mktemp) || { rm -f "$rollback_store"; exit 74; }
rollback_native_on_failure() {
  result=$?
  trap - EXIT
  trap '' HUP INT TERM
  rm -f "$out_tmp"
  if (( result == 0 )); then
    rm -f "$rollback_store"
    exit 0
  fi
  # A browser timeout or cancellation before replacement needs no rebind.
  if (( had_prior_store )) && [[ -f "$credential_store" ]] \
      && [[ "$(sha256sum "$credential_store" | cut -d ' ' -f1)" == "$previous_store_digest" ]] \
      && [[ "native-cli-store:$(date -r "$credential_store" +%s%3N)" == "$current_generation" ]]; then
    rm -f "$rollback_store"
    exit "$result"
  fi
  if (( ! had_prior_store )) && [[ ! -e "$credential_store" ]]; then
    rm -f "$rollback_store"
    exit "$result"
  fi
  rollback_ok=1
  if (( had_prior_store )); then
    restore_tmp=$(mktemp "$HOME/.claude/.agent-studio-restore.XXXXXXXX") || rollback_ok=0
    if (( rollback_ok )) && ! cp -p -- "$rollback_store" "$restore_tmp"; then rollback_ok=0; fi
    if (( rollback_ok )) && ! mv -f -- "$restore_tmp" "$credential_store"; then rollback_ok=0; fi
    [[ -z "${restore_tmp:-}" ]] || rm -f "$restore_tmp"
  elif ! rm -f -- "$credential_store"; then
    rollback_ok=0
  fi
  if (( rollback_ok )) && (( had_prior_store )) && {
      [[ "$(sha256sum "$credential_store" | cut -d ' ' -f1)" != "$previous_store_digest" ]] \
      || [[ "native-cli-store:$(date -r "$credential_store" +%s%3N)" != "$current_generation" ]]; }; then
    rollback_ok=0
  fi
  if (( rollback_ok )) && (( ! had_prior_store )) && [[ -e "$credential_store" ]]; then
    rollback_ok=0
  fi
  if (( rollback_ok )) && { ! command -v agent-host >/dev/null 2>&1 \
      || ! agent-host --rebind-provider-auth claude --drained >/dev/null 2>&1; }; then
    rollback_ok=0
  fi
  if (( rollback_ok )); then
    for unit in agent-host.service agent-runner.service agent-runner-review.service; do
      sudo -n systemctl cat "$unit" >/dev/null 2>&1 || continue
      if [[ "$unit" == agent-runner-review.service ]]; then
        sudo -n /usr/local/sbin/agent-runner-deploy restart-review || rollback_ok=0
      else
        sudo -n systemctl restart "$unit" || rollback_ok=0
      fi
      pid=$(sudo -n systemctl show --property=MainPID --value "$unit")
      [[ "$pid" =~ ^[1-9][0-9]*$ ]] || rollback_ok=0
    done
  fi
  if (( ! rollback_ok )); then
    # A partial rebind or restart is unsafe even when the file was restored.
    for unit in agent-host.service agent-runner.service agent-runner-review.service; do
      sudo -n systemctl cat "$unit" >/dev/null 2>&1 || continue
      sudo -n systemctl stop "$unit" || true
    done
    [[ -z "${receipt_file:-}" ]] || printf 'recovery-required\n' >"$receipt_file"
    echo 'claude-login-status=recovery-required'
  else
    echo 'claude-login-status=rollback-restored'
    rm -f "$rollback_store"
  fi
  exit "$result"
}
trap rollback_native_on_failure EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM
timeout --signal=TERM --kill-after=5s 900s claude /login 2>&1 \
  | tee "$out_tmp" \
  | grep -Eo 'https://(claude.ai|console.anthropic.com)/[^[:space:]<>]+'
login_exit=${PIPESTATUS[0]}
[[ "$login_exit" == 0 ]] || exit "$login_exit"
if ! claude auth status --text >/dev/null 2>&1; then
  echo 'claude-login-status=unverified'
  exit 42
fi
if [[ ! -f "$HOME/.claude/.credentials.json" ]]; then
  echo 'claude-login-status=unchanged-generation'
  exit 42
fi
new_store_digest=$(sha256sum "$HOME/.claude/.credentials.json" | cut -d ' ' -f1) || {
  echo 'claude-login-status=recovery-required'; exit 74;
}
if ! [[ "$previous_store_digest" != "$new_store_digest" ]]; then
  echo 'claude-login-status=unchanged-generation'
  exit 42
fi
published_generation="native-cli-store:$(date -r "$HOME/.claude/.credentials.json" +%s%3N)"
if [[ "$published_generation" == "$current_generation" ]]; then
  previous_ms=${current_generation#native-cli-store:}
  next_second=$((previous_ms / 1000 + 1))
  now_second=$(date +%s)
  if (( now_second > next_second )); then next_second=$now_second; fi
  touch -m -d "@$next_second" "$HOME/.claude/.credentials.json" || {
    echo 'claude-login-status=recovery-required'; exit 74;
  }
  published_generation="native-cli-store:$(date -r "$HOME/.claude/.credentials.json" +%s%3N)"
fi
if ! [[ "$published_generation" != "$expected_generation" ]]; then
  echo 'claude-login-status=unchanged-generation'
  exit 42
fi
if ! command -v agent-host >/dev/null 2>&1 \
    || ! agent-host --rebind-provider-auth claude --drained >/dev/null 2>&1; then
  echo 'claude-login-status=rebind-required'
  exit 43
fi
for unit in agent-host.service agent-runner.service agent-runner-review.service; do
  sudo -n systemctl cat "$unit" >/dev/null 2>&1 || continue
  if [[ "$unit" == agent-runner-review.service ]]; then
    sudo -n /usr/local/sbin/agent-runner-deploy restart-review || exit 44
  else
    sudo -n systemctl restart "$unit" || exit 44
  fi
  pid=$(sudo -n systemctl show --property=MainPID --value "$unit")
  [[ "$pid" =~ ^[1-9][0-9]*$ ]] || exit 44
  printf 'claude-probe-unit=%s\n' "$unit"
done
echo 'claude-login-status=verified'
""";
}
