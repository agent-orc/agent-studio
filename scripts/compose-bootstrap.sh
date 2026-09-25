#!/bin/sh
# Runs once before every Compose start. Creates principal credentials on the
# first start and verifies them on every later start. The volume is private to
# this Compose project; product processes read its owner-only files.
set -eu
umask 077
secrets=/run/secrets
chown 10001:10001 /backup
for name in studio_token engine_token runner_token coding_runner_token review_runner_token; do
    if [ -e "$secrets/$name" ]; then
        if [ ! -s "$secrets/$name" ]; then
            echo "empty credential: $name" >&2
            exit 1
        fi
    else
        legacy=''
        case "$name" in
            studio_token) legacy="${LEGACY_STUDIO_TOKEN:-}" ;;
            engine_token) legacy="${LEGACY_ENGINE_TOKEN:-}" ;;
            runner_token) legacy="${LEGACY_RUNNER_TOKEN:-}" ;;
        esac
        if [ -z "$legacy" ] && [ "$name" != coding_runner_token ] \
           && [ "$name" != review_runner_token ] \
           && [ -n "$(find /store -type f -print -quit)" ]; then
            echo "store exists but $name is missing; restore the credentials volume from backup" >&2
            exit 1
        fi
        temp="$secrets/.$name.$$"
        if [ -n "$legacy" ]; then
            printf '%s' "$legacy" > "$temp"
        else
            head -c 32 /dev/urandom | od -An -tx1 | tr -d ' \n' > "$temp"
        fi
        chmod 600 "$temp"
        chown 10001:10001 "$temp"
        mv "$temp" "$secrets/$name"
        echo "created $name"
    fi
    [ "$(stat -c %a "$secrets/$name")" = 600 ] || {
        echo "credential permissions must be 0600: $name" >&2
        exit 1
    }
done
