#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
bash -n "$repo_root/scripts/monolith-networked-backup-restore.test.sh"
node --check "$repo_root/scripts/monolith-networked-backup-browser-capture.cjs"
bash "$repo_root/scripts/monolith-networked-backup-restore.test.sh" --plan | python3 -c '
import json, sys
plan = json.load(sys.stdin)
assert plan["schema"] == "agent-studio.networked-recovery-plan/v1"
for check in ("scheduled-backup", "manual-backup", "two-project-checkouts", "empty-host-restore", "sealed-secret"):
    assert check in plan["checks"], check
'
