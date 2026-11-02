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

# setup.sh uses the edge image for its root-owned directory handoff. Pull the
# smoke edge image and resolve its registry digest before setup.
docker pull caddy:2-alpine > /dev/null
edge_reference="$(docker image inspect --format '{{index .RepoDigests 0}}' caddy:2-alpine)"
edge_digest="${edge_reference##*@sha256:}"
if [[ ! "$edge_digest" =~ ^[0-9a-f]{64}$ ]]; then
  echo 'The pulled Caddy image has no usable registry digest' >&2
  exit 1
fi

cd "$stack"
export STUDIO_API_DIGEST="$(openssl rand -hex 32)"
export STUDIO_WEB_DIGEST="$(openssl rand -hex 32)"
export STUDIO_EDGE_DIGEST="$edge_digest"
export STUDIO_ACCESS_AUTH_URL=https://access.example.test
export STUDIO_ACCESS_MFA_ENFORCED=true
bash setup.sh --private "$version" studio.test "$smoke_root/data" >/dev/null
cat > "$smoke_root/local-images.yaml" <<EOF
services:
  api:
    image: $api_image
  frontend:
    image: $web_image
EOF
export COMPOSE_FILE="$stack/compose.yaml:$smoke_root/local-images.yaml"
code="$(sed -n 's/^STUDIO_BOOTSTRAP_CODE=//p' .env)"
docker compose up -d --no-build
for service in frontend edge; do
  container="$(docker compose ps -q "$service")"
  [ -n "$container" ] || { echo "$service Caddy container did not start" >&2; exit 1; }
  runtime="$(docker inspect --format '{{.State.Running}} {{.HostConfig.ReadonlyRootfs}} {{.Config.User}}' "$container")"
  [ "$runtime" = 'true true 10001:10001' ] || {
    echo "$service Caddy container is not running with the read-only, non-root baseline: $runtime" >&2
    exit 1
  }
  docker compose exec -T "$service" sh -c \
    'test "$(id -u)" -eq 10001 && test -w /data && test -w /config && test -w /tmp' || {
    echo "$service Caddy writable mounts are unavailable to UID 10001" >&2
    exit 1
  }
done
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
echo 'Website smoke passed: TLS, owner bootstrap, browser login, and board.'
if [ -n "${JOB_RESULTS_DIR:-}" ]; then
  mkdir -p "$JOB_RESULTS_DIR"
  rm -f "$JOB_RESULTS_DIR/website-smoke.log"
  printf 'Website smoke passed: TLS, owner bootstrap, browser login, and board.\n' > "$JOB_RESULTS_DIR/website-smoke.txt"
fi
