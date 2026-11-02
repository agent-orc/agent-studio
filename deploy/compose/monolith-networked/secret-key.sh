#!/usr/bin/env bash
# Manages the secret store key-encryption key of the networked stack.
#
# The key lives at STUDIO_SECRETS_KEY_FILE outside the data directory, and the
# API reads it read-only through Secrets__KeyFile. Backups never contain it:
# keep the copy printed by `print` in a separate safe place (password manager
# or offline media), apart from the backup archives and their age identity.
#
#   secret-key.sh create  <data-dir>            create the key when it is missing
#   secret-key.sh print   <data-dir>            print the key for its separate copy
#   secret-key.sh id      <key-file>            print the key fingerprint
#   secret-key.sh install <data-dir> <key-file> replace the key with a saved copy
#
# The key belongs to UID 10001, so file access runs in a one-shot container;
# the operator account needs only Docker access.
set -euo pipefail

helper_image="${STUDIO_HELPER_IMAGE:-caddy:2-alpine}"
stack_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

usage() {
  echo 'Usage: secret-key.sh create <data-dir> | print <data-dir> | id <key-file> | install <data-dir> <key-file>' >&2
  exit 2
}

absolute_dir() {
  case "$1" in /*) ;; *) echo 'Data directory must be absolute' >&2; exit 2;; esac
}

key_path() {
  local data_dir="$1" configured
  [ -f "$stack_dir/.env" ] || { echo 'Run setup.sh before managing the secret store key' >&2; exit 2; }
  configured="$(sed -n 's/^STUDIO_SECRETS_KEY_FILE=//p' "$stack_dir/.env" | tail -n 1)"
  [ -n "$configured" ] || { echo 'STUDIO_SECRETS_KEY_FILE is missing from .env' >&2; exit 2; }
  case "$configured" in /*) ;; *) echo 'STUDIO_SECRETS_KEY_FILE must be absolute' >&2; exit 2;; esac
  case "${configured%/}/" in "${data_dir%/}"/*)
    echo 'STUDIO_SECRETS_KEY_FILE must lie outside the data directory' >&2; exit 2;;
  esac
  printf '%s' "$configured"
}

# Same derivation as KeyEncryptionKey.Id: SHA-256 over a fixed prefix and the raw key bytes.
fingerprint() {
  { printf 'agent-studio/kek-id/v1|'; tr -d '\r\n' | base64 -d; } | sha256sum | cut -c1-16
}

# Accepts only base64 of exactly 32 bytes, the format KeyEncryptionKey requires.
validate_key_file() {
  local size
  size="$(tr -d '\r\n' < "$1" | base64 -d 2>/dev/null | wc -c)" || size=0
  if [ "$size" -ne 32 ]; then
    echo "$1 is not a secret store key: expected base64 of 32 bytes" >&2
    exit 1
  fi
}

[ "$#" -ge 1 ] || usage
command="$1"
shift
case "$command" in
  create)
    [ "$#" -eq 1 ] || usage
    absolute_dir "$1"
    file="$(key_path "$1")"
    key_dir="$(dirname "$file")"
    mkdir -p "$key_dir"
    docker run --rm --user 0 --network none -v "$key_dir:/keys" --entrypoint /bin/sh "$helper_image" -c '
      set -e
      umask 077
      if [ ! -s "/keys/$1" ]; then
        head -c 32 /dev/urandom | base64 > "/keys/$1.new"
        mv "/keys/$1.new" "/keys/$1"
        echo "Created the secret store key." >&2
      fi
      chown 10001:10001 "/keys/$1"
      chmod 600 "/keys/$1"' sh "$(basename "$file")"
    ;;
  print)
    [ "$#" -eq 1 ] || usage
    absolute_dir "$1"
    file="$(key_path "$1")"
    docker run --rm --user 10001:10001 --network none -v "$file:/key:ro" --entrypoint cat "$helper_image" /key
    ;;
  id)
    [ "$#" -eq 1 ] || usage
    validate_key_file "$1"
    fingerprint < "$1"
    ;;
  install)
    [ "$#" -eq 2 ] || usage
    absolute_dir "$1"
    validate_key_file "$2"
    file="$(key_path "$1")"
    key_dir="$(dirname "$file")"
    docker run -i --rm --user 0 --network none -v "$key_dir:/keys" --entrypoint /bin/sh "$helper_image" -c '
      set -e
      umask 077
      tr -d "\r\n" > "/keys/$1.new"
      echo >> "/keys/$1.new"
      mv "/keys/$1.new" "/keys/$1"
      chown 10001:10001 "/keys/$1"
      chmod 600 "/keys/$1"' sh "$(basename "$file")" < "$2"
    ;;
  *) usage ;;
esac
