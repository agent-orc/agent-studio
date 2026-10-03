#!/bin/sh
# Test double for docker compose, docker and the in-container curl used by
# the Docker lifecycle scripts. It simulates one Task Server installation in
# $SIM (versions, store schema migration, mode, drain, backups, hosts) so the
# upgrade and rollback rehearsals run without a Docker daemon.
#
# Per-version behaviour: $SIM/versions/<tag> holds "schema protoMin protoMax"
# and an optional word "fail" (boot fails before migrating the store).
set -eu
: "${SIM:?SIM must point at the simulation directory}"
role=$(basename "$0")

running() { cat "$SIM/running" 2>/dev/null || true; }
spec() { cat "$SIM/versions/$1" 2>/dev/null || true; }
field() { spec "$1" | awk -v n="$2" '{print $n}'; }
healthy()
{
    v=$(running)
    [ -n "$v" ] || return 1
    case " $(spec "$v") " in *" fail "*) return 1 ;; esac
    [ "$(cat "$SIM/store-schema")" -le "$(field "$v" 1)" ]
}

case "$role" in
compose)
    env_file=
    while [ "$#" -gt 0 ]; do
        case "$1" in
            --project-directory) shift 2 ;;
            --env-file) env_file=$2; shift 2 ;;
            *) break ;;
        esac
    done
    desired=$(sed -n 's/^CONTROL_PLANE_VERSION=//p' "$env_file" | tail -n 1)
    cmd=$1; shift
    case "$cmd" in
        pull)
            [ -f "$SIM/versions/$desired" ] || { echo "manifest unknown: $desired" >&2; exit 1; }
            touch "$SIM/images/$desired" ;;
        up)
            echo "$desired" >"$SIM/running"
            echo "up $desired" >>"$SIM/events"
            store=$(cat "$SIM/store-schema")
            case " $(spec "$desired") " in
                *" fail "*) ;;
                *) target=$(field "$desired" 1)
                   [ "$target" -gt "$store" ] && echo "$target" >"$SIM/store-schema" ;;
            esac
            if [ -f "$SIM/interrupt-after-up" ]; then
                rm -f "$SIM/interrupt-after-up"
                kill -TERM "$PPID"
            fi ;;
        ps)
            if [ "${1:-}" = "-q" ]; then echo "cid-$2"; exit 0; fi
            health=unhealthy; healthy && health=healthy
            for s in task-server orchestrator-engine edge; do
                printf '{"Service":"%s","Health":"%s"}\n' "$s" "$health"
            done ;;
        exec)
            [ "$1" = "-T" ] && shift
            shift            # service
            shift            # sh
            shift            # -c
            script=$(printf '%s' "$1" | sed "s#/run/secrets/studio_token#$SIM/token#g")
            PATH="$SIM/bin:$PATH" sh -c "$script" ;;
        logs) ;;
        *) echo "fake compose: unsupported $cmd" >&2; exit 2 ;;
    esac ;;
docker)
    if [ "$1" = "inspect" ]; then echo "sha256:id-$(running)"; exit 0; fi
    # image inspect [--format F] <ref>
    shift
    ref=
    for a in "$@"; do ref=$a; done
    case "$ref" in
        sha256:id-*) echo "registry.example/agent@sha256:digest-${ref#sha256:id-}" ;;
        *:*) [ -f "$SIM/images/${ref##*:}" ] ;;
        *) exit 1 ;;
    esac ;;
curl)
    method=GET; data=; url=
    while [ "$#" -gt 0 ]; do
        case "$1" in
            -X) method=$2; shift 2 ;;
            -H) shift 2 ;;
            --data-binary) data=$(cat); shift 2 ;;
            -*) shift ;;
            *) url=$1; shift ;;
        esac
    done
    path=${url#http://127.0.0.1:5071}
    echo "$method $path" >>"$SIM/requests"
    healthy || { [ "$path" = "/readyz" ] && exit 22; }
    v=$(running)
    case "$method $path" in
        "GET /readyz")
            printf '{"status":"ready","authority":"restored","mode":"%s"}' "$(cat "$SIM/mode")" ;;
        "GET /api/v1/management/status")
            healthy || exit 22
            printf '{"serverVersion":"%s","schemaVersion":%s,"mode":0,"protocol":{"current":%s,"minimumSupported":%s,"maximumSupported":%s}}' \
                "$v" "$(cat "$SIM/store-schema")" "$(field "$v" 3)" "$(field "$v" 2)" "$(field "$v" 3)" ;;
        "PUT /api/v1/management/mode")
            healthy || exit 22
            n=$(printf '%s' "$data" | sed -n 's/.*"mode":\([0-9]\).*/\1/p')
            case "$n" in 0) m=Normal ;; 1) m=Draining ;; 2) m=ReadOnly ;; *) m=Maintenance ;; esac
            echo "$m" >"$SIM/mode"; printf '{}' ;;
        "POST /api/v1/management/prepare-shutdown")
            active=$(cat "$SIM/active-runs")
            if [ "$active" -le 0 ]; then printf '{"safeToStop":true,"unresolvedAttempts":0}'
            else
                [ -f "$SIM/runs-never-settle" ] || echo $((active - 1)) >"$SIM/active-runs"
                printf '{"safeToStop":false,"unresolvedAttempts":%s}' "$active"
            fi ;;
        "POST /api/v1/management/backups")
            n=$(( $(cat "$SIM/backup-count" 2>/dev/null || echo 0) + 1 ))
            echo "$n" >"$SIM/backup-count"
            printf '{"backupId":"bk-%s","sha256":"sha-%s","path":"/x","createdAt":"2026-10-01T00:00:00Z","sizeBytes":1}' "$n" "$n" ;;
        "POST /api/v1/management/restore")
            printf '{"backupId":"x","verified":true,"restored":false,"sha256":"x","message":"ok"}' ;;
        "GET /api/v1/management/remote-hosts")
            cat "$SIM/hosts.json" ;;
        *) echo "fake curl: unsupported $method $path" >&2; exit 22 ;;
    esac ;;
esac
