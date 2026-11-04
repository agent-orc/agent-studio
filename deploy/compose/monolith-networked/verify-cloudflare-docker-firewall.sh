#!/usr/bin/env bash
set -euo pipefail

# The website unit runs this immediately before Compose starts the public edge.
# The first DOCKER-USER rule must cover every address on the website bridge,
# including Operations Server and Operations Agent.
first_rule="$(iptables -S DOCKER-USER | sed -n '2p')"
if [ "$first_rule" != '-A DOCKER-USER -d 172.30.83.0/24 -j STUDIO-CF-V4' ]; then
  echo 'Studio subnet firewall rule is not first in DOCKER-USER' >&2
  exit 1
fi
iptables -C STUDIO-CF-V4 -i br-studio-web -j RETURN
iptables -C STUDIO-CF-V4 -j REJECT
