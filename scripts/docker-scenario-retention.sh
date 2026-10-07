#!/usr/bin/env bash
# Retention for the Docker residue of scenario and compose smoke runs
# (AGT-2993). Each run removes its own images on exit
# (scripts/docker-scenario-images.sh); this helper catches what a killed run,
# a kept debug stack or an older checkout left behind, and caps the BuildKit
# cache those builds fill. scripts/scenario.sh runs it before every compose run;
# on a runner host deploy/systemd/agent-docker-scenario-retention.timer runs it
# daily. See "Docker scenario image retention" in
# docs/operations/setup/linux-runner-host.md.
#
# An image reference is a candidate when its image carries the
# io.agent-studio.disposable-image label (written by the scenario overlay and
# the compose smoke override) or its repository starts with one of the
# disposable prefixes (agent-studio-scenario-, agent-studio-smoke-; unlabelled
# images from before the label). A candidate is removed only when no container,
# running or stopped, uses its image and both its creation and its last tag are
# older than the age limit. A fully cached rebuild reuses an old creation time,
# so the tag time is what protects a run that has built but not yet started.
set -euo pipefail

usage() {
    cat <<'EOF'
Usage: scripts/docker-scenario-retention.sh [options]

Options:
  --max-age-hours N      Remove candidate images older than N hours
                         (default: $DOCKER_SCENARIO_RETENTION_MAX_AGE_HOURS or 6).
  --keep-storage SIZE    Cap the BuildKit cache at SIZE, e.g. 40GB
                         (default: $DOCKER_SCENARIO_BUILD_CACHE_KEEP_STORAGE or 40GB).
  --prefix PREFIX        Also treat repositories starting with PREFIX as
                         disposable. Repeatable; adds to the defaults.
  --skip-build-cache     Only remove images; leave the BuildKit cache alone.
  --dry-run              Report what would be removed; change nothing.
  -h, --help             Show this help.
EOF
}

readonly disposable_label="io.agent-studio.disposable-image"
max_age_hours="${DOCKER_SCENARIO_RETENTION_MAX_AGE_HOURS:-6}"
keep_storage="${DOCKER_SCENARIO_BUILD_CACHE_KEEP_STORAGE:-40GB}"
prefixes=(agent-studio-scenario- agent-studio-smoke-)
prune_cache=1
dry_run=0

while [ "$#" -gt 0 ]; do
    case "$1" in
        --max-age-hours) max_age_hours="${2:-}"; shift 2 ;;
        --keep-storage) keep_storage="${2:-}"; shift 2 ;;
        --prefix) prefixes+=("${2:?--prefix needs a value}"); shift 2 ;;
        --skip-build-cache) prune_cache=0; shift ;;
        --dry-run) dry_run=1; shift ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown argument: $1" >&2; usage >&2; exit 64 ;;
    esac
done

case "$max_age_hours" in
    ''|*[!0-9]*) echo "error: --max-age-hours must be a non-negative integer" >&2; exit 64 ;;
esac
if ! [[ "$keep_storage" =~ ^[0-9]+([.][0-9]+)?([kKmMgGtT][bB]?|[bB])?$ ]]; then
    echo "error: --keep-storage must be a size such as 40GB" >&2
    exit 64
fi

log() { printf 'docker-scenario-retention: %s\n' "$*" >&2; }

# Epoch seconds of a Docker timestamp; 0 when absent or unparseable. Accepts
# RFC 3339 (.Created) and Go's time.String() form (.Metadata.LastTagTime,
# "2026-09-28 12:18:57.7 +0000 UTC"), whose trailing zone name date rejects.
epoch_of() {
    local value="$1" seconds
    case "$value" in
        ''|'<no value>'|0001-01-01*) printf '0\n'; return ;;
    esac
    if [[ "$value" =~ ^([0-9-]+\ [0-9:.]+\ [+-][0-9]{4})\ [A-Za-z]+$ ]]; then
        value="${BASH_REMATCH[1]}"
    fi
    seconds="$(date -u -d "$value" +%s 2>/dev/null || printf '0')"
    printf '%s\n' "$seconds"
}

now="${DOCKER_SCENARIO_RETENTION_NOW:-$(date -u +%s)}"
max_age_seconds=$((max_age_hours * 3600))

# Candidate references as "<ref>\t<image-id>", one per reference.
candidates="$(
    {
        docker image ls --no-trunc --filter "label=$disposable_label" \
            --format '{{.Repository}}:{{.Tag}}	{{.ID}}'
        docker image ls --no-trunc --format '{{.Repository}}:{{.Tag}}	{{.ID}}' \
            | awk -F '\t' -v list="${prefixes[*]}" '
                BEGIN { count = split(list, wanted, " ") }
                {
                    for (i = 1; i <= count; i++) {
                        if (index($1, wanted[i]) == 1) { print; next }
                    }
                }'
    } | awk -F '\t' '$1 !~ /<none>/ && !seen[$1]++'
)"

# Images any container (running or stopped) was created from. If this cannot be
# read, nothing is removed: an unknown in-use set is not an empty one.
container_ids="$(docker ps --all --quiet --no-trunc)"
in_use=""
if [ -n "$container_ids" ]; then
    # shellcheck disable=SC2086
    in_use="$(docker container inspect --format '{{.Image}}' $container_ids)"
fi

removed=0
kept=0
failed=0
while IFS=$'\t' read -r ref image_id; do
    [ -n "$ref" ] || continue
    if grep -Fxq -- "$image_id" <<<"$in_use"; then
        log "keep $ref reason=in-use"
        kept=$((kept + 1))
        continue
    fi
    times="$(docker image inspect --format '{{.Created}}|{{.Metadata.LastTagTime}}' "$ref" 2>/dev/null || true)"
    created="$(epoch_of "${times%%|*}")"
    tagged="$(epoch_of "${times#*|}")"
    newest=$((created > tagged ? created : tagged))
    if [ "$newest" -eq 0 ]; then
        log "keep $ref reason=unknown-age"
        kept=$((kept + 1))
        continue
    fi
    age=$((now - newest))
    if [ "$age" -lt "$max_age_seconds" ]; then
        log "keep $ref reason=younger-than-${max_age_hours}h ageSeconds=$age"
        kept=$((kept + 1))
        continue
    fi
    if [ "$dry_run" -eq 1 ]; then
        log "would-remove $ref ageSeconds=$age"
        removed=$((removed + 1))
    elif docker image rm "$ref" >/dev/null 2>&1; then
        log "remove $ref ageSeconds=$age"
        removed=$((removed + 1))
    else
        log "failed $ref ageSeconds=$age"
        failed=$((failed + 1))
    fi
done <<<"$candidates"
log "images removed=$removed kept=$kept failed=$failed maxAgeHours=$max_age_hours dryRun=$dry_run"

if [ "$prune_cache" -eq 1 ]; then
    # Docker 28+ spells the cap --max-used-space and keeps --keep-storage only
    # as a deprecated alias; older clients only know --keep-storage.
    cap_flag=--keep-storage
    if docker builder prune --help 2>/dev/null | grep -q -- '--max-used-space'; then
        cap_flag=--max-used-space
    fi
    if [ "$dry_run" -eq 1 ]; then
        log "would-run docker builder prune --force $cap_flag $keep_storage"
    else
        log "docker builder prune --force $cap_flag $keep_storage"
        docker builder prune --force "$cap_flag" "$keep_storage" | tail -n 1 >&2
    fi
fi

[ "$failed" -eq 0 ]
