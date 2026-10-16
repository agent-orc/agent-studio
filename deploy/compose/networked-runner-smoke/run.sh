#!/usr/bin/env bash
set -euo pipefail
trap 'echo "Networked Compose smoke failed at line $LINENO." >&2' ERR

repo_root=$(cd "$(dirname "$0")/../../.." && pwd)
compose_file="$repo_root/deploy/compose/networked-runner-smoke/compose.yaml"
SMOKE_APP_DIR="$repo_root/backend/bin/Debug/net10.0"
if [[ ! -f "$SMOKE_APP_DIR/OrchestratorApi.dll" ]]; then
  echo "Build backend/OrchestratorApi.csproj before the Compose smoke." >&2
  exit 2
fi

smoke_dir=$(mktemp -d)
SMOKE_WORKSPACE="$smoke_dir/workspace"
SMOKE_CADDY_DATA="$smoke_dir/caddy-data"
SMOKE_CADDYFILE="$smoke_dir/Caddyfile"
SMOKE_PROJECT="studio-smoke-$$"
SMOKE_UID=$(id -u)
SMOKE_GID=$(id -g)
mkdir -p "$SMOKE_WORKSPACE/projects/networked-smoke/2-ready" "$SMOKE_CADDY_DATA"
task_dir="$SMOKE_WORKSPACE/projects/networked-smoke/3-progress/AGT-NETWORKED-SMOKE"

free_port() {
  python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1", 0)); print(s.getsockname()[1]); s.close()'
}
SMOKE_API_PORT=$(free_port)
SMOKE_HTTPS_PORT=$(free_port)
cat > "$SMOKE_CADDYFILE" <<EOF
{
    auto_https disable_redirects
    admin off
}
https://localhost:$SMOKE_HTTPS_PORT {
    tls internal
    reverse_proxy http://127.0.0.1:$SMOKE_API_PORT
}
EOF
export SMOKE_APP_DIR SMOKE_WORKSPACE SMOKE_CADDY_DATA SMOKE_CADDYFILE SMOKE_PROJECT SMOKE_API_PORT SMOKE_HTTPS_PORT SMOKE_UID SMOKE_GID
cleanup() {
  docker compose -f "$compose_file" down --remove-orphans >/dev/null 2>&1 || true
  rm -rf "$smoke_dir"
}
trap cleanup EXIT
docker compose -f "$compose_file" up -d --quiet-pull

ca="$smoke_dir/installation-ca.crt"
origin="https://localhost:$SMOKE_HTTPS_PORT"
ready=false
for _ in $(seq 1 60); do
  if docker compose -f "$compose_file" exec -T edge \
      cat /data/caddy/pki/authorities/local/root.crt > "$ca" 2>/dev/null \
      && curl --silent --fail --cacert "$ca" "$origin/healthz" >/dev/null; then
    ready=true
    break
  fi
  sleep 1
done
if [[ "$ready" != true ]]; then
  docker compose -f "$compose_file" logs --tail=60 >&2
  echo 'Networked stack did not become healthy.' >&2
  exit 1
fi
cleartext_status=$(curl --silent --output /dev/null --write-out '%{http_code}' \
  "http://127.0.0.1:$SMOKE_API_PORT/api/tasks")
anonymous_status=$(curl --silent --output /dev/null --write-out '%{http_code}' --cacert "$ca" \
  "$origin/api/tasks")
if [[ "$cleartext_status" != 426 || "$anonymous_status" != 401 ]]; then
  echo "Networked boundary returned HTTP $cleartext_status on HTTP and $anonymous_status without a credential." >&2
  exit 1
fi

owner_password=$(openssl rand -hex 24)
cookie="$smoke_dir/cookie"
session=$(curl --silent --show-error --fail --cacert "$ca" -c "$cookie" -b "$cookie" \
  -H 'Content-Type: application/json' -d "$(jq -nc --arg password "$owner_password" \
  '{username:"smoke.owner",password:$password,displayName:"Smoke Owner"}')" \
  "$origin/api/auth/bootstrap")
csrf=$(jq -er '.csrfToken' <<< "$session")
enrollment=$(curl --silent --show-error --fail --cacert "$ca" -c "$cookie" -b "$cookie" \
  -H 'Content-Type: application/json' -H "X-CSRF-Token: $csrf" \
  -d '{"name":"smoke-runner"}' "$origin/api/auth/runner-enrollments")
code=$(jq -er '.enrollmentCode' <<< "$enrollment")
identity=$(curl --silent --show-error --fail --cacert "$ca" -H 'Content-Type: application/json' \
  -d "$(jq -nc --arg code "$code" '{code:$code}')" "$origin/api/auth/runner-enroll")
runner_id=$(jq -er '.runnerId' <<< "$identity")
runner_name=$(jq -er '.runnerName' <<< "$identity")
token=$(jq -er '.secret' <<< "$identity")
curl --silent --show-error --fail --cacert "$ca" -c "$cookie" -b "$cookie" \
  -X PUT -H 'Content-Type: application/json' -H "X-CSRF-Token: $csrf" \
  -d "$(jq -nc --arg id "$runner_id" '{executionRunner:$id,remoteExecutionEnabled:true,pickupMode:"manual"}')" \
  "$origin/api/projects/networked-smoke/execution-runner" >/dev/null

# Seed after boot recovery has finished so it cannot treat an unleased
# progress card as a startup orphan before the fixture Runner acquires it.
mkdir -p "$task_dir"
cat > "$task_dir/task.json" <<'JSON'
{"id":"AGT-NETWORKED-SMOKE","title":"Networked smoke","state":"3-progress","order":1,"agent":"codex","kind":"task"}
JSON
echo 'Complete the fixture task.' > "$task_dir/prompt.md"
echo 'Result: pending.' > "$task_dir/status.md"

claim_status=404
for _ in $(seq 1 20); do
  claim_response=$(curl --silent --show-error --cacert "$ca" --write-out '\n%{http_code}' \
    -H "Authorization: Bearer $token" -H "X-Client-Id: $runner_id" \
    -H 'Content-Type: application/json' \
    -d "$(jq -nc --arg id "$runner_id" --arg name "$runner_name" \
    '{taskKey:"AGT-NETWORKED-SMOKE",runnerId:$id,runnerName:$name,hostname:"smoke-host",pid:42,backendName:"smoke"}')" \
    "$origin/api/runner/lease/acquire")
  claim_status=$(tail -n 1 <<< "$claim_response")
  claim=$(sed '$d' <<< "$claim_response")
  [[ "$claim_status" != 404 ]] && break
  sleep 0.5
done
if [[ "$claim_status" != 200 ]]; then
  echo "Runner lease acquisition returned HTTP $claim_status: $claim" >&2
  exit 1
fi
[[ $(jq -r '.granted' <<< "$claim") == true ]]
lease_id=$(jq -er '.lease.leaseId' <<< "$claim")
fence=$(jq -er '.lease.fencingToken' <<< "$claim")
attempt_id=$(jq -er '.lease.attemptId' <<< "$claim")
authority_epoch=$(jq -er '.lease.authorityEpoch' <<< "$claim")
completion_response=$(curl --silent --show-error --cacert "$ca" --write-out '\n%{http_code}' \
  -H "Authorization: Bearer $token" -H "X-Client-Id: $runner_id" \
  -H 'Content-Type: application/json' \
  -d "$(jq -nc --arg id "$runner_id" --arg lease "$lease_id" --arg attempt "$attempt_id" \
  --argjson fence "$fence" --argjson epoch "$authority_epoch" \
  '{taskKey:"AGT-NETWORKED-SMOKE",leaseId:$lease,fencingToken:$fence,runnerId:$id,outcome:"Blocked",reason:"networked fixture complete",gateItems:["fixture: controlled completion"],attemptId:$attempt,authorityEpoch:$epoch,idempotencyKey:"networked-compose-smoke"}')" \
  "$origin/api/runner/completion")
completion_status=$(tail -n 1 <<< "$completion_response")
completion=$(sed '$d' <<< "$completion_response")
if [[ "$completion_status" != 200 ]]; then
  echo "Runner completion returned HTTP $completion_status: $completion" >&2
  find "$SMOKE_WORKSPACE/projects/networked-smoke" -maxdepth 4 -type d >&2
  exit 1
fi
jq -e '.taskKey == "AGT-NETWORKED-SMOKE"' <<< "$completion" >/dev/null
echo 'Networked Compose runner claim and completion passed.'
