#!/usr/bin/env bash
# Runs the networked website edge and frontend Caddyfiles in real Caddy
# containers and drives them from fixed fake client addresses (AGT-3230):
# one peer stands in for a Cloudflare range, another for a direct client. A
# stand-in API echoes the forwarded client address. Needs Docker.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
stack="$repo_root/deploy/compose/monolith-networked"
caddy_image="${CADDY_IMAGE:-caddy:2-alpine}"
run_id="edge-test-$$"
net="$run_id"
work="$(mktemp -d)"
chmod 755 "$work"

finish() {
  result=$?
  if [ "$result" -ne 0 ]; then
    for name in edge frontend api; do
      echo "--- $name log" >&2
      docker logs --tail 40 "$run_id-$name" >&2 2>&1 || true
    done
  fi
  docker rm -f "$run_id-edge" "$run_id-frontend" "$run_id-api" > /dev/null 2>&1 || true
  docker network rm "$net" > /dev/null 2>&1 || true
  rm -rf "$work"
  exit "$result"
}
trap finish EXIT

prefix=''
for candidate in 10.231.83 10.232.83 10.233.83 10.234.83; do
  if docker network create --subnet "$candidate.0/24" "$net" > /dev/null 2>&1; then
    prefix="$candidate"
    break
  fi
done
if [ -z "$prefix" ]; then
  echo 'No free test subnet for the edge test' >&2
  exit 1
fi
edge_ip="$prefix.2"
cloudflare_peer="$prefix.10"
direct_peer="$prefix.20"

# The real frontend Caddyfile, with the test edge address in place of the
# Compose edge address it trusts.
sed "s|172\.30\.83\.2/32|$edge_ip/32|" "$stack/Caddyfile.frontend" > "$work/Caddyfile.frontend"
grep -q "$edge_ip/32" "$work/Caddyfile.frontend"
mkdir -p "$work/studio"
printf '<!doctype html><app-root></app-root>\n' > "$work/studio/index.html"
cat > "$work/Caddyfile.api" <<'CADDY'
:5031 {
	header /api/sandboxed Content-Security-Policy "sandbox allow-scripts"
	respond /api/* "client={header.X-Forwarded-For} proto={header.X-Forwarded-Proto}"
}
CADDY
printf 'trusted_proxies static %s/32\n' "$cloudflare_peer" > "$work/cloudflare-ips.caddy"
mkdir -p "$work/origin-pull"
openssl req -x509 -newkey rsa:2048 -nodes -days 1 -subj '/CN=Studio test CA' \
  -keyout "$work/origin-pull/ca.key" -out "$work/origin-pull/ca.pem" > /dev/null 2>&1
openssl req -newkey rsa:2048 -nodes -subj '/CN=Studio test client' \
  -keyout "$work/origin-pull/client.key" -out "$work/origin-pull/client.csr" > /dev/null 2>&1
printf 'extendedKeyUsage=clientAuth\n' > "$work/origin-pull/client.ext"
openssl x509 -req -in "$work/origin-pull/client.csr" -CA "$work/origin-pull/ca.pem" \
  -CAkey "$work/origin-pull/ca.key" -CAcreateserial -days 1 \
  -extfile "$work/origin-pull/client.ext" -out "$work/origin-pull/client.pem" > /dev/null 2>&1

docker run -d --name "$run_id-api" --network "$net" --ip "$prefix.4" --network-alias api \
  -v "$work/Caddyfile.api:/etc/caddy/Caddyfile:ro" "$caddy_image" > /dev/null
docker run -d --name "$run_id-frontend" --network "$net" --ip "$prefix.3" --network-alias frontend \
  -v "$work/Caddyfile.frontend:/etc/caddy/Caddyfile:ro" -v "$work/studio:/srv/studio:ro" \
  "$caddy_image" > /dev/null

start_edge() {
  docker rm -f "$run_id-edge" > /dev/null 2>&1 || true
  docker run -d --name "$run_id-edge" --network "$net" --ip "$edge_ip" \
    -e STUDIO_DOMAIN=studio.localhost -e STUDIO_CLOUDFLARE_ALLOW_DIRECT="$2" \
    -v "$stack/$1:/etc/caddy/Caddyfile:ro" \
    -v "$work/cloudflare-ips.caddy:/etc/caddy/cloudflare-ips.caddy:ro" \
    -v "$work/origin-pull:/etc/caddy/origin-pull:ro" \
    "$caddy_image" > /dev/null
}

# request <peer address> <path> [curl header arguments...]: prints headers and body.
request() {
  local peer="$1" path="$2"
  shift 2
  docker run --rm --network "$net" --ip "$peer" \
    -v "$work/origin-pull:/origin-pull:ro" "$caddy_image" \
    curl --silent --show-error --insecure --max-time 10 --dump-header - \
    --cert /origin-pull/client.pem --key /origin-pull/client.key \
    --resolve "studio.localhost:443:$edge_ip" "$@" "https://studio.localhost$path"
}

# wait_ready <peer address> [curl arguments...]
wait_ready() {
  local peer="$1"
  shift
  for _ in $(seq 1 30); do
    if request "$peer" /api/ready "$@" 2> /dev/null | grep -q '^HTTP/[0-9.]* 200'; then return 0; fi
    sleep 1
  done
  echo 'Edge did not become ready' >&2
  return 1
}

expect() {
  local label="$1" response="$2" pattern="$3"
  if ! grep -Eqi -- "$pattern" <<< "$response"; then
    printf '%s: expected /%s/ in\n%s\n' "$label" "$pattern" "$response" >&2
    exit 1
  fi
}

reject() {
  local label="$1" response="$2" pattern="$3"
  if grep -Eqi -- "$pattern" <<< "$response"; then
    printf '%s: unexpected /%s/ in\n%s\n' "$label" "$pattern" "$response" >&2
    exit 1
  fi
}

spoof=(-H 'CF-Connecting-IP: 198.51.100.7' -H 'X-Forwarded-For: 192.0.2.66')

# Cloudflare mode uses the versioned range to accept its proxy peer and
# rejects a direct peer unless direct access is explicitly enabled.
start_edge Caddyfile.cloudflare false
wait_ready "$cloudflare_peer"
if docker run --rm --network "$net" --ip "$cloudflare_peer" "$caddy_image" \
  curl --silent --show-error --insecure --max-time 10 \
  --resolve "studio.localhost:443:$edge_ip" https://studio.localhost/api/whoami > /dev/null 2>&1; then
  echo 'Cloudflare edge accepted a client without the origin-pull certificate' >&2
  exit 1
fi
response="$(request "$cloudflare_peer" /api/whoami "${spoof[@]}")"
expect 'Cloudflare peer' "$response" '^HTTP/[0-9.]* 200'
expect 'Cloudflare peer' "$response" '^client=198\.51\.100\.7 proto=https$'
response="$(request "$cloudflare_peer" /api/whoami)"
expect 'Cloudflare peer without CF-Connecting-IP' "$response" "^client=${cloudflare_peer//./\\.} proto=https\$"
response="$(request "$direct_peer" /api/whoami "${spoof[@]}")"
expect 'Direct peer' "$response" '^HTTP/[0-9.]* 403'
reject 'Direct peer' "$response" 'client='
echo 'Cloudflare mode: CF-Connecting-IP trusted only from the range; direct peers refused.'

response="$(request "$cloudflare_peer" /)"
expect 'Shell' "$response" '^HTTP/[0-9.]* 200'
expect 'Shell' "$response" "^content-security-policy: .*frame-ancestors 'self'"
expect 'Shell' "$response" '^x-frame-options: SAMEORIGIN'
expect 'Shell' "$response" '^cross-origin-opener-policy: same-origin'
expect 'Shell' "$response" '^permissions-policy: camera=\(\)'
expect 'Shell' "$response" '^strict-transport-security: max-age=31536000; includeSubDomains'
expect 'Shell' "$response" '^x-content-type-options: nosniff'
if [ "$(grep -ci '^strict-transport-security:' <<< "$response")" -ne 1 ]; then
  echo "Shell: expected one Strict-Transport-Security header in\n$response" >&2
  exit 1
fi
response="$(request "$cloudflare_peer" /api/sandboxed)"
expect 'Sandboxed API route' "$response" '^content-security-policy: sandbox allow-scripts'
reject 'Sandboxed API route' "$response" '^x-frame-options:'
expect 'Sandboxed API route' "$response" '^strict-transport-security: max-age=31536000; includeSubDomains'
echo 'Headers: shell policy from the frontend, HSTS from the edge, API routes keep their own policy.'

# Cloudflare mode with direct access explicitly allowed.
start_edge Caddyfile.cloudflare true
wait_ready "$direct_peer"
response="$(request "$direct_peer" /api/whoami "${spoof[@]}")"
expect 'Allowed direct peer' "$response" '^HTTP/[0-9.]* 200'
expect 'Allowed direct peer' "$response" "^client=${direct_peer//./\\.} proto=https\$"
response="$(request "$cloudflare_peer" /api/whoami "${spoof[@]}")"
expect 'Allowed direct mode, Cloudflare peer' "$response" '^client=198\.51\.100\.7 proto=https$'
echo 'Cloudflare mode with direct access allowed: the direct peer is the client and its CF-Connecting-IP is ignored.'

# Public mode ignores headers from the fake peer, which is outside the published ranges.
start_edge Caddyfile.public false
wait_ready "$cloudflare_peer"
response="$(request "$cloudflare_peer" /api/whoami "${spoof[@]}")"
expect 'Public mode' "$response" "^client=${cloudflare_peer//./\\.} proto=https\$"
echo 'Public mode: untrusted peer headers are ignored.'
