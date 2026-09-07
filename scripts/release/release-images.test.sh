#!/bin/sh
# Static contract checks for the release container images. They need no Docker
# daemon, so they run in every CI job that runs the release shell tests.
#
# The entrypoint check exists because three Dockerfiles shipped an ENTRYPOINT
# naming the project rather than the AssemblyName (TaskServer.dll instead of
# task-server.dll). Those images could never start, and nothing noticed because
# no CI job ran the distributed profile.

set -eu

repo_root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
manifest="$repo_root/.github/release-images.txt"
compose="$repo_root/docker-compose.yml"

[ -f "$manifest" ] || { printf 'missing %s\n' "$manifest" >&2; exit 1; }

entries=$(grep -v '^[[:space:]]*#' "$manifest" | grep -v '^[[:space:]]*$')
count=$(printf '%s\n' "$entries" | wc -l)
[ "$count" -eq 6 ] || {
    printf 'expected 6 release images, found %s\n' "$count" >&2
    exit 1
}

printf '%s\n' "$entries" | while read -r name dockerfile
do
    path="$repo_root/$dockerfile"
    [ -f "$path" ] || { printf '%s: missing %s\n' "$name" "$dockerfile" >&2; exit 1; }

    # Every image runs as a non-root user.
    user=$(grep -E '^USER ' "$path" | tail -n 1 | awk '{print $2}')
    [ -n "$user" ] || { printf '%s: no USER instruction\n' "$name" >&2; exit 1; }
    case "$user" in
        root|0) printf '%s: USER is root\n' "$name" >&2; exit 1 ;;
    esac

    # Every image declares a health contract.
    grep -q '^HEALTHCHECK ' "$path" || {
        printf '%s: no HEALTHCHECK instruction\n' "$name" >&2
        exit 1
    }

    # Version and commit reach the OCI labels.
    for label in version revision
    do
        grep -q "org.opencontainers.image.$label=" "$path" || {
            printf '%s: missing org.opencontainers.image.%s label\n' "$name" "$label" >&2
            exit 1
        }
    done

    # Compose must reference the published image at the pinned tag.
    grep -q "/$name:\${AGENT_STUDIO_VERSION" "$compose" || {
        printf '%s: docker-compose.yml does not reference the image at ${AGENT_STUDIO_VERSION}\n' \
            "$name" >&2
        exit 1
    }

    # ENTRYPOINT must name the assembly the publish step actually produces.
    project=$(grep -oE '[A-Za-z0-9_./-]+\.csproj' "$path" | head -n 1 || true)
    if [ -n "$project" ]; then
        assembly=$(grep -oE '<AssemblyName>[^<]+</AssemblyName>' "$repo_root/$project" \
            | sed -e 's|<AssemblyName>||' -e 's|</AssemblyName>||' \
            | head -n 1 || true)
        if [ -z "$assembly" ]; then
            # No override: MSBuild defaults the assembly name to the file stem.
            assembly=$(basename "$project" .csproj)
        fi
        grep -q "ENTRYPOINT .*[\"/]$assembly\.dll" "$path" || {
            printf '%s: ENTRYPOINT does not run %s.dll (from %s)\n' \
                "$name" "$assembly" "$project" >&2
            exit 1
        }
    fi

    printf 'release-image ok: %-28s user=%-6s dockerfile=%s\n' "$name" "$user" "$dockerfile"
done

printf 'Release image contract tests passed.\n'
