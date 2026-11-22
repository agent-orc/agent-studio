#!/usr/bin/env bash
# Operator bootstrap for hosts whose installed helper predates update-clis.
# Run as root from a reviewed release checkout, never through an agent sudo rule.
set -euo pipefail
[[ "$EUID" == 0 ]] || { echo 'Run from a root operator session.' >&2; exit 2; }
[[ "${1:-}" == --apply && $# == 1 ]] || { echo 'Usage: upgrade-agent-runner-deploy.sh --apply' >&2; exit 2; }
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly destination_root=""
asset_root="$repo_root/deploy/agent-host"
backup="$destination_root/var/backups/agent-runner-deploy/$(date -u +%Y%m%dT%H%M%SZ)-$$"
install -d -m 0755 "$destination_root/var/lib/agent-runner/deploy"
exec 9>"$destination_root/var/lib/agent-runner/deploy/.deploy.lock"
flock -n 9 || { echo 'Another deployment is running.' >&2; exit 2; }
# Validate before changing the installed policy or helper.
visudo -cf "$asset_root/sudoers.d/agent-runner"
bash -n "$asset_root/agent-runner-deploy"
install -d -m 0700 "$backup"
install -d -m 0755 "$destination_root/usr/local/sbin" "$destination_root/usr/local/libexec" "$destination_root/etc/sudoers.d"
rollback="$backup/rollback.sh"
(umask 077; printf '#!/usr/bin/env bash\nset -euo pipefail\n' >"$rollback")
candidate=""
restore_on_failure() {
  local status=$?
  [[ -z "$candidate" ]] || rm -f -- "$candidate"
  if ((status != 0)); then
    bash "$rollback"
    printf 'Upgrade failed; previous files restored. Backup: %s\n' "$backup" >&2
  fi
  exit "$status"
}
trap restore_on_failure EXIT
for pair in \
  'agent-runner-deploy usr/local/sbin/agent-runner-deploy' \
  'agent-runner-config-policy usr/local/libexec/agent-runner-config-policy' \
  'agent-runner-deps-closure.py usr/local/libexec/agent-runner-deps-closure' \
  'sudoers.d/agent-runner etc/sudoers.d/agent-runner'; do
  read -r source target <<<"$pair"
  installed="$destination_root/$target"
  if [[ -e "$installed" || -L "$installed" ]]; then
    cp -a --parents "$installed" "$backup/"
    printf 'cp -a --remove-destination -- %q %q\n' "$backup$installed" "$installed" >>"$rollback"
  else
    printf 'rm -f -- %q\n' "$installed" >>"$rollback"
  fi
  mode=0755
  [[ "$source" != sudoers.d/* ]] || mode=0440
  # Rename from the destination directory so readers see a complete file.
  candidate="$(mktemp "$(dirname "$installed")/.deploy-upgrade.XXXXXX")"
  install -o root -g root -m "$mode" "$asset_root/$source" "$candidate"
  mv -fT "$candidate" "$installed"
done
visudo -c
trap - EXIT
printf 'Deploy helper and exact CLI sudo rule refreshed. Backup: %s\n' "$backup"
