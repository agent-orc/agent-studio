#!/bin/sh
# Docker-target rollback: switch to an explicit prior image tag, or to the
# release retained by the last successful update-docker.sh. A rollback never
# runs a release against a store schema newer than the one it recorded; after
# a schema change the verified backup must be restored instead (AGT-2947,
# Dossier AGT-W63 D6). Sibling to rollback.sh (the systemd target).
set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
# shellcheck source=./lib-docker.sh
. "$SCRIPT_DIR/lib-docker.sh"

require_root
[ -f "$ENV_FILE" ] || die "No installation found in $ENV_FILE; run install-docker.sh first."

current=$(read_env_value CONTROL_PLANE_VERSION "$ENV_FILE")
previous=$(read_env_value CONTROL_PLANE_PREVIOUS_VERSION "$ENV_FILE")
if [ "$#" -eq 1 ]; then
    target=$1
else
    target=$previous
    [ -n "$target" ] || die "No previous version is recorded. Pass a target tag explicitly."
fi
[ "$target" != "$current" ] || die "Rollback target is already active."
[ "$(state_get OUTCOME)" != "in-progress" ] \
    || die "An $(state_get OPERATION) to $(state_get TARGET) is still in progress (phase $(state_get PHASE)); rerun it to resume or finish it first."

for key in BACKUP_ID BACKUP_SHA BACKUP_SCHEMA BACKUP_AT REASON \
    OBSERVED_VERSION OBSERVED_SCHEMA OBSERVED_MODE OBSERVED_AT; do
    state_set "$key" ""
done
state_set OPERATION rollback
state_set TARGET "$target"
record_observed_release FROM || die "Could not read the running release; no version was changed."

target_schema=
[ "$target" = "$previous" ] && target_schema=$(read_env_value CONTROL_PLANE_PREVIOUS_SCHEMA "$ENV_FILE")
active_schema=$(state_get FROM_SCHEMA)
if [ -z "$target_schema" ]; then
    die "The store schema of $target is not recorded, so a rollback could be an unsupported downgrade. Restore a verified backup taken with $target instead."
fi
if [ "$active_schema" -gt "$target_schema" ]; then
    die "Rollback refused: the store schema is $active_schema and $target supports $target_schema. Restore a verified backup taken with $target instead; no version was changed."
fi

state_set OUTCOME in-progress
set_phase started
drain_for_switch "rolling back from $current to $target"
set_phase drained
verified_backup "rollback to $target"
set_phase backed-up

write_env_value CONTROL_PLANE_VERSION "$target" "$ENV_FILE"
if compose_cmd pull && compose_cmd up -d && wait_healthy \
    && record_observed_release OBSERVED && [ "$(state_get OBSERVED_VERSION)" = "$target" ] \
    && set_mode Normal "rollback to $target complete" && wait_ready Normal; then
    state_set OBSERVED_MODE "$(observe_mode)"
    record_hosts "$(state_get OBSERVED_PROTO_MIN)" "$(state_get OBSERVED_PROTO_MAX)"
    set_phase resumed
    retain_prior_release
    finish_upgrade succeeded ""
    log "Rolled back the Docker control plane to $target."
    exit 0
fi

log "Rollback target $target failed readiness; restoring $current."
write_env_value CONTROL_PLANE_VERSION "$current" "$ENV_FILE"
compose_cmd up -d
if wait_healthy && set_mode Normal "rollback to $target abandoned"; then
    record_observed_release OBSERVED || true
    finish_upgrade rolled-back "rollback target $target was unhealthy"
else
    finish_upgrade failed "rollback target $target and the restore of $current were both unhealthy"
fi
die "Rollback target $target was unhealthy; $current was restored."
