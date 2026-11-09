#!/usr/bin/env bash
set -euo pipefail

if [[ -z "${JOB_RESULTS_DIR:-}" ]]; then
  echo 'JOB_RESULTS_DIR must point to the task results directory.' >&2
  exit 2
fi

dotnet test backend.Tests/OrchestratorApi.Tests.csproj --no-build \
  --filter 'FullyQualifiedName~BatchFormationReplayTests' \
  --logger 'console;verbosity=normal'
echo "Replay report: $JOB_RESULTS_DIR/batch-formation-replay.md"
