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
# The key belongs to UID 10001, so file access runs in a one-shot container of
# the edge image, pinned by STUDIO_EDGE_DIGEST in .env; every command except
# `id` needs an operator account with Docker access.
set -euo pipefail

stack_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

usage() {
  echo 'Usage: backup-key.sh create | print | id <key-file> | install <key-file>' >&2
  exit 2
}

env_value() { sed -n "s/^$1=//p" "$stack_dir/.env" | tail -n 1; }

# The edge image by digest, as setup.sh uses it, never a mutable tag.
helper_image() {
  local digest
  digest="$(env_value STUDIO_EDGE_DIGEST)"
  [[ "$digest" =~ ^[0-9a-f]{64}$ ]] || { echo 'STUDIO_EDGE_DIGEST in .env must be a 64-character image digest' >&2; exit 2; }
  printf 'caddy@sha256:%s' "$digest"
}

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

is_key() {
  local size
  size="$(printf '%s' "$1" | tr -d '\r\n' | base64 -d 2>/dev/null | wc -c)" || size=0
  [ "$size" -eq 32 ]
}

validate_key_file() {
  if ! is_key "$(cat "$1")"; then
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
    image="$(helper_image)"
    data_dir="$(env_value STUDIO_DATA_DIR)"
    mkdir -p "$dir" "$stack_dir/backup-target"
    # Also prepares the folders an installation from before AGT-3574 lacks.
    docker run --rm --user 0 --network none -v "$dir:/key" -v "$data_dir:/site" \
      -v "$stack_dir/backup-target:/backup-target" --entrypoint /bin/sh "$image" -c '
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
    image="$(helper_image)"
    # Read into a variable first: with `print > copy` the shell has already
    # created the copy, so a failure must say so instead of leaving it silent.
    if ! key="$(docker run --rm --user 10001:10001 --network none -v "$dir:/key:ro" --entrypoint cat "$image" /key/backup.key)"; then
      echo 'Cannot read the backup key through Docker: run as an account with Docker access. Any output file is empty; delete it, it is no copy of the key.' >&2
      exit 1
    fi
    if ! is_key "$key"; then
      echo "$dir/backup.key is not a backup key: expected base64 of 32 bytes. Any output file is no copy of the key; delete it." >&2
      exit 1
    fi
    printf '%s\n' "$key"
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
    image="$(helper_image)"
    mkdir -p "$dir"
    docker run -i --rm --user 0 --network none -v "$dir:/key" --entrypoint /bin/sh "$image" -c '
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
