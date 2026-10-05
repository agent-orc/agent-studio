#!/usr/bin/env bash
# AGT-2943 evidence harness: runs the built agent-studio-setup against this
# host and against real local Task Server processes, and writes a sanitised
# transcript. It is evidence tooling for the task results, not a product script.
# Usage: agt-2943-evidence-harness.sh REPO_ROOT TRANSCRIPT_PATH
set -uo pipefail

repo="$(cd "$1" && pwd)"
out="$2"
setup_dll="$repo/setup/bin/Debug/net10.0/agent-studio-setup.dll"
server_dll="$repo/task-server/bin/Debug/net10.0/task-server.dll"
work="$(mktemp -d)"
pids=()
cleanup() {
    for pid in "${pids[@]}"; do kill "$pid" 2>/dev/null; wait "$pid" 2>/dev/null; done
    rm -rf "$work"
}
trap cleanup EXIT
: >"$out"

say() { printf '%s\n' "$*" >>"$out"; }
clean() { sed -e "s#$work#\$WORK#g" -e "s#$repo#\$REPO#g" -e "s#$HOME#\$HOME#g"; }
run() {
    printf '$ %s\n' "$*" | clean >>"$out"
    "$@" 2>&1 | clean >>"$out"
    local rc=${PIPESTATUS[0]}
    say "[exit $rc]"
    say ""
    return 0
}
setup() { run dotnet "$setup_dll" "$@"; }
free_port() { python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1",0)); print(s.getsockname()[1]); s.close()'; }
secret() { head -c 32 /dev/urandom | od -An -tx1 | tr -d ' \n' >"$1"; chmod 600 "$1"; }
api() { # api TOKEN_FILE METHOD URL [BODY]
    curl --silent --show-error -X "$2" -H "Authorization: Bearer $(cat "$1")" \
        -H 'X-Task-Protocol-Version: 2' -H 'Content-Type: application/json' ${4:+-d "$4"} "$3"
}
start_server() { # start_server NAME -> sets URL_<NAME>
    local name="$1" port dir
    port="$(free_port)"
    dir="$work/server-$name"
    mkdir -p "$dir/data" "$dir/backups"
    secret "$dir/studio.token"; secret "$dir/engine.token"; secret "$dir/runner.token"; secret "$dir/shared.token"
    AUTH=bearer AUTH_TOKEN_FILE="$dir/shared.token" STUDIO_AUTH_TOKEN_FILE="$dir/studio.token" \
        ENGINE_AUTH_TOKEN_FILE="$dir/engine.token" BOOTSTRAP_RUNNER_ID=agent-runner-01 \
        BOOTSTRAP_RUNNER_AUTH_TOKEN_FILE="$dir/runner.token" STORE_PATH="$dir/data" BACKUP_PATH="$dir/backups" \
        dotnet "$server_dll" --urls "http://127.0.0.1:$port" >"$dir/server.log" 2>&1 &
    pids+=("$!")
    for _ in $(seq 1 120); do
        curl --silent --fail "http://127.0.0.1:$port/readyz" >/dev/null 2>&1 && break
        sleep 0.5
    done
    printf -v "URL_$name" '%s' "http://127.0.0.1:$port"
}

say "# AGT-2943 installer transcript"
say "host: $(hostname)"
say "captured (UTC): $(date -u +%Y-%m-%dT%H:%M:%SZ)"
say "platform: $(uname -srm); $(. /etc/os-release && echo "$PRETTY_NAME")"
say "dotnet: $(dotnet --version); docker: $(docker info --format '{{.ServerVersion}}' 2>/dev/null || echo unavailable)"
say "base (develop merged into the delivery): $(git -C "$repo" rev-parse HEAD)"
say "origin/develop: $(git -C "$repo" rev-parse origin/develop)"
say "setup sources sha256 (sha256sum setup/*.cs | sha256sum): $(cd "$repo" && sha256sum setup/*.cs | sha256sum | cut -d' ' -f1)"
say "uncommitted delivery files at capture: $(git -C "$repo" status --porcelain | wc -l)"
say "Secrets are files under \$WORK with mode 600; no token value is printed."
say ""

say "## 1. Preflight, one box (Docker target and native target)"
setup preflight --journey one-box --target docker --install-dir "$work/one-box"
setup preflight --journey one-box --target native --install-dir "$work/one-box"

say "## 2. Preflight, attach-studio without an authority URL"
setup preflight --journey attach-studio

say "## 3. Join-host preflight probes the authority inside the join token"
start_server A
python3 - "$URL_A" "$work/join.token" <<'PY'
import base64, hashlib, json, sys
body = base64.urlsafe_b64encode(json.dumps({"schemaVersion": 1, "serverUrl": sys.argv[1],
    "credential": "c" * 40, "releaseVersion": "0.9.5", "issuedAtUtc": "2026-10-05T00:00:00Z"},
    separators=(",", ":")).encode()).decode().rstrip("=")
check = hashlib.sha256(f"aosj1.{body}".encode()).hexdigest()[:16]
open(sys.argv[2], "w").write(f"aosj1.{body}.{check}")
PY
chmod 600 "$work/join.token"
say "Task Server A (real process, bearer auth) listens on a loopback port; the join token names it."
setup preflight --journey join-host --join-token-file "$work/join.token" --install-dir "$work/host"
setup preflight --journey join-host --join-token-file "$work/join.token" --server-url https://other.wg.internal
printf 'aosj1.not-a-token.0000' >"$work/bad.token"; chmod 600 "$work/bad.token"
setup preflight --journey join-host --join-token-file "$work/bad.token"

say "## 4. Dry-run of the Docker one-box install"
setup --journey one-box --target docker --dry-run --unattended --install-dir "$work/one-box"
say "\$ ls -A \$WORK/one-box (a dry run writes nothing)"
ls -A "$work/one-box" 2>&1 | clean >>"$out"; say ""

say "## 5. Wrong release version leaves no release pin"
setup --journey one-box --target docker --release-version 9.9.9 --unattended --install-dir "$work/wrong"
say "\$ ls -A \$WORK/wrong"
ls -A "$work/wrong" 2>&1 | clean >>"$out"; say ""

say "## 6. Acceptance against Task Server A"
say "No Compose release is published for this development build, so the one-box"
say "installation.json below is seeded with the schema setup writes after service"
say "health (phase awaiting-acceptance). Everything after it is a real run."
mkdir -p "$work/accept"
cat >"$work/accept/installation.json" <<JSON
{"Schema":1,"InstallationId":"inst_agt2943evidence","Journey":"one-box","Mode":"studio","Target":"docker","ReleaseVersion":"0.9.5","Phase":"awaiting-acceptance","Principals":["task-server-admin","orchestrator-engine","studio-bff","runner-coding","runner-review"],"ProjectOrigin":null,"FirstRunner":null,"CodingSlots":null,"ReviewSlots":null,"CreatedUtc":"2026-10-05T00:00:00Z","UpdatedUtc":"2026-10-05T00:00:00Z"}
JSON
chmod 600 "$work/accept/installation.json"
a="$work/server-A"
say "### 6a. Before the first runner contacted the authority"
setup accept --install-dir "$work/accept" --server-url "$URL_A" --token-file "$a/studio.token"
say "\$ (runner principal contacts Task Server A with its own token)"
api "$a/runner.token" GET "$URL_A/api/v1/projects" >/dev/null; say ""
say "### 6b. Identity observed; a full set is created and verified, rehearsal pending"
setup accept --install-dir "$work/accept" --server-url "$URL_A" --token-file "$a/studio.token" \
    --backup-path "$a/backups"
backup_id="$(ls "$a/backups/full" | head -1)"
say "### 6c. The Task Server answers 426 to a management call without the protocol header"
say "\$ curl -X POST \$URL_A/api/v1/management/backups/full/$backup_id/verify  (no X-Task-Protocol-Version)"
curl --silent --output /dev/null --write-out 'HTTP %{http_code}\n' -X POST \
    -H "Authorization: Bearer $(cat "$a/studio.token")" \
    "$URL_A/api/v1/management/backups/full/$backup_id/verify" >>"$out"; say ""

say "### 6d. Empty-target rehearsal on a second, empty Task Server B"
start_server B
b="$work/server-B"
mkdir -p "$b/backups/full"
cp -a "$a/backups/full/$backup_id" "$b/backups/full/"
say "Task Server B (empty store) at $URL_B; set $backup_id staged in its backup directory."
source_verify="$(api "$a/studio.token" POST "$URL_A/api/v1/management/backups/full/$backup_id/verify")"
say "A verify: $(echo "$source_verify" | jq -c '{backupId, verified, identitySha256, setSha256: .summary.setSha256}')"
say "B mode: $(api "$b/studio.token" PUT "$URL_B/api/v1/management/mode" '{"mode":3,"reason":"AGT-2943 empty-target rehearsal"}' | jq -c '{mode}') (3 = Maintenance)"
restore="$(api "$b/studio.token" POST "$URL_B/api/v1/management/backups/full/$backup_id/restore")"
say "B restore: $(echo "$restore" | jq -c '{backupId, restored, identitySha256}')"
set_hash="$(echo "$source_verify" | jq -r .summary.setSha256)"
if [ "$(echo "$restore" | jq -r .restored)" = true ] \
    && [ "$(echo "$restore" | jq -r .identitySha256)" = "$(echo "$source_verify" | jq -r .identitySha256)" ]; then
    printf '{"backupId":"%s","setSha256":"%s","installationId":"inst_agt2943evidence","verified":true,"restoredIntoEmptyTarget":true}\n' \
        "$backup_id" "$set_hash" >"$a/backups/full/$backup_id.rehearsal.json"
    say "Rehearsal receipt written: the restore succeeded and the identity digests match."
else
    say "Rehearsal did not prove the set; no receipt written."
fi
say ""

say "### 6e. Acceptance with the rehearsed set and a configured canary"
say "Canary used here: scripts/scenario-remote-smoke.sh, the AGT-2739 remote management-plane"
say "slice. It proves the installer's canary contract and authenticated API wiring; it is not"
say "the provider-authenticated coding, review and publication canary."
export REPORT_DIR="$work/smoke"
export AGENT_ORCHESTRATOR_CANARY_COMMAND="'$repo/scripts/scenario-remote-smoke.sh' \"\$AGENT_STUDIO_SERVER_URL\" \"\$(cat \"\$AGENT_STUDIO_TOKEN_FILE\")\""
setup accept --install-dir "$work/accept" --server-url "$URL_A" --token-file "$a/studio.token" \
    --recovery-checkpoint "$a/backups/full/$backup_id"
unset AGENT_ORCHESTRATOR_CANARY_COMMAND
say "\$ jq -c '{InstallationId, Journey, Phase, ReleaseVersion}' \$WORK/accept/installation.json"
jq -c '{InstallationId, Journey, Phase, ReleaseVersion}' "$work/accept/installation.json" >>"$out"; say ""
say "\$ jq -c '{checkpoint, outcome, installationId, releaseVersion, setupVersion, host, atUtc}' \$WORK/accept/checkpoints.jsonl"
jq -c '{checkpoint, outcome, installationId, releaseVersion, setupVersion, host, atUtc}' \
    "$work/accept/checkpoints.jsonl" >>"$out"; say ""

say "## 7. Relocation preflight on this host"
setup preflight --journey relocate-authority --target docker --server-url "$URL_B"
