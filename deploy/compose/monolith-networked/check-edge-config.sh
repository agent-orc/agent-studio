#!/usr/bin/env bash
set -euo pipefail

stack_dir="$(cd "$(dirname "$0")" && pwd)"
release_dir="${1:?Usage: check-edge-config.sh <matching-release-stack-directory>}"
release_dir="$(cd "$release_dir" && pwd)"

for file in compose.yaml Caddyfile.public Caddyfile.cloudflare Caddyfile.frontend cloudflare-ips.caddy; do
  if ! cmp -s "$stack_dir/$file" "$release_dir/$file"; then
    echo "Stale $file; copy it from $release_dir" >&2
    exit 1
  fi
done

for edge_file in Caddyfile.public Caddyfile.cloudflare; do
  if ! grep -Eq 'client_auth[[:space:]]*\{' "$stack_dir/$edge_file" \
    || ! grep -Fq 'mode require_and_verify' "$stack_dir/$edge_file" \
    || ! grep -Fq 'trust_pool file /etc/caddy/origin-pull/ca.pem' "$stack_dir/$edge_file"; then
    echo "$edge_file must verify the account-specific origin-pull certificate" >&2
    exit 1
  fi
done
if ! grep -Fq 'Security__TrustedProxyAddresses: 172.30.83.3,172.30.83.2' "$stack_dir/compose.yaml" \
  || ! grep -Fq 'trusted_proxies static 172.30.83.2' "$stack_dir/Caddyfile.frontend" \
  || ! grep -Fq 'handle @health {' "$stack_dir/Caddyfile.frontend" \
  || grep -Fq 'respond @health 200' "$stack_dir/Caddyfile.frontend" \
  || ! grep -Fq 'reverse_proxy api:5031' "$stack_dir/Caddyfile.frontend" \
  || ! grep -Fq 'client_ip_headers CF-Connecting-IP' "$stack_dir/Caddyfile.public" \
  || grep -Fq 'client_ip_headers X-Forwarded-For' "$stack_dir/Caddyfile.public" \
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
  case "$(sed -n 's/^STUDIO_CADDYFILE=//p' "$stack_dir/.env" | tail -n 1)" in
    ./Caddyfile.public|./Caddyfile.cloudflare) ;;
    *) echo 'Networked cutover requires a verified public or Cloudflare Caddyfile' >&2; exit 1 ;;
  esac
  if [ "$(sed -n 's/^STUDIO_PUBLIC_CLOUDFLARE_EDGE=//p' "$stack_dir/.env" | tail -n 1)" != true ]; then
    echo 'Public cutover requires STUDIO_PUBLIC_CLOUDFLARE_EDGE=true' >&2
    exit 1
  fi
  case "$(sed -n 's/^STUDIO_RESTART_POLICY=//p' "$stack_dir/.env" | tail -n 1)" in
    ''|on-failure) ;;
    *) echo 'Public cutover requires STUDIO_RESTART_POLICY=on-failure so the firewall starts first' >&2; exit 1 ;;
  esac
  if [ -z "$data_dir" ] || [ ! -s "$data_dir/origin-pull/ca.pem" ]; then
    echo 'Missing account-specific origin-pull CA certificate' >&2
    exit 1
  fi
  ca_file="$data_dir/origin-pull/ca.pem"
  if [ "$(grep -c -- '-----BEGIN CERTIFICATE-----' "$ca_file" || true)" -ne 1 ] \
    || [ "$(grep -c -- '-----END CERTIFICATE-----' "$ca_file" || true)" -ne 1 ]; then
    echo 'Origin-pull ca.pem must contain exactly one certificate' >&2
    exit 1
  fi
  subject="$(openssl x509 -in "$data_dir/origin-pull/ca.pem" -noout -subject -nameopt RFC2253)" || {
    echo 'Invalid origin-pull CA certificate' >&2
    exit 1
  }
  if grep -Eiq '(^|[=,])CN=origin-pull\.cloudflare\.net(,|$)' <<< "$subject"; then
    echo 'Cloudflare shared origin-pull CA is not account-specific' >&2
    exit 1
  fi
  if [ -s "$data_dir/origin-pull/leaf.pem" ] \
    && ! openssl x509 -in "$data_dir/origin-pull/leaf.pem" -noout -checkend 2592000 > /dev/null; then
    echo 'WARNING: origin-pull leaf certificate expires within 30 days; rotate it in Cloudflare' >&2
  elif [ ! -s "$data_dir/origin-pull/leaf.pem" ]; then
    echo 'WARNING: origin-pull leaf certificate not present for expiry monitoring' >&2
  fi
fi
echo 'Edge files and origin-pull CA are current.'
