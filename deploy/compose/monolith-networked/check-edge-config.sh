#!/usr/bin/env bash
set -euo pipefail

stack_dir="$(cd "$(dirname "$0")" && pwd)"
release_dir="${1:?Usage: check-edge-config.sh <matching-release-stack-directory>}"
release_dir="$(cd "$release_dir" && pwd)"

for file in compose.yaml Caddyfile.public Caddyfile.frontend; do
  if ! cmp -s "$stack_dir/$file" "$release_dir/$file"; then
    echo "Stale $file; copy it from $release_dir" >&2
    exit 1
  fi
done

if ! grep -Eq 'client_auth[[:space:]]*\{' "$stack_dir/Caddyfile.public" \
  || ! grep -Fq 'mode require_and_verify' "$stack_dir/Caddyfile.public" \
  || ! grep -Fq 'trust_pool file /etc/caddy/origin-pull/ca.pem' "$stack_dir/Caddyfile.public"; then
  echo 'Public Caddyfile must verify the account-specific origin-pull certificate' >&2
  exit 1
fi
if ! grep -Fq 'Security__TrustedProxyAddresses: 172.30.83.3,172.30.83.2' "$stack_dir/compose.yaml" \
  || ! grep -Fq 'trusted_proxies static 172.30.83.2' "$stack_dir/Caddyfile.frontend" \
  || ! grep -Fq 'handle @health {' "$stack_dir/Caddyfile.frontend" \
  || grep -Fq 'respond @health 200' "$stack_dir/Caddyfile.frontend" \
  || ! grep -Fq 'reverse_proxy api:5031' "$stack_dir/Caddyfile.frontend" \
  || ! grep -Fq 'header_up X-Forwarded-For {client_ip}' "$stack_dir/Caddyfile.public"; then
  echo 'The edge, frontend, and API proxy chain do not match' >&2
  exit 1
fi
if ! grep -Fq '0.0.0.0:443:443' "$stack_dir/compose.yaml"; then
  echo 'Public HTTPS port must bind IPv4 only for the Docker firewall contract' >&2
  exit 1
fi

if [ -f "$stack_dir/.env" ]; then
  data_dir="$(sed -n 's/^STUDIO_DATA_DIR=//p' "$stack_dir/.env" | tail -n 1)"
  if [ "$(sed -n 's/^STUDIO_CADDYFILE=//p' "$stack_dir/.env" | tail -n 1)" != './Caddyfile.public' ]; then
    echo 'Networked cutover requires Caddyfile.public' >&2
    exit 1
  fi
  if [ -z "$data_dir" ] || [ ! -s "$data_dir/origin-pull/ca.pem" ]; then
    echo 'Missing account-specific origin-pull CA certificate' >&2
    exit 1
  fi
fi
echo 'Edge files and origin-pull CA are current.'
