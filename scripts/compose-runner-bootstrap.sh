#!/usr/bin/env bash
# Compatibility entry point for installers that used the old runner bootstrap.
# The supported one-box runners register with Task Server and use the four
# installation credentials created by compose-distributed-bootstrap.sh.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
printf 'compose-runner-bootstrap: use the Task Server one-box profile\n'
exec "$repo_root/scripts/compose-distributed-bootstrap.sh" "$@"
