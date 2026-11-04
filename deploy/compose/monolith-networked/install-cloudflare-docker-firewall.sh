#!/usr/bin/env bash
set -euo pipefail

if [ "$(id -u)" -ne 0 ]; then
  echo 'Run as root on the Docker host' >&2
  exit 2
fi
stack_dir="$(cd "$(dirname "$0")" && pwd)"
case "$stack_dir" in *[!a-zA-Z0-9._/-]*) echo 'Stack path contains unsupported characters' >&2; exit 2;; esac
unit=/etc/systemd/system/agent-studio-cloudflare-firewall.service
cat > "$unit" <<EOF
[Unit]
Description=Restrict Docker-published Studio ports to Cloudflare
BindsTo=docker.service
PartOf=docker.service
After=docker.service
Before=agent-studio-website.service

[Service]
Type=oneshot
ExecStart=/bin/bash $stack_dir/apply-cloudflare-docker-firewall.sh
RemainAfterExit=yes

[Install]
WantedBy=docker.service
EOF
website_unit=/etc/systemd/system/agent-studio-website.service
if [ ! -e "$website_unit" ]; then
  cat > "$website_unit" <<EOF
[Unit]
Description=Start the Studio website after its Docker firewall
Requires=docker.service agent-studio-cloudflare-firewall.service
After=docker.service agent-studio-cloudflare-firewall.service

[Service]
Type=oneshot
WorkingDirectory=$stack_dir
ExecStart=/usr/bin/docker compose up -d
RemainAfterExit=yes

[Install]
WantedBy=multi-user.target
EOF
elif ! grep -Fq 'After=docker.service agent-studio-cloudflare-firewall.service' "$website_unit" \
  || ! grep -Fq 'Requires=docker.service agent-studio-cloudflare-firewall.service' "$website_unit"; then
  echo "$website_unit must require and start after agent-studio-cloudflare-firewall.service" >&2
  exit 1
fi
# Apply the rule check to both newly generated and pre-existing website units.
# A live firewall unit with a missing or displaced DOCKER-USER rule is not a
# sufficient prerequisite for publishing the edge container.
install -d -m 755 /etc/systemd/system/agent-studio-website.service.d
cat > /etc/systemd/system/agent-studio-website.service.d/10-firewall-rule.conf <<EOF
[Service]
ExecStartPre=/bin/bash $stack_dir/verify-cloudflare-docker-firewall.sh
EOF
systemctl daemon-reload
systemctl enable --now agent-studio-cloudflare-firewall.service
systemctl enable agent-studio-website.service
echo "Installed persistent Docker firewall unit at $unit"
