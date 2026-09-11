#!/usr/bin/env bash
# Product-owned agent host onboarding controller (AGT-2094).
#
# This controller is launched from the standard visible CLI task. All setup
# commands run over SSH on the selected host, while stdout/stderr remain in the
# canonical task conversation. The agent host daemon is started only by systemd.
set -euo pipefail

host=""
server_url=""
topology=""
client_id=""
runner_id=""
auth_token_file=""
runner_name=""
role="coding"
git_remote=""
git_push_remote=""
package_id="CodingAgentRunner"
runner_command="agent-host"
minimum_version="0.5.0"
provider_auth_file="/etc/agent-runner/provider-auth.env"
skip_auth=0

usage() {
  cat <<'EOF'
Usage: remote-runner-onboard.sh \
  --host <ssh-alias-or-user@host> \
  --server <task-server-url-visible-from-host> \
  --topology <central|tunnel|lan> \
  --client-id <optional-attribution-label> \
  --runner-id <owner-enrolled-runner-id> \
  --runner-name <runner-name> \
  --role <coding|review> \
  --git-remote <fetch-origin-url> \
  [--git-push-remote <write-origin-url>] [options]

Options:
  --role <role>           Managed service role (default: coding)
  --package-id <id>       NuGet DotnetTool package (default: CodingAgentRunner)
  --runner-command <cmd>  Installed tool command (default: agent-host)
  --minimum-version <v>   Minimum accepted package version (default: 0.5.0)
  --auth-token-file <p>   Protected Runner credential file already on the host
  --skip-auth             Do not launch login flows; status checks still run
  -h, --help              Show this help

The Task Server URL is tested from the remote host before installation. A
workstation loopback URL is valid only when --topology tunnel is selected and
the URL names the tunnel listener on the remote host (normally port 15031).
EOF
}

die() {
  printf '[onboarding] ERROR: %s\n' "$*" >&2
  exit 2
}

while (($#)); do
  case "$1" in
    --host) host="${2:-}"; shift 2 ;;
    --server) server_url="${2:-}"; shift 2 ;;
    --topology) topology="${2:-}"; shift 2 ;;
    --client-id) client_id="${2:-}"; shift 2 ;;
    --runner-id) runner_id="${2:-}"; shift 2 ;;
    --auth-token-file) auth_token_file="${2:-}"; shift 2 ;;
    --runner-name) runner_name="${2:-}"; shift 2 ;;
    --role) role="${2:-}"; shift 2 ;;
    --git-remote) git_remote="${2:-}"; shift 2 ;;
    --git-push-remote) git_push_remote="${2:-}"; shift 2 ;;
    --package-id) package_id="${2:-}"; shift 2 ;;
    --runner-command) runner_command="${2:-}"; shift 2 ;;
    --minimum-version) minimum_version="${2:-}"; shift 2 ;;
    --skip-auth) skip_auth=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) die "Unknown argument '$1'. Run with --help." ;;
  esac
done

[[ -n "$host" ]] || die "--host is required."
[[ -n "$server_url" ]] || die "--server is required."
[[ -n "$topology" ]] || die "--topology is required."
[[ -n "$runner_name" ]] || die "--runner-name is required."
[[ -n "$git_remote" ]] || die "--git-remote is required."
[[ "$role" =~ ^(coding|review)$ ]] || die "--role must be coding or review."
if [[ "$role" == "coding" ]]; then
  [[ -n "$git_push_remote" ]] || die "--git-push-remote is required for the coding role."
fi
if [[ -z "$auth_token_file" ]]; then
  if [[ "$role" == "coding" ]]; then
    auth_token_file="/etc/agent-runner/runner-auth-token"
  else
    auth_token_file="/etc/agent-runner/review-auth-token"
  fi
fi

host_pattern='^([A-Za-z0-9][A-Za-z0-9._-]*@)?[A-Za-z0-9][A-Za-z0-9._-]*$'
server_url_pattern='^https?://(\[[0-9A-Fa-f:]+\]|[A-Za-z0-9.-]+)(:[0-9]{1,5})?(/[A-Za-z0-9._~:/%+-]*)?$'
https_git_pattern='^https://[A-Za-z0-9.-]+(:[0-9]{1,5})?/[A-Za-z0-9._~/%+@:-]+$'
ssh_git_pattern='^ssh://([A-Za-z0-9][A-Za-z0-9._-]*@)?[A-Za-z0-9.-]+(:[0-9]{1,5})?/[A-Za-z0-9._~/%+@:-]+$'
scp_git_pattern='^[A-Za-z0-9][A-Za-z0-9._-]*@[A-Za-z0-9.-]+:[A-Za-z0-9._~/%+@:-]+$'

[[ "$host" =~ $host_pattern ]] || die "The SSH target contains unsupported characters. Use a configured alias or user@host."
[[ "$topology" =~ ^(central|tunnel|lan)$ ]] || die "--topology must be central, tunnel, or lan."
[[ "$server_url" =~ $server_url_pattern ]] || die "--server must be an http(s) URL without embedded credentials, fragments, whitespace, or shell characters."
[[ -z "$client_id" || "$client_id" =~ ^[A-Za-z0-9][A-Za-z0-9._-]*$ ]] || die "--client-id contains unsupported characters."
[[ -z "$runner_id" || "$runner_id" =~ ^runner_[A-Za-z0-9_-]+$ ]] || die "--runner-id must be the runner_<id> returned by enrollment."
[[ "$auth_token_file" =~ ^/[A-Za-z0-9._/-]+$ ]] || die "--auth-token-file must be an absolute path without shell characters."
[[ "$runner_name" =~ ^[A-Za-z0-9][A-Za-z0-9._-]*$ ]] || die "--runner-name contains unsupported characters."
[[ "$package_id" =~ ^[A-Za-z0-9][A-Za-z0-9._-]*$ ]] || die "--package-id contains unsupported characters."
[[ "$runner_command" =~ ^[A-Za-z0-9][A-Za-z0-9._-]*$ ]] || die "--runner-command contains unsupported characters."
[[ "$minimum_version" =~ ^[0-9]+\.[0-9]+\.[0-9]+([.-][A-Za-z0-9.-]+)?$ ]] || die "--minimum-version is not a semantic version."
if [[ ! "$git_remote" =~ $https_git_pattern \
      && ! "$git_remote" =~ $ssh_git_pattern \
      && ! "$git_remote" =~ $scp_git_pattern ]]; then
  die "--git-remote must be a credential-free SSH or HTTPS origin URL."
fi
if [[ -n "$git_push_remote" \
      && ! "$git_push_remote" =~ $ssh_git_pattern \
      && ! "$git_push_remote" =~ $scp_git_pattern \
      && ! "$git_push_remote" =~ $https_git_pattern ]]; then
  die "--git-push-remote must be a credential-free SSH or HTTPS origin URL."
fi

case "$server_url" in
  http://localhost:*|https://localhost:*|http://127.0.0.1:*|https://127.0.0.1:*)
    [[ "$topology" == "tunnel" ]] || die \
      "'$server_url' is loopback from the runner's point of view. Choose a central/LAN URL, or select tunnel and provide the remote tunnel listener (normally http://127.0.0.1:15031)."
    ;;
esac

service_auth=0
if [[ "$topology" != "tunnel" ]]; then
  [[ "$server_url" == https://* ]] || die "Central and LAN Task Server URLs must use HTTPS."
  [[ -n "$runner_id" ]] || die "--runner-id is required for a networked Task Server. Enroll the Runner as an owner first."
  service_auth=1
else
  runner_id="${runner_id:-$runner_name}"
  [[ -n "$client_id" ]] || die "--client-id is required for the local profile reached through a tunnel."
fi

printf '[onboarding] host=%s runner=%s role=%s client=%s topology=%s server=%s\n' \
  "$host" "$runner_name" "$role" "${client_id:-none}" "$topology" "$server_url"

ssh_base=(ssh -o BatchMode=yes -o ConnectTimeout=10)

printf '[onboarding] phase=preflight Checking SSH, sudo, .NET, and Task Server reachability from the host.\n'
"${ssh_base[@]}" -T "$host" bash -s -- "$server_url" "$client_id" "$runner_id" "$auth_token_file" "$service_auth" <<'REMOTE_PREFLIGHT'
set -euo pipefail
server_url="$1"
client_id="$2"
runner_id="$3"
auth_token_file="$4"
service_auth="$5"
printf '[remote] connected host=%s user=%s\n' "$(hostname)" "$(id -un)"
command -v sudo >/dev/null || { echo '[remote] sudo is required.' >&2; exit 20; }
sudo -n true || { echo '[remote] passwordless sudo is required for unattended systemd installation.' >&2; exit 21; }
command -v dotnet >/dev/null || { echo '[remote] .NET 10 is missing.' >&2; exit 22; }
dotnet_version="$(dotnet --version)"
[[ "$dotnet_version" == 10.* ]] || { printf '[remote] .NET 10 is required; found %s.\n' "$dotnet_version" >&2; exit 23; }
command -v curl >/dev/null || { echo '[remote] curl is missing.' >&2; exit 24; }
health_url="${server_url%/}/healthz"
if ! curl --fail --show-error --silent --max-time 10 "$health_url" >/dev/null; then
  printf '[remote] Task Server is not reachable at %s.\n' "$health_url" >&2
  echo '[remote] Configure a central URL, a supervised reverse tunnel, or a protected LAN binding, then retry.' >&2
  exit 25
fi
printf '[remote] Task Server reachable: %s; dotnet=%s\n' "$health_url" "$dotnet_version"

if [[ "$service_auth" == 1 ]]; then
  sudo test -f "$auth_token_file" || { printf '[remote] Runner credential file is missing: %s\n' "$auth_token_file" >&2; exit 26; }
  token="$(sudo cat "$auth_token_file")"
  [[ "$token" == rnr.* ]] || { echo '[remote] Runner credential file does not contain an rnr.* service credential.' >&2; exit 27; }
  umask 077
  curl_config="$(mktemp)"
  trap 'rm -f "$curl_config"' EXIT
  printf 'header = "Authorization: Bearer %s"\n' "$token" >"$curl_config"
  identity_url="${server_url%/}/api/auth/runner"
  identity_json="$(curl --config "$curl_config" --fail --show-error --silent --max-time 10 "$identity_url")" || {
    echo '[remote] Runner service credential was not accepted.' >&2; exit 28;
  }
  printf '%s' "$identity_json" | grep -Fq "\"id\":\"$runner_id\"" || {
    printf '[remote] Credential identity does not match requested Runner id %s.\n' "$runner_id" >&2; exit 29;
  }
  printf '[remote] Runner service identity verified: %s\n' "$runner_id"
else
  identity_url="${server_url%/}/api/clients/${client_id}"
  curl --fail --show-error --silent --max-time 10 -H "X-Client-Id: $client_id" "$identity_url" >/dev/null || {
    printf '[remote] Local-profile client attribution %s was not found.\n' "$client_id" >&2; exit 26;
  }
fi
REMOTE_PREFLIGHT

printf '[onboarding] phase=install Installing/updating the agent host tool and agent CLIs.\n'
if ! "${ssh_base[@]}" -T "$host" bash -s -- "$package_id" "$runner_command" "$minimum_version" <<'REMOTE_INSTALL'
set -euo pipefail
package_id="$1"
runner_command="$2"
minimum_version="$3"
export PATH="$HOME/.dotnet/tools:$HOME/.local/bin:$PATH"

tool_root="$HOME/.local/share/agent-host-tools"
releases_root="$tool_root/releases"
staging_root="$tool_root/.staging"
mkdir -p "$releases_root" "$staging_root"
stage_root="$(mktemp -d "$staging_root/release.XXXXXX")"
cleanup_stage() {
  [[ -z "${stage_root:-}" ]] || rm -rf -- "$stage_root"
}
trap cleanup_stage EXIT

# Never update the files of a running multi-file .NET application in place.
# Install the complete tool into a new directory, then expose that immutable
# version through one atomic symlink switch. Existing daemons and detached
# workers continue to resolve their already selected release directory.
dotnet tool install --tool-path "$stage_root" "$package_id"
installed_version="$(dotnet tool list --tool-path "$stage_root" | awk -v id="$package_id" 'tolower($1) == tolower(id) { print $2; exit }')"
[[ -n "$installed_version" ]] || { printf '[remote] NuGet package %s did not register as a tool.\n' "$package_id" >&2; exit 30; }
lowest="$(printf '%s\n%s\n' "$minimum_version" "$installed_version" | sort -V | head -n 1)"
[[ "$lowest" == "$minimum_version" ]] || { printf '[remote] Runner tool %s is below required %s (found %s).\n' "$package_id" "$minimum_version" "$installed_version" >&2; exit 31; }
[[ -x "$stage_root/$runner_command" ]] || { printf '[remote] Tool package %s does not expose command %s.\n' "$package_id" "$runner_command" >&2; exit 32; }

release_root="$releases_root/$installed_version"
if [[ -e "$release_root" ]]; then
  [[ -x "$release_root/$runner_command" ]] || {
    printf '[remote] Existing runner release is incomplete: %s\n' "$release_root" >&2
    exit 34
  }
  cleanup_stage
else
  mv "$stage_root" "$release_root"
fi
stage_root=""
# Candidate is not a service-consumed path. The systemd phase moves current
# only after an active Review daemon acknowledges closed claim admission.
ln -sfnT "$release_root" "$tool_root/candidate"

command -v npm >/dev/null || { echo '[remote] Node.js/npm is missing. Install Node 22, then retry.' >&2; exit 33; }
if ! npm install --global @openai/codex @anthropic-ai/claude-code; then
  echo '[remote] User-level npm global install failed; retrying through passwordless sudo.' >&2
  sudo -n npm install --global @openai/codex @anthropic-ai/claude-code
fi
printf '[remote] runner-package=%s runner-version=%s\n' "$package_id" "$installed_version"
codex --version
claude --version
REMOTE_INSTALL
then
  die "Runner installation failed. Verify that '$package_id' version $minimum_version or newer is published as a NuGet package with package type DotnetTool and exposes '$runner_command'. The CodingAgentRunner 0.5.0 library reference alone is not installable with 'dotnet tool'."
fi

printf '[onboarding] phase=provider-auth Checking the Studio-provisioned provider EnvironmentFile.\n'
"${ssh_base[@]}" -T "$host" bash -s -- "$provider_auth_file" <<'REMOTE_PROVIDER_AUTH'
set -euo pipefail
provider_auth_file="$1"
sudo test -f "$provider_auth_file" || {
  printf '[remote] Provider authentication is not provisioned at %s. Use the Execution Hosts auth step first.\n' "$provider_auth_file" >&2
  exit 35
}
metadata="$(sudo stat -c '%U:%G:%a' "$provider_auth_file")"
[[ "$metadata" == "root:agent:640" ]] || {
  printf '[remote] Provider EnvironmentFile must be root:agent:640; found %s.\n' "$metadata" >&2
  exit 36
}
variables="$(sudo awk -F= '/^(CLAUDE_CODE_OAUTH_TOKEN|ANTHROPIC_API_KEY)=/ { print $1 }' "$provider_auth_file")"
[[ -n "$variables" ]] || {
  echo '[remote] Provider EnvironmentFile contains no supported provider variable.' >&2
  exit 37
}
printf '[remote] provider-auth-file=%s metadata=%s variables=%s\n' \
  "$provider_auth_file" "$metadata" "$(printf '%s' "$variables" | paste -sd, -)"
REMOTE_PROVIDER_AUTH

remote_login_status() {
  "${ssh_base[@]}" -T "$host" bash -s -- "$provider_auth_file" <<'REMOTE_AUTH_STATUS'
set -uo pipefail
export PATH="$HOME/.dotnet/tools:$HOME/.local/bin:$PATH"
provider_auth_file="$1"
while IFS= read -r provider_auth_entry; do
  export "$provider_auth_entry"
done < <(sudo awk '/^(CLAUDE_CODE_OAUTH_TOKEN|ANTHROPIC_API_KEY)=/' "$provider_auth_file")
codex_ok=0
claude_ok=0
echo '[remote] Codex authentication status:'
codex login status && codex_ok=1
echo '[remote] Claude authentication status:'
claude auth status --text && claude_ok=1
((codex_ok == 1 && claude_ok == 1))
REMOTE_AUTH_STATUS
}

printf '[onboarding] phase=oauth Checking host-owned CLI authentication.\n'
if ! remote_login_status; then
  ((skip_auth == 0)) || die "One or more agent CLIs are not authenticated and --skip-auth was selected."

  printf '[onboarding] oauth=codex Open the URL shown below in the operator browser and enter the one-time device code.\n'
  "${ssh_base[@]}" -tt "$host" "export PATH=\"\$HOME/.dotnet/tools:\$HOME/.local/bin:\$PATH\"; codex login status || codex login --device-auth; codex login status"

  printf '[onboarding] oauth=claude A headless setup token must already be provisioned through SSH stdin at /etc/agent-runner/provider-auth.env.\n'

  remote_login_status || die "Authentication did not verify. Provision Claude through provider-auth.env as documented; never copy credential files from another host."
fi

printf '[onboarding] phase=systemd Writing configuration and enabling the OS-owned service.\n'
resource_governance_script="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/agent-host-resource-governance.sh"
[[ -x "$resource_governance_script" ]] \
  || die "The agent-host resource governance helper is missing or not executable: $resource_governance_script"
review_restart_guard="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/deploy/agent-host/systemd/20-agent-runner-review-restart-guard.conf"
"${ssh_base[@]}" -T "$host" \
  'helper_tmp="$(mktemp)"; trap '"'"'rm -f "$helper_tmp"'"'"' EXIT; cat >"$helper_tmp"; chmod 0755 "$helper_tmp"; sudo install -d -m 0755 /usr/local/libexec; sudo install -m 0755 "$helper_tmp" /usr/local/libexec/agent-host-resource-governance' \
  <"$resource_governance_script"
if [[ "$role" == "review" ]]; then
  [[ -f "$review_restart_guard" ]] \
    || die "The Review restart guard is missing: $review_restart_guard"
  "${ssh_base[@]}" -T "$host" \
    'guard_tmp="$(mktemp)"; trap '"'"'rm -f "$guard_tmp"'"'"' EXIT; cat >"$guard_tmp"; sudo install -d -m 0755 /usr/local/libexec; sudo install -m 0644 "$guard_tmp" /usr/local/libexec/agent-runner-review-restart-guard.conf' \
    <"$review_restart_guard"
fi
"${ssh_base[@]}" -T "$host" bash -s -- \
  "$server_url" "$client_id" "$runner_id" "$runner_name" "$role" "$git_remote" "$git_push_remote" "$runner_command" "$auth_token_file" "$service_auth" "$provider_auth_file" <<'REMOTE_SYSTEMD'
set -euo pipefail
server_url="$1"
client_id="$2"
runner_id="$3"
runner_name="$4"
role="$5"
git_remote="$6"
git_push_remote="$7"
runner_command="$8"
auth_token_file="$9"
service_auth="${10}"
provider_auth_file="${11}"
restart_guard_source="/usr/local/libexec/agent-runner-review-restart-guard.conf"
export PATH="$HOME/.dotnet/tools:$HOME/.local/bin:$PATH"
runner_user="$(id -un)"
runner_group="$(id -gn)"
runner_home="$HOME"
tool_root="$runner_home/.local/share/agent-host-tools"
candidate_release="$(readlink -f -- "$tool_root/candidate")"
runner_bin="$candidate_release/$runner_command"
agent_host_root="/opt/agent-host"
legacy_root="/opt/agent-runner"
[[ -x "$runner_bin" ]] || {
  printf '[remote] Immutable runner release is missing command %s: %s\n' "$runner_command" "$runner_bin" >&2
  exit 42
}
[[ "$(dirname -- "$candidate_release")" == "$tool_root/releases" ]] || {
  printf '[remote] Candidate runner release escaped the immutable release root: %s\n' \
    "$candidate_release" >&2
  exit 42
}

if [[ "$role" == "coding" ]]; then
  service_name="agent-runner"
  env_file="/etc/agent-runner/runner.env"
  service_root="/var/lib/agent-runner"
else
  service_name="agent-runner-review"
  env_file="/etc/agent-runner/review.env"
  service_root="/var/lib/agent-runner-review"
fi

env_tmp="$(mktemp)"
unit_tmp="$(mktemp)"
trap 'rm -f "$env_tmp" "$unit_tmp"' EXIT
chmod 600 "$env_tmp"
{
  printf 'RUNNER_SERVER_URL=%s\n' "$server_url"
  [[ -z "$client_id" ]] || printf 'RUNNER_CLIENT_ID=%s\n' "$client_id"
  printf 'RUNNER_ID=%s\n' "$runner_id"
  printf 'RUNNER_NAME=%s\n' "$runner_name"
  printf 'RUNNER_ROLE=%s\n' "$role"
  [[ "$service_auth" == 1 ]] && printf 'RUNNER_AUTH_TOKEN_FILE=%s\n' "$auth_token_file"
  printf 'RUNNER_GIT_REMOTE=%s\n' "$git_remote"
  [[ -z "$git_push_remote" ]] || printf 'RUNNER_GIT_PUSH_REMOTE=%s\n' "$git_push_remote"
  printf 'RUNNER_WORKDIR=%s/work\n' "$service_root"
  [[ "$role" != "review" ]] || printf 'RUNNER_REVIEW_WORKDIR=%s/review-work\n' "$service_root"
  printf 'RUNNER_STATE_DIR=%s/state\n' "$service_root"
  printf 'RUNNER_MAX_PARALLELISM=2\n'
} >"$env_tmp"

resource_policy="$(sudo /usr/local/libexec/agent-host-resource-governance \
  --role "$role" \
  --profile /etc/agent-host/profile.conf \
  --drop-in-dir "/etc/systemd/system/${service_name}.service.d" \
  --migrate-drop-ins)"

cat >"$unit_tmp" <<EOF
[Unit]
Description=Agent Studio $role agent host daemon
After=network-online.target
Wants=network-online.target
StartLimitIntervalSec=300
StartLimitBurst=5

[Service]
Type=simple
User=$runner_user
Group=$runner_group
WorkingDirectory=$service_root
Environment=HOME=$runner_home
Environment="PATH=$runner_home/.dotnet/tools:$runner_home/.local/bin:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin"
EnvironmentFile=$env_file
# One shared provider credential file for coding and review units. Keeping it
# after the role-specific file gives the centrally rotated value precedence.
EnvironmentFile=$provider_auth_file
ExecStart=$agent_host_root/current/$runner_command --poll
Restart=always
RestartSec=10s
TimeoutStopSec=90s
KillSignal=SIGTERM
KillMode=process
SyslogIdentifier=$service_name
StandardOutput=journal
StandardError=journal
$resource_policy
NoNewPrivileges=true
# PrivateTmp=true tears the unit's private /tmp mount away on every daemon
# restart, including from underneath a detached coding/review worker that
# KillMode=process deliberately left running. The worker keeps the deleted
# mount (dotnet/MSBuild pipes and NuGet's mkdtemp mutex fail with MSB1025,
# SocketException (99), or ENOENT) and a real build/test run is graded as a
# product failure instead of infrastructure. Plain host /tmp with per-attempt
# TMPDIR isolation (see RemoteReviewWorkspace/RunnerOptions) survives a
# restart untouched.
PrivateTmp=false
ProtectSystem=full
ReadWritePaths=$service_root $runner_home

[Install]
WantedBy=multi-user.target
Alias=agent-runner.service
EOF

sudo install -d -m 0750 /etc/agent-runner "$service_root" "$service_root/work" "$service_root/state"
if [[ "$role" == "review" ]]; then
  sudo install -d -m 0750 "$service_root/review-work"
fi
sudo chown -R "$runner_user:$runner_group" "$service_root"
provider_auth_metadata="$(sudo stat -c '%U:%G:%a' "$provider_auth_file")"
[[ "$provider_auth_metadata" == "root:agent:640" ]] || {
  printf '[remote] Provider EnvironmentFile changed during setup; expected root:agent:640, found %s.\n' \
    "$provider_auth_metadata" >&2
  exit 46
}
sudo install -d -m 0755 "$agent_host_root"
# Preserve the release boundary below the stable /opt path. The compatibility
# command links point through current, never into files that an update mutates.
sudo ln -sfnT "$tool_root/current" "$agent_host_root/current"
sudo ln -sfn "current/$runner_command" "$agent_host_root/agent-host"
sudo ln -sfn agent-host "$agent_host_root/agent-runner"
if [[ -e "$legacy_root" && ! -L "$legacy_root" ]]; then
  legacy_backup="${legacy_root}.pre-agent-host"
  [[ ! -e "$legacy_backup" ]] || {
    printf '[remote] Cannot preserve legacy publish directory: %s already exists.\n' "$legacy_backup" >&2
    exit 41
  }
  sudo mv "$legacy_root" "$legacy_backup"
  printf '[remote] Preserved legacy publish directory at %s.\n' "$legacy_backup"
fi
sudo ln -sfnT "$agent_host_root" "$legacy_root"
if [[ "$service_auth" == 1 ]]; then
  sudo chown root:"$runner_group" "$auth_token_file"
  sudo chmod 0640 "$auth_token_file"
fi
sudo install -m 0640 -o root -g "$runner_group" "$env_tmp" "$env_file"
if [[ -f /etc/systemd/system/agent-runner.service && ! -L /etc/systemd/system/agent-runner.service ]]; then
  sudo systemctl stop agent-runner.service || true
fi
sudo install -m 0644 "$unit_tmp" "/etc/systemd/system/${service_name}.service"
if [[ "$role" == "review" ]]; then
  sudo install -d -m 0755 "/etc/systemd/system/${service_name}.service.d"
  sudo install -m 0644 \
    "$restart_guard_source" \
    "/etc/systemd/system/${service_name}.service.d/20-agent-runner-review-restart-guard.conf"
fi
sudo systemctl daemon-reload
sudo systemctl enable "$service_name"
previous_main_pid="$(sudo systemctl show --property=MainPID --value "$service_name")"
[[ "$previous_main_pid" =~ ^[0-9]+$ ]] || {
  printf '[remote] Service %s returned an invalid MainPID before replacement.\n' "$service_name" >&2
  exit 47
}
if [[ "$role" == "review" ]]; then
  refuse_manual_stop="$(sudo systemctl show --property=RefuseManualStop --value "$service_name")"
  [[ "$refuse_manual_stop" == "yes" ]] || {
    printf '[remote] Review service %s did not adopt RefuseManualStop=true.\n' "$service_name" >&2
    exit 48
  }
  restart_policy="$(sudo systemctl show --property=Restart --value "$service_name")"
  [[ "$restart_policy" == "on-failure" ]] || {
    printf '[remote] Review service %s did not adopt Restart=on-failure.\n' "$service_name" >&2
    exit 49
  }
  if [[ "$previous_main_pid" =~ ^[1-9][0-9]*$ ]] \
      && sudo systemctl is-active --quiet "$service_name"; then
    live_state_dir="$(
      sudo cat "/proc/$previous_main_pid/environ" \
        | tr '\0' '\n' \
        | awk -F= '$1 == "RUNNER_STATE_DIR" { print substr($0, index($0, "=") + 1) }'
    )"
    [[ -n "$live_state_dir" && "$live_state_dir" == /* && "$live_state_dir" != "/" ]] || {
      printf '[remote] Could not resolve the active Review RUNNER_STATE_DIR; replacement refused.\n' >&2
      exit 50
    }
    guard_status=0
    env -i PATH="$PATH" \
      "$runner_bin" --restart-guard --hold-admission --role review \
      --state-dir "$live_state_dir" || guard_status="$?"
    ((guard_status == 0)) || {
      printf '[remote] Review replacement guard refused (exit %s). Drain first with agent-runner-deploy drain.\n' \
        "$guard_status" >&2
      exit 50
    }
    guarded_main_pid="$(sudo systemctl show --property=MainPID --value "$service_name")"
    [[ "$guarded_main_pid" == "$previous_main_pid" ]] \
        && sudo systemctl is-active --quiet "$service_name" || {
      printf '[remote] Review MainPID changed before guarded replacement; admission remains closed.\n' >&2
      exit 50
    }
    # RefuseManualStop rejects stop/restart. Signal only the main daemon and
    # wait for its successful handoff exit before explicitly starting the next
    # generation. Review's on-failure policy leaves a completed drain stopped.
    sudo systemctl kill --kill-whom=main --signal=SIGTERM "$service_name"
    daemon_stopped=0
    for _ in $(seq 1 120); do
      active_state="$(sudo systemctl show --property=ActiveState --value "$service_name")"
      candidate_main_pid="$(sudo systemctl show --property=MainPID --value "$service_name")"
      if [[ "$active_state" != "active" \
          && "$active_state" != "activating" \
          && "$candidate_main_pid" == "0" ]]; then
        daemon_stopped=1
        break
      fi
      sleep 1
    done
    ((daemon_stopped == 1)) || {
      printf '[remote] Review service %s did not stop cleanly before replacement.\n' "$service_name" >&2
      exit 50
    }
  fi
  ln -sfnT "$candidate_release" "$tool_root/current"
  # An explicit install/update resumes a previously drained role only after
  # the old daemon is confirmed gone.
  sudo rm -f -- \
    "$service_root/state/review-drain-requested.json" \
    "$service_root/state/review-drain-acknowledged.json"
  if [[ -n "${live_state_dir:-}" && "$live_state_dir" != "$service_root/state" ]]; then
    sudo rm -f -- \
      "$live_state_dir/review-drain-requested.json" \
      "$live_state_dir/review-drain-acknowledged.json"
  fi
  sudo systemctl start "$service_name"
else
  ln -sfnT "$candidate_release" "$tool_root/current"
  sudo systemctl restart "$service_name"
fi

main_pid=""
for _ in $(seq 1 120); do
  if sudo systemctl is-active --quiet "$service_name"; then
    candidate_main_pid="$(sudo systemctl show --property=MainPID --value "$service_name")"
    if [[ "$candidate_main_pid" =~ ^[1-9][0-9]*$ \
        && "$candidate_main_pid" != "$previous_main_pid" ]]; then
      main_pid="$candidate_main_pid"
      break
    fi
  fi
  sleep 1
done
sudo systemctl is-enabled "$service_name"
sudo systemctl is-active "$service_name"
[[ "$main_pid" =~ ^[1-9][0-9]*$ ]] || {
  printf '[remote] Service %s did not expose a replacement MainPID.\n' "$service_name" >&2
  exit 43
}
provider_variables="$(sudo cat "/proc/${main_pid}/environ" | tr '\0' '\n' \
  | sed -n -E 's/^(CLAUDE_CODE_OAUTH_TOKEN|ANTHROPIC_API_KEY)=.*/\1/p')"
[[ -n "$provider_variables" ]] || {
  printf '[remote] Service %s did not receive provider authentication through %s.\n' \
    "$service_name" "$provider_auth_file" >&2
  exit 44
}
printf '[remote] provider-auth-process-environment=%s variables=%s\n' \
  "$service_name" "$(printf '%s' "$provider_variables" | paste -sd, -)"
RUNNER_AUTH_TOKEN_FILE="$([[ "$service_auth" == 1 ]] && printf '%s' "$auth_token_file")" \
  "$agent_host_root/current/$runner_command" --health-check --server "$server_url"

if [[ "$role" == "coding" ]]; then
  git_status=""
  for _ in $(seq 1 30); do
    journal="$(sudo journalctl -u "$service_name" -n 80 --no-pager)"
    printf '%s' "$journal" | grep -Fq 'runner-git-capability status=ready-no-workflow-scope' && { git_status=ready-no-workflow-scope; break; }
    printf '%s' "$journal" | grep -Fq 'runner-git-capability status=ready' && { git_status=ready; break; }
    printf '%s' "$journal" | grep -Fq 'runner-git-capability status=read-only' && { git_status=read-only; break; }
    sleep 2
  done
  [[ "$git_status" == ready || "$git_status" == ready-no-workflow-scope ]] || {
    sudo journalctl -u "$service_name" -n 40 --no-pager >&2
    printf '[remote] Runner Git push capability is %s; claims remain disabled.\n' "${git_status:-unreported}" >&2
    exit 40
  }
  if [[ "$git_status" == ready-no-workflow-scope ]]; then
    printf '[remote] Contents push is ready, but GitHub workflow writes need additional token permissions. See docs/operations/setup/linux-runner-host.md#token-requirements.\n' >&2
  fi
  provider_probe_lines=""
  for _ in $(seq 1 30); do
    provider_probe_lines="$(sudo journalctl -u "$service_name" -n 120 --no-pager \
      | grep -F 'runner-provider-auth binary=' || true)"
    [[ -n "$provider_probe_lines" ]] && break
    sleep 2
  done
  [[ -n "$provider_probe_lines" ]] || {
    sudo journalctl -u "$service_name" -n 60 --no-pager >&2
    echo '[remote] Runner provider-auth probe did not publish a result.' >&2
    exit 45
  }
  printf '%s\n' "$provider_probe_lines" | tail -n 4
fi
printf '[remote] service=%s role=%s active health=passed identity=%s\n' "$service_name" "$role" "$runner_id"
REMOTE_SYSTEMD

printf '[onboarding] completed host=%s runner=%s role=%s client=%s\n' "$host" "$runner_name" "$role" "$client_id"
printf '[onboarding] Next: assign the probe project to %s and run one Ready task through the fenced remote claim path.\n' "$runner_name"
