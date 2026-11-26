#!/usr/bin/env bash
set -euo pipefail
# One-shot, append-only import of the project's last 30 days of Git evidence.
# DISPATCH_API_TOKEN is optional for installations using local authentication.
if [[ $# -ne 2 ]]; then
  echo 'Usage: dispatch-conflicts-backfill.sh <api-base-url> <project-name>' >&2
  exit 2
fi
project_encoded=$(python3 -c 'import sys, urllib.parse; print(urllib.parse.quote(sys.argv[1], safe=""))' "$2")
auth=()
if [[ -n ${DISPATCH_API_TOKEN:-} ]]; then
  auth=(-H "Authorization: Bearer ${DISPATCH_API_TOKEN}")
fi
curl --fail-with-body --silent --show-error -X POST "${auth[@]}" \
  "${1%/}/api/projects/${project_encoded}/dispatch/conflicts/backfill"
