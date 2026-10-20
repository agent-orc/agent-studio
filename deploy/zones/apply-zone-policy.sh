#!/bin/sh
# Render, apply and verify the zone network policy on a Docker host
# (AGT-3231). A dry run by default: it prints the plan, the rendered iptables
# and ip6tables rules and the zone resolver configuration, validates the rules
# with iptables-restore --test and changes nothing. With --execute (as root) it
# ensures the zone networks, ipsets and resolver, replaces the AGENT-ZONE-*
# chains, inserts their jumps, proves the rules with probe containers and
# records the result for the daemons' zone-policy capability. Rerunning it
# with an unchanged policy changes nothing. --remove rolls the rules back
# (again a dry run unless --execute).
#
#   deploy/zones/apply-zone-policy.sh [--execute] [--zones work] [--state FILE] [--env-file FILE]
#   deploy/zones/apply-zone-policy.sh --verify [--record] [--zones work]
#   deploy/zones/apply-zone-policy.sh --render [--out-dir DIR]
#   deploy/zones/apply-zone-policy.sh --remove [--execute] [--state FILE]
#
# The policy defaults to the checked-in zone-policy.json next to this script
# (override with --policy or ZONE_POLICY_FILE). AGENT_ZONES=work limits a work
# host to its zone. The agent-host binary is AGENT_HOST_BIN, else the
# installed /opt/agent-host/current/agent-host, else agent-host on PATH.
# See docs/operations/setup/zone-network-policy.md.
set -eu

here=$(cd "$(dirname "$0")" && pwd)

die()
{
    printf '[apply-zone-policy] ERROR: %s\n' "$*" >&2
    exit 2
}

resolve_agent_host()
{
    if [ -n "${AGENT_HOST_BIN:-}" ]; then
        printf '%s\n' "$AGENT_HOST_BIN"
        return 0
    fi
    for candidate in /opt/agent-host/current/agent-host /opt/agent-host/agent-host; do
        if [ -x "$candidate" ]; then
            printf '%s\n' "$candidate"
            return 0
        fi
    done
    command -v agent-host 2>/dev/null
}

verb=apply
case "${1:-}" in
    --verify) verb=verify; shift ;;
    --render) verb=render; shift ;;
    --remove) verb=remove; shift ;;
esac

has_policy=false
has_zones=false
for argument in "$@"; do
    case "$argument" in
        --policy) has_policy=true ;;
        --zones) has_zones=true ;;
        --execute)
            [ "$(id -u)" -eq 0 ] || die "--execute changes Docker networks, ipsets and iptables; run it as root."
            ;;
    esac
done

agent_host=$(resolve_agent_host) \
    || die "agent-host not found; set AGENT_HOST_BIN or install the agent host first."

# remove needs no policy: it deletes the jumps and the AGENT-ZONE-* chains.
if [ "$verb" != remove ] && [ "$has_policy" = false ]; then
    policy=${ZONE_POLICY_FILE:-$here/zone-policy.json}
    [ -f "$policy" ] || die "policy file $policy does not exist."
    set -- --policy "$policy" "$@"
fi
if [ "$verb" != remove ] && [ "$has_zones" = false ] && [ -n "${AGENT_ZONES:-}" ]; then
    set -- --zones "$AGENT_ZONES" "$@"
fi

exec "$agent_host" zone-policy "$verb" "$@"
