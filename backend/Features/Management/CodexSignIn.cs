using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using AgentStudio.Bus;

namespace AgentStudio.Management;

public sealed record CodexSignInRequest(string SshTarget);

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
    IProviderSignInAudit audit)
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
        var state = new SessionState(
            "codex_" + Guid.NewGuid().ToString("N"),
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
            state.Transport = transport.Start(
                request.SshTarget.Trim(),
                line => CaptureInstructions(state, line),
                state.Timeout.Token);
            _ = ObserveCompletionAsync(state);

            await state.InstructionsReady.Task.WaitAsync(InstructionTimeout, cancellationToken);
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
                var detail = result.RestartedServices.Count > 0
                    ? "Codex sign-in completed. Runner services restarted and a fresh provider probe is expected."
                    : "Codex sign-in completed. Waiting for the runner's next provider probe.";
                await CompleteAsync(state, "completed", detail, "completed").ConfigureAwait(false);
            }
            else
            {
                await CompleteAsync(
                    state,
                    "failed",
                    "Codex sign-in did not complete or login status could not be verified.",
                    "failed").ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (state.Timeout?.IsCancellationRequested == true)
        {
            await CompleteAsync(state, "failed", "Codex sign-in timed out after 15 minutes.", "timeout").ConfigureAwait(false);
        }
        catch (Exception)
        {
            await CompleteAsync(state, "failed", "The remote Codex sign-in process failed.", "failed").ConfigureAwait(false);
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

        var completion = RunAsync(process, onOutput, cancellationToken);
        return new CodexDeviceAuthTransportSession(completion, () => TryKill(process));
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

    private static async Task<CodexDeviceAuthTransportResult> RunAsync(
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
echo 'codex-login-status=verified'

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
    printf 'codex-probe-unit=%s\n' "$unit"
  fi
done
""";
}
