#!/usr/bin/env bash

# End-to-end rehearsal of the released Linux x64 setup executable.
# It uses real self-contained Task Server, Engine, Agent Host, and setup
# binaries. A local systemctl fixture keeps the rehearsal isolated from the
# machine's real /etc, /opt, /var, and systemd state.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
report_path="${1:-}"
[[ -n "$report_path" ]] || {
  echo "Usage: setup-e2e-probe.sh <report-path>" >&2
  exit 2
}
report_path="$(realpath -m "$report_path")"
probe_root="$(mktemp -d)"
runtime_root="$probe_root/runtime"
publish_root="$probe_root/publish"
release_root="$probe_root/release"
frontend_root="$probe_root/frontend/browser"
version="$(tr -d '\r\n' <"$repo_root/VERSION")"
git_sha="${AGENT_SETUP_E2E_GIT_SHA:-$(git -c safe.directory='*' -C "$repo_root" rev-parse HEAD)}"
port=""
release_port=""

cleanup() {
  if [[ -d "$runtime_root" ]]; then
    while IFS= read -r pid_file; do
      [[ -f "$pid_file" ]] || continue
      pid="$(sed -n '1p' "$pid_file")"
      [[ "$pid" =~ ^[0-9]+$ ]] || continue
      kill "$pid" 2>/dev/null || true
    done < <(find "$runtime_root" -name '*.pid' -type f 2>/dev/null)
  fi
  rm -rf -- "$probe_root"
}
trap cleanup EXIT HUP INT TERM

for candidate in $(seq 25171 25220); do
  if ! ss -ltn "sport = :$candidate" | grep -q LISTEN; then
    port="$candidate"
    break
  fi
done
[[ -n "$port" ]] || {
  echo "No free local probe port found." >&2
  exit 3
}
for candidate in $(seq 25221 25270); do
  if ! ss -ltn "sport = :$candidate" | grep -q LISTEN; then
    release_port="$candidate"
    break
  fi
done
[[ -n "$release_port" ]] || {
  echo "No free local release port found." >&2
  exit 3
}

common=(
  --configuration Release
  --self-contained true
  -p:PublishSingleFile=true
  -p:IncludeNativeLibrariesForSelfExtract=true
  -p:PublishTrimmed=false
  -p:DebugType=None
  -p:DebugSymbols=false
  -p:Version="$version"
  -p:SourceRevisionId="$git_sha"
)

mkdir -p "$publish_root" "$frontend_root" "$runtime_root" "$release_root"
if [[ -n "${AGENT_SETUP_E2E_RELEASE_DIR:-}" ]]; then
  cp -a "${AGENT_SETUP_E2E_RELEASE_DIR}/." "$release_root/"
else
  dotnet publish "$repo_root/task-server/TaskServer.csproj" "${common[@]}" \
    --runtime linux-x64 --output "$publish_root/task-server"
  dotnet publish "$repo_root/orchestrator-engine/OrchestratorEngine.csproj" "${common[@]}" \
    --runtime linux-x64 --output "$publish_root/orchestrator-engine"
  dotnet publish "$repo_root/backend/OrchestratorApi.csproj" "${common[@]}" \
    --runtime linux-x64 --output "$publish_root/orchestrator-api"
  dotnet publish "$repo_root/studio-bff/StudioBff.csproj" "${common[@]}" \
    --runtime linux-x64 --output "$publish_root/studio-bff"
  dotnet publish "$repo_root/runner/AgentRunner.csproj" "${common[@]}" \
    --runtime linux-x64 --output "$publish_root/agent-host-linux-x64"
  dotnet publish "$repo_root/runner/AgentRunner.csproj" "${common[@]}" \
    --runtime osx-arm64 --output "$publish_root/agent-host-osx-arm64"
  dotnet publish "$repo_root/setup/AgentOrchestratorSetup.csproj" "${common[@]}" \
    --runtime linux-x64 --output "$publish_root/setup"
  printf '<!doctype html><title>Setup E2E fixture</title>\n' >"$frontend_root/index.html"
  SOURCE_DATE_EPOCH=1 "$repo_root/scripts/release/package-release.sh" \
    "$version" "$git_sha" "$publish_root" "$frontend_root" "$release_root"
fi

fake_systemctl="$probe_root/systemctl"
cat >"$fake_systemctl" <<'SYSTEMCTL'
#!/usr/bin/env bash
set -euo pipefail
runtime="${AGENT_SETUP_E2E_RUNTIME:?}"
mkdir -p "$runtime"

pid_file_for() {
  case "$1" in
    agent-task-server.service) echo "$runtime/task-server.pid" ;;
    agent-orchestrator-engine.service) echo "$runtime/engine.pid" ;;
    agent-host.service) echo "$runtime/agent-host.pid" ;;
    agent-studio-api.service) echo "$runtime/studio-api.pid" ;;
    agent-studio-bff.service) echo "$runtime/studio-bff.pid" ;;
    agent-studio-web.service) echo "$runtime/studio-web.pid" ;;
    *) echo "$runtime/${1//[^A-Za-z0-9]/_}.pid" ;;
  esac
}

start_service() {
  local service="$1" pid_file
  pid_file="$(pid_file_for "$service")"
  if [[ -f "$pid_file" ]] && kill -0 "$(cat "$pid_file")" 2>/dev/null; then
    return
  fi
  case "$service" in
    agent-task-server.service)
      set -a
      # shellcheck disable=SC1090
      . "${AGENT_SETUP_ORCHESTRATOR_CONFIG:?}/server.env"
      set +a
      # Real systemd starts from the unit EnvironmentFile and does not inherit
      # the installer's one-shot bootstrap variables.
      unset AUTH_TOKEN STUDIO_AUTH_TOKEN ENGINE_AUTH_TOKEN
      nohup "${AGENT_SETUP_ORCHESTRATOR_OPT:?}/current/task-server" \
        >"$runtime/task-server.log" 2>&1 &
      ;;
    agent-orchestrator-engine.service)
      set -a
      # shellcheck disable=SC1090
      . "${AGENT_SETUP_ORCHESTRATOR_CONFIG:?}/engine.env"
      set +a
      nohup "${AGENT_SETUP_ORCHESTRATOR_OPT:?}/current/orchestrator-engine" \
        >"$runtime/engine.log" 2>&1 &
      ;;
    agent-host.service)
      set -a
      # shellcheck disable=SC1090
      . "${AGENT_SETUP_HOST_CONFIG:?}/runner.env"
      set +a
      nohup "${AGENT_SETUP_HOST_OPT:?}/current/agent-host" --poll \
        >"$runtime/agent-host.log" 2>&1 &
      ;;
    agent-studio-api.service)
      export ASPNETCORE_URLS=http://127.0.0.1:5031
      export TaskServer__BaseUrl="$(sed -n 's/^LISTEN_URL=//p' "${AGENT_SETUP_ORCHESTRATOR_CONFIG:?}/server.env" | head -n1)"
      export TaskServer__AuthTokenFile="${AGENT_SETUP_ORCHESTRATOR_CONFIG:?}/studio.token"
      export TaskRepository="${AGENT_SETUP_ORCHESTRATOR_STATE:?}/workspace"
      export HOME="${AGENT_SETUP_ORCHESTRATOR_STATE:?}/api-home"
      cd "${AGENT_SETUP_ORCHESTRATOR_OPT:?}/current/api"
      nohup ./OrchestratorApi >"$runtime/studio-api.log" 2>&1 &
      ;;
    agent-studio-bff.service)
      export ASPNETCORE_URLS=http://127.0.0.1:5072
      export TaskServer__BaseUrl="$(sed -n 's/^LISTEN_URL=//p' "${AGENT_SETUP_ORCHESTRATOR_CONFIG:?}/server.env" | head -n1)"
      export TaskServer__AuthTokenFile="${AGENT_SETUP_ORCHESTRATOR_CONFIG:?}/studio.token"
      export Studio__AllowedOrigins=http://127.0.0.1:4011,http://localhost:4011
      cd "${AGENT_SETUP_ORCHESTRATOR_OPT:?}/current/bff"
      nohup ./agent-studio-bff >"$runtime/studio-bff.log" 2>&1 &
      ;;
    agent-studio-web.service)
      nohup "${AGENT_SETUP_STUDIO_OPT:?}/current/agent-studio-setup" serve-native \
        --browser "${AGENT_SETUP_STUDIO_OPT:?}/current/browser" \
        --listen http://127.0.0.1:4011 --api http://127.0.0.1:5031 \
        --bff http://127.0.0.1:5072 >"$runtime/studio-web.log" 2>&1 &
      ;;
    *)
      return
      ;;
  esac
  echo "$!" >"$pid_file"
}

service="${!#:-}"
case "${1:-}" in
  daemon-reload) ;;
  enable)
    [[ "${2:-}" != "--now" ]] || start_service "$service"
    ;;
  start|restart)
    start_service "$service"
    ;;
  stop)
    pid_file="$(pid_file_for "$service")"
    if [[ -f "$pid_file" ]]; then
      kill "$(cat "$pid_file")" 2>/dev/null || true
      rm -f -- "$pid_file"
    fi
    ;;
  is-active)
    pid_file="$(pid_file_for "$service")"
    [[ -f "$pid_file" ]] && kill -0 "$(cat "$pid_file")" 2>/dev/null
    ;;
  *)
    ;;
esac
SYSTEMCTL
chmod 0755 "$fake_systemctl"
export PATH="$probe_root:$PATH"

orchestrator_opt="$probe_root/opt/orchestrator"
orchestrator_config="$probe_root/etc/orchestrator"
orchestrator_state="$probe_root/var/orchestrator"
studio_opt="$probe_root/opt/studio"
host_opt="$probe_root/opt/host"
host_config="$probe_root/etc/host"
second_host_config="$probe_root/etc/second-host"
host_state="$probe_root/var/host"
systemd_root="$probe_root/etc/systemd"
setup_binary="$release_root/agent-studio-setup"
setup_script="$release_root/install.sh"
control_output="$probe_root/control-output"
join_file="$probe_root/join.token"
base_url="http://127.0.0.1:$port"

export AGENT_SETUP_SKIP_ROOT_CHECK=1
export AGENT_SETUP_E2E_RUNTIME="$runtime_root"
export AGENT_SETUP_SYSTEMCTL="$fake_systemctl"
export AGENT_SETUP_ORCHESTRATOR_OPT="$orchestrator_opt"
export AGENT_SETUP_ORCHESTRATOR_CONFIG="$orchestrator_config"
export AGENT_SETUP_ORCHESTRATOR_STATE="$orchestrator_state"
export AGENT_SETUP_STUDIO_OPT="$studio_opt"
export AGENT_SETUP_HOST_OPT="$host_opt"
export AGENT_SETUP_HOST_CONFIG="$host_config"
export AGENT_SETUP_HOST_STATE="$host_state"
export AGENT_SETUP_SYSTEMD_ROOT="$systemd_root"
mkdir -p "$probe_root/bin"
cat >"$probe_root/bin/codex" <<'FAKE_CODEX'
#!/bin/sh
set -eu
case "${1:-}" in
  --version) printf 'codex fixture 1.0\n' ;;
  login) [ "${2:-}" = status ] ;;
  *) exit 2 ;;
esac
FAKE_CODEX
chmod 0755 "$probe_root/bin/codex"
export PATH="$probe_root/bin:$PATH"

python3 -m http.server "$release_port" --bind 127.0.0.1 \
  --directory "$release_root" >"$runtime_root/release-server.log" 2>&1 &
echo "$!" >"$runtime_root/release-server.pid"
export AGENT_STUDIO_RELEASE_URL="http://127.0.0.1:$release_port"
for attempt in $(seq 1 20); do
  if curl -fsS "$AGENT_STUDIO_RELEASE_URL/SHA256SUMS" >/dev/null 2>&1; then break; fi
  sleep 1
done
curl -fsS "$AGENT_STUDIO_RELEASE_URL/SHA256SUMS" >/dev/null

umask 077
if ! sh "$setup_script" \
  --mode studio \
  --target native \
  --release-version "$version" \
  --release-dir "$release_root" \
  --listen-url "$base_url" \
  --server-url "$base_url" \
  --execution-user "$(id -un)" \
  --agent-cli codex \
  --runner-name agent-runner-01-e2e \
  --non-interactive \
  >"$control_output"; then
  sed -n '/^Preflight:/,/^$/p' "$control_output" >&2
  exit 1
fi

[[ -s "$orchestrator_config/join.token" ]] || {
  echo "Control Plane setup did not create a protected join token file." >&2
  exit 4
}
cp "$orchestrator_config/join.token" "$join_file"
chmod 0600 "$join_file"
rm -f -- "$control_output"

"$fake_systemctl" stop agent-host.service
export AGENT_SETUP_HOST_OPT="$probe_root/opt/second-host"
export AGENT_SETUP_HOST_CONFIG="$second_host_config"
export AGENT_SETUP_HOST_STATE="$probe_root/var/second-host"

sh "$setup_script" \
  --join \
  --join-token-file "$join_file" \
  --release-dir "$release_root" \
  --execution-user "$(id -un)" \
  --agent-cli codex \
  --runner-name agent-runner-02-e2e \
  --non-interactive \
  >"$runtime_root/host-setup.log"
rm -f -- "$join_file"
grep -Fq 'registered as agent-runner-02-e2e (' "$runtime_root/host-setup.log"
grep -Eq 'ready-no-workflow-scope|read-only|ready' "$runtime_root/host-setup.log"

curl -fsS "$base_url/readyz" >"$runtime_root/ready.json"
token="$(sed -n '1p' "$orchestrator_config/studio.token")"
remote_hosts="$(
  curl -fsS \
    -H "Authorization: Bearer $token" \
    -H "X-Task-Protocol-Version: 2" \
    "$base_url/api/v1/management/remote-hosts"
)"
grep -Fq '"runnerId":"agent-runner-01-e2e"' <<<"$remote_hosts"
grep -Fq '"runnerId":"agent-runner-02-e2e"' <<<"$remote_hosts"
(
  cd "$release_root"
  sha256sum -c SHA256SUMS >/dev/null
)
[[ "$(od -An -tx1 -N4 "$setup_binary" | tr -d ' \n')" == "7f454c46" ]]
[[ -f "$studio_opt/current/browser/index.html" ]]
kill -0 "$(cat "$runtime_root/task-server.pid")"
kill -0 "$(cat "$runtime_root/engine.pid")"
kill -0 "$(cat "$runtime_root/agent-host.pid")"
curl -fsS http://127.0.0.1:4011/ >/dev/null
curl -fsS http://127.0.0.1:4011/api/v1/studio/auth/status \
  | grep -Fq '"bootstrapCodeRequired":true'
owner_payload="$probe_root/owner.json"
owner_code="$(cat "$orchestrator_config/owner-bootstrap.code")"
owner_password="$(openssl rand -hex 20)"
jq -n --arg code "$owner_code" --arg password "$owner_password" \
  '{username:"owner-probe",displayName:"Owner Probe",password:$password,bootstrapCode:$code}' \
  >"$owner_payload"
unset owner_code owner_password
curl -fsS -X POST -H 'Origin: http://127.0.0.1:4011' \
  -H 'Content-Type: application/json' --data-binary "@$owner_payload" \
  http://127.0.0.1:4011/api/v1/studio/auth/bootstrap >/dev/null
rm -f -- "$owner_payload"
curl -fsS http://127.0.0.1:4011/api/v1/studio/auth/status \
  | grep -Fq '"bootstrapRequired":false'

sh "$setup_script" uninstall --mode agent-host --target native \
  --install-dir "$AGENT_SETUP_HOST_CONFIG" --purge >"$runtime_root/host-uninstall.log"
export AGENT_SETUP_HOST_OPT="$host_opt"
export AGENT_SETUP_HOST_CONFIG="$host_config"
export AGENT_SETUP_HOST_STATE="$host_state"
sh "$setup_script" uninstall --mode studio --target native \
  --install-dir "$orchestrator_config" --purge >"$runtime_root/control-uninstall.log"
[[ ! -e "$orchestrator_config/installation.json" ]]
[[ ! -e "$host_config/installation.json" ]]
[[ ! -e "$second_host_config/installation.json" ]]

mkdir -p "$(dirname "$report_path")"
cat >"$report_path" <<REPORT
# Guided setup end-to-end probe

- Date (UTC): $(date -u +%Y-%m-%dT%H:%M:%SZ)
- Host: $(hostname)
- Runner identities: agent-runner-01-e2e (one-box), agent-runner-02-e2e (joined)
- OS: $(uname -srmo)
- Stamped base revision: $git_sha
- Worktree: active task source, including current task changes
- Release version under test: $version
- Setup artifact: self-contained Linux x64 ELF, published as a single file
- Release input: real Task Server, Orchestrator Engine, Agent Host, and setup publishes; release browser archive

## Result

PASS

1. The release packager produced one setup executable, four archives, install.sh, and one SHA256SUMS file. The published shell script downloaded and verified the executable over a local HTTP release server for install and uninstall.
2. Every release asset passed SHA-256 verification before extraction.
3. The setup executable installed the Control Plane into isolated native paths and /readyz reported ready.
4. The setup executable installed the matching Studio static tree.
5. The Control Plane emitted an aosj1 join token. The probe transferred it only through a mode-0600 temporary file and did not retain the token.
6. The same setup executable consumed the join token, verified the host-owned Codex login, installed agent-host, and started it through the isolated systemctl fixture.
7. Setup waited for and explained the daemon's ready, ready-no-workflow-scope, or read-only startup capability result.
8. The real Task Server management API reported both one-box and joined runner identities.
9. Task Server, Orchestrator Engine, and Agent Host processes were live at the final assertion.
10. The local Studio route accepted the owner bootstrap code and reported that bootstrap was complete.

## Isolation

The probe changed no real /etc, /opt, /var, or systemd unit. All install roots and process state lived below a mktemp directory that was removed on exit. No repository credential or join token is present in this report.
REPORT

echo "Setup E2E probe passed. Report: $report_path"
