#!/usr/bin/env bash
set -euo pipefail

stack_dir="$(cd "$(dirname "$0")" && pwd)"
if [ "${1:-}" = --baseline-only ]; then
  stack_dir="$(cd "${2:?Usage: check-edge-config.sh --baseline-only <stack-directory>}" && pwd)"
  baseline_only=true
else
  baseline_only=false
fi
python3 - "$stack_dir" <<'PY'
import pathlib
import re
import sys

stack = pathlib.Path(sys.argv[1])
compose = (stack / 'compose.yaml').read_text()
for service in ('api', 'frontend', 'edge'):
    match = re.search(r'^  ' + service + r':\n(.*?)(?=^  [a-z-]+:|^networks:)',
                      compose, re.M | re.S)
    if not match:
        sys.exit(f'{service}: service missing')
    body = match.group(1)
    checks = (
        ('digest', r'^    image: [^\n]+@sha256:\$\{STUDIO_[A-Z_]+_DIGEST:\?'),
        ('non-root', r"^    user: '10001:1654'$" if service == 'api'
         else r"^    user: '10001:10001'$"),
        ('read-only root', r'^    read_only: true$'),
        ('cap_drop', r'^    cap_drop: \[ALL\]$'),
        ('no-new-privileges', r'^    security_opt: \[no-new-privileges:true\]$'),
    ) + ((('disk-backed /tmp volume', r'^      - \$\{STUDIO_DATA_DIR\}/api-tmp:/tmp$'),) if service == 'api'
         else (('writable tmpfs', r'^    tmpfs: \[/tmp\]$'),))
    for reason, pattern in checks:
        if not re.search(pattern, body, re.M):
            sys.exit(f'{service}: missing {reason}')
    # Agent CLI and build scratch files would count against the API memory limit.
    if service == 'api' and re.search(r'^    tmpfs:', body, re.M):
        sys.exit('api: memory-backed /tmp; mount the disk-backed api-tmp volume instead')
    required = ('/data/workspace', '/data/projects', '/data/home') if service == 'api' else ('/data', '/config')
    for mount in required:
        if not re.search(r'^      - [^\n]+:' + re.escape(mount) + r'$', body, re.M):
            sys.exit(f'{service}: missing writable volume {mount}')
    if service == 'edge' and not re.search(r'^      STUDIO_ACCESS_AUTH_URL:', body, re.M):
        sys.exit('edge: missing access layer URL')
    if service in ('frontend', 'edge'):
        for key, value in (('XDG_CONFIG_HOME', '/config'), ('XDG_DATA_HOME', '/data'), ('HOME', '/tmp')):
            if not re.search(r'^      ' + key + r': ' + value + r'$', body, re.M):
                sys.exit(f'{service}: missing writable {key} path')
    if service == 'edge':
        if not re.search(r"^      net.ipv4.ip_unprivileged_port_start: '0'$", body, re.M):
            sys.exit('edge: missing unprivileged port setting')
for service in ('operations-bootstrap', 'operations-server', 'operations-agent'):
    match = re.search(r'^  ' + service + r':\n(.*?)(?=^  [a-z-]+:|^networks:)', compose, re.M | re.S)
    if not match:
        sys.exit(f'{service}: service missing')
    body = match.group(1)
    image_key = 'STUDIO_OPERATIONS_AGENT_DIGEST' if service == 'operations-agent' else 'STUDIO_OPERATIONS_SERVER_DIGEST'
    if not re.search(r'^    image: [^\n]+@sha256:\$\{' + image_key + r':\?', body, re.M):
        sys.exit(f'{service}: missing digest')
    for reason, pattern in (
        ('read-only root', r'^    read_only: true$'),
        ('cap_drop', r'^    cap_drop: \[ALL\]$'),
        ('no-new-privileges', r'^    security_opt: \[no-new-privileges:true\]$'),
    ):
        if not re.search(pattern, body, re.M):
            sys.exit(f'{service}: missing {reason}')
    if service == 'operations-server' and not re.search(r"^    ports: \['127\.0\.0\.1:5081:5081'\]$", body, re.M):
        sys.exit('operations-server: public port exposure')
    if service != 'operations-server' and re.search(r'^    ports:', body, re.M):
        sys.exit(f'{service}: public port exposure')
addresses = {
    'edge': '172.30.83.2',
    'frontend': '172.30.83.3',
    'api': '172.30.83.4',
    'quality-studio': '172.30.83.5',
    'operations-server': '172.30.83.6',
    'operations-agent': '172.30.83.7',
}
for service, address in addresses.items():
    match = re.search(r'^  ' + service + r':\n(.*?)(?=^  [a-z-]+:|^networks:)', compose, re.M | re.S)
    if not match or not re.search(r'^    networks:\n      website:\n        ipv4_address: ' + re.escape(address) + r'$', match.group(1), re.M):
        sys.exit(f'{service}: address is outside the protected website subnet')
    if not re.search(r'^    restart: \$\{STUDIO_RESTART_POLICY:-on-failure\}$', match.group(1), re.M):
        sys.exit(f'{service}: public restart policy is not firewall-gated')
if not re.search(r'^    network_mode: none$',
                 re.search(r'^  operations-bootstrap:\n(.*?)(?=^  [a-z-]+:|^networks:)', compose, re.M | re.S).group(1), re.M):
    sys.exit('operations-bootstrap: must not join a network')
if 'subnet: 172.30.83.0/24' not in compose or 'com.docker.network.bridge.name: br-studio-web' not in compose:
    sys.exit('website: subnet or bridge is not covered by the Docker firewall')
rules = (stack / 'apply-cloudflare-docker-firewall.sh').read_text()
verifier = (stack / 'verify-cloudflare-docker-firewall.sh').read_text()
installer = (stack / 'install-cloudflare-docker-firewall.sh').read_text()
if '-I DOCKER-USER 1 -d 172.30.83.0/24 -j STUDIO-CF-V4' not in rules or 'br-studio-web' not in rules:
    sys.exit('website: Docker firewall does not cover the website bridge')
if '172.30.83.0/24' not in verifier or 'ExecStartPre=/bin/bash $stack_dir/verify-cloudflare-docker-firewall.sh' not in installer:
    sys.exit('website: firewall rule is not checked before Compose starts')
for name in ('Caddyfile.public', 'Caddyfile.cloudflare'):
    caddy = (stack / name).read_text()
    if not re.search(r'forward_auth \{\$STUDIO_ACCESS_AUTH_URL\} \{\s*uri /verify\s*\}', caddy):
        sys.exit(f'{name}: missing access layer authorization for all paths')
if not re.search(r'^\s*skip_install_trust\s*$', (stack / 'Caddyfile.private').read_text(), re.M):
    sys.exit('Caddyfile.private: missing read-only trust-store setting')
for name, marker, reason in (
    ('backup-encrypted.sh', '-aes-256-gcm', 'encrypted backup'),
    ('restore-to-scratch.sh', 'website.restore.signin', 'monitored scratch restore'),
    ('monitor-baseline.py', 'login-failures', 'login failure alert'),
    ('monitor-baseline.py', 'new-session', 'new session alert'),
    ('monitor-baseline.py', 'certificate-expiry', 'certificate expiry alert'),
):
    path = stack / name
    if not path.is_file() or marker not in path.read_text():
        sys.exit(f'{name}: missing {reason}')
print('Website container and access layer baseline passed.')
PY
if [ "$baseline_only" = true ]; then exit 0; fi
release_dir="${1:?Usage: check-edge-config.sh <matching-release-stack-directory>}"
release_dir="$(cd "$release_dir" && pwd)"

for file in compose.yaml Caddyfile.public Caddyfile.cloudflare Caddyfile.private Caddyfile.frontend cloudflare-ips.caddy backup-encrypted.sh restore-to-scratch.sh monitor-baseline.py apply-cloudflare-docker-firewall.sh verify-cloudflare-docker-firewall.sh install-cloudflare-docker-firewall.sh; do
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
  for key in STUDIO_API_DIGEST STUDIO_WEB_DIGEST STUDIO_EDGE_DIGEST STUDIO_OPERATIONS_SERVER_DIGEST STUDIO_OPERATIONS_AGENT_DIGEST; do
    digest="$(sed -n "s/^${key}=//p" "$stack_dir/.env" | tail -n 1)"
    if ! [[ "$digest" =~ ^[0-9a-f]{64}$ ]]; then
      echo "$key must be a 64-character lowercase image digest" >&2
      exit 1
    fi
  done
  auth_url="$(sed -n 's/^STUDIO_ACCESS_AUTH_URL=//p' "$stack_dir/.env" | tail -n 1)"
  if [[ ! "$auth_url" =~ ^https://[A-Za-z0-9.-]+(:[0-9]+)?$ ]]; then
    echo 'STUDIO_ACCESS_AUTH_URL must be an HTTPS origin' >&2
    exit 1
  fi
  if [ "$(sed -n 's/^STUDIO_ACCESS_MFA_ENFORCED=//p' "$stack_dir/.env" | tail -n 1)" != true ]; then
    echo 'STUDIO_ACCESS_MFA_ENFORCED must attest the provider MFA policy' >&2
    exit 1
  fi
  data_dir="$(sed -n 's/^STUDIO_DATA_DIR=//p' "$stack_dir/.env" | tail -n 1)"
  public_edge="$(sed -n 's/^STUDIO_PUBLIC_CLOUDFLARE_EDGE=//p' "$stack_dir/.env" | tail -n 1)"
  case "$public_edge" in
    true|false) ;;
    *) echo 'STUDIO_PUBLIC_CLOUDFLARE_EDGE must be exactly true or false' >&2; exit 1 ;;
  esac
  if [ "${STUDIO_PUBLIC_CLOUDFLARE_EDGE+x}" = x ]; then
    case "$STUDIO_PUBLIC_CLOUDFLARE_EDGE" in
      true|false) ;;
      *) echo 'STUDIO_PUBLIC_CLOUDFLARE_EDGE must be exactly true or false' >&2; exit 1 ;;
    esac
    if [ "$STUDIO_PUBLIC_CLOUDFLARE_EDGE" != "$public_edge" ]; then
      echo 'STUDIO_PUBLIC_CLOUDFLARE_EDGE conflicts with .env' >&2
      exit 1
    fi
  fi
  case "$(sed -n 's/^STUDIO_CADDYFILE=//p' "$stack_dir/.env" | tail -n 1)" in
    ./Caddyfile.public|./Caddyfile.cloudflare) ;;
    *) echo 'Networked cutover requires a verified public or Cloudflare Caddyfile' >&2; exit 1 ;;
  esac
  if [ "$public_edge" != true ]; then
    echo 'Public Cloudflare edge requires STUDIO_PUBLIC_CLOUDFLARE_EDGE=true' >&2
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
  identity="$(openssl x509 -in "$ca_file" -noout -subject -issuer -nameopt RFC2253)" || {
    echo 'Invalid origin-pull CA certificate' >&2
    exit 1
  }
  if grep -Eiq '(^|[=,])CN=origin-pull\.cloudflare\.net(,|$)' <<< "$identity" \
    || grep -Eiq '(^|[=,])O=Cloudflare' <<< "$identity"; then
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
