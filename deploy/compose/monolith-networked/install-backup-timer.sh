#!/usr/bin/env bash
# Installs a systemd timer that runs backup.sh from this stack directory.
#
#   sudo bash install-backup-timer.sh [--on-calendar <systemd calendar spec>]
#   sudo bash install-backup-timer.sh --remove
#
# The timer is useful even before a destination is configured: each run then
# logs that backups are off (journalctl -u agent-studio-backup.service).
set -euo pipefail

if [ "$(id -u)" -ne 0 ]; then
  echo 'Run as root on the Docker host' >&2
  exit 2
fi
stack_dir="$(cd "$(dirname "$0")" && pwd)"
case "$stack_dir" in *[!a-zA-Z0-9._/-]*) echo 'Stack path contains unsupported characters' >&2; exit 2;; esac
service=/etc/systemd/system/agent-studio-backup.service
timer=/etc/systemd/system/agent-studio-backup.timer
on_calendar='*-*-* 03:15:00'
case "${1:-}" in
  --remove)
    systemctl disable --now agent-studio-backup.timer 2>/dev/null || true
    rm -f "$service" "$timer"
    systemctl daemon-reload
    echo 'Removed the Studio backup timer'
    exit 0
    ;;
  --on-calendar)
    [ "$#" -eq 2 ] || { echo 'Usage: install-backup-timer.sh [--on-calendar <spec>] | --remove' >&2; exit 2; }
    systemd-analyze calendar "$2" > /dev/null || { echo "Invalid calendar spec: $2" >&2; exit 2; }
    on_calendar="$2"
    ;;
  '') ;;
  *) echo 'Usage: install-backup-timer.sh [--on-calendar <spec>] | --remove' >&2; exit 2 ;;
esac

cat > "$service" <<EOF
[Unit]
Description=Encrypted off-host backup of the Studio website
Requires=docker.service
After=docker.service

[Service]
Type=oneshot
WorkingDirectory=$stack_dir
ExecStart=/bin/bash $stack_dir/backup.sh run
EOF
cat > "$timer" <<EOF
[Unit]
Description=Schedule encrypted off-host backups of the Studio website

[Timer]
OnCalendar=$on_calendar
Persistent=true
RandomizedDelaySec=10m
Unit=agent-studio-backup.service

[Install]
WantedBy=timers.target
EOF
systemctl daemon-reload
systemctl enable --now agent-studio-backup.timer
echo "Installed agent-studio-backup.timer ($on_calendar)"
bash "$stack_dir/backup.sh" status
