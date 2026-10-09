using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentStudio.Management;

public sealed record ProviderAuthProvisioningRequest(
    string SshTarget,
    string RunnerId,
    string EnvironmentVariable,
    string Secret,
    string? IdempotencyKey = null);

public sealed record ProviderAuthProvisioningResponse(
    string Provider,
    string EnvironmentVariable,
    string Host,
    string State,
    string Detail,
    DateTime RequestedAt,
    IReadOnlyList<string> RestartedServices,
    bool ProcessEnvironmentVerified,
    string? OperationId = null);

public interface IProviderAuthProvisioner
{
    Task<ProviderAuthProvisioningResponse> ProvisionAsync(
        ProviderAuthProvisioningRequest request,
        ProviderAuthRenewalFence? fence, CancellationToken cancellationToken);
    Task<string> FinalizePendingAsync(string sshTarget, string operationId, CancellationToken cancellationToken)
        => Task.FromResult("verified");
}

public sealed record ProviderAuthRenewalFence(string OperationId, string ExpectedGeneration);

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

    public async Task<string> FinalizePendingAsync(string sshTarget, string operationId,
        CancellationToken cancellationToken)
    {
        if (!Regex.IsMatch(sshTarget, @"^([A-Za-z0-9][A-Za-z0-9._-]*@)?[A-Za-z0-9][A-Za-z0-9._-]*$")
            || !Regex.IsMatch(operationId, @"^[A-Za-z0-9_.-]{1,128}$"))
            throw new ArgumentException("Invalid host renewal target or operation.");
        var startInfo = BuildStartInfo(sshTarget, "ANTHROPIC_API_KEY");
        using var process = new Process { StartInfo = startInfo };
        process.Start();
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(ProvisioningTimeout);
        try
        {
            await process.StandardInput.WriteAsync(
                PendingFinalizationScript.Replace("__OPERATION_ID__", operationId, StringComparison.Ordinal)
                    .AsMemory(), bounded.Token);
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync(bounded.Token);
            var stderr = process.StandardError.ReadToEndAsync(bounded.Token);
            await process.WaitForExitAsync(bounded.Token);
            if (process.ExitCode != 0)
                throw new ProviderAuthProvisioningException(
                    "Pending renewal verification failed: " + SafeExcerpt(await stderr));
            return (await stdout).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault()?.Trim() ?? "pending";
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
    }

    public async Task<ProviderAuthProvisioningResponse> ProvisionAsync(
        ProviderAuthProvisioningRequest request, ProviderAuthRenewalFence? fence,
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
            var standardInput = BuildStandardInput(request.EnvironmentVariable.Trim(), request.Secret, fence);
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

    internal static string BuildStandardInput(string environmentVariable, string secret,
        ProviderAuthRenewalFence? fence = null)
    {
        static bool Safe(string value) => value.Length is > 0 and <= 128 &&
            value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' or ':');
        if (fence is not null && (!Safe(fence.OperationId) || !Safe(fence.ExpectedGeneration)))
            throw new ArgumentException("Renewal fence identifiers are invalid.", nameof(fence));
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(secret));
        return RemoteScript.Replace("__PAYLOAD_BASE64__", payload, StringComparison.Ordinal)
            .Replace("__ENVIRONMENT_VARIABLE__", environmentVariable, StringComparison.Ordinal)
            .Replace("__OPERATION_ID__", fence?.OperationId ?? "", StringComparison.Ordinal)
            .Replace("__EXPECTED_GENERATION__", fence?.ExpectedGeneration ?? "", StringComparison.Ordinal);
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
operation_id='__OPERATION_ID__'
expected_generation='__EXPECTED_GENERATION__'
provider_auth_file='/etc/agent-runner/provider-auth.env'
payload_base64='__PAYLOAD_BASE64__'
generation="renewal_$(tr -d '-' </proc/sys/kernel/random/uuid)"
rollback_file="/etc/agent-runner/.provider-auth.rollback.${generation}"
installed=0
install_committed=0
verified_all=0
changed_units=()
units=()
created_dropins=()
had_prior_file=0

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
rollback_on_failure() {
  result=$?
  trap - EXIT
  set +e
  rm -f -- "$token_tmp" "$env_tmp" "$dropin_tmp"
  [[ -z "$provider_auth_install_tmp" ]] || rm -f -- "$provider_auth_install_tmp"
  if ((result == 0 && verified_all == 1)); then
    rm -f -- "$rollback_file" || {
      [[ -z "${receipt_file:-}" ]] || printf 'recovery-required\n' >"$receipt_file"
      echo 'provider-auth-recovery-required=rollback-copy-cleanup-failed' >&2
      exit 39
    }
    exit 0
  fi
  if ((result != 0 && installed == 0)); then
    rm -f -- "$rollback_file"
  fi
  if ((result != 0 && installed == 1 && install_committed == 0)); then
    restored=1
    if ((had_prior_file)); then
      restore_tmp=$(mktemp /etc/agent-runner/.provider-auth.restore.XXXXXX) || restored=0
      if ((restored)) && ! install -m 0640 -o root -g agent "$rollback_file" "$restore_tmp"; then restored=0; fi
      if ((restored)) && ! mv -fT -- "$restore_tmp" "$provider_auth_file"; then restored=0; fi
      [[ -z "${restore_tmp:-}" ]] || rm -f -- "$restore_tmp"
    else
      rm -f -- "$provider_auth_file" || restored=0
      for dropin in "${created_dropins[@]}"; do rm -f -- "$dropin" || restored=0; done
      systemctl daemon-reload || restored=0
    fi
    if ((restored)); then
      for unit in "${changed_units[@]}"; do
        if [[ "$unit" == agent-runner-review.service ]]; then
          /usr/local/sbin/agent-runner-deploy restart-review || restored=0
        else
          systemctl restart "$unit" || restored=0
        fi
        pid=$(systemctl show --property=MainPID --value "$unit")
        [[ "$pid" =~ ^[1-9][0-9]*$ ]] || restored=0
        if ((restored)) && ! bash -c '
          if [[ -f "$2" ]]; then
            set -a; source "$2"; set +a
          fi
          oauth=${CLAUDE_CODE_OAUTH_TOKEN:-}; api=${ANTHROPIC_API_KEY:-}
          generation=${AGENT_STUDIO_CLAUDE_AUTH_GENERATION:-}
          seen_oauth=0; seen_api=0; seen_generation=0
          while IFS= read -r -d "" entry; do
            [[ -z "$oauth" || "$entry" != "CLAUDE_CODE_OAUTH_TOKEN=$oauth" ]] || seen_oauth=1
            [[ -z "$api" || "$entry" != "ANTHROPIC_API_KEY=$api" ]] || seen_api=1
            [[ -z "$generation" || "$entry" != "AGENT_STUDIO_CLAUDE_AUTH_GENERATION=$generation" ]] || seen_generation=1
            if [[ -z "$oauth" && "$entry" == CLAUDE_CODE_OAUTH_TOKEN=* ]] ||
               [[ -z "$api" && "$entry" == ANTHROPIC_API_KEY=* ]] ||
               [[ -z "$generation" && "$entry" == AGENT_STUDIO_CLAUDE_AUTH_GENERATION=* ]]; then exit 1; fi
          done <"/proc/$1/environ"
          [[ ( -z "$oauth" || "$seen_oauth" == 1 ) &&
             ( -z "$api" || "$seen_api" == 1 ) &&
             ( -z "$generation" || "$seen_generation" == 1 ) ]]
        ' bash "$pid" "$provider_auth_file"; then restored=0; fi
      done
    fi
    if ((restored)); then
      if rm -f -- "$rollback_file"; then
        echo 'provider-auth-rollback-restored'
      else
        [[ -z "${receipt_file:-}" ]] || printf 'recovery-required\n' >"$receipt_file"
        echo 'provider-auth-recovery-required=rollback-copy-cleanup-failed' >&2
      fi
    else
      for unit in "${units[@]}"; do systemctl stop "$unit" || true; done
      [[ -z "${receipt_file:-}" ]] || printf 'recovery-required\n' >"$receipt_file"
      echo 'provider-auth-recovery-required=rollback-failed' >&2
    fi
  fi
  exit "$result"
}
trap rollback_on_failure EXIT
printf '%s' "$payload_base64" | base64 --decode >"$token_tmp"
unset payload_base64
[[ -s "$token_tmp" ]] || { echo '[provider-auth] Decoded credential is empty.' >&2; exit 34; }
if LC_ALL=C grep -q '[^A-Za-z0-9._~+/=-]' "$token_tmp"; then
  echo '[provider-auth] Credential contains unsupported EnvironmentFile characters.' >&2
  exit 35
fi

install -d -m 0750 -o root -g agent /etc/agent-runner
exec 9>/etc/agent-runner/.provider-auth-renewal.lock
flock -n 9 || { echo 'provider-auth-binding-busy' >&2; exit 73; }
if [[ -n "$expected_generation" ]]; then
  current_generation=absent
  if [[ -f "$provider_auth_file" ]]; then
    current_generation=$(awk -F= '$1 == "AGENT_STUDIO_CLAUDE_AUTH_GENERATION" { print $2 }' "$provider_auth_file")
    [[ -n "$current_generation" ]] || current_generation=unknown
  fi
  [[ "$current_generation" == "$expected_generation" ]] || {
    echo 'provider-auth-stale-generation' >&2; exit 46;
  }
fi
if [[ -n "$operation_id" ]]; then
  receipt_dir=/etc/agent-runner/provider-renewal
  install -d -m 0700 -o root -g root "$receipt_dir"
  receipt_file="$receipt_dir/$operation_id"
  [[ ! -e "$receipt_file" ]] || { echo 'provider-auth-recovery-required' >&2; exit 74; }
  printf 'started\n' >"$receipt_file"
fi
if [[ -f "$provider_auth_file" ]]; then
  had_prior_file=1
  install -m 0600 -o root -g root "$provider_auth_file" "$rollback_file"
  awk -F= '$1 != "CLAUDE_CODE_OAUTH_TOKEN" && $1 != "ANTHROPIC_API_KEY" && $1 != "AGENT_STUDIO_CLAUDE_AUTH_GENERATION" { print }' \
    "$provider_auth_file" >"$env_tmp"
else
  install -m 0600 -o root -g root /dev/null "$rollback_file"
fi
printf '%s=' "$environment_variable" >>"$env_tmp"
cat "$token_tmp" >>"$env_tmp"
printf '\n' >>"$env_tmp"
printf 'AGENT_STUDIO_CLAUDE_AUTH_GENERATION=%s\n' "$generation" >>"$env_tmp"
provider_auth_install_tmp="$(mktemp /etc/agent-runner/.provider-auth.env.XXXXXX)"
install -m 0640 -o root -g agent "$env_tmp" "$provider_auth_install_tmp"
mv -fT -- "$provider_auth_install_tmp" "$provider_auth_file"
provider_auth_install_tmp=''
installed=1

printf '[Service]\nEnvironmentFile=%s\n' "$provider_auth_file" >"$dropin_tmp"
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
  [[ -e "$dropin_dir/90-provider-auth.conf" ]] || created_dropins+=("$dropin_dir/90-provider-auth.conf")
  install -m 0644 "$dropin_tmp" "$dropin_dir/90-provider-auth.conf"
  configured+=("$unit")
done

if ((${#configured[@]} == 0)); then
  if [[ -n "${receipt_file:-}" ]]; then
    { printf 'installed-awaiting-runner\n%s\n%s\n%s\n' "$rollback_file" "$had_prior_file" "$generation"; printf 'pending=%s\n' 'no-runner-unit'; } >"$receipt_file"
  fi
  echo 'provider-auth-file=installed'
  echo 'provider-auth-process-environment=pending-runner'
  install_committed=1
  exit 0
fi

systemctl daemon-reload
verified=0
pending=0
for unit in "${configured[@]}"; do
  if [[ "$unit" == agent-runner-review.service ]]; then
    changed_units+=("$unit")
    if [[ ! -x /usr/local/sbin/agent-runner-deploy ]] \
        || ! /usr/local/sbin/agent-runner-deploy restart-review; then
      printf 'provider-auth-unit-pending=%s\n' "$unit"
      pending=$((pending + 1))
      continue
    fi
  else
    changed_units+=("$unit")
    systemctl restart "$unit"
  fi
  main_pid="$(systemctl show --property=MainPID --value "$unit")"
  [[ "$main_pid" =~ ^[1-9][0-9]*$ ]] || {
    printf '[provider-auth] Unit %s did not expose a running MainPID.\n' "$unit" >&2
    exit 36
  }
  if ! (set -a; source "$provider_auth_file"; set +a
        candidate="${!environment_variable}"
        matched=0
        marker=0
        while IFS= read -r -d '' entry; do
          [[ "$entry" == "$environment_variable=$candidate" ]] && matched=1
          [[ "$entry" == "AGENT_STUDIO_CLAUDE_AUTH_GENERATION=$generation" ]] && marker=1
        done <"/proc/${main_pid}/environ"
        [[ "$matched" == 1 && "$marker" == 1 ]]); then
    printf '[provider-auth] Unit %s did not receive the selected provider generation.\n' \
      "$unit" >&2
    exit 37
  fi
  verified=$((verified + 1))
  printf 'provider-auth-unit=%s\n' "$unit"
done
[[ "$((verified + pending))" -eq "${#configured[@]}" ]] || exit 38
echo 'provider-auth-file=installed'
if ((pending == 0)); then
  verified_all=1
  echo 'provider-auth-process-environment=verified'
else
  if [[ -n "${receipt_file:-}" ]]; then
    { printf 'installed-awaiting-runner\n%s\n%s\n%s\n' "$rollback_file" "$had_prior_file" "$generation"; for unit in "${configured[@]}"; do printf 'unit=%s\n' "$unit"; done; for unit in "${created_dropins[@]}"; do printf 'dropin=%s\n' "$unit"; done; for unit in "${configured[@]}"; do printf 'pending=%s\n' "$unit"; done; } >"$receipt_file"
  fi
  echo 'provider-auth-process-environment=pending-runner'
fi
install_committed=1
""";

    private const string PendingFinalizationScript = """
set -euo pipefail
operation_id='__OPERATION_ID__'
receipt_file="/etc/agent-runner/provider-renewal/$operation_id"
provider_auth_file=/etc/agent-runner/provider-auth.env
exec 9>/etc/agent-runner/.provider-auth-renewal.lock
flock -n 9 || exit 73
[[ -f "$receipt_file" ]] || { echo recovery-required; exit 0; }
mapfile -t receipt <"$receipt_file"
[[ "${receipt[0]}" == installed-awaiting-runner ]] || { echo "${receipt[0]}"; exit 0; }
rollback_file="${receipt[1]}"
had_prior_file="${receipt[2]}"
installed_generation="${receipt[3]}"
[[ "$rollback_file" == /etc/agent-runner/.provider-auth.rollback.* && -f "$rollback_file" ]] || exit 74
units=(); dropins=(); pending=()
for line in "${receipt[@]:4}"; do
  case "$line" in
    unit=agent-host.service|unit=agent-runner.service|unit=agent-runner-review.service) units+=("${line#unit=}") ;;
    dropin=/etc/systemd/system/agent-host.service.d/90-provider-auth.conf|dropin=/etc/systemd/system/agent-runner.service.d/90-provider-auth.conf|dropin=/etc/systemd/system/agent-runner-review.service.d/90-provider-auth.conf) dropins+=("${line#dropin=}") ;;
    pending=*) pending+=("${line#pending=}") ;;
  esac
done
recover() {
  trap - ERR
  set +e
  restored=1
  if [[ "$had_prior_file" == 1 ]]; then
    restore_tmp=$(mktemp /etc/agent-runner/.provider-auth.restore.XXXXXX) || restored=0
    if ((restored)) && ! install -m 0640 -o root -g agent "$rollback_file" "$restore_tmp"; then restored=0; fi
    if ((restored)) && ! mv -fT -- "$restore_tmp" "$provider_auth_file"; then restored=0; fi
    [[ -z "${restore_tmp:-}" ]] || rm -f -- "$restore_tmp"
  else
    rm -f -- "$provider_auth_file" || restored=0
    for dropin in "${dropins[@]}"; do rm -f -- "$dropin" || restored=0; done
    systemctl daemon-reload || restored=0
  fi
  if ((restored)); then
    for unit in "${units[@]}"; do
      if [[ "$unit" == agent-runner-review.service ]]; then
        /usr/local/sbin/agent-runner-deploy restart-review || restored=0
      else
        systemctl restart "$unit" || restored=0
      fi
      pid=$(systemctl show --property=MainPID --value "$unit")
      [[ "$pid" =~ ^[1-9][0-9]*$ ]] || restored=0
      if ((restored)) && ! bash -c '
        set -a; [[ ! -f "$2" ]] || source "$2"; set +a
        oauth=${CLAUDE_CODE_OAUTH_TOKEN:-}; api=${ANTHROPIC_API_KEY:-}
        generation=${AGENT_STUDIO_CLAUDE_AUTH_GENERATION:-}
        seen_oauth=0; seen_api=0; seen_generation=0
        while IFS= read -r -d "" entry; do
          [[ -z "$oauth" || "$entry" != "CLAUDE_CODE_OAUTH_TOKEN=$oauth" ]] || seen_oauth=1
          [[ -z "$api" || "$entry" != "ANTHROPIC_API_KEY=$api" ]] || seen_api=1
          [[ -z "$generation" || "$entry" != "AGENT_STUDIO_CLAUDE_AUTH_GENERATION=$generation" ]] || seen_generation=1
          if [[ -z "$oauth" && "$entry" == CLAUDE_CODE_OAUTH_TOKEN=* ]] ||
             [[ -z "$api" && "$entry" == ANTHROPIC_API_KEY=* ]] ||
             [[ -z "$generation" && "$entry" == AGENT_STUDIO_CLAUDE_AUTH_GENERATION=* ]]; then exit 1; fi
        done <"/proc/$1/environ"
        [[ ( -z "$oauth" || "$seen_oauth" == 1 ) && ( -z "$api" || "$seen_api" == 1 ) && ( -z "$generation" || "$seen_generation" == 1 ) ]]
      ' bash "$pid" "$provider_auth_file"; then restored=0; fi
    done
  fi
  if ((restored)); then
    rm -f -- "$rollback_file" || restored=0
  fi
  if ((restored)); then
    printf 'rollback-restored\n' >"$receipt_file"
    echo rollback-restored
  else
    for unit in "${units[@]}"; do systemctl stop "$unit" || true; done
    printf 'recovery-required\n' >"$receipt_file"
    echo recovery-required
  fi
  exit 0
}
trap recover ERR
if ((${#units[@]} == 0)); then echo pending; exit 0; fi
for unit in "${pending[@]}"; do
  if [[ "$unit" == agent-runner-review.service ]]; then
    /usr/local/sbin/agent-runner-deploy restart-review || { echo pending; exit 0; }
  fi
done
generation=$(awk -F= '$1 == "AGENT_STUDIO_CLAUDE_AUTH_GENERATION" { print $2 }' "$provider_auth_file")
[[ "$generation" == "$installed_generation" ]] || false
for unit in "${units[@]}"; do
  pid=$(systemctl show --property=MainPID --value "$unit")
  [[ "$pid" =~ ^[1-9][0-9]*$ ]] || false
  (set -a; source "$provider_auth_file"; set +a
    candidate=${ANTHROPIC_API_KEY:-${CLAUDE_CODE_OAUTH_TOKEN:-}}
    [[ -n "$candidate" ]]
    matched=0; marker=0
    while IFS= read -r -d '' entry; do
      [[ "$entry" == "ANTHROPIC_API_KEY=$candidate" || "$entry" == "CLAUDE_CODE_OAUTH_TOKEN=$candidate" ]] && matched=1
      [[ "$entry" == "AGENT_STUDIO_CLAUDE_AUTH_GENERATION=$generation" ]] && marker=1
    done <"/proc/${pid}/environ"
    [[ "$matched" == 1 && "$marker" == 1 ]]) || false
done
printf 'verified\n' >"$receipt_file"
rm -f -- "$rollback_file"
echo verified
""";

    internal static string BuildPendingFinalizationScriptForTest(string operationId)
        => PendingFinalizationScript.Replace("__OPERATION_ID__", operationId, StringComparison.Ordinal);
}
