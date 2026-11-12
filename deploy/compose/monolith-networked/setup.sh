#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")"
usage() {
  echo 'Usage: setup.sh [--private | --cloudflare [--allow-direct-origin]] <release-version> <hostname> <absolute-data-directory>' >&2
  exit 2
}
edge=public
allow_direct=false
while [ "$#" -gt 0 ]; do
  case "$1" in
    --cloudflare) edge=cloudflare; shift ;;
    --private) edge=private; shift ;;
    --allow-direct-origin) allow_direct=true; shift ;;
    --) shift; break ;;
    -*) usage ;;
    *) break ;;
  esac
done
if [ "$#" -ne 3 ]; then usage; fi
if [ "$allow_direct" = true ] && [ "$edge" != cloudflare ]; then
  echo '--allow-direct-origin applies only with --cloudflare' >&2
  exit 2
fi
if [ "$edge" = private ]; then
  public_edge=false
  restart_policy=unless-stopped
else
  public_edge=true
  restart_policy=on-failure
fi
if [ "${STUDIO_PUBLIC_CLOUDFLARE_EDGE+x}" = x ]; then
  case "$STUDIO_PUBLIC_CLOUDFLARE_EDGE" in
    true|false) ;;
    *) echo 'STUDIO_PUBLIC_CLOUDFLARE_EDGE must be exactly true or false' >&2; exit 2 ;;
  esac
  if [ "$STUDIO_PUBLIC_CLOUDFLARE_EDGE" != "$public_edge" ]; then
    echo 'STUDIO_PUBLIC_CLOUDFLARE_EDGE conflicts with the selected setup mode' >&2
    exit 2
  fi
fi
version="$1"
domain="$2"
data_dir="$3"
case "$version" in *[!0-9A-Za-z._-]*|'') echo 'Invalid version' >&2; exit 2;; esac
case "$domain" in *[!0-9A-Za-z.-]*|'') echo 'Invalid hostname' >&2; exit 2;; esac
case "$data_dir" in /*) ;; *) echo 'Data directory must be absolute' >&2; exit 2;; esac
case "$data_dir" in *[!0-9A-Za-z._/-]*) echo 'Data directory contains unsupported characters' >&2; exit 2;; esac
for key in STUDIO_API_DIGEST STUDIO_WEB_DIGEST STUDIO_EDGE_DIGEST STUDIO_OPERATIONS_SERVER_DIGEST STUDIO_OPERATIONS_AGENT_DIGEST; do
  value="${!key:-}"
  if [[ ! "$value" =~ ^[0-9a-f]{64}$ ]]; then
    echo "$key must be a 64-character lowercase image digest" >&2
    exit 2
  fi
done
if [[ ! "${STUDIO_ACCESS_AUTH_URL:-}" =~ ^https://[A-Za-z0-9.-]+(:[0-9]+)?$ ]]; then
  echo 'STUDIO_ACCESS_AUTH_URL must be an HTTPS origin' >&2
  exit 2
fi
if [ "${STUDIO_ACCESS_MFA_ENFORCED:-}" != true ]; then
  echo 'STUDIO_ACCESS_MFA_ENFORCED=true is required after provider MFA policy is verified' >&2
  exit 2
fi
# The secret store's key-encryption key. It lives outside the data directory so
# that a copy of the data never carries the key that opens its secrets.
key_file="${STUDIO_SECRETS_KEY_FILE:-$PWD/keys/secrets.kek}"
host_ca_dir="${STUDIO_HOST_CA_DIR:-$PWD/keys/host-ca}"
# The backup key encrypts every scheduled backup (AGT-3574). Like the secret
# store key it lives outside the data directory and is escrowed by the owner.
backup_key_dir="${STUDIO_BACKUP_KEY_DIR:-$PWD/keys/backup}"
backup_target_dir="$PWD/backup-target"
case "$key_file" in /*) ;; *) echo 'STUDIO_SECRETS_KEY_FILE must be absolute' >&2; exit 2;; esac
case "$key_file" in *[!0-9A-Za-z._/-]*) echo 'STUDIO_SECRETS_KEY_FILE contains unsupported characters' >&2; exit 2;; esac
case "${key_file%/}/" in "${data_dir%/}"/*) echo 'STUDIO_SECRETS_KEY_FILE must lie outside the data directory' >&2; exit 2;; esac
if [ -e .env ]; then echo '.env already exists; keep its owner code and edit the version for an update.' >&2; exit 2; fi
case "$host_ca_dir" in /*) ;; *) echo 'STUDIO_HOST_CA_DIR must be absolute' >&2; exit 2;; esac
case "$host_ca_dir" in *[!0-9A-Za-z._/-]*) echo 'STUDIO_HOST_CA_DIR contains unsupported characters' >&2; exit 2;; esac
case "${host_ca_dir%/}/" in "${data_dir%/}"/*) echo 'STUDIO_HOST_CA_DIR must lie outside the data directory' >&2; exit 2;; esac
case "$backup_key_dir" in /*) ;; *) echo 'STUDIO_BACKUP_KEY_DIR must be absolute' >&2; exit 2;; esac
case "$backup_key_dir" in *[!0-9A-Za-z._/-]*) echo 'STUDIO_BACKUP_KEY_DIR contains unsupported characters' >&2; exit 2;; esac
case "${backup_key_dir%/}/" in "${data_dir%/}"/*) echo 'STUDIO_BACKUP_KEY_DIR must lie outside the data directory' >&2; exit 2;; esac
mkdir -p "$data_dir/workspace" "$data_dir/projects" "$data_dir/home/.local/share" "$data_dir/api-tmp" "$data_dir/host-certificates" "$data_dir/backups" "$data_dir/caddy-data" "$data_dir/caddy-config" "$data_dir/frontend-data" "$data_dir/frontend-config" "$data_dir/origin-pull"
chmod 711 "$data_dir"
chmod 700 "$data_dir/workspace" "$data_dir/projects" "$data_dir/home" "$data_dir/api-tmp" "$data_dir/host-certificates" "$data_dir/backups"
chmod 700 "$data_dir/caddy-data" "$data_dir/caddy-config" "$data_dir/frontend-data" "$data_dir/frontend-config"
chmod 755 "$data_dir/origin-pull"
if [ ! -d "$data_dir/workspace/.git" ]; then
  git -C "$data_dir/workspace" init -q -b main
  # .security/ holds access state and secrets; it stays out of every commit.
  printf '%s\n' '/.security/' > "$data_dir/workspace/.gitignore"
  git -C "$data_dir/workspace" add .gitignore
  git -C "$data_dir/workspace" -c user.name='Studio Installer' -c user.email='installer@studio.invalid' -c commit.gpgsign=false commit -qm 'Initialize Studio workspace'
fi
exclude_file="$(git -C "$data_dir/workspace" rev-parse --git-path info/exclude)"
case "$exclude_file" in /*) ;; *) exclude_file="$data_dir/workspace/$exclude_file";; esac
mkdir -p "$(dirname "$exclude_file")"
grep -qxF '/.security/' "$exclude_file" 2>/dev/null || printf '%s\n' '/.security/' >> "$exclude_file"
key_dir="$(dirname "$key_file")"
if [ -e "$key_file" ]; then
  echo "Keeping the existing secret store key $key_file." >&2
else
  (umask 077 && mkdir -p "$key_dir" && openssl rand -base64 32 > "$key_file")
fi
chmod 600 "$key_file"
if [ ! -e "$host_ca_dir/ca" ]; then
  (umask 077 && mkdir -p "$host_ca_dir" && ssh-keygen -q -t ed25519 -N '' -f "$host_ca_dir/ca")
fi
chmod 700 "$host_ca_dir"
chmod 600 "$host_ca_dir/ca"
if [ -e "$backup_key_dir/backup.key" ]; then
  echo "Keeping the existing backup key $backup_key_dir/backup.key." >&2
else
  (umask 077 && mkdir -p "$backup_key_dir" && openssl rand -base64 32 > "$backup_key_dir/backup.key")
fi
chmod 700 "$backup_key_dir"
chmod 600 "$backup_key_dir/backup.key"
(umask 077 && mkdir -p "$backup_target_dir")
if [ "$(id -u)" -eq 0 ]; then
  chown -R 10001:10001 "$data_dir/workspace" "$data_dir/projects" "$data_dir/home" "$data_dir/api-tmp" "$data_dir/host-certificates" "$data_dir/backups" "$data_dir/caddy-data" "$data_dir/caddy-config" "$data_dir/frontend-data" "$data_dir/frontend-config" "$host_ca_dir" "$key_file" "$backup_key_dir" "$backup_target_dir"
else
  docker run --rm --user 0 -v "$data_dir:/site" -v "$key_dir:/keys" -v "$host_ca_dir:/host-ca" -v "$backup_key_dir:/backup-key" -v "$backup_target_dir:/backup-target" --entrypoint sh "caddy@sha256:$STUDIO_EDGE_DIGEST" -c \
    'chown -R 10001:10001 /site/workspace /site/projects /site/home /site/api-tmp /site/host-certificates /site/backups /site/caddy-data /site/caddy-config /site/frontend-data /site/frontend-config /host-ca "/keys/$1" /backup-key /backup-target' \
    sh "$(basename "$key_file")"
fi
code="$(openssl rand -hex 24)"
umask 077
cat > .env <<EOF
STUDIO_VERSION=$version
STUDIO_DOMAIN=$domain
QUALITY_DOMAIN=quality-$domain
STUDIO_DATA_DIR=$data_dir
STUDIO_SECRETS_KEY_FILE=$key_file
STUDIO_HOST_CA_DIR=$host_ca_dir
STUDIO_BOOTSTRAP_CODE=$code
STUDIO_CADDYFILE=./Caddyfile.$edge
STUDIO_CLOUDFLARE_ALLOW_DIRECT=$allow_direct
STUDIO_PUBLIC_CLOUDFLARE_EDGE=$public_edge
STUDIO_RESTART_POLICY=$restart_policy
STUDIO_BACKUP_KEY_DIR=$backup_key_dir
STUDIO_BACKUP_TARGET=none
STUDIO_BACKUP_DESTINATION=
STUDIO_API_DIGEST=$STUDIO_API_DIGEST
STUDIO_WEB_DIGEST=$STUDIO_WEB_DIGEST
STUDIO_EDGE_DIGEST=$STUDIO_EDGE_DIGEST
STUDIO_OPERATIONS_SERVER_DIGEST=$STUDIO_OPERATIONS_SERVER_DIGEST
STUDIO_OPERATIONS_AGENT_DIGEST=$STUDIO_OPERATIONS_AGENT_DIGEST
STUDIO_ACCESS_AUTH_URL=$STUDIO_ACCESS_AUTH_URL
STUDIO_ACCESS_MFA_ENFORCED=true
EOF
printf 'Installer owner code: %s\nStore it until the first owner signs in. It is accepted once.\n' "$code"
printf 'Secret store key: %s\nCopy it to a safe place outside the data backups; a restore needs it.\n' "$key_file"
printf 'Backup key: %s\nThe API backs up every 6 hours with it; escrow a copy apart from the backups and the secret store key.\n' "$backup_key_dir/backup.key"
printf 'Host CA: %s\nEscrow the private key and its .pub file apart from the data backups. A restore needs this CA for enrolled hosts.\n' "$host_ca_dir/ca"
echo 'Backups stay on this host until an off-host target (STUDIO_BACKUP_TARGET) is set; see docs/operations/setup/studio-backup-restore.md.'
if [ "$edge" = cloudflare ]; then
  echo 'Cloudflare mode: create an A record only (no AAAA) and set SSL/TLS mode Full (strict).'
  echo 'Install the account-specific origin-pull CA and Docker firewall before starting the stack.'
fi
