#!/bin/sh
# Scheduled Task Server backup with off-host copy. The sidecar asks the sole
# Task Server writer to create the SQLite snapshot and audit record through its
# authenticated management API, then copies the resulting backup off host.
set -eu

: "${BACKUP_INTERVAL_SECONDS:=300}"
: "${BACKUP_PATH:?BACKUP_PATH is required}"
: "${BACKUP_OFFHOST_PATH:?BACKUP_OFFHOST_PATH is required}"
: "${TASK_SERVER_URL:?TASK_SERVER_URL is required}"
: "${STUDIO_AUTH_TOKEN_FILE:?STUDIO_AUTH_TOKEN_FILE is required}"

log()
{
    printf '[backup] %s\n' "$*"
}

while true; do
    name="docker-$(date -u +%Y%m%dT%H%M%SZ)"
    log "Creating backup $name"
    if output=$(printf 'header = "Authorization: Bearer %s"\n' "$(cat "$STUDIO_AUTH_TOKEN_FILE")" \
        | curl --config - --fail --silent --show-error \
            --header 'Content-Type: application/json' \
            --header 'X-Actor-Id: backup-sidecar' \
            --header 'X-Task-Protocol-Version: 2' \
            --request POST --data "{\"name\":\"$name\"}" \
            "$TASK_SERVER_URL/api/v1/management/backups" 2>&1); then
        log "$output"
        backup_id=$(printf '%s\n' "$output" | sed -n 's/.*"backupId":"\([^"]*\)".*/\1/p')
        expected_sha=$(printf '%s\n' "$output" | sed -n 's/.*"sha256":"\([^"]*\)".*/\1/p')
        case "$backup_id" in
            ""|*[!a-zA-Z0-9._-]*) log "Backup response has an invalid ID."; exit 1 ;;
        esac
        [ -n "$expected_sha" ] || { log "Backup response has no SHA-256."; exit 1; }
        log "Copying $backup_id to off-host destination $BACKUP_OFFHOST_PATH"
        cp "$BACKUP_PATH/$backup_id.db" "$BACKUP_OFFHOST_PATH/$backup_id.db"
        actual_sha=$(sha256sum "$BACKUP_OFFHOST_PATH/$backup_id.db" | cut -d ' ' -f 1)
        [ "$actual_sha" = "$expected_sha" ] || { log "Off-host SHA-256 mismatch for $backup_id."; exit 1; }
        touch /tmp/backup-last-success
        log "Off-host copy complete; sha256=$actual_sha."
    else
        log "Backup failed: $output"
    fi
    sleep "$BACKUP_INTERVAL_SECONDS"
done
