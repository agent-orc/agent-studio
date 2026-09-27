#!/bin/sh
# Request a snapshot through the serving Task Server's write gate, then copy
# only the completed and verified snapshot to the off-host destination.
set -eu

: "${BACKUP_INTERVAL_SECONDS:=300}"
: "${BACKUP_PATH:?BACKUP_PATH is required}"
: "${BACKUP_OFFHOST_PATH:?BACKUP_OFFHOST_PATH is required}"

log()
{
    printf '[backup] %s\n' "$*"
}

backup_once()
{
    name="docker-$(date -u +%Y%m%dT%H%M%SZ)"
    token=$(cat /run/secrets/studio_token)
    output=$(curl --fail --silent --show-error --max-time 120 \
        -H "Authorization: Bearer $token" \
        -H 'X-Task-Protocol-Version: 2' \
        -H 'X-Actor-Id: docker-backup' \
        -H 'Content-Type: application/json' \
        --data "{\"name\":\"$name\"}" \
        http://task-server:5071/api/v1/management/backups)
    backup_id=$(printf '%s' "$output" | sed -n 's/.*"backupId":"\([^"]*\)".*/\1/p')
    expected_sha=$(printf '%s' "$output" | sed -n 's/.*"sha256":"\([0-9a-fA-F]*\)".*/\1/p')
    case "$backup_id" in
        ''|*[!a-zA-Z0-9-]*) log "Invalid backup ID in API response"; return 1 ;;
    esac
    if [ "${#expected_sha}" -ne 64 ]; then
        log "Invalid backup hash in API response"
        return 1
    fi
    source_file="$BACKUP_PATH/$backup_id.db"
    target_file="$BACKUP_OFFHOST_PATH/$backup_id.db"
    staging_file="$target_file.partial"
    actual_sha=$(sha256sum "$source_file" | cut -d ' ' -f 1) || return 1
    if [ "$actual_sha" != "$expected_sha" ]; then
        log "Backup hash mismatch for $backup_id"
        return 1
    fi
    cp "$source_file" "$staging_file" || return 1
    copied_sha=$(sha256sum "$staging_file" | cut -d ' ' -f 1) || return 1
    if [ "$copied_sha" != "$expected_sha" ]; then
        rm -f "$staging_file"
        log "Off-host copy hash mismatch for $backup_id"
        return 1
    fi
    mv "$staging_file" "$target_file" || return 1
    log "Off-host copy complete: backupId=$backup_id sha256=$expected_sha"
}

while true; do
    if ! backup_once; then
        log "Backup or copy failed"
    fi
    sleep "$BACKUP_INTERVAL_SECONDS"
done
