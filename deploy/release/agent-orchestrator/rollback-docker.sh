#!/bin/sh
# Docker-target rollback: switch to an explicit prior image tag, or to the
# tag recorded by the last successful update-docker.sh. Sibling to
# rollback.sh (the systemd target).
set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
# shellcheck source=./lib-docker.sh
. "$SCRIPT_DIR/lib-docker.sh"

require_root
[ -f "$ENV_FILE" ] || die "No installation found in $ENV_FILE; run install-docker.sh first."

current=$(read_env_value CONTROL_PLANE_VERSION "$ENV_FILE")
if [ "$#" -eq 1 ]; then
    target=$1
else
    target=$(read_env_value CONTROL_PLANE_PREVIOUS_VERSION "$ENV_FILE")
    [ -n "$target" ] || die "No previous version is recorded. Pass a target tag explicitly."
fi
[ "$target" != "$current" ] || die "Rollback target is already active."

drain_for_switch "rolling back from $current to $target"

write_env_value CONTROL_PLANE_VERSION "$target" "$ENV_FILE"
if compose_cmd pull && compose_cmd up -d && wait_healthy; then
    set_mode Normal "rollback to $target complete"
    write_env_value CONTROL_PLANE_PREVIOUS_VERSION "$current" "$ENV_FILE"
    log "Rolled back the Docker control plane to $target."
    exit 0
fi

log "Rollback target $target failed readiness; restoring $current."
write_env_value CONTROL_PLANE_VERSION "$current" "$ENV_FILE"
compose_cmd up -d
wait_healthy || true
die "Rollback target $target was unhealthy; $current was restored."
