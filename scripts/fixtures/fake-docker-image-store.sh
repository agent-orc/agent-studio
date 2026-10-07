#!/usr/bin/env bash
# Stateful stand-in for the Docker image, container and builder commands used by
# scripts/docker-scenario-images.sh and scripts/docker-scenario-retention.sh.
# Shell contract tests put it on PATH as `docker` (or delegate to it) so they can
# assert which images a cleanup or retention pass removed without a daemon.
#
# State lives in $FAKE_DOCKER_STATE:
#   images      <ref>\t<id>\t<labels k=v,...>\t<created RFC 3339>\t<last tag time>
#   containers  <container id>\t<image id>
#   calls       every invocation, one per line
# Test hooks: FAKE_DOCKER_PS_FAIL=1 fails `docker ps`;
# FAKE_DOCKER_LEGACY_BUILDER=1 hides --max-used-space from `builder prune --help`.
# `fake-docker-image-store.sh _add <ref> <id> <labels> <created> [<last tag>]`
# seeds an image the way a build would.
set -euo pipefail

state="${FAKE_DOCKER_STATE:?set FAKE_DOCKER_STATE}"
images="$state/images"
containers="$state/containers"
touch "$images" "$containers"
printf '%s\n' "$*" >>"$state/calls"

image_row() {
    awk -F '\t' -v ref="$1" '$1 == ref { print; exit }' "$images"
}

command="${1:-}"
[ "$#" -eq 0 ] || shift
case "$command" in
    _add)
        printf '%s\t%s\t%s\t%s\t%s\n' "$1" "$2" "$3" "$4" "${5:-}" >>"$images"
        ;;
    image)
        sub="${1:-}"
        shift
        case "$sub" in
            ls)
                label="" format=""
                while [ "$#" -gt 0 ]; do
                    case "$1" in
                        --filter) label="${2#label=}"; shift 2 ;;
                        --format) format="$2"; shift 2 ;;
                        *) shift ;;
                    esac
                done
                awk -F '\t' -v label="$label" -v with_id="$([[ "$format" == *.ID* ]] && echo 1)" '
                    function has_label(labels, wanted,   parts, count, i) {
                        if (wanted == "") return 1
                        count = split(labels, parts, ",")
                        for (i = 1; i <= count; i++) {
                            if (parts[i] == wanted || index(parts[i], wanted "=") == 1) return 1
                        }
                        return 0
                    }
                    has_label($3, label) { print (with_id ? $1 "\t" $2 : $1) }
                ' "$images"
                ;;
            inspect)
                format=""
                if [ "${1:-}" = "--format" ]; then format="$2"; shift 2; fi
                row="$(image_row "$1")"
                [ -n "$row" ] || { printf 'Error: No such image: %s\n' "$1" >&2; exit 1; }
                created="$(cut -f4 <<<"$row")"
                tagged="$(cut -f5 <<<"$row")"
                case "$format" in
                    *Created*) printf '%s|%s\n' "$created" "${tagged:-0001-01-01 00:00:00 +0000 UTC}" ;;
                    *) printf '[{"Id":"%s"}]\n' "$(cut -f2 <<<"$row")" ;;
                esac
                ;;
            rm)
                row="$(image_row "$1")"
                [ -n "$row" ] || { printf 'Error: No such image: %s\n' "$1" >&2; exit 1; }
                id="$(cut -f2 <<<"$row")"
                if cut -f2 "$containers" | grep -Fxq -- "$id"; then
                    printf 'Error: conflict: image is being used by a container\n' >&2
                    exit 1
                fi
                awk -F '\t' -v ref="$1" '$1 != ref' "$images" >"$images.next"
                mv "$images.next" "$images"
                printf 'Untagged: %s\n' "$1"
                ;;
            *) printf 'fake docker: unsupported image command %s\n' "$sub" >&2; exit 2 ;;
        esac
        ;;
    ps)
        [ "${FAKE_DOCKER_PS_FAIL:-0}" != 1 ] || { printf 'Cannot connect to the Docker daemon\n' >&2; exit 1; }
        cut -f1 "$containers"
        ;;
    container)
        [ "${1:-}" = inspect ] || exit 2
        shift 3 # inspect --format '{{.Image}}'
        for container in "$@"; do
            awk -F '\t' -v id="$container" '$1 == id { print $2 }' "$containers"
        done
        ;;
    builder)
        [ "${1:-}" = prune ] || exit 2
        if [ "${2:-}" = "--help" ]; then
            printf 'Usage:  docker buildx prune\n      --keep-storage bytes\n'
            [ "${FAKE_DOCKER_LEGACY_BUILDER:-0}" = 1 ] \
                || printf '      --max-used-space bytes\n      --reserved-space bytes\n'
            exit 0
        fi
        printf 'Total:\t0B\n'
        ;;
    *)
        printf 'fake docker: unsupported command %s\n' "$command" >&2
        exit 2
        ;;
esac
