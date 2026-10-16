#!/usr/bin/env bash
# Exercise the published Compose bundle with a disposable API record.
set -euo pipefail

candidate_dir="${1:?candidate directory required}"
for command_name in docker curl jq; do
  command -v "$command_name" >/dev/null || {
    echo "Install $command_name before running the Docker installation scenario." >&2
    exit 2
  }
done
if [[ -n "$(docker ps --all --quiet --filter label=com.docker.compose.project=agent-studio)" ]]; then
  echo "The agent-studio Compose project already exists. Use a disposable clean host for this scenario." >&2
  exit 2
fi
install_root="$(mktemp -d)"
setup="$candidate_dir/agent-studio-setup"
version="$("$setup" --version | awk '{print $2}')"
compose=(docker compose --project-name agent-studio --env-file "$install_root/.env"
  -f "$install_root/releases/$version/docker-compose.yml")
cleanup() {
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
workspace_id="$(curl -fsS -X POST \
  -H "Authorization: Bearer $studio_token" \
  -H 'X-Task-Protocol-Version: 2' -H 'Content-Type: application/json' \
  -d '{"name":"Installation retention probe"}' \
  http://127.0.0.1:5071/api/v1/workspaces | jq -r '.workspaceId')"
[[ -n "$workspace_id" && "$workspace_id" != null ]]

"$setup" uninstall --install-dir "$install_root" >/dev/null
[[ -f "$install_root/installation.json" ]]
"$setup" --unattended --release-dir "$candidate_dir" --install-dir "$install_root" "${install_flags[@]}" >/dev/null
[[ "$(jq -r '.installationId' "$install_root/installation.json")" == "$installation_id" ]]
curl -fsS http://127.0.0.1:4011/healthz >/dev/null
curl -fsS -H "Authorization: Bearer $studio_token" -H 'X-Task-Protocol-Version: 2' \
  http://127.0.0.1:5071/api/v1/workspaces \
  | jq -e --arg id "$workspace_id" 'any(.[]; .workspaceId == $id)' >/dev/null
printf 'Candidate install, uninstall, and reinstall retained the installation ID and API workspace.\n'
