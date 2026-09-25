#!/usr/bin/env bash
# One command rotates a principal via the Task Server, writes the generated
# value to the private Compose volume, and restarts its client.
set -euo pipefail
case "${1:-}" in
    studio) service=studio-bff ;;
    engine) service=orchestrator-engine ;;
    runner) service=agent-host ;;
    coding) service=agent-host-coding ;;
    review) service=agent-host-review ;;
    *) echo "usage: $0 <studio|engine|runner|coding|review>" >&2; exit 64 ;;
esac
cd "$(dirname "$0")/.."
docker compose --profile maintenance run --rm credential-manager "$1"
if [ "$service" = agent-host-coding ] || [ "$service" = agent-host-review ]; then
    docker compose --profile runner restart "$service"
else
    docker compose restart "$service"
fi
echo "compose-credential-rotation=ok principal=$1 service=$service"
