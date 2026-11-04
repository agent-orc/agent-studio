#!/bin/sh
# Run as root on an enrolled host after reviewing the dedicated operations account.
set -eu
if [ "$(id -u)" -ne 0 ]; then echo 'root-required' >&2; exit 1; fi
if [ "$#" -ne 1 ] || [ ! -f "$1" ]; then echo 'ca-public-key-required' >&2; exit 1; fi
account=agent-operations
service_account=agent-operations-service
if ! id "$account" >/dev/null 2>&1; then
  useradd --system --user-group --create-home --shell /bin/sh "$account"
fi
if ! id "$service_account" >/dev/null 2>&1; then
  useradd --system --user-group --no-create-home --shell /usr/sbin/nologin "$service_account"
fi
for name in "$account" "$service_account"; do
  if ! getent group "$name" >/dev/null; then
    groupadd --system "$name"
    usermod --gid "$name" "$name"
  fi
done
home_dir=$(getent passwd "$account" | cut -d: -f6)
if [ -z "$home_dir" ] || [ ! -d "$home_dir" ]; then echo 'operations-home-missing' >&2; exit 1; fi
install -d -m 700 -o "$account" -g "$account" "$home_dir/.ssh"
install -d -m 700 -o "$service_account" -g "$service_account" /var/lib/agent-operations
# This account is dedicated to certificates. Drop every standing authorized key.
install -m 600 -o "$account" -g "$account" /dev/null "$home_dir/.ssh/authorized_keys"
install -m 644 "$1" /etc/ssh/agent-operations-ca.pub
install -d -m 755 /usr/local/libexec
install -m 755 "$(dirname "$0")/agent-operations-command" /usr/local/libexec/agent-operations-command
if [ ! -f /var/lib/agent-operations/revoked.krl ]; then
  ssh-keygen -q -k -f /var/lib/agent-operations/revoked.krl -z 1
fi
chown "$service_account:$service_account" /var/lib/agent-operations/revoked.krl
chmod 600 /var/lib/agent-operations/revoked.krl
cat >/etc/ssh/sshd_config.d/60-agent-operations.conf <<'EOF'
TrustedUserCAKeys /etc/ssh/agent-operations-ca.pub
RevokedKeys /var/lib/agent-operations/revoked.krl
Match User agent-operations
    AuthorizedKeysFile none
    PasswordAuthentication no
    AuthenticationMethods publickey
    ForceCommand /usr/local/libexec/agent-operations-command
    PermitTTY no
    AllowTcpForwarding no
    AllowAgentForwarding no
    X11Forwarding no
    PermitTunnel no
EOF
sshd -t
effective=$(sshd -T -C "user=$account,host=localhost,addr=127.0.0.1")
printf '%s\n' "$effective" | grep -qx 'authorizedkeysfile none' || { echo 'standing-key-login-still-enabled' >&2; exit 1; }
printf '%s\n' "$effective" | grep -qx 'trustedusercakeys /etc/ssh/agent-operations-ca.pub' || { echo 'operations-ca-not-effective' >&2; exit 1; }
printf '%s\n' "$effective" | grep -qx 'revokedkeys /var/lib/agent-operations/revoked.krl' || { echo 'operations-revocation-not-effective' >&2; exit 1; }
printf '%s\n' "$effective" | grep -qx 'forcecommand /usr/local/libexec/agent-operations-command' || { echo 'operations-command-not-effective' >&2; exit 1; }
systemctl reload sshd 2>/dev/null || systemctl reload ssh
echo 'operations-certificate-account-ready; standing authorized keys removed'
