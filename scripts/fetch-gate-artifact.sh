#!/usr/bin/env bash
set -euo pipefail

if [ "$#" -ne 4 ]; then
  echo "Usage: fetch-gate-artifact.sh <studio-url> <server-artifact-run-id> <name> <output>" >&2
  exit 2
fi

studio_url=${1%/}
run_id=$2
artifact_name=$3
output_path=$4

if [[ ! $studio_url =~ ^https://[A-Za-z0-9._:-]+$ ]] && [[ ! $studio_url =~ ^http://(127\.0\.0\.1|localhost)(:[0-9]+)?$ ]]; then
  echo "Studio URL must use HTTPS, except on loopback." >&2
  exit 2
fi
if [[ ! $run_id =~ ^[0-9a-f]{32}$ ]] || [[ ! $artifact_name =~ ^[A-Za-z0-9._-]+$ ]] || [[ $artifact_name == "." ]] || [[ $artifact_name == ".." ]]; then
  echo "Invalid gate artifact identity." >&2
  exit 2
fi
if [ -z "${GATE_ARTIFACT_TOKEN:-}" ]; then
  echo "GATE_ARTIFACT_TOKEN is required." >&2
  exit 2
fi

printf 'header = "Authorization: Bearer %s"\n' "$GATE_ARTIFACT_TOKEN" |
  curl --config - --fail --silent --show-error --output "$output_path" "$studio_url/api/gate-artifacts/$run_id/$artifact_name"
