#!/bin/sh
# Read the owner-only Engine principal file at process start. This also keeps
# published Engine releases that predate CLIENT_CREDENTIAL_FILE compatible.
set -eu
CLIENT_CREDENTIAL="$(cat /run/secrets/engine_token)"
export CLIENT_CREDENTIAL
exec dotnet /app/orchestrator-engine.dll
