#!/usr/bin/env bash
# Restores a scheduled backup (AGT-3574) into a fresh, not yet started stack.
#
#   restore-backup.sh --key <backup-key-copy> --secret-key <secret-key-copy> --host-ca <escrowed-ca-private-key> <archive>
#
# Run setup.sh for the new stack first and do not start it. Fetch the
# archive and its .sha256 file from the off-host target, and bring the
# escrowed backup key, secret store key and host CA copies to the host only for
# the restore. The API image of this stack does the work in a one-shot container:
# it checks the checksum and both key fingerprints, clones every project
# checkout from its recorded origin (through this account's SSH agent when
# SSH_AUTH_SOCK is set), and only then writes the workspace, home, task
# storage, local Git refs, host certificates and Caddy data. The archived stack
# configuration lands in restored-stack/ for comparison; the new .env stays.
# A restore receipt (archive, archive time, restore time, operator, key
# fingerprints) goes into the workspace audit and onto the Operations page.
set -euo pipefail

stack_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$stack_dir"

usage() {
  echo 'Usage: restore-backup.sh --key <backup-key-copy> --secret-key <secret-key-copy> [--host-ca <escrowed-ca-private-key>] <archive>' >&2
  exit 2
}
fail() { echo "Restore failed: $*" >&2; exit 1; }

key=''
secret_key=''
host_ca=''
while [ "$#" -gt 0 ]; do
  case "$1" in
    --key) [ "$#" -ge 2 ] || usage; key="$2"; shift 2 ;;
    --secret-key) [ "$#" -ge 2 ] || usage; secret_key="$2"; shift 2 ;;
    --host-ca) [ "$#" -ge 2 ] || usage; host_ca="$2"; shift 2 ;;
    --) shift; break ;;
    -*) usage ;;
    *) break ;;
  esac
done
[ "$#" -eq 1 ] && [ -n "$key" ] && [ -n "$secret_key" ] || usage
archive="$(realpath "$1")"
key="$(realpath "$key")"
secret_key="$(realpath "$secret_key")"
if [ -n "$host_ca" ]; then host_ca="$(realpath "$host_ca")"; fi

env_value() { sed -n "s/^$1=//p" .env | tail -n 1; }
[ -f .env ] || fail 'no .env in the stack directory; run setup.sh for the new stack first'
data_dir="$(env_value STUDIO_DATA_DIR)"
digest="$(env_value STUDIO_API_DIGEST)"
[ -n "$data_dir" ] && [ -n "$digest" ] || fail 'STUDIO_DATA_DIR and STUDIO_API_DIGEST must be set in .env'
[ -r "$archive" ] || fail "cannot read the archive $archive"
[ -r "$key" ] || fail "cannot read the backup key copy $key"
[ -r "$secret_key" ] || fail "cannot read the secret store key copy $secret_key"
if [ -n "$host_ca" ]; then
  [ -r "$host_ca" ] || fail "cannot read the host CA copy $host_ca"
  [ -r "$host_ca.pub" ] || fail "cannot read the host CA public key copy $host_ca.pub"
  host_ca_dir="$(env_value STUDIO_HOST_CA_DIR)"
  [ -n "$host_ca_dir" ] && [ -f "$host_ca_dir/ca" ] || fail 'STUDIO_HOST_CA_DIR must contain the CA created by setup.sh'
  derived_public="$(ssh-keygen -y -f "$host_ca" 2>/dev/null)" || fail 'the host CA private key is invalid'
  [ "$(printf '%s\n' "$derived_public" | awk '{print $1, $2}')" = "$(awk '{print $1, $2}' "$host_ca.pub")" ] \
    || fail 'the host CA private and public keys do not match'
fi
if [ -n "$(docker compose ps --status running -q 2>/dev/null)" ]; then
  fail 'the stack is running; restore needs a fresh stack from setup.sh that has not been started'
fi

restored_stack="$stack_dir/restored-stack"
[ ! -e "$restored_stack" ] || fail "$restored_stack already exists"
mkdir -p "$restored_stack"
image="ghcr.io/agent-orc/agent-studio-api@sha256:$digest"
volume="studio-restore-$$"
docker volume create "$volume" > /dev/null
trap 'docker volume rm -f "$volume" > /dev/null' EXIT

# The API runs as UID 10001 and reads only owner-only key files: a root
# one-shot hands the inputs and the empty targets to that account.
docker run --rm --user 0 --network none \
  -v "$archive:/inputs/archive:ro" -v "$key:/inputs/backup.key:ro" -v "$secret_key:/inputs/secrets.kek:ro" \
  -v "$volume:/work" -v "$restored_stack:/restored-stack" -v "$data_dir/origin-pull:/origin-pull" \
  --entrypoint /bin/sh "$image" -c '
    set -e
    umask 077
    cp /inputs/archive /inputs/backup.key /inputs/secrets.kek /work/
    chown -R 10001:10001 /work /restored-stack /origin-pull'
if [ -f "$archive.sha256" ]; then
  (cd "$(dirname "$archive")" && sha256sum --check --quiet "$(basename "$archive").sha256") \
    || fail 'the archive does not match its .sha256 file'
fi

agent=()
if [ -n "${SSH_AUTH_SOCK:-}" ] && [ -S "$SSH_AUTH_SOCK" ]; then
  agent=(-v "$SSH_AUTH_SOCK:/run/ssh-agent" -e SSH_AUTH_SOCK=/run/ssh-agent)
fi
docker run --rm --user 10001:10001 "${agent[@]}" \
  -e GIT_SSH_COMMAND='ssh -o StrictHostKeyChecking=accept-new' \
  -v "$volume:/work" \
  -v "$data_dir/workspace:/data/workspace" -v "$data_dir/home:/data/home" -v "$data_dir/projects:/data/projects" \
  -v "$data_dir/host-certificates:/data/host-certificates" -v "$data_dir/caddy-data:/data/caddy-data" \
  -v "$data_dir/origin-pull:/data/origin-pull" -v "$restored_stack:/data/restored-stack" \
  "$image" --studio-backup restore --key /work/backup.key --archive /work/archive --secret-key /work/secrets.kek \
    --actor "offline:$(id -un)@$(hostname)" \
    --target workspace=/data/workspace --target home=/data/home --target projects=/data/projects \
    --target host-certificates=/data/host-certificates --target caddy-data=/data/caddy-data \
    --target origin-pull=/data/origin-pull --target stack=/data/restored-stack \
    --restored-by "$(id -un)@$(hostname)" \
  || { rmdir "$restored_stack" 2>/dev/null || true
       fail 'the restore did not complete; see the message above. The targets are empty again and the workspace repository from setup.sh is back; fix the cause and run restore-backup.sh again.'; }

bash "$stack_dir/secret-key.sh" install "$data_dir" "$secret_key"
bash "$stack_dir/backup-key.sh" install "$key"
if [ -n "$host_ca" ]; then
  if [ -f "$data_dir/host-certificates/ca-fingerprint" ]; then
    expected_ca="$(cat "$data_dir/host-certificates/ca-fingerprint")"
    supplied_ca="$(ssh-keygen -lf "$host_ca.pub" -E sha256 | awk '{print $2}')"
    [ "$expected_ca" = "$supplied_ca" ] || fail 'the escrowed host CA does not match the restored host certificate state'
  fi
  ca_owner="$(stat -c '%u:%g' "$host_ca_dir/ca")"
  install -m 600 "$host_ca" "$host_ca_dir/ca"
  install -m 644 "$host_ca.pub" "$host_ca_dir/ca.pub"
  chown "$ca_owner" "$host_ca_dir/ca" "$host_ca_dir/ca.pub"
else
  echo 'No escrowed host CA supplied. The Operations page will warn if the restored host certificate state does not match this CA.' >&2
fi
echo 'Restore complete. Compare restored-stack/.env with the new .env (domain, edge mode, backup target),'
echo 'then start the stack with docker compose up -d and sign in with an existing account.'
