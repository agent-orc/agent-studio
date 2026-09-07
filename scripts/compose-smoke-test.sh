#!/usr/bin/env bash
# Compatibility entry point. The onboarding curls now run inside the canonical scenario.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
export COMPOSE_SCENARIO_PROJECT="${COMPOSE_SMOKE_PROJECT:-agent-studio-smoke}"
exec "$repo_root/scripts/scenario.sh" --target compose --level smoke "$@"
