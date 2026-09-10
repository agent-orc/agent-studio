#!/bin/sh
# Default-deny firewall for task-server-01. Only SSH and the WireGuard
# handshake are reachable on the public interface; TCP 443 (the private TLS
# edge) is reachable only on the WireGuard interface. Run as root after
# WireGuard is configured (the interface named by WG_INTERFACE must already
# exist) and before opening the control plane to normal admission. See
# docs/operations/setup/control-plane-docker.md.
set -eu

WG_INTERFACE=${WG_INTERFACE:-wg0}
WG_PORT=${WG_PORT:-51820}
SSH_PORT=${SSH_PORT:-22}
# Restrict SSH to a known source range once one is available, e.g.
# SSH_SOURCE=203.0.113.4/32. Left empty, SSH stays open to any source and the
# host depends on key-only authentication; narrow this before production use.
SSH_SOURCE=${SSH_SOURCE:-}

log()
{
    printf '[configure-firewall] %s\n' "$*" >&2
}

die()
{
    printf '[configure-firewall] ERROR: %s\n' "$*" >&2
    exit 1
}

[ "$(id -u)" -eq 0 ] || die "Run this script as root."
command -v ufw >/dev/null 2>&1 || die "ufw is required."
ip link show "$WG_INTERFACE" >/dev/null 2>&1 \
    || die "Interface $WG_INTERFACE does not exist yet. Bring up WireGuard first."

ufw --force reset
ufw default deny incoming
ufw default allow outgoing

if [ -n "$SSH_SOURCE" ]; then
    ufw allow from "$SSH_SOURCE" to any port "$SSH_PORT" proto tcp comment 'admin ssh'
    log "SSH restricted to $SSH_SOURCE."
else
    ufw allow "$SSH_PORT"/tcp comment 'admin ssh (unrestricted source; narrow with SSH_SOURCE)'
    log "SSH left open to any source. Set SSH_SOURCE and rerun to narrow it."
fi

ufw allow "$WG_PORT"/udp comment 'wireguard handshake'
ufw allow in on "$WG_INTERFACE" to any port 443 proto tcp comment 'private tls edge'

ufw --force enable
ufw status verbose

log "Firewall applied. Public interface: SSH ($SSH_PORT/tcp) and WireGuard handshake ($WG_PORT/udp) only."
log "TCP 443 admitted only on $WG_INTERFACE. No rule permits 80, 443, 5030, 5031, or 5071 on the public interface."
log "Run network/verify-no-public-listener.sh next to prove the binding."
