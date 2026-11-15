#!/usr/bin/env bash
# One-time migration before upgrading an existing networked stack to bind-backed Operations state.
set -euo pipefail
cd "$(dirname "$0")"
fail() { echo "Operations migration failed: $*" >&2; exit 1; }
[ -f .env ] || fail 'no stack .env'
data_dir="$(sed -n 's/^STUDIO_DATA_DIR=//p' .env | tail -n 1)"
digest="$(sed -n 's/^STUDIO_EDGE_DIGEST=//p' .env | tail -n 1)"
[ -n "$data_dir" ] && [ -n "$digest" ] || fail 'data directory or edge image digest is missing'
[ -z "$(docker compose ps --status running -q 2>/dev/null)" ] || fail 'stop the stack before migrating Operations volumes'
for name in operations-data operations-principals operations-agent-secret operations-client-secret operations-agent-data; do
  docker volume inspect "agent-studio-website_$name" >/dev/null 2>&1 || fail "missing volume $name"
done
if [ -d "$data_dir/operations" ] && [ -n "$(find "$data_dir/operations" -type f -print -quit)" ]; then
  fail 'the bind-backed Operations directory already holds files'
fi
docker run --rm --user 0 --network none \
  -v "$data_dir:/site" \
  -v agent-studio-website_operations-data:/old/data:ro \
  -v agent-studio-website_operations-principals:/old/principals:ro \
  -v agent-studio-website_operations-agent-secret:/old/agent-secret:ro \
  -v agent-studio-website_operations-client-secret:/old/client-secret:ro \
  -v agent-studio-website_operations-agent-data:/old/agent-data:ro \
  --entrypoint /bin/sh "caddy@sha256:$digest" -c '
    set -eu
    for name in data principals agent-secret client-secret agent-data; do
      mkdir -p "/site/operations/$name"
      cp -a "/old/$name/." "/site/operations/$name/"
    done
    chown -R 1654:1654 /site/operations
    find /site/operations -type d -exec chmod 750 {} +
    find /site/operations -type f -exec chmod g+r {} +
    mkdir -p /site/operations-recovery
    chown 1654:1654 /site/operations-recovery
    chmod 750 /site/operations-recovery'
echo 'Operations state copied. Keep the old named volumes until a new encrypted archive is verified off-host.'
