#!/usr/bin/env bash
set -euo pipefail

if [ "$(id -u)" -ne 0 ]; then
  echo 'Run as root on the Docker host' >&2
  exit 2
fi
stack_dir="$(cd "$(dirname "$0")" && pwd)"
caddyfile="$stack_dir/Caddyfile.public"
mapfile -t ranges < <(awk '/trusted_proxies static/ {for (i=3; i<=NF; i++) print $i}' "$caddyfile")
if [ "${#ranges[@]}" -lt 20 ]; then
  echo 'Cloudflare ranges missing from Caddyfile.public' >&2
  exit 1
fi

iptables -N STUDIO-CF-V4 2>/dev/null || true
iptables -F STUDIO-CF-V4
iptables -A STUDIO-CF-V4 -m conntrack --ctstate ESTABLISHED,RELATED -j RETURN
for range in "${ranges[@]}"; do
  if [[ "$range" != *:* ]]; then
    iptables -A STUDIO-CF-V4 -s "$range" -j RETURN
  fi
done
iptables -A STUDIO-CF-V4 -j REJECT
while iptables -C DOCKER-USER -d 172.30.83.2 -p tcp -m multiport --dports 80,443 -j STUDIO-CF-V4 2>/dev/null; do
  iptables -D DOCKER-USER -d 172.30.83.2 -p tcp -m multiport --dports 80,443 -j STUDIO-CF-V4
done
iptables -I DOCKER-USER 1 -d 172.30.83.2 -p tcp -m multiport --dports 80,443 -j STUDIO-CF-V4
echo 'Docker-published Studio ports now accept Cloudflare source ranges only.'
