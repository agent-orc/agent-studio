#!/bin/sh
# Scheduled Task Server backup with off-host copy, run inside the `backup`
# service container against the same task-server.dll used by the API. This
# keeps backup semantics (SQLite snapshot, integrity check, audit record)
# identical to the packaged systemd timer
# (deploy/release/agent-orchestrator/systemd/agent-task-server-backup.service).
set -eu

: "${BACKUP_INTERVAL_SECONDS:=300}"
: "${BACKUP_PATH:?BACKUP_PATH is required}"
: "${BACKUP_OFFHOST_PATH:?BACKUP_OFFHOST_PATH is required}"

log()
{
    printf '[backup] %s\n' "$*"
}

while true; do
    name="docker-$(date -u +%Y%m%dT%H%M%SZ)"
    log "Creating backup $name"
    if output=$(dotnet /app/task-server.dll backup --name "$name" 2>&1); then
        log "$output"
        log "Copying $BACKUP_PATH to off-host destination $BACKUP_OFFHOST_PATH"
        cp -a "$BACKUP_PATH"/. "$BACKUP_OFFHOST_PATH"/
        log "Off-host copy complete."
    else
        log "Backup failed: $output"
    fi
    sleep "$BACKUP_INTERVAL_SECONDS"
done
