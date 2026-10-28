#!/usr/bin/env bash
# Exercise the installed Compose bundle with a disposable first-use card.
set -euo pipefail

candidate_dir="${1:?candidate directory required}"
for command_name in docker curl git jq python3; do
  command -v "$command_name" >/dev/null || {
    echo "Install $command_name before running the Docker installation scenario." >&2
    exit 2
  }
done
[[ -x "$(git --exec-path)/git-daemon" ]] || {
  echo 'Install git-daemon before running the Docker installation scenario.' >&2
  exit 2
}
if [[ -n "$(docker ps --all --quiet --filter label=com.docker.compose.project=agent-studio)" ]]; then
  echo "The agent-studio Compose project already exists. Use a disposable clean host for this scenario." >&2
  exit 2
fi
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
install_root="$(mktemp -d)"
chmod 0700 "$install_root"
setup="$candidate_dir/agent-studio-setup"
version="$("$setup" --version | awk '{print $2}')"
compose=(docker compose --project-name agent-studio --env-file "$install_root/.env"
  -f "$install_root/releases/$version/docker-compose.yml")
cleanup() {
  for role in coding review integration; do
    docker rm -f "agent-studio-install-$role" >/dev/null 2>&1 || true
  done
  if [[ -f "$install_root/git-daemon.pid" ]]; then
    kill "$(cat "$install_root/git-daemon.pid")" 2>/dev/null || true
  fi
  "$setup" uninstall --install-dir "$install_root" --purge >/dev/null 2>&1 || true
  rm -rf -- "$install_root"
}
trap cleanup EXIT

install_flags=()
if [[ "${INSTALL_SCENARIO_OFFLINE:-0}" == 1 ]]; then install_flags+=(--offline); fi
"$setup" --unattended --release-dir "$candidate_dir" --install-dir "$install_root" "${install_flags[@]}" >/dev/null
curl -fsS http://127.0.0.1:4011/healthz >/dev/null
installation_id="$(jq -r '.installationId' "$install_root/installation.json")"
[[ -n "$installation_id" && "$installation_id" != null ]]
studio_token="$("${compose[@]}" exec -T task-server cat /run/agent-studio-secrets/studio_token)"
printf '%s\n' "$studio_token" >"$install_root/studio.token"
"${compose[@]}" exec -T task-server cat /run/agent-studio-secrets/owner_bootstrap_code >"$install_root/owner.code"
"${compose[@]}" stop agent-host-distributed agent-host-review-distributed >/dev/null
git_port="$(python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1",0)); print(s.getsockname()[1]); s.close()')"
mkdir -p "$install_root/git" "$install_root/seed" "$install_root/seed/tests"
git init --bare --initial-branch=main "$install_root/git/scenario.git" >/dev/null
git -C "$install_root/git/scenario.git" config daemon.receivepack true
printf 'Installed Compose fixture\n' >"$install_root/seed/README.md"
printf '#!/bin/sh\nexit 0\n' >"$install_root/seed/tests/known-passing.sh"
printf '#!/bin/sh\nexit 1\n' >"$install_root/seed/tests/known-failing.sh"
chmod 0755 "$install_root/seed/tests/known-"*.sh
git -C "$install_root/seed" init --initial-branch=main >/dev/null
git -C "$install_root/seed" add .
git -C "$install_root/seed" -c user.name=Scenario -c user.email=scenario@localhost commit -qm seed
git -C "$install_root/seed" remote add origin "$install_root/git/scenario.git"
git -C "$install_root/seed" push -q origin main
git daemon --reuseaddr --listen=127.0.0.1 --port="$git_port" \
  --base-path="$install_root/git" --export-all --enable=receive-pack \
  "$install_root/git" >"$install_root/git-daemon.log" 2>&1 &
echo "$!" >"$install_root/git-daemon.pid"
repository_url="git://127.0.0.1:$git_port/scenario.git"
for attempt in $(seq 1 30); do
  if git ls-remote "$repository_url" HEAD >/dev/null 2>&1; then break; fi
  if [[ "$attempt" == 30 ]]; then echo 'Docker fixture Git daemon did not start.' >&2; exit 4; fi
  sleep 1
done
python3 "$repo_root/scripts/release/installation-first-use.py" \
  --server-url http://127.0.0.1:5071 \
  --studio-token-file "$install_root/studio.token" \
  --owner-code-file "$install_root/owner.code" \
  --owner-session-file "$install_root/owner.session" \
  --repository-url "$repository_url" >/dev/null
python3 "$repo_root/scripts/release/installation-card.py" prepare \
  --server-url http://127.0.0.1:5071 \
  --studio-token-file "$install_root/studio.token" \
  --owner-session-file "$install_root/owner.session" \
  --repository-url "$repository_url" --card-file "$install_root/card.json" \
  --review-token-file "$install_root/review.token" \
  --integration-token-file "$install_root/integration.token" >/dev/null
rm -f -- "$install_root/owner.session" "$install_root/owner.code"
cp "$repo_root/testsupport/scenario/scenario-coding-agent.sh" "$install_root/scenario-coding-agent.sh"
cat >"$install_root/codex" <<'CODEX'
#!/usr/bin/env sh
case "$*" in
  --version) printf 'codex-scenario 1.0\n' ;;
  'login status') printf 'Scenario login active\n' ;;
  *) exec /scenario/scenario-coding-agent.sh "$@" ;;
esac
CODEX
chmod 0755 "$install_root/codex" "$install_root/scenario-coding-agent.sh"
touch "$install_root/release"
mkdir -p "$install_root/results"
runner_image="$("${compose[@]}" images --quiet agent-host-distributed)"
[[ -n "$runner_image" ]]
for role in coding review integration; do
  case "$role" in
    coding) runner_id=distributed-runner; runner_token="$install_root/runner.token";
      "${compose[@]}" exec -T task-server cat /run/agent-studio-secrets/runner/runner_token >"$runner_token" ;;
    review) runner_id=scenario-install-review; runner_token="$install_root/review.token" ;;
    integration) runner_id=scenario-install-integration; runner_token="$install_root/integration.token" ;;
  esac
  docker run -d --name "agent-studio-install-$role" --network host \
    --user "$(id -u):$(id -g)" -v "$install_root:/scenario" \
    -e HOME=/scenario -e RUNNER_SERVER_URL=http://127.0.0.1:5071 \
    -e RUNNER_ALLOW_INSECURE_HTTP=1 -e RUNNER_AUTH_TOKEN_FILE="/scenario/$(basename "$runner_token")" \
    -e RUNNER_ID="$runner_id" -e RUNNER_NAME="$runner_id" -e RUNNER_ROLE="$role" \
    -e RUNNER_GIT_REMOTE="$repository_url" -e RUNNER_CLI_TYPE=codex \
    -e RUNNER_CODEX_CLI_BIN=/scenario/codex -e SCENARIO_RELEASE_FILE=/scenario/release \
    -e SCENARIO_EXPECTED_WORK_ROOT=/scenario \
    -e JOB_RESULTS_DIR=/scenario/results \
    -e RUNNER_WORKDIR="/scenario/$role-work" -e RUNNER_STATE_DIR="/scenario/$role-state" \
    "$runner_image" >/dev/null
done
python3 "$repo_root/scripts/release/installation-card.py" verify \
  --server-url http://127.0.0.1:5071 --studio-token-file "$install_root/studio.token" \
  --repository-url "$repository_url" --card-file "$install_root/card.json"
for role in coding review integration; do
  docker rm -f "agent-studio-install-$role" >/dev/null
done
workspace_id="$(curl -fsS -H "Authorization: Bearer $studio_token" -H 'X-Task-Protocol-Version: 2' \
  http://127.0.0.1:5071/api/v1/workspaces | jq -r '.[] | select(.name == "Installation scenario") | .workspaceId')"
[[ -n "$workspace_id" && "$workspace_id" != null ]]

"$setup" uninstall --install-dir "$install_root" >/dev/null
[[ -f "$install_root/installation.json" ]]
"$setup" --unattended --release-dir "$candidate_dir" --install-dir "$install_root" "${install_flags[@]}" >/dev/null
[[ "$(jq -r '.installationId' "$install_root/installation.json")" == "$installation_id" ]]
curl -fsS http://127.0.0.1:4011/healthz >/dev/null
curl -fsS -H "Authorization: Bearer $studio_token" -H 'X-Task-Protocol-Version: 2' \
  http://127.0.0.1:5071/api/v1/workspaces \
  | jq -e --arg id "$workspace_id" 'any(.[]; .workspaceId == $id)' >/dev/null
curl -fsS -H "Authorization: Bearer $studio_token" -H 'X-Task-Protocol-Version: 2' \
  http://127.0.0.1:5071/api/v1/projects/scenario-project/tasks \
  | jq -e 'any(.[]; .title == "Installed first-use fixture" and .state == "5-human-review")' >/dev/null
printf 'Candidate install, coding, review, integration, uninstall, and reinstall retained the card and workspace.\n'
