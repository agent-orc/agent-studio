#!/usr/bin/env bash
# Runs the networked website edge and frontend Caddyfiles in real Caddy
# containers and drives them from fixed fake client addresses (AGT-3230):
# one peer stands in for a Cloudflare range, another for a direct client. A
# test CA stands in for Cloudflare's origin-pull CA, so the Authenticated
# Origin Pulls requirement is exercised with real client certificates. A
# stand-in API echoes the forwarded client address. Needs Docker and openssl.
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

# Origin-pull PKI: the edge trusts pull-ca.pem; pull-client is what Cloudflare
# presents, other-client comes from an unrelated CA.
mkdir -p "$work/pki"
issue_client() {
  local ca="$1" name="$2"
  openssl req -newkey ec -pkeyopt ec_paramgen_curve:P-256 -nodes -subj "/CN=$name" \
    -keyout "$work/pki/$name.key" -out "$work/pki/$name.csr" 2> /dev/null
  printf 'extendedKeyUsage=clientAuth\n' > "$work/pki/client.ext"
  openssl x509 -req -in "$work/pki/$name.csr" -CA "$work/pki/$ca.pem" -CAkey "$work/pki/$ca.key" \
    -CAcreateserial -days 2 -extfile "$work/pki/client.ext" -out "$work/pki/$name.pem" 2> /dev/null
}
for ca in pull-ca other-ca; do
  openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:P-256 -nodes -days 2 -subj "/CN=$ca" \
    -keyout "$work/pki/$ca.key" -out "$work/pki/$ca.pem" 2> /dev/null
done
issue_client pull-ca pull-client
issue_client other-ca other-client
chmod 644 "$work/pki/"*
pull_cert=(--cert /pki/pull-client.pem --key /pki/pull-client.key)
other_cert=(--cert /pki/other-client.pem --key /pki/other-client.key)

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
    -v "$work/pki/pull-ca.pem:/etc/caddy/cloudflare-origin-pull-ca.pem:ro" \
    "$caddy_image" > /dev/null
}

# request <peer address> <path> [curl header arguments...]: prints headers and body.
request() {
  local peer="$1" path="$2"
  shift 2
  docker run --rm --network "$net" --ip "$peer" -v "$work/pki:/pki:ro" "$caddy_image" \
    curl --silent --show-error --insecure --max-time 10 --dump-header - \
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

# refuse_tls <label> <peer address> [curl arguments...]: the handshake must fail.
refuse_tls() {
  local label="$1" peer="$2" output
  shift 2
  if output="$(request "$peer" /api/whoami "$@" 2>&1)"; then
    printf '%s: expected a refused TLS handshake, got\n%s\n' "$label" "$output" >&2
    exit 1
  fi
  reject "$label" "$output" 'client='
}

# Cloudflare mode, direct access refused: Cloudflare range and origin-pull
# client certificate are both required.
start_edge Caddyfile.cloudflare false
wait_ready "$cloudflare_peer" "${pull_cert[@]}"
response="$(request "$cloudflare_peer" /api/whoami "${pull_cert[@]}" "${spoof[@]}")"
expect 'Cloudflare peer' "$response" '^HTTP/[0-9.]* 200'
expect 'Cloudflare peer' "$response" '^client=198\.51\.100\.7 proto=https$'
response="$(request "$cloudflare_peer" /api/whoami "${pull_cert[@]}")"
expect 'Cloudflare peer without CF-Connecting-IP' "$response" "^client=${cloudflare_peer//./\\.} proto=https\$"
refuse_tls 'Cloudflare peer without origin-pull certificate' "$cloudflare_peer" "${spoof[@]}"
refuse_tls 'Cloudflare peer with a foreign client certificate' "$cloudflare_peer" "${other_cert[@]}" "${spoof[@]}"
response="$(request "$direct_peer" /api/whoami "${pull_cert[@]}" "${spoof[@]}")"
expect 'Direct peer' "$response" '^HTTP/[0-9.]* 403'
reject 'Direct peer' "$response" 'client='
refuse_tls 'Direct peer without origin-pull certificate' "$direct_peer" "${spoof[@]}"
# Another SNI value selects Caddy's catch-all TLS policy, which asks for no
# client certificate; strict SNI-Host matching must still keep the site shut.
for sni in other.localhost "$edge_ip"; do
  if output="$(docker run --rm --network "$net" --ip "$cloudflare_peer" "$caddy_image" \
    curl --silent --show-error --insecure --max-time 10 --dump-header - \
    --resolve "other.localhost:443:$edge_ip" -H 'Host: studio.localhost' "${spoof[@]}" \
    "https://$sni/api/whoami" 2>&1)"; then
    reject "SNI $sni with Studio Host" "$output" 'client='
  fi
done
echo 'Cloudflare mode: origin-pull certificate required; CF-Connecting-IP trusted only from the range; direct peers refused.'

response="$(request "$cloudflare_peer" / "${pull_cert[@]}")"
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
response="$(request "$cloudflare_peer" /api/sandboxed "${pull_cert[@]}")"
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
expect 'Allowed direct mode, Cloudflare peer without certificate' "$response" '^client=198\.51\.100\.7 proto=https$'
echo 'Cloudflare mode with direct access allowed: no client certificate; the peer is the client, its CF-Connecting-IP is ignored.'

# Public mode trusts no proxy headers at all.
start_edge Caddyfile.public false
wait_ready "$cloudflare_peer"
response="$(request "$cloudflare_peer" /api/whoami "${spoof[@]}")"
expect 'Public mode' "$response" "^client=${cloudflare_peer//./\\.} proto=https\$"
echo 'Public mode: forwarded and Cloudflare headers from clients are ignored.'
