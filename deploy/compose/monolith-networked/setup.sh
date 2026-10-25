#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")"
usage() {
  echo 'Usage: setup.sh [--cloudflare [--allow-direct-origin]] <release-version> <hostname> <absolute-data-directory>' >&2
  exit 2
}
edge=public
allow_direct=false
while [ "$#" -gt 0 ]; do
  case "$1" in
    --cloudflare) edge=cloudflare; shift ;;
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
version="$1"
domain="$2"
data_dir="$3"
case "$version" in *[!0-9A-Za-z._-]*|'') echo 'Invalid version' >&2; exit 2;; esac
case "$domain" in *[!0-9A-Za-z.-]*|'') echo 'Invalid hostname' >&2; exit 2;; esac
case "$data_dir" in /*) ;; *) echo 'Data directory must be absolute' >&2; exit 2;; esac
case "$data_dir" in *[!0-9A-Za-z._/-]*) echo 'Data directory contains unsupported characters' >&2; exit 2;; esac
if [ -e .env ]; then echo '.env already exists; keep its owner code and edit the version for an update.' >&2; exit 2; fi
mkdir -p "$data_dir/workspace" "$data_dir/projects" "$data_dir/home" "$data_dir/caddy-data" "$data_dir/caddy-config" "$data_dir/origin-pull"
chmod 711 "$data_dir"
chmod 700 "$data_dir/workspace" "$data_dir/projects" "$data_dir/home"
chmod 755 "$data_dir/caddy-data" "$data_dir/caddy-config"
chmod 755 "$data_dir/origin-pull"
if [ ! -d "$data_dir/workspace/.git" ]; then
  git -C "$data_dir/workspace" init -q -b main
  git -C "$data_dir/workspace" -c user.name='Studio Installer' -c user.email='installer@studio.invalid' -c commit.gpgsign=false commit -qm 'Initialize Studio workspace' --allow-empty
fi
if [ "$(id -u)" -eq 0 ]; then
  chown -R 10001:10001 "$data_dir/workspace" "$data_dir/projects" "$data_dir/home"
else
  docker run --rm --user 0 -v "$data_dir:/site" --entrypoint chown caddy:2-alpine \
    -R 10001:10001 /site/workspace /site/projects /site/home
fi
code="$(openssl rand -hex 24)"
umask 077
cat > .env <<EOF
STUDIO_VERSION=$version
STUDIO_DOMAIN=$domain
STUDIO_DATA_DIR=$data_dir
STUDIO_BOOTSTRAP_CODE=$code
STUDIO_CADDYFILE=./Caddyfile.$edge
STUDIO_PUBLIC_CLOUDFLARE_EDGE=true
STUDIO_CLOUDFLARE_ALLOW_DIRECT=$allow_direct
EOF
printf 'Installer owner code: %s\nStore it until the first owner signs in. It is accepted once.\n' "$code"
if [ "$edge" = cloudflare ]; then
  echo 'Cloudflare mode: create an A record only (no AAAA) and set SSL/TLS mode Full (strict).'
  echo 'Install the account-specific origin-pull CA and Docker firewall before starting the stack.'
fi
