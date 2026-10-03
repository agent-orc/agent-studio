#!/bin/sh
# Docker-target update: drain, verified backup, switch the image tag,
# health-gate, protocol-gate, resume admission and canary. The previous tag
# is retained and restored automatically when the candidate fails and the
# store schema allows it. Every phase is recorded in the installation
# manifest (AGT-2947, Dossier AGT-W63 D6), so an interrupted run resumes.
# Sibling to update.sh (the systemd target).
set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
# shellcheck source=./lib-docker.sh
. "$SCRIPT_DIR/lib-docker.sh"

require_root
[ "$#" -eq 1 ] || die "Usage: update-docker.sh <image-tag>"
[ -f "$ENV_FILE" ] || die "No installation found in $ENV_FILE; run install-docker.sh first."

new_version=$1
resume_phase=
pending_outcome=$(state_get OUTCOME)
if [ "$pending_outcome" = "in-progress" ] || [ "$pending_outcome" = "awaiting-canary" ]; then
    pending_target=$(state_get TARGET)
    if [ "$(state_get OPERATION)" != "update" ] || [ "$pending_target" != "$new_version" ]; then
        die "An $(state_get OPERATION) to $pending_target is still in progress (phase $(state_get PHASE)). Finish it first."
    fi
    resume_phase=$(state_get PHASE)
    old_version=$(state_get FROM_VERSION)
    state_set OUTCOME in-progress
    log "Resuming the interrupted update to $new_version after phase $resume_phase."
else
    old_version=$(read_env_value CONTROL_PLANE_VERSION "$ENV_FILE")
    if [ "$new_version" = "$old_version" ]; then
        log "Version $new_version is already active; no update is needed."
        exit 0
    fi
fi

# Restores the retained prior release unless the candidate already moved
# the store schema forward; a downgrade after a schema change is never
# attempted, the operator restores the verified backup instead.
candidate_failed()
{
    why=$1
    from_schema=$(state_get FROM_SCHEMA)
    candidate_schema=$(state_get OBSERVED_SCHEMA)
    if [ -n "$candidate_schema" ] && [ -n "$from_schema" ] && [ "$candidate_schema" -gt "$from_schema" ]; then
        set_mode Maintenance "candidate $new_version failed after a schema change" || true
        finish_upgrade awaiting-restore "$why; the store schema moved from $from_schema to $candidate_schema, so $old_version cannot run against it. Restore backup $(state_get BACKUP_ID) with $old_version."
        die "Candidate $new_version failed ($why) after changing the store schema from $from_schema to $candidate_schema. No downgrade was attempted; the installation is in Maintenance. Restore verified backup $(state_get BACKUP_ID) with release $old_version."
    fi
    log "Candidate $new_version failed ($why); restoring $old_version."
    write_env_value CONTROL_PLANE_VERSION "$old_version" "$ENV_FILE"
    compose_cmd up -d
    if wait_healthy && set_mode Normal "update rollback to $old_version complete"; then
        record_observed_release OBSERVED || true
        finish_upgrade rolled-back "$why"
        die "Candidate $new_version was rolled back ($why); $old_version was restored."
    fi
    finish_upgrade failed "$why; the automatic rollback to $old_version also failed"
    die "Candidate $new_version failed ($why) and the automatic rollback to $old_version also failed; inspect docker compose logs. Verified backup: $(state_get BACKUP_ID)."
}

case "$resume_phase" in
    ""|started|drained|backed-up)
        if [ -z "$resume_phase" ]; then
            for key in BACKUP_ID BACKUP_SHA BACKUP_SCHEMA BACKUP_AT REASON \
                OBSERVED_VERSION OBSERVED_SCHEMA OBSERVED_MODE OBSERVED_AT; do
                state_set "$key" ""
            done
            state_set OPERATION update
            state_set TARGET "$new_version"
            state_set OUTCOME in-progress
            record_observed_release FROM \
                || die "Could not read the running release; no version was changed."
            release_images_present "$old_version" \
                || die "The images of the active release $old_version are not present locally, so it cannot be retained for rollback."
            set_phase started
        fi
        drain_for_switch "updating from $old_version to $new_version"
        [ "$resume_phase" = "backed-up" ] || {
            set_phase drained
            verified_backup "update to $new_version"
            set_phase backed-up
        }
        write_env_value CONTROL_PLANE_VERSION "$new_version" "$ENV_FILE"
        if ! compose_cmd pull; then
            write_env_value CONTROL_PLANE_VERSION "$old_version" "$ENV_FILE"
            set_mode Normal "update to $new_version aborted before switch" || true
            finish_upgrade failed "could not pull images for $new_version"
            die "Could not pull images for $new_version; restored $old_version in $ENV_FILE. No container was changed."
        fi
        compose_cmd up -d || true
        set_phase switched
        ;;
esac

case "$resume_phase" in
    ""|started|drained|backed-up|switched|healthy)
        wait_healthy || candidate_failed "health checks stayed red"
        record_observed_release OBSERVED || candidate_failed "the candidate did not report its release"
        observed_version=$(state_get OBSERVED_VERSION)
        [ "$observed_version" = "$new_version" ] \
            || candidate_failed "the running authority reports $observed_version, not $new_version"
        set_phase healthy
        record_hosts "$(state_get OBSERVED_PROTO_MIN)" "$(state_get OBSERVED_PROTO_MAX)"
        [ "$(incompatible_online_hosts)" -eq 0 ] \
            || candidate_failed "an online runner host uses a protocol outside $(state_get OBSERVED_PROTO_MIN)-$(state_get OBSERVED_PROTO_MAX)"
        set_phase compatible
        ;;
esac

case "$resume_phase" in
    ""|started|drained|backed-up|switched|healthy|compatible)
        set_mode Normal "update to $new_version: admission resumed"
        wait_ready Normal || candidate_failed "the candidate did not return to Normal"
        state_set OBSERVED_MODE "$(observe_mode)"
        set_phase resumed
        ;;
esac

canary_status=0
run_canary || canary_status=$?
if [ "$canary_status" -eq 0 ]; then
    retain_prior_release
    set_phase canary-passed
    finish_upgrade succeeded ""
    log "Updated the Docker control plane from $old_version to $new_version; canary passed."
elif [ "$canary_status" -eq 2 ]; then
    retain_prior_release
    finish_upgrade awaiting-canary "no canary command was configured"
    log "Switched the Docker control plane from $old_version to $new_version. Run the detached canary; the manifest stays awaiting-canary until it passes."
else
    candidate_failed "the canary failed with exit $canary_status"
fi
