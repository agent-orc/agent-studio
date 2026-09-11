using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentStudio.Management;

public sealed record ProviderAuthProvisioningRequest(
    string SshTarget,
    string RunnerId,
    string EnvironmentVariable,
    string Secret);

public sealed record ProviderAuthProvisioningResponse(
    string Provider,
    string EnvironmentVariable,
    string Host,
    string State,
    string Detail,
    DateTime RequestedAt,
    IReadOnlyList<string> RestartedServices,
    bool ProcessEnvironmentVerified);

public interface IProviderAuthProvisioner
{
    Task<ProviderAuthProvisioningResponse> ProvisionAsync(
        ProviderAuthProvisioningRequest request,
        CancellationToken cancellationToken);
}

public sealed class ProviderAuthProvisioningException(string message) : Exception(message);

/// <summary>
/// Validates the intentionally narrow provider-auth provisioning boundary.
/// Secrets may cross this boundary in request memory and SSH stdin only. They
/// are never accepted in a path, command argument, persisted command, or task.
/// </summary>
public static partial class ProviderAuthProvisioningPolicy
{
    public const string ProviderAuthEnvironmentFile = "/etc/agent-runner/provider-auth.env";

    public static readonly IReadOnlySet<string> SupportedEnvironmentVariables =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "CLAUDE_CODE_OAUTH_TOKEN",
            "ANTHROPIC_API_KEY",
        };

    public static string? Validate(ProviderAuthProvisioningRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SshTarget)
            || !SshTargetPattern().IsMatch(request.SshTarget.Trim()))
            return "SSH target must be a configured alias or user@host without shell characters.";
        if (string.IsNullOrWhiteSpace(request.RunnerId)
            || !RunnerIdPattern().IsMatch(request.RunnerId.Trim()))
            return "Runner identity is required and may contain letters, numbers, dots, underscores, and hyphens.";
        if (!SupportedEnvironmentVariables.Contains(request.EnvironmentVariable?.Trim() ?? ""))
            return "Choose CLAUDE_CODE_OAUTH_TOKEN or ANTHROPIC_API_KEY.";
        if (string.IsNullOrEmpty(request.Secret) || request.Secret.Length is < 16 or > 8192)
            return "Provider credential must contain between 16 and 8192 characters.";
        if (!SecretPattern().IsMatch(request.Secret))
            return "Provider credential contains whitespace or characters that cannot be stored safely in an EnvironmentFile.";
        return null;
    }

    public static string ProviderFor(string environmentVariable)
        => environmentVariable is "CLAUDE_CODE_OAUTH_TOKEN" or "ANTHROPIC_API_KEY"
            ? "claude"
            : "unknown";

    [GeneratedRegex(@"^([A-Za-z0-9][A-Za-z0-9._-]*@)?[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex SshTargetPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex RunnerIdPattern();

    [GeneratedRegex(@"^[A-Za-z0-9._~+/=-]+$")]
    private static partial Regex SecretPattern();
}

/// <summary>
/// Sends one fixed remote script plus a base64-wrapped credential through the
/// SSH process stdin. The secret is absent from the local and remote command
/// lines, shell history, logs, response payload, and durable Studio state.
/// </summary>
public sealed class SshProviderAuthProvisioner : IProviderAuthProvisioner
{
    private static readonly TimeSpan ProvisioningTimeout = TimeSpan.FromSeconds(45);

    public async Task<ProviderAuthProvisioningResponse> ProvisionAsync(
        ProviderAuthProvisioningRequest request,
        CancellationToken cancellationToken)
    {
        var validation = ProviderAuthProvisioningPolicy.Validate(request);
        if (validation is not null) throw new ArgumentException(validation, nameof(request));

        var requestedAt = DateTime.UtcNow;
        var startInfo = BuildStartInfo(request.SshTarget.Trim(), request.EnvironmentVariable.Trim());
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                throw new ProviderAuthProvisioningException("The SSH provisioning process could not be started.");
        }
        catch (Exception exception) when (exception is not ProviderAuthProvisioningException)
        {
            throw new ProviderAuthProvisioningException(
                $"The SSH provisioning process could not be started: {SafeExcerpt(exception.Message)}");
        }

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(ProvisioningTimeout);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(bounded.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(bounded.Token);
        try
        {
            var standardInput = BuildStandardInput(request.EnvironmentVariable.Trim(), request.Secret);
            await process.StandardInput.WriteAsync(standardInput.AsMemory(), bounded.Token);
            await process.StandardInput.FlushAsync(bounded.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(bounded.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new ProviderAuthProvisioningException(
                $"SSH provisioning did not finish within {ProvisioningTimeout.TotalSeconds:0} seconds.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
        {
            throw new ProviderAuthProvisioningException(
                $"SSH provisioning failed with exit code {process.ExitCode}: "
                + SafeExcerpt(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr));
        }

        var restarted = stdout
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("provider-auth-unit=", StringComparison.Ordinal))
            .Select(line => line["provider-auth-unit=".Length..].Trim())
            .Where(line => line.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var processEnvironmentVerified = stdout.Contains(
            "provider-auth-process-environment=verified",
            StringComparison.Ordinal);
        var state = processEnvironmentVerified ? "awaiting-probe" : "installed-awaiting-runner";
        var detail = processEnvironmentVerified
            ? "The protected EnvironmentFile was installed, active units were restarted, and /proc confirms that the provider variable reached each daemon. Waiting for the runner probe."
            : "The protected EnvironmentFile was installed. At least one runner is not started yet or still needs the guarded Review drain and replacement before it can publish a credential probe.";

        return new ProviderAuthProvisioningResponse(
            ProviderAuthProvisioningPolicy.ProviderFor(request.EnvironmentVariable.Trim()),
            request.EnvironmentVariable.Trim(),
            request.SshTarget.Trim(),
            state,
            detail,
            requestedAt,
            restarted,
            processEnvironmentVerified);
    }

    internal static ProcessStartInfo BuildStartInfo(string sshTarget, string environmentVariable)
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
        startInfo.ArgumentList.Add("sudo");
        startInfo.ArgumentList.Add("bash");
        startInfo.ArgumentList.Add("-s");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(environmentVariable);
        return startInfo;
    }

    internal static string BuildStandardInput(string environmentVariable, string secret)
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(secret));
        return RemoteScript.Replace("__PAYLOAD_BASE64__", payload, StringComparison.Ordinal)
            .Replace("__ENVIRONMENT_VARIABLE__", environmentVariable, StringComparison.Ordinal);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception exception)
        {
            SilentCatch.Note(exception, "SshProviderAuthProvisioner: bounded SSH process cleanup");
        }
    }

    private static string SafeExcerpt(string? value, int maxLength = 500)
    {
        var text = string.Join(' ', (value ?? "")
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return text.Length <= maxLength ? text : text[..maxLength] + "...";
    }

    private const string RemoteScript = """
set -euo pipefail
environment_variable="$1"
expected_environment_variable='__ENVIRONMENT_VARIABLE__'
provider_auth_file='/etc/agent-runner/provider-auth.env'
payload_base64='__PAYLOAD_BASE64__'

if [[ "$environment_variable" != "$expected_environment_variable" ]]; then
  echo '[provider-auth] Environment variable binding changed in transit.' >&2
  exit 31
fi
case "$environment_variable" in
  CLAUDE_CODE_OAUTH_TOKEN|ANTHROPIC_API_KEY) ;;
  *) echo '[provider-auth] Unsupported provider environment variable.' >&2; exit 32 ;;
esac
getent group agent >/dev/null || groupadd --system agent

umask 077
token_tmp="$(mktemp)"
env_tmp="$(mktemp)"
dropin_tmp="$(mktemp)"
provider_auth_install_tmp=''
cleanup() {
  rm -f -- "$token_tmp" "$env_tmp" "$dropin_tmp"
  [[ -z "$provider_auth_install_tmp" ]] || rm -f -- "$provider_auth_install_tmp"
}
trap cleanup EXIT
printf '%s' "$payload_base64" | base64 --decode >"$token_tmp"
unset payload_base64
[[ -s "$token_tmp" ]] || { echo '[provider-auth] Decoded credential is empty.' >&2; exit 34; }
if LC_ALL=C grep -q '[^A-Za-z0-9._~+/=-]' "$token_tmp"; then
  echo '[provider-auth] Credential contains unsupported EnvironmentFile characters.' >&2
  exit 35
fi

install -d -m 0750 -o root -g agent /etc/agent-runner
if [[ -f "$provider_auth_file" ]]; then
  awk -F= '$1 != "CLAUDE_CODE_OAUTH_TOKEN" && $1 != "ANTHROPIC_API_KEY" { print }' \
    "$provider_auth_file" >"$env_tmp"
fi
printf '%s=' "$environment_variable" >>"$env_tmp"
cat "$token_tmp" >>"$env_tmp"
printf '\n' >>"$env_tmp"
provider_auth_install_tmp="$(mktemp /etc/agent-runner/.provider-auth.env.XXXXXX)"
install -m 0640 -o root -g agent "$env_tmp" "$provider_auth_install_tmp"
mv -fT -- "$provider_auth_install_tmp" "$provider_auth_file"
provider_auth_install_tmp=''

printf '[Service]\nEnvironmentFile=%s\n' "$provider_auth_file" >"$dropin_tmp"
units=()
if systemctl cat agent-host.service >/dev/null 2>&1; then
  units+=(agent-host.service)
elif systemctl cat agent-runner.service >/dev/null 2>&1; then
  units+=(agent-runner.service)
fi
if systemctl cat agent-runner-review.service >/dev/null 2>&1; then
  units+=(agent-runner-review.service)
fi
configured=()
for unit in "${units[@]}"; do
  dropin_dir="/etc/systemd/system/${unit}.d"
  install -d -m 0755 "$dropin_dir"
  install -m 0644 "$dropin_tmp" "$dropin_dir/90-provider-auth.conf"
  configured+=("$unit")
done

if ((${#configured[@]} == 0)); then
  echo 'provider-auth-file=installed'
  echo 'provider-auth-process-environment=pending-runner'
  exit 0
fi

systemctl daemon-reload
verified=0
pending=0
for unit in "${configured[@]}"; do
  if [[ "$unit" == agent-runner-review.service ]]; then
    if [[ ! -x /usr/local/sbin/agent-runner-deploy ]] \
        || ! /usr/local/sbin/agent-runner-deploy restart-review; then
      printf 'provider-auth-unit-pending=%s\n' "$unit"
      pending=$((pending + 1))
      continue
    fi
  else
    systemctl restart "$unit"
  fi
  main_pid="$(systemctl show --property=MainPID --value "$unit")"
  [[ "$main_pid" =~ ^[1-9][0-9]*$ ]] || {
    printf '[provider-auth] Unit %s did not expose a running MainPID.\n' "$unit" >&2
    exit 36
  }
  if ! tr '\0' '\n' <"/proc/${main_pid}/environ" | grep -q "^${environment_variable}="; then
    printf '[provider-auth] Unit %s did not receive %s through EnvironmentFile.\n' \
      "$unit" "$environment_variable" >&2
    exit 37
  fi
  verified=$((verified + 1))
  printf 'provider-auth-unit=%s\n' "$unit"
done
[[ "$((verified + pending))" -eq "${#configured[@]}" ]] || exit 38
echo 'provider-auth-file=installed'
if ((pending == 0)); then
  echo 'provider-auth-process-environment=verified'
else
  echo 'provider-auth-process-environment=pending-runner'
fi
""";
}
