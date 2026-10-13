using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentStudio.Management;

/// <summary>Resumes a host-local receipt under the same binding lock as login.</summary>
internal static class ProviderSignInPendingFinalizer
{
    internal static string NativeScript(string operationId, string provider)
    {
        ValidateOperation(operationId);
        if (provider is not ("claude" or "codex")) throw new ArgumentException("Invalid provider.");
        return NativeTemplate.Replace("__OPERATION_ID__", operationId, StringComparison.Ordinal)
            .Replace("__PROVIDER__", provider, StringComparison.Ordinal);
    }

    internal static string ClaudeEnvironmentScript(string operationId)
    {
        ValidateOperation(operationId);
        return EnvironmentTemplate.Replace("__OPERATION_ID__", operationId, StringComparison.Ordinal);
    }

    internal static async Task<string> RunAsync(string sshTarget, string script, CancellationToken cancellationToken)
    {
        if (!Regex.IsMatch(sshTarget, @"^([A-Za-z0-9][A-Za-z0-9._-]*@)?[A-Za-z0-9][A-Za-z0-9._-]*$"))
            throw new ArgumentException("Invalid renewal host.", nameof(sshTarget));
        var info = new ProcessStartInfo("ssh")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, StandardInputEncoding = Encoding.UTF8,
        };
        foreach (var argument in new[] { "-o", "BatchMode=yes", "-o", "ConnectTimeout=10", "-T", sshTarget, "bash", "-s" })
            info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        process.Start();
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(bounded.Token);
            var stderr = process.StandardError.ReadToEndAsync(bounded.Token);
            await process.StandardInput.WriteAsync(script.AsMemory(), bounded.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(bounded.Token);
            await stderr;
            var result = (await stdout).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            return process.ExitCode == 0 && result is ("verified" or "pending" or "rollback-restored" or "recovery-required")
                ? result : "pending";
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
    }

    private static void ValidateOperation(string operationId)
    {
        if (!Regex.IsMatch(operationId, @"^[A-Za-z0-9_.-]{1,128}$"))
            throw new ArgumentException("Invalid renewal operation.", nameof(operationId));
    }

    private const string NativeTemplate = """
set -uo pipefail
operation_id='__OPERATION_ID__'
provider='__PROVIDER__'
umask 077
if [[ "$provider" == codex ]]; then
  store_dir="$HOME/.codex"; store_file="$HOME/.codex/auth.json"
else
  store_dir="$HOME/.claude"; store_file="$HOME/.claude/.credentials.json"
fi
exec 9>"$store_dir/.agent-studio-renewal.lock"
flock -n 9 || { echo pending; exit 0; }
receipt_file="$HOME/.local/state/agent-studio/provider-renewal/$operation_id"
[[ -f "$receipt_file" ]] || { echo pending; exit 0; }
mapfile -t receipt <"$receipt_file"
[[ "${receipt[0]}" == installed-awaiting-runner ]] || {
  case "${receipt[0]}" in verified) echo verified ;; *) echo recovery-required ;; esac
  exit 0
}
rollback_store="${receipt[1]:-}"
new_digest="${receipt[2]:-}"
new_generation="${receipt[3]:-}"
had_prior_store="${receipt[4]:-unknown}"
if [[ "$rollback_store" != "$store_dir/.agent-studio-rollback."* || ! -f "$rollback_store" \
    || ! "$new_digest" =~ ^[a-f0-9]{64}$ || ! "$new_generation" =~ ^native-cli-store:[0-9]+$ ]]; then
  for unit in agent-host.service agent-runner.service agent-runner-review.service; do
    sudo -n systemctl cat "$unit" >/dev/null 2>&1 || continue
    sudo -n systemctl stop "$unit" || true
  done
  printf 'recovery-required\n' >"$receipt_file"
  echo recovery-required; exit 0
fi
if [[ "$had_prior_store" != 0 && "$had_prior_store" != 1 ]]; then
  had_prior_store=0
  [[ ! -s "$rollback_store" ]] || had_prior_store=1
fi
if pgrep -u "$(id -u)" -x "$provider" >/dev/null 2>&1; then echo pending; exit 0; fi
units=()
if sudo -n systemctl cat agent-host.service >/dev/null 2>&1; then units+=(agent-host.service)
elif sudo -n systemctl cat agent-runner.service >/dev/null 2>&1; then units+=(agent-runner.service); fi
if sudo -n systemctl cat agent-runner-review.service >/dev/null 2>&1; then units+=(agent-runner-review.service); fi
if ((${#units[@]} < 2)); then echo pending; exit 0; fi
check_unit() {
  local pid
  pid=$(sudo -n systemctl show --property=MainPID --value "$1")
  [[ "$pid" =~ ^[1-9][0-9]*$ ]] || return 1
  sudo -n bash -c '
    unit_home=; unit_config=; environment_auth=0
    while IFS= read -r -d "" entry; do
      case "$entry" in
        HOME=*) unit_home=${entry#HOME=} ;;
        CODEX_HOME=*) [[ "$5" != codex ]] || unit_config=${entry#CODEX_HOME=} ;;
        CLAUDE_CONFIG_DIR=*) [[ "$5" != claude ]] || unit_config=${entry#CLAUDE_CONFIG_DIR=} ;;
        OPENAI_API_KEY=*) [[ "$5" != codex ]] || environment_auth=1 ;;
        CLAUDE_CODE_OAUTH_TOKEN=*|ANTHROPIC_API_KEY=*) [[ "$5" != claude ]] || environment_auth=1 ;;
      esac
    done <"/proc/$1/environ"
    [[ -n "$unit_home" && "$environment_auth" == 0 ]] || exit 1
    if [[ "$5" == codex ]]; then unit_store="${unit_config:-$unit_home/.codex}/auth.json"
    else unit_store="${unit_config:-$unit_home/.claude}/.credentials.json"; fi
    [[ -f "$unit_store" ]] || exit 1
    [[ "$(stat -Lc %d:%i "$unit_store")" == "$(stat -Lc %d:%i "$2")" ]] || exit 1
    [[ "$(sha256sum "$unit_store" | cut -d " " -f1)" == "$3" ]] || exit 1
    [[ "native-cli-store:$(date -r "$unit_store" +%s%3N)" == "$4" ]]
  ' bash "$pid" "$store_file" "$2" "$3" "$provider"
}
check_absent() {
  local pid
  pid=$(sudo -n systemctl show --property=MainPID --value "$1")
  [[ "$pid" =~ ^[1-9][0-9]*$ ]] || return 1
  sudo -n bash -c '
    unit_home=; unit_config=; environment_auth=0
    while IFS= read -r -d "" entry; do
      case "$entry" in
        HOME=*) unit_home=${entry#HOME=} ;;
        CODEX_HOME=*) [[ "$2" != codex ]] || unit_config=${entry#CODEX_HOME=} ;;
        CLAUDE_CONFIG_DIR=*) [[ "$2" != claude ]] || unit_config=${entry#CLAUDE_CONFIG_DIR=} ;;
        OPENAI_API_KEY=*) [[ "$2" != codex ]] || environment_auth=1 ;;
        CLAUDE_CODE_OAUTH_TOKEN=*|ANTHROPIC_API_KEY=*) [[ "$2" != claude ]] || environment_auth=1 ;;
      esac
    done <"/proc/$1/environ"
    [[ -n "$unit_home" && "$environment_auth" == 0 ]] || exit 1
    if [[ "$2" == codex ]]; then unit_store="${unit_config:-$unit_home/.codex}/auth.json"
    else unit_store="${unit_config:-$unit_home/.claude}/.credentials.json"; fi
    [[ ! -e "$unit_store" ]]
  ' bash "$pid" "$provider"
}
restart_unit() {
  if [[ "$1" == agent-runner-review.service ]]; then
    sudo -n /usr/local/sbin/agent-runner-deploy restart-review >/dev/null 2>&1
  else sudo -n systemctl restart "$1"; fi
}
recover() {
  local restored=1 old_digest old_generation tmp unit
  if [[ "$had_prior_store" == 1 ]]; then
    old_digest=$(sha256sum "$rollback_store" | cut -d ' ' -f1) || restored=0
    old_generation="native-cli-store:$(date -r "$rollback_store" +%s%3N)"
    tmp=$(mktemp "$store_dir/.agent-studio-restore.XXXXXXXX") || restored=0
    if ((restored)) && ! cp -p -- "$rollback_store" "$tmp"; then restored=0; fi
    if ((restored)) && ! mv -f -- "$tmp" "$store_file"; then restored=0; fi
    [[ -z "${tmp:-}" ]] || rm -f -- "$tmp"
  else
    rm -f -- "$store_file" || restored=0
  fi
  if ((restored)) && ! agent-host --rebind-provider-auth "$provider" --drained >/dev/null 2>&1; then restored=0; fi
  if ((restored)); then
    for unit in "${units[@]}"; do
      if ! restart_unit "$unit"; then restored=0; break; fi
      if [[ "$had_prior_store" == 1 ]]; then
        if ! check_unit "$unit" "$old_digest" "$old_generation"; then restored=0; break; fi
      elif ! check_absent "$unit"; then restored=0; break; fi
    done
  fi
  if ((restored)) && rm -f -- "$rollback_store"; then
    printf 'rollback-restored\n' >"$receipt_file"; echo rollback-restored
  else
    for unit in "${units[@]}"; do sudo -n systemctl stop "$unit" || true; done
    printf 'recovery-required\n' >"$receipt_file"; echo recovery-required
  fi
}
if [[ ! -f "$store_file" || "$(sha256sum "$store_file" | cut -d ' ' -f1)" != "$new_digest" \
    || "native-cli-store:$(date -r "$store_file" +%s%3N)" != "$new_generation" ]]; then
  recover; exit 0
fi
if ! agent-host --rebind-provider-auth "$provider" --drained >/dev/null 2>&1; then recover; exit 0; fi
for unit in "${units[@]}"; do
  if ! restart_unit "$unit" || ! check_unit "$unit" "$new_digest" "$new_generation"; then recover; exit 0; fi
done
if ! rm -f -- "$rollback_store"; then
  printf 'recovery-required\n' >"$receipt_file"; echo recovery-required; exit 0
fi
printf 'verified\n' >"$receipt_file"
echo verified
""";

    private const string EnvironmentTemplate = """
set -uo pipefail
operation_id='__OPERATION_ID__'
umask 077
exec 9>"$HOME/.claude/.agent-studio-renewal.lock"
flock -n 9 || { echo pending; exit 0; }
receipt_file="$HOME/.local/state/agent-studio/provider-renewal/$operation_id"
[[ -f "$receipt_file" ]] || { echo pending; exit 0; }
mapfile -t receipt <"$receipt_file"
[[ "${receipt[0]}" == installed-awaiting-runner ]] || {
  case "${receipt[0]}" in verified) echo verified ;; *) echo recovery-required ;; esac
  exit 0
}
rollback_file="${receipt[1]:-}"
new_generation="${receipt[2]:-}"
had_prior_file="${receipt[3]:-unknown}"
provider_auth_file=/etc/agent-runner/provider-auth.env
if [[ "$rollback_file" != /etc/agent-runner/.provider-auth.rollback.* ]] \
    || ! sudo -n test -f "$rollback_file" || [[ ! "$new_generation" =~ ^renewal_[a-f0-9]+$ ]]; then
  for unit in agent-host.service agent-runner.service agent-runner-review.service; do
    sudo -n systemctl cat "$unit" >/dev/null 2>&1 || continue
    sudo -n systemctl stop "$unit" || true
  done
  printf 'recovery-required\n' >"$receipt_file"; echo recovery-required; exit 0
fi
if [[ "$had_prior_file" != 0 && "$had_prior_file" != 1 ]]; then
  had_prior_file=0
  if sudo -n test -s "$rollback_file"; then had_prior_file=1; fi
fi
if pgrep -u "$(id -u)" -x claude >/dev/null 2>&1; then echo pending; exit 0; fi
units=()
if sudo -n systemctl cat agent-host.service >/dev/null 2>&1; then units+=(agent-host.service)
elif sudo -n systemctl cat agent-runner.service >/dev/null 2>&1; then units+=(agent-runner.service); fi
if sudo -n systemctl cat agent-runner-review.service >/dev/null 2>&1; then units+=(agent-runner-review.service); fi
if ((${#units[@]} < 2)); then echo pending; exit 0; fi
check_unit() {
  local pid
  pid=$(sudo -n systemctl show --property=MainPID --value "$1")
  [[ "$pid" =~ ^[1-9][0-9]*$ ]] || return 1
  sudo -n bash -c '
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
  ' bash "$pid" "$provider_auth_file"
}
restart_unit() {
  if [[ "$1" == agent-runner-review.service ]]; then
    sudo -n /usr/local/sbin/agent-runner-deploy restart-review >/dev/null 2>&1
  else sudo -n systemctl restart "$1"; fi
}
recover() {
  local restored=1 tmp unit
  if [[ "$had_prior_file" == 1 ]]; then
    tmp=$(sudo -n mktemp /etc/agent-runner/.provider-auth.restore.XXXXXX) || restored=0
    if ((restored)) && ! sudo -n install -m 0640 -o root -g agent "$rollback_file" "$tmp"; then restored=0; fi
    if ((restored)) && ! sudo -n mv -fT -- "$tmp" "$provider_auth_file"; then restored=0; fi
    [[ -z "${tmp:-}" ]] || sudo -n rm -f -- "$tmp"
  else
    sudo -n rm -f -- "$provider_auth_file" || restored=0
  fi
  if ((restored)); then
    for unit in "${units[@]}"; do
      if ! restart_unit "$unit" || ! check_unit "$unit"; then restored=0; break; fi
    done
  fi
  if ((restored)) && sudo -n rm -f -- "$rollback_file"; then
    printf 'rollback-restored\n' >"$receipt_file"; echo rollback-restored
  else
    for unit in "${units[@]}"; do sudo -n systemctl stop "$unit" || true; done
    printf 'recovery-required\n' >"$receipt_file"; echo recovery-required
  fi
}
generation=$(sudo -n awk -F= '$1 == "AGENT_STUDIO_CLAUDE_AUTH_GENERATION" { print $2 }' "$provider_auth_file")
if [[ "$generation" != "$new_generation" ]]; then recover; exit 0; fi
for unit in "${units[@]}"; do
  if ! restart_unit "$unit" || ! check_unit "$unit"; then recover; exit 0; fi
done
if ! sudo -n rm -f -- "$rollback_file"; then
  printf 'recovery-required\n' >"$receipt_file"; echo recovery-required; exit 0
fi
printf 'verified\n' >"$receipt_file"
echo verified
""";
}
