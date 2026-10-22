#!/usr/bin/env bash
# Renders the networked website stack for each edge mode without starting it:
# setup.sh flags, the Compose model, the adapted Caddy configuration and the
# Cloudflare range update script (AGT-3230). Needs Docker for `compose config`
# and `caddy adapt`; no container stays running.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
stack="$repo_root/deploy/compose/monolith-networked"
caddy_image="${CADDY_IMAGE:-caddy:2-alpine}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# setup.sh hands ownership to UID 10001 through a Docker one-shot when it does
# not run as root. The render needs no ownership change, so stub that call.
mkdir -p "$work/bin"
cat > "$work/bin/docker" <<'STUB'
#!/usr/bin/env bash
exit 0
STUB
chmod +x "$work/bin/docker"

render_setup() {
  local name="$1"
  shift
  mkdir -p "$work/$name"
  cp -a "$stack/." "$work/$name/stack"
  rm -f "$work/$name/stack/.env"
  PATH="$work/bin:$PATH" bash "$work/$name/stack/setup.sh" "$@" 0.0.0-test studio.example.test "$work/$name/data" > /dev/null
  docker compose --project-directory "$work/$name/stack" -f "$work/$name/stack/compose.yaml" \
    --env-file "$work/$name/stack/.env" config --format json > "$work/$name/compose.json"
}

render_setup public
render_setup cloudflare --cloudflare
render_setup direct --cloudflare --allow-direct-origin

mkdir -p "$work/invalid"
cp -a "$stack/." "$work/invalid/stack"
rm -f "$work/invalid/stack/.env"
if PATH="$work/bin:$PATH" bash "$work/invalid/stack/setup.sh" --allow-direct-origin 0.0.0-test studio.example.test "$work/invalid/data" 2> /dev/null; then
  echo 'setup.sh accepted --allow-direct-origin without --cloudflare' >&2
  exit 1
fi
if [ -e "$work/invalid/stack/.env" ]; then
  echo 'setup.sh wrote .env after rejecting its flags' >&2
  exit 1
fi

python3 - "$work" <<'PY'
import json, os, sys
work = sys.argv[1]

def model(name):
    with open(os.path.join(work, name, 'compose.json'), encoding='utf-8') as handle:
        return json.load(handle)

def env_file(name):
    values = {}
    with open(os.path.join(work, name, 'stack', '.env'), encoding='utf-8') as handle:
        for line in handle:
            key, _, value = line.rstrip('\n').partition('=')
            values[key] = value
    return values

def mounts(service):
    return {volume['target']: volume['source'] for volume in service['volumes']}

expected = {
    'public': ('Caddyfile.public', 'false'),
    'cloudflare': ('Caddyfile.cloudflare', 'false'),
    'direct': ('Caddyfile.cloudflare', 'true'),
}
for name, (caddyfile, allow_direct) in expected.items():
    rendered = model(name)
    env = env_file(name)
    assert env['STUDIO_CADDYFILE'] == './' + caddyfile, (name, env)
    assert env['STUDIO_CLOUDFLARE_ALLOW_DIRECT'] == allow_direct, (name, env)
    edge = rendered['services']['edge']
    edge_mounts = mounts(edge)
    assert edge_mounts['/etc/caddy/Caddyfile'].endswith('/' + caddyfile), (name, edge_mounts)
    assert edge_mounts['/etc/caddy/cloudflare-ips.caddy'].endswith('/cloudflare-ips.caddy'), (name, edge_mounts)
    assert '/etc/caddy/cloudflare-origin-pull-ca.pem' not in edge_mounts, (name, edge_mounts)
    assert edge['environment']['STUDIO_CLOUDFLARE_ALLOW_DIRECT'] == allow_direct, (name, edge['environment'])
    assert edge['networks']['website']['ipv4_address'] == '172.30.83.2', edge['networks']
    frontend = rendered['services']['frontend']
    assert mounts(frontend)['/etc/caddy/Caddyfile'].endswith('/Caddyfile.frontend'), frontend['volumes']
    assert frontend['networks']['website']['ipv4_address'] == '172.30.83.3', frontend['networks']
    api = rendered['services']['api']
    assert api['environment']['Security__TrustedProxyAddresses'] == '172.30.83.3,172.30.83.2', api['environment']
    assert 'ports' not in api and 'ports' not in frontend, 'only the edge may publish ports'
print('Compose render passed for the public, Cloudflare and Cloudflare direct-access modes.')
PY

# Adapting proves the Caddyfiles parse and shows what the edge trusts.
# adapt <Caddyfile> [allow-direct] [output name]
adapt() {
  local output="${3:-$1}"
  docker run --rm -e STUDIO_DOMAIN=studio.example.test -e STUDIO_CLOUDFLARE_ALLOW_DIRECT="${2:-false}" \
    -v "$stack/$1:/etc/caddy/Caddyfile:ro" \
    -v "$stack/cloudflare-ips.caddy:/etc/caddy/cloudflare-ips.caddy:ro" \
    "$caddy_image" caddy adapt --config /etc/caddy/Caddyfile > "$work/$output.json" 2> "$work/$output.log" || {
    cat "$work/$output.log" >&2
    exit 1
  }
}
for caddyfile in Caddyfile.public Caddyfile.private Caddyfile.cloudflare Caddyfile.frontend; do
  adapt "$caddyfile"
done
adapt Caddyfile.cloudflare true Caddyfile.cloudflare-direct
if docker run --rm -e STUDIO_DOMAIN=studio.example.test -e STUDIO_CLOUDFLARE_ALLOW_DIRECT=yes \
  -v "$stack/Caddyfile.cloudflare:/etc/caddy/Caddyfile:ro" \
  -v "$stack/cloudflare-ips.caddy:/etc/caddy/cloudflare-ips.caddy:ro" \
  "$caddy_image" caddy adapt --config /etc/caddy/Caddyfile > /dev/null 2>&1; then
  echo 'Caddyfile.cloudflare accepted STUDIO_CLOUDFLARE_ALLOW_DIRECT=yes' >&2
  exit 1
fi
python3 - "$work" "$stack/cloudflare-ips.caddy" "$stack/Caddyfile.frontend" <<'PY'
import ipaddress, json, os, re, sys
work, ranges_file, frontend_file = sys.argv[1:4]

def adapted(name):
    with open(os.path.join(work, name + '.json'), encoding='utf-8') as handle:
        return json.load(handle)['apps']['http']['servers']

listed = re.findall(r'^\s*([0-9A-Fa-f.:]+/\d+)', open(ranges_file, encoding='utf-8').read(), re.M)
assert listed, 'cloudflare-ips.caddy lists no ranges'
for cidr in listed:
    ipaddress.ip_network(cidr)
assert any(':' in cidr for cidr in listed) and any('.' in cidr for cidr in listed), listed

servers = adapted('Caddyfile.cloudflare')
site = next(server for server in servers.values() if ':443' in server['listen'])
assert site['trusted_proxies'] == {'source': 'static', 'ranges': listed}, site['trusted_proxies']
assert site['client_ip_headers'] == ['CF-Connecting-IP'], site.get('client_ip_headers')
assert site.get('trusted_proxies_strict') == 1, site
config = json.dumps(site)
assert '{http.vars.trusted_proxy}' in config and '{env.STUDIO_CLOUDFLARE_ALLOW_DIRECT}' in config, config

# Origin authentication is owned by AGT-3298. Both edge modes preserve the
# same trusted proxy range and have no client certificate policy yet.
assert not any(policy.get('client_authentication') for policy in site.get('tls_connection_policies', [])), site
direct = next(server for server in adapted('Caddyfile.cloudflare-direct').values() if ':443' in server['listen'])
assert not any(policy.get('client_authentication') for policy in direct.get('tls_connection_policies', [])), direct
assert direct['trusted_proxies'] == site['trusted_proxies'], direct

public = next(server for server in adapted('Caddyfile.public').values() if ':443' in server['listen'])
assert public['trusted_proxies'] == {'source': 'static', 'ranges': listed}, public
public_matchers = re.findall(r'@cloudflare remote_ip (.+)', open(os.path.join(os.path.dirname(ranges_file), 'Caddyfile.public'), encoding='utf-8').read())
assert len(public_matchers) == 1 and public_matchers[0].split() == listed, public_matchers
for server in adapted('Caddyfile.private').values():
    assert 'trusted_proxies' not in server, server
    assert 'client_ip_headers' not in server, server

frontend = adapted('Caddyfile.frontend')['srv0']
assert frontend['trusted_proxies']['ranges'] == ['172.30.83.2/32'], frontend['trusted_proxies']
text = json.dumps(frontend)
assert "frame-ancestors 'self'" in text and 'SAMEORIGIN' in text, text
assert re.search(r'header_up X-Forwarded-For \{client_ip\}', open(frontend_file, encoding='utf-8').read())
print('Caddy adapt passed: edge modes trust Cloudflare ranges; the frontend trusts only the edge.')
PY

# The range update accepts only CIDR lists and leaves the file alone when
# nothing changed.
mkdir -p "$work/ranges-source" "$work/ranges-stack"
cp "$stack/update-cloudflare-ips.sh" "$stack/cloudflare-ips.caddy" "$stack/Caddyfile.public" "$work/ranges-stack/"
sed -n 's/^[[:space:]]*\([0-9.]*\/[0-9]*\).*/\1/p' "$stack/cloudflare-ips.caddy" > "$work/ranges-source/ips-v4"
sed -n 's/^[[:space:]]*\([0-9A-Fa-f]*:[0-9A-Fa-f:]*\/[0-9]*\).*/\1/p' "$stack/cloudflare-ips.caddy" > "$work/ranges-source/ips-v6"
export CLOUDFLARE_IPS_BASE_URL="file://$work/ranges-source"
bash "$work/ranges-stack/update-cloudflare-ips.sh" --check > /dev/null
cmp -s "$stack/cloudflare-ips.caddy" "$work/ranges-stack/cloudflare-ips.caddy"

printf '203.0.113.0/24\n' >> "$work/ranges-source/ips-v4"
if bash "$work/ranges-stack/update-cloudflare-ips.sh" --check > /dev/null 2>&1; then
  echo 'update-cloudflare-ips.sh --check missed a published range change' >&2
  exit 1
fi
cmp -s "$stack/cloudflare-ips.caddy" "$work/ranges-stack/cloudflare-ips.caddy"
bash "$work/ranges-stack/update-cloudflare-ips.sh" > /dev/null
grep -q '^	203\.0\.113\.0/24 \\$' "$work/ranges-stack/cloudflare-ips.caddy"
grep -q '@cloudflare remote_ip .*203\.0\.113\.0/24' "$work/ranges-stack/Caddyfile.public"
docker run --rm -v "$work/ranges-stack/cloudflare-ips.caddy:/etc/caddy/cloudflare-ips.caddy:ro" \
  -v "$stack/Caddyfile.cloudflare:/etc/caddy/Caddyfile:ro" -e STUDIO_DOMAIN=studio.example.test \
  "$caddy_image" caddy adapt --config /etc/caddy/Caddyfile 2> /dev/null | grep -q '"203.0.113.0/24"'

printf 'not-a-range\n' >> "$work/ranges-source/ips-v6"
cp "$work/ranges-stack/cloudflare-ips.caddy" "$work/ranges-before"
if bash "$work/ranges-stack/update-cloudflare-ips.sh" > /dev/null 2>&1; then
  echo 'update-cloudflare-ips.sh accepted a malformed range' >&2
  exit 1
fi
cmp -s "$work/ranges-before" "$work/ranges-stack/cloudflare-ips.caddy"

# A range wider than IPv4 /12 or IPv6 /24 would trust arbitrary peers.
refuse_ranges() {
  local label="$1" family="$2" entry="$3"
  rm -rf "$work/wide-source"
  mkdir -p "$work/wide-source"
  sed -n 's/^[[:space:]]*\([0-9.]*\/[0-9]*\).*/\1/p' "$work/ranges-before" > "$work/wide-source/ips-v4"
  sed -n 's/^[[:space:]]*\([0-9A-Fa-f]*:[0-9A-Fa-f:]*\/[0-9]*\).*/\1/p' "$work/ranges-before" > "$work/wide-source/ips-v6"
  printf '%s\n' "$entry" >> "$work/wide-source/$family"
  if CLOUDFLARE_IPS_BASE_URL="file://$work/wide-source" bash "$work/ranges-stack/update-cloudflare-ips.sh" > /dev/null 2> "$work/wide.log"; then
    echo "update-cloudflare-ips.sh accepted $label $entry" >&2
    exit 1
  fi
  grep -q "Refusing $entry" "$work/wide.log"
  cmp -s "$work/ranges-before" "$work/ranges-stack/cloudflare-ips.caddy"
}
refuse_ranges 'the IPv4 catch-all' ips-v4 0.0.0.0/0
refuse_ranges 'an IPv4 range wider than /12' ips-v4 10.0.0.0/11
refuse_ranges 'the IPv6 catch-all' ips-v6 ::/0
refuse_ranges 'an IPv6 range wider than /24' ips-v6 2400::/23

# Without a test base URL, curl may use and follow only HTTPS.
mkdir -p "$work/curl-bin"
cat > "$work/curl-bin/curl" <<STUB
#!/usr/bin/env bash
printf '%s\n' "\$@" > "$work/curl-args"
exit 7
STUB
chmod +x "$work/curl-bin/curl"
if env -u CLOUDFLARE_IPS_BASE_URL PATH="$work/curl-bin:$PATH" bash "$work/ranges-stack/update-cloudflare-ips.sh" --check > /dev/null 2>&1; then
  echo 'update-cloudflare-ips.sh succeeded with a failing curl' >&2
  exit 1
fi
tr '\n' ' ' < "$work/curl-args" | grep -q -- "--proto =https --proto-redir =https .* https://www.cloudflare.com/ips-v4"
CLOUDFLARE_IPS_BASE_URL="file://$work/ranges-source" PATH="$work/curl-bin:$PATH" \
  bash "$work/ranges-stack/update-cloudflare-ips.sh" --check > /dev/null 2>&1 || true
if grep -q -- '--proto' "$work/curl-args"; then
  echo 'update-cloudflare-ips.sh restricted protocols for a test base URL' >&2
  exit 1
fi
echo 'Cloudflare range update passed: unchanged lists keep the file, changes are detected, malformed and over-wide lists are refused, HTTPS only by default.'
