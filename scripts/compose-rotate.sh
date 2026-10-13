#!/usr/bin/env bash
# Rotate one Compose principal without showing or copying the issued credential.
# Rerunning after a failure resumes the same rotation operation.
set -euo pipefail
role="${1:-}"
case "$role" in studio|engine|runner|review-runner) ;; *) echo 'usage: compose-rotate.sh studio|engine|runner|review-runner [--dev]' >&2; exit 64 ;; esac
mode="${2:-}"
if [[ -n "$mode" && "$mode" != --dev ]]; then
    echo 'usage: compose-rotate.sh studio|engine|runner|review-runner [--dev]' >&2
    exit 64
fi
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
compose=(docker compose -f "$repo_root/docker-compose.yml")
if [[ -n "${COMPOSE_ROTATE_OVERRIDE_FILE:-}" ]]; then
    compose+=(-f "$COMPOSE_ROTATE_OVERRIDE_FILE")
fi
suffix=''
if [[ "$mode" == --dev ]]; then suffix='-dev'; fi
if [[ "$mode" == --dev ]]; then
    "${compose[@]}" build credential-manager-dev
fi
"${compose[@]}" run --rm --no-deps "credential-manager${suffix}" "$role"
case "$role" in
    studio) services=("studio-bff${suffix}" "orchestrator-api${suffix}") ;;
    engine) services=("orchestrator-engine${suffix}") ;;
    runner) services=("agent-host-distributed${suffix}") ;;
    review-runner) services=("agent-host-review-distributed${suffix}") ;;
esac
"${compose[@]}" up -d --no-deps --force-recreate --wait "${services[@]}"
# The recreated service now reads the new bearer. Prove and acknowledge it so
# Task Server retires the previous generation instead of waiting for the deadline.
"${compose[@]}" run --rm --no-deps "credential-manager${suffix}" "$role" --complete
printf 'credential-rotation=complete role=%s\n' "$role"
