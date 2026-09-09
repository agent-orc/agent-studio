#!/bin/sh
# Prove task-server-01 has no public-interface listener for the API, health,
# or management surface. Run locally on the host for the socket-binding
# proof; pass a public hostname or IP as $1 to also prove, from wherever you
# invoke this script, that a real connection to that address is refused.
# A same-host loopback check cannot stand in for that second proof, so the
# argument form should be run from a separate machine (your workstation, not
# task-server-01 itself) before Phase B is called complete.
set -eu

WG_INTERFACE=${WG_INTERFACE:-wg0}
PORTS='80 443 5030 5031 5071'
public_target=${1:-}

log()
{
    printf '[verify-no-public-listener] %s\n' "$*"
}

fail=0

if command -v ss >/dev/null 2>&1; then
    wg_address=$(ip -4 -o addr show dev "$WG_INTERFACE" 2>/dev/null | awk '{print $4}' | cut -d/ -f1 || true)
    [ -n "$wg_address" ] || {
        printf 'FAIL: no IPv4 address on %s; is WireGuard up?\n' "$WG_INTERFACE" >&2
        exit 1
    }
    for port in $PORTS; do
        listeners=$(ss -Htln "sport = :$port" 2>/dev/null | awk '{print $4}')
        for listener in $listeners; do
            addr=${listener%:*}
            case "$addr" in
                127.0.0.1 | '[::1]' | "$wg_address") ;;
                *)
                    printf 'FAIL: port %s is bound to %s (not loopback or %s)\n' \
                        "$port" "$addr" "$WG_INTERFACE" >&2
                    fail=1
                    ;;
            esac
        done
    done
    [ "$fail" -eq 0 ] && log "Socket check: no listener on $PORTS outside loopback or $WG_INTERFACE ($wg_address)."
else
    printf 'FAIL: ss is required for the local socket-binding proof.\n' >&2
    fail=1
fi

if [ -n "$public_target" ]; then
    for port in 80 443; do
        if command -v curl >/dev/null 2>&1 \
            && curl --max-time 5 --silent --show-error --fail \
                "http://$public_target:$port/healthz" >/dev/null 2>&1; then
            printf 'FAIL: %s:%s answered a request from the public address\n' "$public_target" "$port" >&2
            fail=1
        else
            log "Connection test: $public_target:$port did not answer (expected)."
        fi
    done
else
    log "No public hostname/IP given; skipping the external connection test. Pass one to complete the proof."
fi

if [ "$fail" -eq 0 ]; then
    log "OK: no public-interface listener found for {$PORTS}."
fi
exit "$fail"
