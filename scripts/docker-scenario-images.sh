#!/usr/bin/env bash
# Shared image cleanup for the disposable Compose stacks built by
# scripts/scenario.sh (--target compose --level full) and
# scripts/compose-smoke-test.sh (AGT-2993). Without it every run left its
# images behind (~2.6 GB per scenario run) until the runner host disk filled.
#
# The run's Compose project name is the key: `docker compose build` labels each
# image it builds with com.docker.compose.project=<project>, so a label match
# finds exactly this run's images and never a published image or another run's.
# Callers may also pass the exact references they built, which covers an image
# whose label a Compose version did not write.
#
# Source it and call docker_scenario_remove_project_images <project> [ref...],
# or run it directly: scripts/docker-scenario-images.sh <project> [ref...].
# Only references are removed, never an image ID, so Docker deletes the image
# only when its last tag goes.

# Prints each "<repository>:<tag>" reference of the project once.
docker_scenario_project_image_refs() {
    local project="$1"
    shift
    [ -n "$project" ] || return 0
    {
        docker image ls --filter "label=com.docker.compose.project=$project" \
            --format '{{.Repository}}:{{.Tag}}' 2>/dev/null || true
        local ref
        for ref in "$@"; do
            [ -n "$ref" ] || continue
            case "$ref" in
                *:*) ;;
                *) ref="$ref:latest" ;;
            esac
            if docker image inspect "$ref" >/dev/null 2>&1; then
                printf '%s\n' "$ref"
            fi
        done
    } | awk '$0 != "" && $0 !~ /<none>/ && !seen[$0]++'
}

# Removes the project's image references. Best effort: cleanup must never turn a
# passed run into a failed one, so it reports what it could not remove instead.
docker_scenario_remove_project_images() {
    local project="$1"
    [ -n "$project" ] || return 0
    local refs ref removed=0 failed=0
    refs="$(docker_scenario_project_image_refs "$@")"
    while IFS= read -r ref; do
        [ -n "$ref" ] || continue
        if docker image rm "$ref" >/dev/null 2>&1; then
            removed=$((removed + 1))
        else
            failed=$((failed + 1))
            printf 'docker-scenario-images: could not remove %s\n' "$ref" >&2
        fi
    done <<<"$refs"
    printf 'docker-scenario-images: project=%s removed=%s failed=%s\n' \
        "$project" "$removed" "$failed" >&2
}

if [ "${BASH_SOURCE[0]}" = "$0" ]; then
    set -euo pipefail
    if [ "$#" -lt 1 ] || [ -z "$1" ]; then
        echo "usage: $0 <compose-project-name> [image-ref...]" >&2
        exit 64
    fi
    docker_scenario_remove_project_images "$@"
fi
