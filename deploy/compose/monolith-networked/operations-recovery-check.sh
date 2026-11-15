#!/usr/bin/env bash
# Records one verified recovery check. The CLI validates a fresh principal for reenrolment.
set -euo pipefail
cd "$(dirname "$0")"
if [ "$#" -ne 2 ]; then
  echo 'Usage: operations-recovery-check.sh <kind> <id>' >&2
  exit 2
fi
data_dir="$(sed -n 's/^STUDIO_DATA_DIR=//p' .env | tail -n 1)"
digest="$(sed -n 's/^STUDIO_OPERATIONS_SERVER_DIGEST=//p' .env | tail -n 1)"
[ -n "$data_dir" ] && [ -n "$digest" ] || { echo 'Operations recovery settings are missing from .env' >&2; exit 1; }
docker run --rm --user 1654:1654 --network none -v "$data_dir/operations:/data/operations" \
  "ghcr.io/agent-orc/agent-operations-server@sha256:$digest" \
  recovery-complete /data/operations "$1" "$2"
