#!/usr/bin/env bash
# Restores a backup made by backup.sh into a fresh, not yet started stack.
#
#   restore.sh --identity <age-identity-file> [--secret-key <key-copy>] <archive>
#
# Run setup.sh for the new stack first and do not start it. The age identity
# and the secret store key copy come from where the operator keeps them, not
# from this host; remove them again after the restore. Project checkouts are
# cloned from their remotes with this account's Git credentials, then the
# Studio refs from the archive are put back. Afterwards run
# `docker compose up -d` and sign in with an existing account.
set -euo pipefail

stack_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$stack_dir"

usage() {
  echo 'Usage: restore.sh --identity <age-identity-file> [--secret-key <key-copy>] <archive>' >&2
  exit 2
}
fail() { echo "Restore failed: $*" >&2; exit 1; }

identity=''
secret_key=''
while [ "$#" -gt 0 ]; do
  case "$1" in
    --identity) [ "$#" -ge 2 ] || usage; identity="$2"; shift 2 ;;
    --secret-key) [ "$#" -ge 2 ] || usage; secret_key="$2"; shift 2 ;;
    --) shift; break ;;
    -*) usage ;;
    *) break ;;
  esac
done
[ "$#" -eq 1 ] && [ -n "$identity" ] || usage
archive="$1"

env_value() { sed -n "s/^$1=//p" .env | tail -n 1; }
[ -f .env ] || fail 'no .env in the stack directory; run setup.sh for the new stack first'
data_dir="$(env_value STUDIO_DATA_DIR)"
version="$(env_value STUDIO_VERSION)"
[ -n "$data_dir" ] && [ -n "$version" ] || fail 'STUDIO_DATA_DIR and STUDIO_VERSION must be set in .env'
image="ghcr.io/agent-orc/agent-studio-api:v$version"
command -v age > /dev/null || fail 'age is not installed (apt install age)'
[ -r "$identity" ] || fail "cannot read the age identity $identity"
[ -r "$archive" ] || fail "cannot read the archive $archive"
if [ -n "$(docker compose ps --status running -q 2>/dev/null)" ]; then
  fail 'the stack is running; restore needs a fresh stack from setup.sh that has not been started'
fi

if [ -f "$archive.sha256" ]; then
  (cd "$(dirname "$archive")" && sha256sum --check --quiet "$(basename "$archive").sha256") \
    || fail 'the archive does not match its .sha256 file'
fi

apply() {
  docker run -i --rm --user 10001:10001 --network none \
    -v "$stack_dir/restore-apply.sh:/usr/local/bin/restore-apply.sh:ro" "$@"
}

# The manifest is at the start of the archive; reading it stops early, so
# only the reader's exit status counts (age then ends on a closed pipe).
manifest="$(set +o pipefail; age --decrypt --identity "$identity" "$archive" 2>/dev/null \
  | apply --entrypoint /bin/bash "$image" /usr/local/bin/restore-apply.sh manifest)" \
  || fail 'cannot decrypt the archive with this identity, or it is not a Studio backup'
manifest_value() { printf '%s\n' "$manifest" | sed -n '1,/^--$/p' | sed -n "s/^$1=//p"; }
[ "$(manifest_value format)" = 1 ] || fail 'unsupported backup format'
echo "Backup of $(manifest_value domain) from $(manifest_value created), Studio $(manifest_value studio_version)"

# The sealed secrets in the workspace open only with the key that sealed them.
expected_key="$(manifest_value secret_key_id)"
if [ "$expected_key" != none ]; then
  [ -n "$secret_key" ] || fail "the backup needs the secret store key with id $expected_key; pass its copy with --secret-key"
  actual_key="$(bash "$stack_dir/secret-key.sh" id "$secret_key")"
  [ "$actual_key" = "$expected_key" ] || fail "the secret store key copy has id $actual_key, the backup needs $expected_key"
fi

# Checkouts come back from their remotes. Clone them before touching the
# stack so a missing credential stops the restore while it is still empty.
clones="$(mktemp -d)"
trap 'rm -rf "$clones"' EXIT
chmod 755 "$clones"
umask 022
while IFS=$'\t' read -r index root origin branch bundle; do
  [ -n "$index" ] && [ "$origin" != - ] || continue
  echo "Cloning $origin for $root"
  git clone --quiet "$origin" "$clones/$index" || fail "cannot clone $origin; give this account read access to it and run restore.sh again"
done < <(printf '%s\n' "$manifest" | sed '1,/^--$/d')

age --decrypt --identity "$identity" "$archive" \
  | apply -v "$data_dir/workspace:/data/workspace" -v "$data_dir/projects:/data/projects" \
      -v "$clones:/restore-clones:ro" --entrypoint /bin/bash "$image" /usr/local/bin/restore-apply.sh unpack \
  || fail 'unpacking the archive failed; the stack is back to the state setup.sh left, so fix the cause and run restore.sh again'

if [ "$expected_key" != none ]; then
  bash "$stack_dir/secret-key.sh" install "$data_dir" "$secret_key"
  echo "Installed the secret store key $expected_key"
fi
echo 'Restore complete. Start the stack with docker compose up -d and sign in with an existing account.'
