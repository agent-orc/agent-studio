#!/bin/sh
# Docker-target update: drain, switch the image tag, health-gate, and
# automatically restore the previous tag on failure. Sibling to update.sh
# (the systemd target).
set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
# shellcheck source=./lib-docker.sh
. "$SCRIPT_DIR/lib-docker.sh"

require_root
[ "$#" -eq 1 ] || die "Usage: update-docker.sh <image-tag>"
[ -f "$ENV_FILE" ] || die "No installation found in $ENV_FILE; run install-docker.sh first."

new_version=$1
old_version=$(read_env_value CONTROL_PLANE_VERSION "$ENV_FILE")

if [ "$new_version" = "$old_version" ]; then
    log "Version $new_version is already active; no update is needed."
    exit 0
fi

drain_for_switch "updating from $old_version to $new_version"

write_env_value CONTROL_PLANE_VERSION "$new_version" "$ENV_FILE"
if ! compose_cmd pull; then
    write_env_value CONTROL_PLANE_VERSION "$old_version" "$ENV_FILE"
    die "Could not pull images for $new_version; restored $old_version in $ENV_FILE. No container was changed."
fi

if compose_cmd up -d && wait_healthy; then
    set_mode Normal "update to $new_version complete"
    write_env_value CONTROL_PLANE_PREVIOUS_VERSION "$old_version" "$ENV_FILE"
    log "Updated the Docker control plane from $old_version to $new_version."
    exit 0
fi

log "Candidate $new_version failed readiness; restoring $old_version."
write_env_value CONTROL_PLANE_VERSION "$old_version" "$ENV_FILE"
compose_cmd up -d
if wait_healthy; then
    set_mode Normal "update rollback to $old_version complete" || true
    die "Candidate $new_version was rolled back because health checks stayed red; $old_version was restored."
fi
die "Candidate $new_version failed readiness and the automatic rollback to $old_version also failed; inspect docker compose logs."
