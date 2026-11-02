#!/usr/bin/env bash
# Encrypted off-host backup of the networked Studio stack.
#
#   backup.sh [run]   create one backup and send it to the destination
#   backup.sh status  show whether backups are on and the last result
#
# Settings come from .env next to this script:
#   STUDIO_BACKUP_DESTINATION  empty: backups are off. Otherwise either
#                              ssh://user@host[:port][/path] (an rsync target whose
#                              key is restricted to `rrsync -wo`, write-only), or an
#                              absolute directory on a mounted remote filesystem
#                              that this host may write but not read back.
#   STUDIO_BACKUP_RECIPIENTS   age recipients file (default ./backup-recipients.txt):
#                              public keys only; the identity stays off this host.
#   STUDIO_BACKUP_SSH_KEY      SSH key for an ssh:// destination (default ./backup-ssh.key)
#   STUDIO_BACKUP_KNOWN_HOSTS  pinned host key for an ssh:// destination (default ./backup-known-hosts)
#   STUDIO_BACKUP_PAUSE_API    true (default) pauses the API while the archive is read
#
# The archive holds the workspace (cards, metadata, access state, sealed
# secrets), task storage under the projects directory, and the
# refs/agent-studio/* refs of each project checkout. The secret store key is
# not in it. Restore with restore.sh; see docs/operations/setup/studio-backup-restore.md.
set -euo pipefail

stack_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$stack_dir"
state_file="$stack_dir/.backup-last"

# Reads KEY=value from .env without executing it.
env_value() {
  [ -f .env ] || return 0
  sed -n "s/^$1=//p" .env | tail -n 1
}

setting() {
  local value="${!1:-}"
  [ -n "$value" ] || value="$(env_value "$1")"
  printf '%s' "${value:-${2:-}}"
}

resolve_path() {
  case "$1" in /*) printf '%s' "$1" ;; *) printf '%s/%s' "$stack_dir" "${1#./}" ;; esac
}

record() {
  printf '%s %s %s\n' "$1" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$2" > "$state_file"
}

fail() {
  echo "Backup failed: $*" >&2
  record failed "$*"
  exit 1
}

off_message='Backups are off: STUDIO_BACKUP_DESTINATION is not set in .env. The stack runs normally without off-host backups; see docs/operations/setup/studio-backup-restore.md to turn them on.'

if [ ! -f .env ]; then
  echo 'No .env in the stack directory; run setup.sh first.' >&2
  exit 2
fi
destination="$(setting STUDIO_BACKUP_DESTINATION)"
recipients="$(resolve_path "$(setting STUDIO_BACKUP_RECIPIENTS ./backup-recipients.txt)")"

case "${1:-run}" in
  status)
    if [ -z "$destination" ]; then
      echo "$off_message"
    else
      echo "Backups are on: destination $destination, recipients $recipients"
    fi
    if [ -f "$state_file" ]; then
      echo "Last result: $(cat "$state_file")"
    else
      echo 'Last result: none'
    fi
    exit 0
    ;;
  run) ;;
  *) echo 'Usage: backup.sh [run|status]' >&2; exit 2 ;;
esac

if [ -z "$destination" ]; then
  echo "$off_message"
  exit 0
fi

data_dir="$(setting STUDIO_DATA_DIR)"
version="$(setting STUDIO_VERSION)"
domain="$(setting STUDIO_DOMAIN)"
key_file="$(setting STUDIO_SECRETS_KEY_FILE)"
[ -n "$data_dir" ] && [ -n "$version" ] && [ -n "$key_file" ] || fail 'STUDIO_DATA_DIR, STUDIO_VERSION and STUDIO_SECRETS_KEY_FILE must be set in .env'
[ -f "$key_file" ] || fail "secret store key is missing at $key_file"
command -v age > /dev/null || fail 'age is not installed (apt install age)'
[ -s "$recipients" ] || fail "no age recipients file at $recipients"
# The private half must not be on this host: refuse anything that looks like one.
if grep -Eq 'AGE-SECRET-KEY-|PRIVATE KEY' "$recipients"; then
  fail "$recipients holds a private key; put only public recipients there and keep the identity off this host"
fi

case "$destination" in
  ssh://*)
    command -v rsync > /dev/null || fail 'rsync is not installed'
    ssh_key="$(resolve_path "$(setting STUDIO_BACKUP_SSH_KEY ./backup-ssh.key)")"
    known_hosts="$(resolve_path "$(setting STUDIO_BACKUP_KNOWN_HOSTS ./backup-known-hosts)")"
    [ -r "$ssh_key" ] || fail "no SSH key at $ssh_key"
    [ -s "$known_hosts" ] || fail "no pinned host key at $known_hosts"
    remote="${destination#ssh://}"
    authority="${remote%%/*}"
    remote_path=''
    [ "$authority" = "$remote" ] || remote_path="${remote#*/}"
    port=22
    case "$authority" in *:*) port="${authority##*:}"; authority="${authority%:*}" ;; esac
    case "$authority" in *@*) ;; *) fail 'an ssh:// destination needs user@host' ;; esac
    ;;
  /*) [ -d "$destination" ] || fail "destination directory $destination does not exist (is the remote filesystem mounted?)" ;;
  *) fail 'STUDIO_BACKUP_DESTINATION must be ssh://user@host[:port][/path] or an absolute directory' ;;
esac

# One backup at a time; a second timer run waits instead of overlapping.
exec 9> "$stack_dir/.backup.lock"
if command -v flock > /dev/null; then flock 9; fi

work="$(mktemp -d)"
paused=false
cleanup() {
  if [ "$paused" = true ]; then docker compose unpause api > /dev/null 2>&1 || true; fi
  rm -rf "$work"
}
trap cleanup EXIT

name="studio-backup-${domain:-studio}-$(date -u +%Y%m%dT%H%M%SZ).tar.gz.age"
archive="$work/$name"

# A paused API cannot change the files while they are read, so the archive
# is one consistent point in time. Requests wait during the pause.
if [ "$(setting STUDIO_BACKUP_PAUSE_API true)" = true ] && [ -n "$(docker compose ps --status running -q api 2>/dev/null)" ]; then
  docker compose pause api > /dev/null
  paused=true
fi
set +e
docker run --rm --network none --user 10001:10001 \
  -e STUDIO_VERSION="$version" -e STUDIO_DOMAIN="$domain" \
  -v "$data_dir/workspace:/data/workspace:ro" \
  -v "$data_dir/projects:/data/projects:ro" \
  -v "$key_file:/run/secrets/studio-secrets.kek:ro" \
  -v "$stack_dir/backup-collect.sh:/usr/local/bin/backup-collect.sh:ro" \
  --entrypoint /bin/bash "ghcr.io/agent-orc/agent-studio-api:v$version" /usr/local/bin/backup-collect.sh \
  | age --encrypt --recipients-file "$recipients" --output "$archive"
statuses=("${PIPESTATUS[@]}")
set -e
if [ "$paused" = true ]; then
  docker compose unpause api > /dev/null
  paused=false
fi
[ "${statuses[0]}" -eq 0 ] || fail "collecting the data failed (exit ${statuses[0]})"
[ "${statuses[1]}" -eq 0 ] || fail "encrypting the archive failed (exit ${statuses[1]})"

(cd "$work" && sha256sum "$name" > "$name.sha256")
sha="$(cut -d' ' -f1 "$work/$name.sha256")"
size="$(wc -c < "$archive")"

case "$destination" in
  ssh://*)
    # rsync writes to a temporary name and renames, so a partial upload never
    # looks complete. The checksum goes last and marks the archive as done.
    ssh_command="ssh -i $ssh_key -p $port -o IdentitiesOnly=yes -o BatchMode=yes -o StrictHostKeyChecking=yes -o UserKnownHostsFile=$known_hosts"
    target="$authority:${remote_path:+$remote_path/}"
    rsync -e "$ssh_command" "$archive" "$target" || fail "upload to $destination failed"
    rsync -e "$ssh_command" "$archive.sha256" "$target" || fail "upload of the checksum to $destination failed"
    ;;
  /*)
    cp "$archive" "$destination/.$name.partial" || fail "copy to $destination failed"
    mv "$destination/.$name.partial" "$destination/$name" || fail "rename in $destination failed"
    cp "$archive.sha256" "$destination/$name.sha256" || fail "checksum copy to $destination failed"
    ;;
esac

record ok "$name $size bytes sha256 $sha"
echo "Backup sent: $name ($size bytes, sha256 $sha) to $destination"
