#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
if [ "$(grep -Fc 'restart: ${STUDIO_RESTART_POLICY:-on-failure}' "$repo_root/deploy/compose/monolith-networked/compose.yaml")" -ne 3 ]; then
  echo 'All website containers must wait for the firewall boot unit' >&2
  exit 1
fi
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/bin"
cat > "$work/bin/id" <<'STUB'
#!/usr/bin/env bash
printf '0\n'
STUB
cat > "$work/bin/iptables" <<'STUB'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$STUDIO_FIREWALL_TEST_LOG"
if [ "$1" = -C ]; then exit 1; fi
STUB
chmod +x "$work/bin/id" "$work/bin/iptables"
STUDIO_FIREWALL_TEST_LOG="$work/iptables.log" PATH="$work/bin:$PATH" \
  bash "$repo_root/deploy/compose/monolith-networked/apply-cloudflare-docker-firewall.sh" > /dev/null

grep -Fqx -- '-I DOCKER-USER 1 -d 172.30.83.0/24 -j STUDIO-CF-V4' "$work/iptables.log"
grep -Fqx -- '-A STUDIO-CF-V4 -i br-studio-web -j RETURN' "$work/iptables.log"
grep -Fqx -- '-A STUDIO-CF-V4 -j REJECT' "$work/iptables.log"
grep -Eq -- '^-A STUDIO-CF-V4 -s [^ ]+ -d 172.30.83.2 -p tcp -m multiport --dports 80,443 -j RETURN$' "$work/iptables.log"
if grep -Eq -- '^-A STUDIO-CF-V4 -s [^ ]+ -j RETURN$' "$work/iptables.log"; then
  echo 'Cloudflare source range bypassed the edge destination and port check' >&2
  exit 1
fi
echo 'Cloudflare Docker firewall rule contract passed.'
