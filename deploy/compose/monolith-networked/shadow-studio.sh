#!/bin/sh
set -eu
case "${1-status}" in
    --help|-h)
        cat <<'HELP'
Usage: shadow-studio.sh start [IMAGE-TAG] | status | stop
Start with the two live image IDs, or use the specified locally installed release.
STUDIO_SHADOW_LIMIT_MIB defaults to 4096. STUDIO_SHADOW_LIFETIME_SECONDS defaults to 1800.
At least 15 percent free on the scratch filesystem is required at start.
STUDIO_STACK_DIR selects the installed Compose directory (default: beside this tool).
STUDIO_COPY_STATE_DIR selects private scratch storage (default: /var/lib/agent-studio-copies).
HELP
        exit 0 ;;
    start) [ "$#" -le 2 ] || exit 2 ;;
    status|stop) [ "$#" -le 1 ] || exit 2 ;;
    *) echo 'See shadow-studio.sh --help' >&2; exit 2 ;;
esac
[ "$(id -u)" = 0 ] || { echo 'Root is required for the shadow.' >&2; exit 2; }
umask 077
exec python3 -I "$(dirname "$0")/studio-copy.py" shadow "${1-status}" "${2-}"
