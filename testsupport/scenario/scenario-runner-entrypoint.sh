#!/bin/sh
set -eu

repository_head="$SCENARIO_WORK_ROOT/repository/origin.git/HEAD"
while [ ! -f "$repository_head" ]; do
  sleep 0.1
done

exec dotnet /opt/agent-host/agent-host.dll --poll
