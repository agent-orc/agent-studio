#!/usr/bin/env bash
# Manages the key that encrypts the scheduled backups of the networked stack
# (AGT-3574). The key lives at $STUDIO_BACKUP_KEY_DIR/backup.key (default
# keys/backup/backup.key in the stack directory), outside the data directory,
# and the API reads it read-only. No backup contains it: keep the copy printed
# by `print` in a safe place apart from the backups and apart from the secret
# store key copy.
#
#   backup-key.sh create             create the key (and the backup folders) when missing
#   backup-key.sh print              print the key for its separate copy
#   backup-key.sh id <key-file>      print the key fingerprint shown on the Operations page
#   backup-key.sh install <key-file> replace the key with a saved copy (after a restore)
#
# The key belongs to UID 10001, so file access runs in a one-shot container;
# the operator account needs only Docker access.
set -euo pipefail

helper_image="${STUDIO_HELPER_IMAGE:-caddy:2-alpine}"
stack_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

usage() {
  echo 'Usage: backup-key.sh create | print | id <key-file> | install <key-file>' >&2
  exit 2
}

env_value() { sed -n "s/^$1=//p" "$stack_dir/.env" | tail -n 1; }

key_dir() {
  [ -f "$stack_dir/.env" ] || { echo 'Run setup.sh before managing the backup key' >&2; exit 2; }
  local configured data_dir
  configured="$(env_value STUDIO_BACKUP_KEY_DIR)"
  configured="${configured:-$stack_dir/keys/backup}"
  data_dir="$(env_value STUDIO_DATA_DIR)"
  case "$configured" in /*) ;; *) echo 'STUDIO_BACKUP_KEY_DIR must be absolute' >&2; exit 2;; esac
  case "${configured%/}/" in "${data_dir%/}"/*)
    echo 'STUDIO_BACKUP_KEY_DIR must lie outside the data directory' >&2; exit 2;;
  esac
  printf '%s' "$configured"
}

# Same derivation as BackupKey.Id: SHA-256 over a fixed prefix and the raw key bytes.
fingerprint() {
  { printf 'agent-studio/backup-key-id/v1|'; tr -d '\r\n' | base64 -d; } | sha256sum | cut -c1-16
}

validate_key_file() {
  local size
  size="$(tr -d '\r\n' < "$1" | base64 -d 2>/dev/null | wc -c)" || size=0
  if [ "$size" -ne 32 ]; then
    echo "$1 is not a backup key: expected base64 of 32 bytes" >&2
    exit 1
  fi
}

[ "$#" -ge 1 ] || usage
command="$1"
shift
case "$command" in
  create)
    [ "$#" -eq 0 ] || usage
    dir="$(key_dir)"
    data_dir="$(env_value STUDIO_DATA_DIR)"
    mkdir -p "$dir" "$stack_dir/backup-target"
    # Also prepares the folders an installation from before AGT-3574 lacks.
    docker run --rm --user 0 --network none -v "$dir:/key" -v "$data_dir:/site" \
      -v "$stack_dir/backup-target:/backup-target" --entrypoint /bin/sh "$helper_image" -c '
      set -e
      umask 077
      mkdir -p /site/backups
      if [ ! -s /key/backup.key ]; then
        head -c 32 /dev/urandom | base64 > /key/backup.key.new
        mv /key/backup.key.new /key/backup.key
        echo "Created the backup key." >&2
      fi
      chown -R 10001:10001 /key /site/backups /backup-target
      chmod 700 /key /site/backups /backup-target
      chmod 600 /key/backup.key'
    ;;
  print)
    [ "$#" -eq 0 ] || usage
    dir="$(key_dir)"
    docker run --rm --user 10001:10001 --network none -v "$dir:/key:ro" --entrypoint cat "$helper_image" /key/backup.key
    ;;
  id)
    [ "$#" -eq 1 ] || usage
    validate_key_file "$1"
    fingerprint < "$1"
    ;;
  install)
    [ "$#" -eq 1 ] || usage
    validate_key_file "$1"
    dir="$(key_dir)"
    mkdir -p "$dir"
    docker run -i --rm --user 0 --network none -v "$dir:/key" --entrypoint /bin/sh "$helper_image" -c '
      set -e
      umask 077
      tr -d "\r\n" > /key/backup.key.new
      echo >> /key/backup.key.new
      mv /key/backup.key.new /key/backup.key
      chown -R 10001:10001 /key
      chmod 700 /key
      chmod 600 /key/backup.key' < "$1"
    ;;
  *) usage ;;
esac
