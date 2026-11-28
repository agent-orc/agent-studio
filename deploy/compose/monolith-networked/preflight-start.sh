#!/bin/sh
set -eu
if [ "${1:-}" = --help ] || [ "${1:-}" = -h ]; then
    cat <<'HELP'
Usage: preflight-start.sh IMAGE-TAG [DEADLINE-SECONDS]
Start a locally installed release on isolated overlays. Default deadline: 240s.
Print the health result and elapsed seconds after cleanup.
STUDIO_STACK_DIR selects the installed Compose directory (default: beside this tool).
STUDIO_COPY_STATE_DIR selects private scratch storage (default: /var/lib/agent-studio-copies).
HELP
    exit 0
fi
if [ "$#" -lt 1 ] || [ "$#" -gt 2 ]; then
    echo 'See preflight-start.sh --help' >&2
    echo 'result=unhealthy seconds=0'
    exit 2
fi
if [ "$(id -u)" != 0 ]; then
    echo 'Root is required for overlay mounts.' >&2
    echo 'result=unhealthy seconds=0'
    exit 2
fi
umask 077
exec python3 -I "$(dirname "$0")/studio-copy.py" preflight "$1" "${2-240}"
