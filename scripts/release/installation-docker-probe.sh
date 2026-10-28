#!/usr/bin/env bash
# Exercise the published Compose bundle with an installed first-use card.
set -euo pipefail

candidate_dir="${1:?candidate directory required}"
report_dir="${2:?report directory required}"
previous_dir="${INSTALL_SCENARIO_PREVIOUS_DIR:?previous candidate directory required for update proof}"
for command_name in docker curl jq git python3 tar; do
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
mkdir -p "$report_dir"
setup="$candidate_dir/agent-studio-setup"
version="$("$setup" --version | awk '{print $2}')"
previous_setup="$previous_dir/agent-studio-setup"
chmod 0755 "$previous_setup"
previous_version="$("$previous_setup" --version | awk '{print $2}')"
compose=(docker compose --project-name agent-studio --env-file "$install_root/.env"
  -f "$install_root/releases/$version/docker-compose.yml")
cleanup() {
  "$setup" uninstall --install-dir "$install_root" --purge >/dev/null 2>&1 || true
  rm -rf -- "$install_root"
}
trap cleanup EXIT

install_flags=()
if [[ "${INSTALL_SCENARIO_OFFLINE:-0}" == 1 ]]; then install_flags+=(--offline); fi
"$previous_setup" --unattended --release-dir "$previous_dir" --install-dir "$install_root" >/dev/null
before_installation_id="$(jq -r '.InstallationId' "$install_root/installation.json")"
"$setup" update --unattended --release-dir "$candidate_dir" --install-dir "$install_root" "${install_flags[@]}" >/dev/null
curl -fsS http://127.0.0.1:4011/healthz >/dev/null
installation_id="$(jq -r '.InstallationId' "$install_root/installation.json")"
[[ -n "$installation_id" && "$installation_id" != null ]]
[[ "$installation_id" == "$before_installation_id" ]]
[[ "$(jq -r '.ReleaseVersion' "$install_root/installation.json")" == "$version" ]]
"${compose[@]}" exec -T task-server cat /run/agent-studio-secrets/studio_token >"$install_root/studio.token"
"${compose[@]}" exec -T task-server cat /run/agent-studio-secrets/owner_bootstrap_code >"$install_root/owner.code"
chmod 0600 "$install_root/studio.token" "$install_root/owner.code"
"${compose[@]}" stop agent-host-distributed agent-host-review-distributed >/dev/null
tar -xzf "$candidate_dir/agent-host-$version.tar.gz" -C "$install_root"
python3 "$(dirname "$0")/installed-card-journey.py" \
  --server-url http://127.0.0.1:5071 \
  --studio-token-file "$install_root/studio.token" \
  --owner-code-file "$install_root/owner.code" \
  --runner-binary "$install_root/agent-host-$version/linux-x64/agent-host" \
  --work-dir "$install_root/card" \
  --report "$report_dir/linux-docker-card.json"
project_id="$(jq -r '.projectId' "$report_dir/linux-docker-card.json")"
task_key="$(jq -r '.taskKey' "$report_dir/linux-docker-card.json")"

"$setup" uninstall --install-dir "$install_root" >/dev/null
[[ -f "$install_root/installation.json" ]]
"$setup" --unattended --release-dir "$candidate_dir" --install-dir "$install_root" "${install_flags[@]}" >/dev/null
[[ "$(jq -r '.InstallationId' "$install_root/installation.json")" == "$installation_id" ]]
curl -fsS http://127.0.0.1:4011/healthz >/dev/null
curl -fsS -H "Authorization: Bearer $(cat "$install_root/studio.token")" -H 'X-Task-Protocol-Version: 2' \
  "http://127.0.0.1:5071/api/v1/projects/$project_id/tasks/$task_key" \
  | jq -e '.state == "5-human-review"' >/dev/null
python3 - "$report_dir/linux-docker-lifecycle.json" "$installation_id" "$previous_version" "$version" <<'PY'
import json
import sys
with open(sys.argv[1], "w", encoding="utf-8") as report:
    json.dump({"status": "passed", "installationId": sys.argv[2],
               "previousVersion": sys.argv[3], "candidateVersion": sys.argv[4],
               "updatedFromPrevious": True, "uninstalledAndReinstalled": True,
               "retainedCard": True}, report, indent=2)
    report.write("\n")
PY
printf 'Candidate install, fake-CLI coding and review, integration, and retained-data reinstall passed.\n'
