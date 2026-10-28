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
release_root="${SETUP_E2E_RELEASE_DIR:-${AGENT_SETUP_E2E_RELEASE_DIR:-$probe_root/release}}"
frontend_root="$probe_root/frontend/browser"
version="$(tr -d '\r\n' <"$repo_root/VERSION")"
git_sha="${AGENT_SETUP_E2E_GIT_SHA:-${GITHUB_SHA:-$(git -c safe.directory='*' -C "$repo_root" rev-parse HEAD)}}"
port=""
candidate_dir="${SETUP_E2E_RELEASE_DIR:-${AGENT_SETUP_E2E_RELEASE_DIR:-}}"

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
trap cleanup EXIT
trap 'exit 130' HUP INT TERM

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

mkdir -p "$publish_root" "$frontend_root" "$runtime_root"
if [[ -n "$candidate_dir" ]]; then
  candidate_dir="$(realpath "$candidate_dir")"
  [[ -f "$candidate_dir/SHA256SUMS" && -x "$candidate_dir/agent-studio-setup" ]] || {
    echo "Candidate directory needs SHA256SUMS and executable agent-studio-setup: $candidate_dir" >&2
    exit 2
  }
  (cd "$candidate_dir" && sha256sum -c SHA256SUMS >/dev/null)
  release_root="$candidate_dir"
  version="$($release_root/agent-studio-setup --version | awk '{print $2}')"
  [[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || {
    echo "Candidate setup has no valid version." >&2
    exit 2
  }
  candidate_archive="$release_root/agent-orchestrator-$version-linux-x64.tar.gz"
  [[ -f "$candidate_archive" ]] || { echo "Candidate authority archive is missing." >&2; exit 2; }
  candidate_sha="$(tar -xOzf "$candidate_archive" "agent-orchestrator-$version-linux-x64/RELEASE-SHA" | tr -d '\r\n')"
  [[ "$candidate_sha" =~ ^[0-9a-fA-F]{40}$ ]] || { echo "Candidate revision is invalid." >&2; exit 2; }
  if [[ -n "${GITHUB_SHA:-}" && "$candidate_sha" != "$GITHUB_SHA" ]]; then
    echo "Candidate revision does not match the checked-out workflow revision." >&2
    exit 2
  fi
  git_sha="$candidate_sha"
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

for inherited_name in $(compgen -e); do
  case "$inherited_name" in
    RUNNER_*|CODEX_HOME|CLAUDE_CONFIG_DIR|GIT_CONFIG_GLOBAL|GIT_CONFIG_COUNT|GIT_CONFIG_KEY_*|GIT_CONFIG_VALUE_*)
      unset "$inherited_name" ;;
  esac
done
mkdir -p "$probe_root/home"
export HOME="$probe_root/home"
export XDG_CONFIG_HOME="$probe_root/home/.config"
export JOB_RESULTS_DIR="${JOB_RESULTS_DIR:-$probe_root/results}"
mkdir -p "$JOB_RESULTS_DIR"

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
    agent-studio-api.service|agent-studio-bff.service|agent-studio-web.service)
      unit="${AGENT_SETUP_SYSTEMD_ROOT:?}/$service"
      while IFS= read -r assignment; do
        export "$assignment"
      done < <(sed -n 's/^Environment="\([^"]*\)"$/\1/p' "$unit")
      cd "$(sed -n 's/^WorkingDirectory=//p' "$unit")"
      read -r -a command <<<"$(sed -n 's/^ExecStart=//p' "$unit")"
      nohup "${command[@]}" >"$runtime/${service%.service}.log" 2>&1 &
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

# The host enrollment sees a provider login owned by the execution user. CI
# never uses a real provider session for this installation probe.
mkdir -p "$probe_root/bin"
cat >"$probe_root/bin/codex" <<'CODEX'
#!/usr/bin/env sh
case "$*" in
  --version) printf 'codex-scenario 1.0\n' ;;
  'login status') printf 'Scenario login active\n' ;;
  *) exec "${SCENARIO_CODING_SCRIPT:?}" "$@" ;;
esac
CODEX
chmod 0755 "$probe_root/bin/codex"
export PATH="$probe_root/bin:$PATH"
export SCENARIO_CODING_SCRIPT="$repo_root/testsupport/scenario/scenario-coding-agent.sh"
export SCENARIO_EXPECTED_WORK_ROOT="$probe_root"
export SCENARIO_RELEASE_FILE="$probe_root/release-coding"
touch "$SCENARIO_RELEASE_FILE"
printf 'scenario-only placeholder\n' >"$probe_root/git-key"
chmod 0600 "$probe_root/git-key"
external_git_remote="${SCENARIO_GIT_REMOTE:-}"
unset SCENARIO_GIT_REMOTE
if [[ -z "${SCENARIO_GIT_REMOTE:-}" ]]; then
  [[ -x "$(git --exec-path)/git-daemon" ]] || {
    echo "Install git-daemon to run the local scratch repository scenario." >&2
    exit 2
  }
  mkdir -p "$probe_root/git"
  git init --bare --initial-branch=main "$probe_root/git/scenario.git" >/dev/null
  git -C "$probe_root/git/scenario.git" config daemon.receivepack true
  git -C "$probe_root/git/scenario.git" config receive.denyDeleteCurrent ignore
  mkdir -p "$probe_root/seed"
  git -C "$probe_root/seed" init --initial-branch=main >/dev/null
  printf 'Scenario scratch repository\n' >"$probe_root/seed/README.md"
  mkdir -p "$probe_root/seed/tests"
  printf '#!/bin/sh\nexit 0\n' >"$probe_root/seed/tests/known-passing.sh"
  printf '#!/bin/sh\nexit 1\n' >"$probe_root/seed/tests/known-failing.sh"
  chmod 0755 "$probe_root/seed/tests/known-"*.sh
  git -C "$probe_root/seed" add README.md tests
  git -C "$probe_root/seed" -c user.name=Scenario -c user.email=scenario@localhost commit -qm seed
  git -C "$probe_root/seed" remote add origin "$probe_root/git/scenario.git"
  git -C "$probe_root/seed" push -q origin main
  git_port="$(python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1",0)); print(s.getsockname()[1]); s.close()')"
  git daemon --reuseaddr --listen=127.0.0.1 --port="$git_port" \
    --base-path="$probe_root/git" --export-all --enable=receive-pack \
    "$probe_root/git" >"$runtime_root/git-daemon.log" 2>&1 &
  echo "$!" >"$runtime_root/git-daemon.pid"
  export SCENARIO_GIT_REMOTE="git://127.0.0.1:$git_port/scenario.git"
  for attempt in $(seq 1 30); do
    if git ls-remote "$SCENARIO_GIT_REMOTE" HEAD >/dev/null 2>&1; then break; fi
    if [[ "$attempt" == 30 ]]; then echo "Local scratch Git daemon did not start." >&2; exit 4; fi
    sleep 1
  done
fi
if [[ -n "${INSTALL_SCENARIO_DEPLOY_KEY:-}" ]]; then
  [[ -n "${INSTALL_SCENARIO_KNOWN_HOSTS:-}" ]] || {
    echo "INSTALL_SCENARIO_KNOWN_HOSTS is required with a deploy key." >&2
    exit 2
  }
  printf '%s\n' "$INSTALL_SCENARIO_DEPLOY_KEY" >"$probe_root/deploy-key"
  chmod 0600 "$probe_root/deploy-key"
  printf '%s\n' "$INSTALL_SCENARIO_KNOWN_HOSTS" >"$probe_root/known-hosts"
  chmod 0600 "$probe_root/known-hosts"
  export GIT_SSH_COMMAND="ssh -i $probe_root/deploy-key -o IdentitiesOnly=yes -o StrictHostKeyChecking=yes -o UserKnownHostsFile=$probe_root/known-hosts"
  unset INSTALL_SCENARIO_DEPLOY_KEY INSTALL_SCENARIO_KNOWN_HOSTS
fi
if [[ -n "$external_git_remote" ]]; then
  git ls-remote "$external_git_remote" HEAD >/dev/null
  git -C "$probe_root/seed" push --dry-run "$external_git_remote" \
    "HEAD:refs/heads/installation-preflight/$RANDOM" >/dev/null
fi

orchestrator_opt="$probe_root/opt/orchestrator"
orchestrator_config="$probe_root/etc/orchestrator"
orchestrator_state="$probe_root/var/orchestrator"
studio_opt="$probe_root/opt/studio"
host_opt="$probe_root/opt/host"
host_config="$probe_root/etc/host"
host_state="$probe_root/var/host"
systemd_root="$probe_root/etc/systemd"
setup_binary="$release_root/agent-studio-setup"
http_port="$(python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1",0)); print(s.getsockname()[1]); s.close()')"
python3 -m http.server "$http_port" --bind 127.0.0.1 --directory "$release_root" \
  >"$runtime_root/release-http.log" 2>&1 &
echo "$!" >"$runtime_root/release-http.pid"
for attempt in $(seq 1 30); do
  if curl -fsS "http://127.0.0.1:$http_port/SHA256SUMS" >/dev/null; then break; fi
  if [[ "$attempt" == 30 ]]; then echo 'Candidate download server did not start.' >&2; exit 4; fi
  sleep 1
done
control_output="$probe_root/control-output"
join_file="$probe_root/join.token"
owner_session_file="$probe_root/owner.session"
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

umask 077
AGENT_STUDIO_RELEASE_URL="http://127.0.0.1:$http_port" sh "$release_root/install.sh" \
  --mode control-plane \
  --target native \
  --release-version "$version" \
  --release-dir "$release_root" \
  --listen-url "$base_url" \
  --server-url "$base_url" \
  --execution-user "$(id -un)" \
  --agent-cli codex \
  --runner-name agent-runner-01-e2e \
  --non-interactive \
  >"$control_output"

if [[ "${SETUP_E2E_CONTROL_ONLY:-0}" == 1 ]]; then
  rm -f -- "$control_output"
  curl -fsS "$base_url/readyz" >"$runtime_root/ready.json"
  (cd "$release_root" && sha256sum -c SHA256SUMS >/dev/null)
  [[ -f "$studio_opt/current/browser/index.html" ]]
  kill -0 "$(cat "$runtime_root/task-server.pid")"
  kill -0 "$(cat "$runtime_root/engine.pid")"
  mkdir -p "$(dirname "$report_path")"
  printf '# Linux native install smoke\n\nPASS: setup, Task Server, Engine, Studio UI, checksums, and readyz for %s (%s).\n' "$version" "$git_sha" >"$report_path"
  exit 0
fi

[[ "$(stat -c '%a' "$orchestrator_config/studio.token")" == 600 ]] || {
  echo "Control Plane management token is not owner-only." >&2
  exit 4
}
python3 "$repo_root/scripts/release/installation-first-use.py" \
  --server-url "$base_url" \
  --studio-token-file "$orchestrator_config/studio.token" \
  --owner-code-file "$orchestrator_config/owner_bootstrap_code" \
  --owner-session-file "$owner_session_file" \
  --repository-url "$SCENARIO_GIT_REMOTE" \
  >"$runtime_root/first-use.log"
"$setup_binary" join-token \
  --runner-name agent-runner-02-e2e \
  --route reverse-tunnel \
  --server-url "$base_url" \
  --authority-url "$base_url" \
  --token-file "$orchestrator_config/studio.token" \
  --owner-session-file "$owner_session_file" \
  --join-token-file "$join_file" \
  --release-version "$version" \
  >"$runtime_root/join-issuance.log"
[[ "$(head -c 6 "$join_file")" == 'aosj2.' ]] || {
  echo "Join-token command did not issue a current single-use token." >&2
  exit 4
}
rm -f -- "$control_output"

if ! "$setup_binary" \
  --join \
  --join-token-file "$join_file" \
  --release-dir "$release_root" \
  --execution-user "$(id -un)" \
  --agent-cli codex \
  --runner-name agent-runner-02-e2e \
  --git-remote "$SCENARIO_GIT_REMOTE" \
  --git-access existing-ssh \
  --git-ssh-key-file "$probe_root/git-key" \
  --non-interactive \
  >"$runtime_root/host-setup.log" 2>&1; then
  sed -n '/^Setup failed:/p' "$runtime_root/host-setup.log" >&2
  exit 1
fi
rm -f -- "$join_file"
if ! grep -Eq 'registered as agent-runner-02-e2e \((ready|ready-no-workflow-scope)\)' "$runtime_root/host-setup.log"; then
    python3 - "$runtime_root/host-setup.log" "$runtime_root/agent-host.log" <<'PY' >&2
import pathlib
import re
import sys
for name in sys.argv[1:]:
    for line in pathlib.Path(name).read_text(errors="replace").splitlines():
        if "git" not in line.lower() and "read-only" not in line.lower():
            continue
        line = re.sub(r"(?i)\b[0-9a-f]{64}\b", "[redacted]", line)
        line = re.sub(r"aosj[12][.][A-Za-z0-9._-]+", "[redacted]", line)
        print(line[:300])
PY
  echo "The scratch repository did not provide Git write access." >&2
  exit 4
fi

curl -fsS "$base_url/readyz" >"$runtime_root/ready.json"
token="$(sed -n '1p' "$orchestrator_config/studio.token")"
remote_hosts="$(
  curl -fsS \
    -H "Authorization: Bearer $token" \
    -H "X-Task-Protocol-Version: 2" \
    "$base_url/api/v1/management/remote-hosts"
)"
grep -Fq '"runnerId":"agent-runner-02-e2e"' <<<"$remote_hosts"
review_token_file="$probe_root/review.token"
integration_token_file="$probe_root/integration.token"
card_file="$probe_root/card.json"
python3 "$repo_root/scripts/release/installation-card.py" prepare \
  --server-url "$base_url" --studio-token-file "$orchestrator_config/studio.token" \
  --owner-session-file "$owner_session_file" \
  --repository-url "$SCENARIO_GIT_REMOTE" --card-file "$card_file" \
  --review-token-file "$review_token_file" --integration-token-file "$integration_token_file" \
  >"$runtime_root/card-prepare.log"
rm -f -- "$owner_session_file"

RUNNER_AUTH_TOKEN_FILE="$review_token_file" RUNNER_ALLOW_INSECURE_HTTP=1 \
RUNNER_CODEX_CLI_BIN="$probe_root/bin/codex" RUNNER_GIT_REMOTE="$SCENARIO_GIT_REMOTE" \
RUNNER_HOST_CODING_SLOTS=0 RUNNER_HOST_REVIEW_SLOTS=1 \
  nohup "$host_opt/current/agent-host" --poll --server "$base_url" \
    --runner-id scenario-install-review --runner-name scenario-install-review \
    --hostname scenario-review-host --role review --git-remote "$SCENARIO_GIT_REMOTE" \
    --workdir "$probe_root/review-work" --state-dir "$probe_root/review-state" \
    --cli codex --ttl 15 --max-parallelism 1 --poll-seconds 1 \
    >"$runtime_root/review.log" 2>&1 &
echo "$!" >"$runtime_root/review.pid"
RUNNER_AUTH_TOKEN_FILE="$integration_token_file" RUNNER_ALLOW_INSECURE_HTTP=1 \
RUNNER_GIT_REMOTE="$SCENARIO_GIT_REMOTE" \
  nohup "$host_opt/current/agent-host" --poll --server "$base_url" \
    --runner-id scenario-install-integration --runner-name scenario-install-integration \
    --hostname scenario-integration-host --role integration --git-remote "$SCENARIO_GIT_REMOTE" \
    --workdir "$probe_root/integration-work" --state-dir "$probe_root/integration-state" \
    --cli codex --ttl 15 --max-parallelism 1 --poll-seconds 1 \
    >"$runtime_root/integration.log" 2>&1 &
echo "$!" >"$runtime_root/integration.pid"
python3 "$repo_root/scripts/release/installation-card.py" verify \
  --server-url "$base_url" --studio-token-file "$orchestrator_config/studio.token" \
  --repository-url "$SCENARIO_GIT_REMOTE" --card-file "$card_file" \
  >"$runtime_root/card-verify.log"
installation_id_before="$(python3 - "$orchestrator_config/installation.json" <<'PY'
import json
import sys
print(json.load(open(sys.argv[1], encoding='utf-8'))['InstallationId'])
PY
)"
"$setup_binary" uninstall --mode control-plane --target native --non-interactive \
  >"$runtime_root/uninstall.log"
[[ -f "$orchestrator_config/installation.json" ]]
"$setup_binary" --mode control-plane --target native --release-version "$version" \
  --release-dir "$release_root" --listen-url "$base_url" --server-url "$base_url" \
  --execution-user "$(id -un)" --agent-cli codex --runner-name agent-runner-01-e2e \
  --non-interactive >"$runtime_root/reinstall.log"
installation_id_after="$(python3 - "$orchestrator_config/installation.json" <<'PY'
import json
import sys
print(json.load(open(sys.argv[1], encoding='utf-8'))['InstallationId'])
PY
)"
[[ "$installation_id_before" == "$installation_id_after" ]]
python3 "$repo_root/scripts/release/installation-card.py" verify \
  --server-url "$base_url" --studio-token-file "$orchestrator_config/studio.token" \
  --repository-url "$SCENARIO_GIT_REMOTE" --card-file "$card_file" \
  >"$runtime_root/card-retained.log"
(
  cd "$release_root"
  sha256sum -c SHA256SUMS >/dev/null
)
[[ "$(od -An -tx1 -N4 "$setup_binary" | tr -d ' \n')" == "7f454c46" ]]
[[ -f "$studio_opt/current/browser/index.html" ]]
kill -0 "$(cat "$runtime_root/task-server.pid")"
kill -0 "$(cat "$runtime_root/engine.pid")"
kill -0 "$(cat "$runtime_root/agent-host.pid")"

mkdir -p "$(dirname "$report_path")"
cat >"$report_path" <<REPORT
# Guided setup end-to-end probe

- Date (UTC): $(date -u +%Y-%m-%dT%H:%M:%SZ)
- Host: $(hostname)
- Runner identity: ${RUNNER_NAME:-not-set}
- OS: $(uname -srmo)
- Stamped base revision: $git_sha
- Worktree: active task source, including current task changes
- Release version under test: $version
- Setup artifact: self-contained Linux x64 ELF, published as a single file
- Release input: ${candidate_dir:-local publishes with a static Studio fixture}
- Card outcome: $(cat "$runtime_root/card-verify.log")

## Result

PASS

1. The release directory contained one setup executable, install.sh, four archives, and one SHA256SUMS file.
2. Every release asset passed SHA-256 verification before extraction.
3. The setup executable installed the Control Plane into isolated native paths and /readyz reported ready.
4. The setup executable installed the matching Studio static tree.
5. The owner bootstrapped and the setup join-token command issued an aosj2 single-use token through a mode-0600 temporary file. The probe did not retain the token.
6. The same setup executable consumed the join token, verified a fake host-owned Codex login and Git read access, installed agent-host, and started it through the isolated systemctl fixture.
7. Setup waited for the daemon's startup capability result and required Git write access to the scratch repository.
8. The owner bootstrap API accepted the installer-generated one-time code, and the owner registered the scratch repository as a project before token issuance.
9. The real Task Server management API reported the joined runner identity.
10. The installed Agent Host ran the fixture card with the fake CLI. Installed review and integration roles passed its review and published the fenced result to the scratch repository.
11. Native uninstall and reinstall kept the installation ID and integrated card.
12. Task Server, Orchestrator Engine, and Agent Host processes were live at the final assertion.

## Isolation

The probe changed no real /etc, /opt, /var, or systemd unit. All install roots and process state lived below a mktemp directory that was removed on exit. No repository credential or join token is present in this report.
REPORT

echo "Setup E2E probe passed. Report: $report_path"
