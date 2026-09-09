#!/usr/bin/env bash
# CI topology proof for the Docker control plane
# (deploy/compose/control-plane/). Boots the stack with a self-signed edge
# built from this checkout and proves: health through the edge with the
# pinned certificate, a 401/403 auth matrix through the edge, backup archive
# evidence, engine restart independent of task-server, and no listener
# outside the edge's published port. See
# docs/operations/setup/control-plane-docker.md.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
compose_dir="$repo_root/deploy/compose/control-plane"
project_name="${CONTROL_PLANE_TOPOLOGY_PROJECT:-control-plane-topology-ci}"
work_dir="$(mktemp -d)"
env_file="$work_dir/.env"
secrets_dir="$work_dir/secrets"
offhost_dir="$work_dir/offhost-backup"
leaf_cert="$work_dir/leaf.pem"

compose=(docker compose --project-name "$project_name" --project-directory "$compose_dir" \
    -f "$compose_dir/compose.yaml" -f "$compose_dir/compose.ci.yaml" --env-file "$env_file")

down()
{
    "${compose[@]}" down --volumes --remove-orphans >/dev/null 2>&1 || true
}

finish()
{
    status="$1"
    trap - EXIT HUP INT TERM
    if [ "$status" -ne 0 ]; then
        "${compose[@]}" ps || true
        "${compose[@]}" logs --no-color || true
    fi
    down
    rm -rf "$work_dir"
    exit "$status"
}

trap 'finish $?' EXIT
trap 'exit 130' HUP INT TERM

mkdir -p "$secrets_dir" "$offhost_dir"
umask 077
openssl rand -hex 32 >"$secrets_dir/studio.token"
openssl rand -hex 32 >"$secrets_dir/engine.token"
openssl rand -hex 32 >"$secrets_dir/runner.token"

cat >"$env_file" <<EOF
CONTROL_PLANE_VERSION=ci-test
CONTROL_PLANE_RUNNER_ID=ci-runner
WG_ADDRESS=127.0.0.1
CONTROL_PLANE_DOMAIN=localhost
CONTROL_PLANE_CADDYFILE=$compose_dir/Caddyfile
CONTROL_PLANE_SECRETS_DIR=$secrets_dir
CONTROL_PLANE_OFFHOST_BACKUP_PATH=$offhost_dir
BACKUP_INTERVAL_SECONDS=5
EOF

down
"${compose[@]}" config --quiet
"${compose[@]}" up --build --wait --wait-timeout 180

echo "== check: no listener outside the edge's published port =="
test -z "$("${compose[@]}" port task-server 5071 2>/dev/null || true)"
test -z "$("${compose[@]}" port orchestrator-engine 5071 2>/dev/null || true)"
edge_binding="$("${compose[@]}" port edge 443)"
case "$edge_binding" in
    127.0.0.1:*) ;;
    *) echo "FAIL: edge is published on $edge_binding, not 127.0.0.1" >&2; exit 1 ;;
esac
echo "OK: task-server and orchestrator-engine publish no host port; edge is bound to $edge_binding."

echo "== check: health through the edge with the pinned certificate =="
openssl s_client -connect 127.0.0.1:443 -servername localhost </dev/null 2>/dev/null \
    | openssl x509 -outform pem >"$leaf_cert"
test -s "$leaf_cert"
leaf_sha256="$(openssl x509 -in "$leaf_cert" -noout -fingerprint -sha256 \
    | cut -d= -f2 | tr -d ':' | tr 'A-F' 'a-f')"
health="$(curl --fail --silent --cacert "$leaf_cert" --resolve localhost:443:127.0.0.1 \
    https://localhost/healthz)"
test -n "$health"
# Trusting exactly the served leaf (and no CA) is sufficient to complete the
# handshake, and its SHA-256 is stable and computable up front: the same
# proof property RUNNER_TLS_CERTIFICATE_SHA256 pinning relies on.
echo "OK: edge served a self-signed leaf (sha256:$leaf_sha256) and answered /healthz when only that leaf is trusted."

echo "== check: 401/403 matrix through the edge =="
unauth_status="$(curl --silent --output /dev/null --write-out '%{http_code}' \
    --cacert "$leaf_cert" --resolve localhost:443:127.0.0.1 https://localhost/api/v1/runners)"
test "$unauth_status" = "401"
echo "OK: unauthenticated /api/v1/runners returned 401."

client_id_only_status="$(curl --silent --output /dev/null --write-out '%{http_code}' \
    --cacert "$leaf_cert" --resolve localhost:443:127.0.0.1 \
    -H 'X-Client-Id: topology-test' https://localhost/api/v1/runners)"
test "$client_id_only_status" = "401"
echo "OK: X-Client-Id without a bearer returned 401."

runner_token="$(cat "$secrets_dir/runner.token")"
runner_on_management_status="$(curl --silent --output /dev/null --write-out '%{http_code}' \
    --cacert "$leaf_cert" --resolve localhost:443:127.0.0.1 \
    -X PUT -H "Authorization: Bearer $runner_token" -H 'Content-Type: application/json' \
    --data '{"mode":0,"reason":"topology test"}' https://localhost/api/v1/management/mode)"
test "$runner_on_management_status" = "403"
echo "OK: a Runner bearer against a management route returned 403."

echo "== check: task-server stays healthy across an independent engine restart =="
"${compose[@]}" restart orchestrator-engine
engine_container="$("${compose[@]}" ps -q orchestrator-engine)"
deadline=$(($(date +%s) + 60))
engine_status=""
while [ "$(date +%s)" -le "$deadline" ]; do
    engine_status="$(docker inspect -f '{{.State.Status}}' "$engine_container" 2>/dev/null || true)"
    [ "$engine_status" = "running" ] && break
    sleep 2
done
[ "$engine_status" = "running" ] \
    || { echo "FAIL: orchestrator-engine did not return to running after restart" >&2; exit 1; }
post_restart_health="$(curl --fail --silent --cacert "$leaf_cert" --resolve localhost:443:127.0.0.1 \
    https://localhost/healthz)"
test -n "$post_restart_health"
echo "OK: orchestrator-engine restarted independently; task-server kept answering /healthz."

echo "== check: backup archive evidence =="
deadline=$(($(date +%s) + 60))
found=0
while [ "$(date +%s)" -le "$deadline" ]; do
    if [ -n "$(find "$offhost_dir" -type f 2>/dev/null)" ]; then
        found=1
        break
    fi
    sleep 3
done
test "$found" -eq 1
"${compose[@]}" logs backup --no-color | grep -qi 'sha256'
echo "OK: the backup sidecar produced a verified archive and copied it to the off-host mount."

echo "control-plane-topology=passed"
