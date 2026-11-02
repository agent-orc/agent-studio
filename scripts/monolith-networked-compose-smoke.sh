#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
stack="$repo_root/deploy/compose/monolith-networked"
version="$(tr -d '\r\n' < "$repo_root/VERSION")"
api_image="ghcr.io/agent-orc/agent-studio-api:v$version"
web_image="ghcr.io/agent-orc/agent-studio-web:v$version"
smoke_root="$(mktemp -d)"
chmod 755 "$smoke_root"
export COMPOSE_PROJECT_NAME="website-smoke-$$"

finish() {
  result=$?
  if [ "$result" -ne 0 ] && [ -n "${JOB_RESULTS_DIR:-}" ]; then
    mkdir -p "$JOB_RESULTS_DIR"
    docker compose -f "$stack/compose.yaml" --env-file "$stack/.env" logs --tail 150 > "$JOB_RESULTS_DIR/website-smoke.log" 2>&1 || true
  fi
  docker compose -f "$stack/compose.yaml" --env-file "$stack/.env" down --remove-orphans >/dev/null 2>&1 || true
  rm -f "$stack/.env"
  docker run --rm --user 0 -v "$smoke_root:/cleanup" --entrypoint chown caddy:2-alpine \
    -R "$(id -u):$(id -g)" /cleanup >/dev/null 2>&1 || true
  rm -rf "$smoke_root"
  exit "$result"
}
trap finish EXIT

if [ "${WEBSITE_REUSE_WEBSITE_IMAGES:-0}" != 1 ]; then
  if [ "${WEBSITE_REUSE_MONOLITH_IMAGE:-0}" = 1 ] && docker image inspect agent-studio-monolith-smoke:local >/dev/null 2>&1; then
    docker tag agent-studio-monolith-smoke:local "$api_image"
  else
    docker build -f "$repo_root/backend/Dockerfile" --build-arg VERSION="$version" -t "$api_image" "$repo_root"
  fi
  docker build -f "$repo_root/frontend/Dockerfile" --build-arg VERSION="$version" -t "$web_image" "$repo_root"
fi

cd "$stack"
bash setup.sh --private "$version" studio.test "$smoke_root/data" >/dev/null
code="$(sed -n 's/^STUDIO_BOOTSTRAP_CODE=//p' .env)"
docker compose up -d --no-build
url=https://studio.test
curl_args=(--silent --show-error --insecure --resolve studio.test:443:127.0.0.1)
for attempt in $(seq 1 90); do
  if curl "${curl_args[@]}" --fail "$url/api/auth/status" > "$smoke_root/status.json"; then break; fi
  if [ "$attempt" -eq 90 ]; then echo 'Website did not become ready' >&2; exit 1; fi
  sleep 2
done
python3 - "$smoke_root/status.json" <<'PY'
import json, sys
status = json.load(open(sys.argv[1], encoding='utf-8'))
assert status['bootstrapRequired'] and status['bootstrapCodeRequired'], status
PY
curl "${curl_args[@]}" --fail "$url/" | grep -q '<app-root'
payload="$(python3 - "$code" <<'PY'
import json, sys
print(json.dumps({'username':'website.owner','password':'correct horse battery staple!', 'displayName':'Website Owner', 'bootstrapCode':sys.argv[1]}))
PY
)"
curl "${curl_args[@]}" --fail -H 'Content-Type: application/json' -H 'Origin: https://studio.test' -d "$payload" "$url/api/auth/bootstrap" >/dev/null
STUDIO_SMOKE_URL="$url" \
STUDIO_SMOKE_USERNAME=website.owner \
STUDIO_SMOKE_PASSWORD='correct horse battery staple!' \
  node "$repo_root/frontend/scripts/monolith-networked-browser-smoke.mjs"

# Local state (batch gates, gate cache, quota and routing state) must sit on the
# data directory, not in the container layer below /app (AGT-3401).
wait_for_api() {
  for attempt in $(seq 1 90); do
    if curl "${curl_args[@]}" --fail "$url/api/auth/status" >/dev/null 2>&1; then return 0; fi
    if [ "$attempt" -eq 90 ]; then echo "API did not become ready after $1" >&2; exit 1; fi
    sleep 2
  done
}
local_state="$smoke_root/data/home/.local/share"
test -d "$local_state" || { echo "Local state is not on the data directory: $local_state" >&2; exit 1; }
api_log="$(docker compose logs --no-color api)"
grep -q 'local-state-root "\{0,1\}/data/home/.local/share' <<< "$api_log" \
  || { echo 'API did not report /data/home/.local/share as its local state root' >&2; exit 1; }
# State an older release wrote relative to /app moves once on the next start.
docker compose exec -T api sh -ec 'mkdir -p /app/agentstudio/smoke-legacy && printf "legacy\n" > /app/agentstudio/smoke-legacy/marker'
docker compose restart api >/dev/null
wait_for_api 'the legacy state restart'
docker compose exec -T api sh -ec 'test "$(cat /data/home/.local/share/agentstudio/smoke-legacy/marker)" = legacy && test ! -e /app/agentstudio' \
  || { echo 'Legacy local state below /app was not moved to /data/home/.local/share' >&2; exit 1; }
marker="recreate-$(openssl rand -hex 8)"
docker compose exec -T api sh -ec "printf '%s\n' '$marker' > /data/home/.local/share/agentstudio/smoke-recreate-marker"
before="$(docker compose ps -q api)"
docker compose up -d --no-build --no-deps --force-recreate api >/dev/null
after="$(docker compose ps -q api)"
if [ -z "$after" ] || [ "$before" = "$after" ]; then echo 'The API container was not recreated' >&2; exit 1; fi
wait_for_api 'the container recreate'
docker compose exec -T api sh -ec "test \"\$(cat /data/home/.local/share/agentstudio/smoke-recreate-marker)\" = '$marker' && test \"\$(cat /data/home/.local/share/agentstudio/smoke-legacy/marker)\" = legacy" \
  || { echo 'Local state did not survive the API container recreate' >&2; exit 1; }
echo 'Local state smoke passed: legacy state moved once and a marker survived the container recreate.'
echo 'Website smoke passed: TLS, owner bootstrap, browser login, and board.'
if [ -n "${JOB_RESULTS_DIR:-}" ]; then
  mkdir -p "$JOB_RESULTS_DIR"
  rm -f "$JOB_RESULTS_DIR/website-smoke.log"
  printf 'Website smoke passed: TLS, owner bootstrap, browser login, and board.\n' > "$JOB_RESULTS_DIR/website-smoke.txt"
fi
