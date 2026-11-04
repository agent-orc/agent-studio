#!/usr/bin/env bash
set -euo pipefail

# Match the configuration of the published server and worker projects.
for project in \
  backend/OrchestratorApi.csproj \
  runner/AgentRunner.csproj \
  task-server/TaskServer.csproj; do
  dotnet build "$project" --configuration Release --no-restore -p:UseSharedCompilation=false
done
