#!/bin/sh
# Runbook step "restart": restarts this environment's service container and
# shows its state afterwards. The deploy host runs it as
# /etc/agent-host/runbooks/<project>/restart.sh; docs/deployments/README.md
# explains the install. Studio passes AGENT_STUDIO_PROJECT and
# AGENT_STUDIO_ENVIRONMENT; set RUNBOOK_COMPOSE_FILE and RUNBOOK_SERVICE as
# project.yml variables when your paths differ from the defaults below.
set -eu

environment_dir=$AGENT_STUDIO_ENVIRONMENT
if [ "$environment_dir" = stage ]; then environment_dir=staging; fi
compose_file=${RUNBOOK_COMPOSE_FILE:-/srv/apps/$AGENT_STUDIO_PROJECT/$environment_dir/compose.yml}
service=${RUNBOOK_SERVICE:-$AGENT_STUDIO_PROJECT}

echo "Restarting $service from $compose_file"
docker compose -f "$compose_file" restart "$service"
docker compose -f "$compose_file" ps "$service"
