#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
stack="$repo_root/deploy/compose/monolith-networked"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/stack" "$work/data/origin-pull"
cp "$stack"/{compose.yaml,Caddyfile.public,Caddyfile.cloudflare,Caddyfile.frontend,cloudflare-ips.caddy,check-edge-config.sh} "$work/stack/"
printf 'STUDIO_CADDYFILE=./Caddyfile.cloudflare\nSTUDIO_PUBLIC_CLOUDFLARE_EDGE=true\nSTUDIO_DATA_DIR=%s/data\n' "$work" > "$work/stack/.env"

make_ca() {
  openssl req -x509 -newkey rsa:2048 -nodes -days 2 \
    -subj "/CN=$1" -keyout "$work/ca.key" \
    -out "$work/data/origin-pull/ca.pem" > /dev/null 2>&1
}

make_ca origin-pull.cloudflare.net
if bash "$work/stack/check-edge-config.sh" "$stack" > "$work/check.log" 2>&1; then
  echo 'Shared Cloudflare origin-pull CA was accepted' >&2
  exit 1
fi
grep -q 'shared origin-pull CA' "$work/check.log"

make_ca studio-account-ca
bash "$work/stack/check-edge-config.sh" "$stack" > "$work/check.log" 2>&1
grep -q 'Edge files and origin-pull CA are current.' "$work/check.log"

printf 'STUDIO_PUBLIC_CLOUDFLARE_EDGE=false\n' >> "$work/stack/.env"
if bash "$work/stack/check-edge-config.sh" "$stack" > "$work/check.log" 2>&1; then
  echo 'Public edge was accepted with a false effective login guard' >&2
  exit 1
fi
grep -q 'STUDIO_PUBLIC_CLOUDFLARE_EDGE=true' "$work/check.log"
sed -i '$d' "$work/stack/.env"

openssl req -x509 -newkey rsa:2048 -nodes -days 1 \
  -subj '/CN=studio.test' -keyout "$work/leaf.key" \
  -out "$work/data/origin-pull/leaf.pem" > /dev/null 2>&1
bash "$work/stack/check-edge-config.sh" "$stack" > "$work/check.log" 2>&1
grep -q 'WARNING: origin-pull leaf certificate expires within 30 days' "$work/check.log"
echo 'Account-specific origin-pull CA and leaf expiry checks passed.'
