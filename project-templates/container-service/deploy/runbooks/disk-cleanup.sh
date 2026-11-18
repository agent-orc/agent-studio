#!/bin/sh
# Runbook step "disk-cleanup": frees disk space on the deploy host without
# touching volumes or running containers. The deploy host runs it as
# /etc/agent-host/runbooks/<project>/disk-cleanup.sh; docs/deployments/README.md
# explains the install. RUNBOOK_DISK_PATH, RUNBOOK_PRUNE_UNTIL and
# RUNBOOK_JOURNAL_MAX are optional project.yml variables.
set -eu

disk_path=${RUNBOOK_DISK_PATH:-/}
prune_until=${RUNBOOK_PRUNE_UNTIL:-168h}
journal_max=${RUNBOOK_JOURNAL_MAX:-500M}

echo "Disk usage before cleanup:"
df -h "$disk_path"
docker container prune --force
docker image prune --all --force --filter "until=$prune_until"
docker builder prune --force --filter "until=$prune_until"
if command -v journalctl >/dev/null 2>&1; then
  journalctl --vacuum-size="$journal_max" || echo "Journal vacuum skipped: the runbook user may not rotate the journal."
fi
echo "Disk usage after cleanup:"
df -h "$disk_path"
