using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using AgentStudio.Bus;

namespace AgentStudio.Management;

public sealed record CodexSignInRequest(string SshTarget, string? IdempotencyKey = null);

public sealed record CodexSignInStartResponse(
    string Handle,
    string State,
    string VerificationUrl,
    string UserCode,
    DateTime ExpiresAt);

public sealed record CodexSignInStatusResponse(
    string Handle,
    string State,
    string Detail,
    DateTime RequestedAt,
    DateTime ExpiresAt,
    DateTime? CompletedAt);

public sealed class CodexSignInException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

public sealed record CodexDeviceAuthTransportResult(
    int ExitCode,
    bool LoginStatusVerified,
    IReadOnlyList<string> RestartedServices);

public sealed class CodexDeviceAuthTransportSession(
    Task<CodexDeviceAuthTransportResult> completion,
    Action cancel)
{
    public Task<CodexDeviceAuthTransportResult> Completion { get; } = completion;
    public void Cancel() => cancel();
}

public interface ICodexDeviceAuthTransport
{
    CodexDeviceAuthTransportSession Start(
        string sshTarget,
        Action<string> onOutput,
        CancellationToken cancellationToken);

    CodexDeviceAuthTransportSession StartFenced(string sshTarget, string operationId,
        string expectedGeneration, Action<string> onOutput, CancellationToken cancellationToken)
        => Start(sshTarget, onOutput, cancellationToken);
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
/// Owns bounded, in-memory Codex device-auth sessions. The CLI writes its
/// credential only into the remote runner user's Codex store. Studio retains
/// the one-time browser instructions only while the process is pending and
/// never writes the transcript, code, or credential to durable state.
/// </summary>
public sealed partial class CodexSignInCoordinator(
    ICodexDeviceAuthTransport transport,
    IProviderSignInAudit audit,
    IProviderRenewalJournal? journal = null)
{
    internal static readonly TimeSpan SessionTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan InstructionTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan TerminalSessionRetention = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);
    private readonly object _startGate = new();

    public async Task<CodexSignInStartResponse> StartAsync(
        string hostId,
        CodexSignInRequest request,
        string actor,
        CancellationToken cancellationToken)
    {
        var validation = Validate(hostId, request);
        if (validation is not null)
            throw new CodexSignInException(400, "invalid-codex-sign-in-request", validation);

        var normalizedHost = hostId.Trim();
        var now = DateTime.UtcNow;
        string handle;
        AgentStudio.TaskServer.Contracts.ProviderRenewalReceiptDto? reservation = null;
        try
        {
            reservation = journal is null ? null : await journal.BeginAsync(normalizedHost, "R3", actor,
                request.IdempotencyKey, cancellationToken);
            handle = reservation?.OperationId ?? "codex_" + Guid.NewGuid().ToString("N");
        }
        catch (Exception) when (journal is not null)
        {
            throw new CodexSignInException(409, "codex-renewal-unavailable",
                "The durable host renewal could not be reserved. Check the credential binding or resume its current operation.");
        }
        if (reservation is not null && reservation.Step != "requested")
        {
            if (_sessions.TryGetValue(handle, out var existing))
            {
                await existing.InstructionsReady.Task.WaitAsync(InstructionTimeout, cancellationToken);
                lock (existing.Gate)
                {
                    if (existing.State == "pending" && existing.VerificationUrl is not null &&
                        existing.UserCode is not null)
                        return new(existing.Handle, existing.State, existing.VerificationUrl,
                            existing.UserCode, existing.ExpiresAt);
                }
            }
            throw new CodexSignInException(409, "codex-renewal-resume-required",
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
                throw new CodexSignInException(409, "codex-sign-in-active", "A Codex sign-in is already pending for this host.");
            if (!_sessions.TryAdd(state.Handle, state))
                throw new CodexSignInException(500, "codex-sign-in-handle-failed", "The Codex sign-in session could not be created.");
        }

        state.Timeout = new CancellationTokenSource(SessionTimeout);
        try
        {
            if (journal is not null)
                await journal.AdvanceAsync(state.Handle, "preflight", cancellationToken);
            state.Transport = reservation is null
                ? transport.Start(request.SshTarget.Trim(),
                    line => CaptureInstructions(state, line), state.Timeout.Token)
                : transport.StartFenced(request.SshTarget.Trim(), reservation.OperationId,
                    reservation.ExpectedGeneration,
                    line => CaptureInstructions(state, line), state.Timeout.Token);
            _ = ObserveCompletionAsync(state);

            await state.InstructionsReady.Task.WaitAsync(InstructionTimeout, cancellationToken);
            if (journal is not null)
                await journal.AdvanceAsync(state.Handle, "awaiting-human", cancellationToken);
            lock (state.Gate)
            {
                if (state.State != "pending" || state.VerificationUrl is null || state.UserCode is null)
                    throw new CodexSignInException(502, "codex-device-auth-unavailable", state.Detail);
                return new CodexSignInStartResponse(
                    state.Handle,
                    state.State,
                    state.VerificationUrl,
                    state.UserCode,
                    state.ExpiresAt);
            }
        }
        catch (TimeoutException)
        {
            state.Transport?.Cancel();
            await CompleteAsync(state, "failed", "Codex did not provide device sign-in instructions.", "failed");
            throw new CodexSignInException(
                502,
                "codex-device-auth-unavailable",
                "Codex did not provide device sign-in instructions.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            state.Transport?.Cancel();
            await CompleteAsync(state, "failed", "The sign-in request was cancelled before instructions were returned.", "cancelled");
            throw;
        }
        catch (CodexSignInException)
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
            throw new CodexSignInException(
                502,
                "codex-device-auth-start-failed",
                "The SSH device-auth process could not be started.");
        }
    }

    public CodexSignInStatusResponse? Get(string hostId, string handle)
    {
        if (!_sessions.TryGetValue(handle, out var session)
            || !string.Equals(session.HostId, hostId, StringComparison.Ordinal)) return null;
        lock (session.Gate)
        {
            return new CodexSignInStatusResponse(
                session.Handle,
                session.State,
                session.Detail,
                session.RequestedAt,
                session.ExpiresAt,
                session.CompletedAt);
        }
    }

    public async Task<CodexSignInStatusResponse?> GetAsync(
        string hostId, string handle, CancellationToken ct)
    {
        if (journal is null) return Get(hostId, handle);
        var receipt = await journal.GetAsync(handle, ct);
        if (receipt is null || receipt.HostId != hostId || receipt.Method != "R3") return null;
        if (receipt.Step == "installed") receipt = await journal.TryVerifyAsync(receipt, ct);
        if (receipt.Step == "complete")
        {
            if (_sessions.TryGetValue(handle, out var state))
                await CompleteAsync(state, "completed", "Codex renewal passed both runner real-request checks.", "completed");
            return new(handle, "completed", "Codex renewal passed both runner real-request checks.",
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

    public async Task<CodexSignInStatusResponse?> CancelAsync(
        string hostId, string handle, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(handle, out var state) || state.HostId != hostId)
            return await GetAsync(hostId, handle, ct);
        state.Transport?.Cancel();
        if (journal is not null) await TryMarkRecoveryAsync(handle);
        await CompleteAsync(state, "failed", "Codex sign-in was cancelled; host recovery may be required.", "cancelled");
        return await GetAsync(hostId, handle, ct);
    }

    internal static string? Validate(string hostId, CodexSignInRequest request)
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
                && IsOpenAiSignInHost(uri.Host))
                state.VerificationUrl = uri.ToString();

            if (!line.Contains("http", StringComparison.OrdinalIgnoreCase))
            {
                var codeMatch = UserCodePattern().Match(line.ToUpperInvariant());
                if (codeMatch.Success) state.UserCode = codeMatch.Value;
            }

            if (state.VerificationUrl is not null && state.UserCode is not null)
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
                if (journal is not null)
                {
                    await journal.AdvanceAsync(state.Handle, "staged", CancellationToken.None);
                    await journal.AdvanceAsync(state.Handle, "installed", CancellationToken.None);
                    lock (state.Gate)
                        state.Detail = "Codex installed the host credential. Waiting for real requests from both runner units.";
                    return;
                }
                var detail = result.RestartedServices.Count > 0
                    ? "Codex sign-in completed. Runner services restarted and a fresh provider probe is expected."
                    : "Codex sign-in completed. Waiting for the runner's next provider probe.";
                await CompleteAsync(state, "completed", detail, "completed").ConfigureAwait(false);
            }
            else
            {
                if (journal is not null)
                    await journal.AdvanceAsync(state.Handle, "recovery-required", CancellationToken.None);
                await CompleteAsync(
                    state,
                    "failed",
                    "Codex sign-in did not complete or login status could not be verified.",
                    "failed").ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (state.Timeout?.IsCancellationRequested == true)
        {
            if (journal is not null)
                await TryMarkRecoveryAsync(state.Handle);
            await CompleteAsync(state, "failed", "Codex sign-in timed out after 15 minutes.", "timeout").ConfigureAwait(false);
        }
        catch (Exception)
        {
            if (journal is not null)
                await TryMarkRecoveryAsync(state.Handle);
            await CompleteAsync(state, "failed", "The remote Codex sign-in process failed.", "failed").ConfigureAwait(false);
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
            SilentCatch.Note(exception, "Codex renewal recovery receipt unavailable; binding remains fenced");
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
                "codex",
                actor,
                auditOutcome)).ConfigureAwait(false);
        }
    }

    private static bool IsOpenAiSignInHost(string host)
        => host.Equals("openai.com", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".openai.com", StringComparison.OrdinalIgnoreCase)
           || host.Equals("chatgpt.com", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".chatgpt.com", StringComparison.OrdinalIgnoreCase);

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
        public string? UserCode { get; set; }
        public DateTime? CompletedAt { get; set; }
        public CancellationTokenSource? Timeout { get; set; }
        public CodexDeviceAuthTransportSession? Transport { get; set; }
        public TaskCompletionSource InstructionsReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>
/// Runs the fixed device-auth script through the same local SSH boundary used
/// by provider provisioning. Output is streamed to the coordinator parser and
/// is never logged or persisted by Studio.
/// </summary>
public sealed class SshCodexDeviceAuthTransport : ICodexDeviceAuthTransport
{
    public CodexDeviceAuthTransportSession Start(
        string sshTarget,
        Action<string> onOutput,
        CancellationToken cancellationToken)
    {
        var process = new Process { StartInfo = BuildStartInfo(sshTarget) };
        if (!process.Start()) throw new InvalidOperationException("SSH could not be started.");

        var completion = RunAsync(process, onOutput, cancellationToken,
            FencedScript("", ""));
        return new CodexDeviceAuthTransportSession(completion, () => TryKill(process));
    }

    public CodexDeviceAuthTransportSession StartFenced(string sshTarget, string operationId,
        string expectedGeneration, Action<string> onOutput, CancellationToken cancellationToken)
    {
        var script = FencedScript(operationId, expectedGeneration);
        var process = new Process { StartInfo = BuildStartInfo(sshTarget) };
        if (!process.Start()) throw new InvalidOperationException("SSH could not be started.");
        return new CodexDeviceAuthTransportSession(
            RunAsync(process, onOutput, cancellationToken, script), () => TryKill(process));
    }

    private static string FencedScript(string operationId, string expectedGeneration)
    {
        static bool Safe(string value) => value.Length <= 128 &&
            value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' or ':');
        if (!Safe(operationId) || !Safe(expectedGeneration))
            throw new ArgumentException("Renewal fence identifiers are invalid.");
        return RemoteScript.Replace("__OPERATION_ID__", operationId, StringComparison.Ordinal)
            .Replace("__EXPECTED_GENERATION__", expectedGeneration, StringComparison.Ordinal);
    }

    internal static string BuildFencedScriptForTest(string operationId, string expectedGeneration)
        => FencedScript(operationId, expectedGeneration);

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

    private static async Task<CodexDeviceAuthTransportResult> RunAsync(
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
            return new CodexDeviceAuthTransportResult(
                process.ExitCode,
                markers.Contains("codex-login-status=verified", StringComparer.Ordinal),
                markers.Where(line => line.StartsWith("codex-probe-unit=", StringComparison.Ordinal))
                    .Select(line => line["codex-probe-unit=".Length..])
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
            if (line == "codex-login-status=verified"
                || line.StartsWith("codex-probe-unit=", StringComparison.Ordinal))
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
            SilentCatch.Note(exception, "SshCodexDeviceAuthTransport: bounded SSH process cleanup");
        }
    }

    private const string RemoteScript = """
set -uo pipefail
operation_id='__OPERATION_ID__'
expected_generation='__EXPECTED_GENERATION__'

umask 077
mkdir -p "$HOME/.codex"
exec 9>"$HOME/.codex/.agent-studio-renewal.lock"
flock -n 9 || { echo 'codex-login-status=binding-busy'; exit 73; }
if [[ -n "$expected_generation" ]]; then
  current_generation=absent
  if [[ -f "$HOME/.codex/auth.json" ]]; then
    current_generation="native-cli-store:$(date -r "$HOME/.codex/auth.json" +%s%3N)"
  fi
  [[ "$current_generation" == "$expected_generation" ]] || {
    echo 'codex-login-status=stale-generation'; exit 46;
  }
fi
if pgrep -u "$(id -u)" -x codex >/dev/null 2>&1; then
  echo 'codex-login-status=workers-busy'
  exit 73
fi
if [[ -n "$operation_id" ]]; then
  receipt_dir="$HOME/.local/state/agent-studio/provider-renewal"
  mkdir -p "$receipt_dir"
  chmod 0700 "$receipt_dir"
  receipt_file="$receipt_dir/$operation_id"
  [[ ! -e "$receipt_file" ]] || { echo 'codex-login-status=recovery-required'; exit 74; }
  printf 'started\n' >"$receipt_file"
fi

if ! command -v codex >/dev/null 2>&1; then
  echo 'codex-login-status=binary-missing'
  exit 41
fi

credential_store="$HOME/.codex/auth.json"
rollback_store=$(mktemp "$HOME/.codex/.agent-studio-rollback.XXXXXXXX") || {
  echo 'codex-login-status=recovery-required'; exit 74;
}
had_prior_store=0
previous_store_digest=absent
previous_store_generation=absent
if [[ -f "$credential_store" ]]; then
  previous_store_digest=$(sha256sum "$credential_store" | cut -d ' ' -f1) || exit 74
  previous_store_generation="native-cli-store:$(date -r "$credential_store" +%s%3N)"
  if ! cp -p -- "$credential_store" "$rollback_store" || ! chmod 0600 "$rollback_store"; then
    rm -f "$rollback_store"
    echo 'codex-login-status=recovery-required'; exit 74
  fi
  had_prior_store=1
fi
out_tmp=$(mktemp) || { rm -f "$rollback_store"; exit 74; }
rollback_codex_on_failure() {
  result=$?
  trap - EXIT
  trap '' HUP INT TERM
  rm -f "$out_tmp"
  if (( result == 0 )); then
    rm -f "$rollback_store"
    exit 0
  fi
  if (( had_prior_store )) && [[ -f "$credential_store" ]] \
      && [[ "$(sha256sum "$credential_store" | cut -d ' ' -f1)" == "$previous_store_digest" ]] \
      && [[ "native-cli-store:$(date -r "$credential_store" +%s%3N)" == "$previous_store_generation" ]]; then
    rm -f "$rollback_store"
    exit "$result"
  fi
  if (( ! had_prior_store )) && [[ ! -e "$credential_store" ]]; then
    rm -f "$rollback_store"
    exit "$result"
  fi
  rollback_ok=1
  if (( had_prior_store )); then
    restore_tmp=$(mktemp "$HOME/.codex/.agent-studio-restore.XXXXXXXX") || rollback_ok=0
    if (( rollback_ok )) && ! cp -p -- "$rollback_store" "$restore_tmp"; then rollback_ok=0; fi
    if (( rollback_ok )) && ! mv -f -- "$restore_tmp" "$credential_store"; then rollback_ok=0; fi
    [[ -z "${restore_tmp:-}" ]] || rm -f "$restore_tmp"
  elif ! rm -f -- "$credential_store"; then
    rollback_ok=0
  fi
  if (( rollback_ok )) && (( had_prior_store )) && {
      [[ "$(sha256sum "$credential_store" | cut -d ' ' -f1)" != "$previous_store_digest" ]] \
      || [[ "native-cli-store:$(date -r "$credential_store" +%s%3N)" != "$previous_store_generation" ]]; }; then
    rollback_ok=0
  fi
  if (( rollback_ok )) && { ! command -v agent-host >/dev/null 2>&1 \
      || ! agent-host --rebind-provider-auth codex --drained >/dev/null 2>&1; }; then
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
      if (( rollback_ok )) && ! sudo -n bash -c '
        unit_home=; unit_config=; environment_auth=0
        while IFS= read -r -d "" entry; do
          case "$entry" in
            HOME=*) unit_home=${entry#HOME=} ;;
            CODEX_HOME=*) unit_config=${entry#CODEX_HOME=} ;;
            OPENAI_API_KEY=*) environment_auth=1 ;;
          esac
        done <"/proc/$1/environ"
        [[ -n "$unit_home" && "$environment_auth" == 0 ]] || exit 1
        unit_store="${unit_config:-$unit_home/.codex}/auth.json"
        [[ -f "$unit_store" ]] || exit 1
        [[ "$(stat -Lc %d:%i "$unit_store")" == "$(stat -Lc %d:%i "$2")" ]] || exit 1
        [[ "$(sha256sum "$unit_store" | cut -d " " -f1)" == "$3" ]] || exit 1
        [[ "native-cli-store:$(date -r "$unit_store" +%s%3N)" == "$4" ]]
      ' bash "$pid" "$credential_store" "$previous_store_digest" "$previous_store_generation"; then rollback_ok=0; fi
    done
  fi
  if (( ! rollback_ok )); then
    for unit in agent-host.service agent-runner.service agent-runner-review.service; do
      sudo -n systemctl cat "$unit" >/dev/null 2>&1 || continue
      sudo -n systemctl stop "$unit" || true
    done
    [[ -z "${receipt_file:-}" ]] || printf 'recovery-required\n' >"$receipt_file"
    echo 'codex-login-status=recovery-required'
  else
    rm -f "$rollback_store"
    echo 'codex-login-status=rollback-restored'
  fi
  exit "$result"
}
trap rollback_codex_on_failure EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM
timeout --signal=TERM --kill-after=5s 900s codex login --device-auth 2>&1 \
  | tee "$out_tmp" \
  | grep -Eo 'https://(auth.openai.com|login.openai.com)/[^[:space:]<>]+|[A-Z0-9]{4,8}-[A-Z0-9]{4,8}'
login_exit=${PIPESTATUS[0]}
if [[ "$login_exit" -ne 0 ]]; then
  echo 'codex-login-status=login-failed'
  exit "$login_exit"
fi
if [[ ! -f "$credential_store" ]]; then
  echo 'codex-login-status=unchanged-generation'
  exit 42
fi
new_store_digest=$(sha256sum "$credential_store" | cut -d ' ' -f1) || exit 74
if [[ "$new_store_digest" == "$previous_store_digest" ]]; then
  echo 'codex-login-status=unchanged-generation'
  exit 42
fi
published_generation="native-cli-store:$(date -r "$credential_store" +%s%3N)"
if [[ "$published_generation" == "$previous_store_generation" ]]; then
  previous_ms=${previous_store_generation#native-cli-store:}
  next_second=$((previous_ms / 1000 + 1))
  now_second=$(date +%s)
  if (( now_second > next_second )); then next_second=$now_second; fi
  touch -m -d "@$next_second" "$credential_store" || exit 74
  published_generation="native-cli-store:$(date -r "$credential_store" +%s%3N)"
fi
if [[ "$published_generation" == "$expected_generation" ]]; then
  echo 'codex-login-status=unchanged-generation'
  exit 42
fi

if ! command -v agent-host >/dev/null 2>&1 \
    || ! agent-host --rebind-provider-auth codex --drained >/dev/null 2>&1; then
  echo 'codex-login-status=rebind-required'
  exit 43
fi
if ! codex login status >/dev/null 2>&1; then
  echo 'codex-login-status=unverified'
  exit 42
fi
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
  if [[ "$unit" == agent-runner-review.service ]]; then
    sudo -n /usr/local/sbin/agent-runner-deploy restart-review || exit 44
  else
    sudo -n systemctl restart "$unit" || exit 44
  fi
  pid=$(sudo -n systemctl show --property=MainPID --value "$unit")
  [[ "$pid" =~ ^[1-9][0-9]*$ ]] || exit 44
  printf 'codex-probe-unit=%s\n' "$unit"
done
echo 'codex-login-status=verified'
""";
}
