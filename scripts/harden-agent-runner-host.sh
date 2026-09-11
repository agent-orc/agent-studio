#!/usr/bin/env bash
# One-time root migration for the audited agent-host service account.
set -euo pipefail

readonly service_user="agent"
readonly service_group="agent"
readonly legacy_sudoers="/etc/sudoers.d/agent"
readonly installed_sudoers="/etc/sudoers.d/agent-runner"
readonly installed_helper="/usr/local/sbin/agent-runner-deploy"
readonly installed_policy="/usr/local/libexec/agent-runner-config-policy"
readonly installed_deps_validator="/usr/local/libexec/agent-runner-deps-closure"

usage() {
  printf '%s\n' \
    "Usage: harden-agent-runner-host.sh --apply" \
    "" \
    "Installs the versioned deploy/config helper and sudoers whitelist, removes" \
    "privileged service-account memberships, and verifies the result." \
    "Run from a root-owned operator session, not from an Agent CLI."
}

die() {
  printf 'agent-runner host hardening: %s\n' "$*" >&2
  exit 2
}

[[ "${1:-}" == "--apply" && "$#" -eq 1 ]] || { usage >&2; exit 2; }
[[ "$EUID" -eq 0 ]] || die "must run as root from an operator session"

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
sudoers_source="$repo_root/deploy/agent-host/sudoers.d/agent-runner"
helper_source="$repo_root/deploy/agent-host/agent-runner-deploy"
policy_source="$repo_root/deploy/agent-host/agent-runner-config-policy"
deps_validator_source="$repo_root/deploy/agent-host/agent-runner-deps-closure.py"
handoff_source="$repo_root/deploy/agent-host/systemd/10-agent-runner-hardening.conf"
restart_guard_source="$repo_root/deploy/agent-host/systemd/20-agent-runner-review-restart-guard.conf"

for required_source in \
  "$sudoers_source" \
  "$helper_source" \
  "$policy_source" \
  "$deps_validator_source" \
  "$handoff_source" \
  "$restart_guard_source"; do
  [[ -f "$required_source" ]] || die "versioned host asset is missing: $required_source"
done
command -v visudo >/dev/null || die "visudo is required"
command -v gpasswd >/dev/null || die "gpasswd is required"
[[ -x /usr/bin/python3 ]] || die "/usr/bin/python3 is required"
id "$service_user" >/dev/null 2>&1 || die "service account '$service_user' does not exist"
getent group "$service_group" >/dev/null || die "service group '$service_group' does not exist"

for unit in agent-runner.service agent-runner-review.service; do
  systemctl cat "$unit" >/dev/null || die "required service unit is missing: $unit"
done

candidate="$(mktemp)"
trap 'rm -f "$candidate"' EXIT
install -m 0440 "$sudoers_source" "$candidate"
visudo -cf "$candidate" >/dev/null || die "versioned sudoers source did not parse"

if [[ -e "$legacy_sudoers" ]]; then
  legacy_rule="$(sed -e 's/[[:space:]]//g' -e '/^#/d' -e '/^$/d' "$legacy_sudoers")"
  [[ "$legacy_rule" == "agentALL=(ALL)NOPASSWD:ALL" \
      || "$legacy_rule" == "agentALL=(ALL:ALL)NOPASSWD:ALL" ]] \
    || die "refusing to replace unexpected content in $legacy_sudoers"
fi

backup_root="/var/backups/agent-runner-hardening/$(date -u +%Y%m%dT%H%M%SZ)"
install -d -o root -g root -m 0700 "$backup_root"
[[ ! -e "$legacy_sudoers" ]] || cp -a "$legacy_sudoers" "$backup_root/legacy-sudoers"
[[ ! -e "$installed_sudoers" ]] || cp -a "$installed_sudoers" "$backup_root/previous-agent-runner-sudoers"
[[ ! -e "$installed_helper" ]] || cp -a "$installed_helper" "$backup_root/previous-agent-runner-deploy"
[[ ! -e "$installed_policy" ]] || cp -a "$installed_policy" "$backup_root/previous-agent-runner-config-policy"
[[ ! -e "$installed_deps_validator" ]] \
  || cp -a "$installed_deps_validator" "$backup_root/previous-agent-runner-deps-closure"
for unit in agent-runner.service agent-runner-review.service; do
  drop_in="/etc/systemd/system/$unit.d/10-agent-runner-hardening.conf"
  [[ ! -e "$drop_in" ]] || cp -a "$drop_in" "$backup_root/previous-$unit-handoff.conf"
done
review_restart_guard="/etc/systemd/system/agent-runner-review.service.d/20-agent-runner-review-restart-guard.conf"
[[ ! -e "$review_restart_guard" ]] \
  || cp -a "$review_restart_guard" "$backup_root/previous-agent-runner-review-restart-guard.conf"

install -d -o root -g root -m 0755 /usr/local/sbin /usr/local/libexec
install -o root -g root -m 0755 "$helper_source" "$installed_helper"
install -o root -g root -m 0755 "$policy_source" "$installed_policy"
install -o root -g root -m 0755 "$deps_validator_source" "$installed_deps_validator"
install -o root -g root -m 0440 "$sudoers_source" "$installed_sudoers"
visudo -c >/dev/null || die "installed sudoers policy did not parse"
rm -f -- "$legacy_sudoers"

for unit in agent-runner.service agent-runner-review.service; do
  drop_in_directory="/etc/systemd/system/$unit.d"
  install -d -o root -g root -m 0755 "$drop_in_directory"
  install -o root -g root -m 0644 \
    "$handoff_source" "$drop_in_directory/10-agent-runner-hardening.conf"
done
install -o root -g root -m 0644 \
  "$restart_guard_source" "$review_restart_guard"
systemctl daemon-reload
for unit in agent-runner.service agent-runner-review.service; do
  [[ "$(systemctl show "$unit" --property=KillMode --value)" == "process" ]] \
    || die "$unit did not adopt KillMode=process"
  [[ "$(systemctl show "$unit" --property=PrivateTmp --value)" == "no" ]] \
    || die "$unit did not adopt PrivateTmp=false"
done
[[ "$(systemctl show agent-runner-review.service --property=RefuseManualStop --value)" == "yes" ]] \
  || die "agent-runner-review.service did not adopt RefuseManualStop=true"
[[ "$(systemctl show agent-runner-review.service --property=Restart --value)" == "on-failure" ]] \
  || die "agent-runner-review.service did not adopt Restart=on-failure"
[[ "$(systemctl show agent-runner.service --property=RefuseManualStop --value)" != "yes" ]] \
  || die "RefuseManualStop must remain review-only"

for privileged_group in sudo docker; do
  if id -nG "$service_user" | tr ' ' '\n' | grep -Fxq "$privileged_group"; then
    gpasswd -d "$service_user" "$privileged_group" >/dev/null
  fi
done

install -d -o root -g root -m 0755 \
  /var/lib/agent-runner/deploy \
  /var/lib/agent-runner/deploy/accepted
install -d -o "$service_user" -g "$service_group" -m 0750 \
  /var/lib/agent-runner/deploy/incoming

visudo -c >/dev/null || die "final sudoers policy did not parse"
groups_after="$(id -nG "$service_user")"
for privileged_group in sudo docker; do
  ! tr ' ' '\n' <<<"$groups_after" | grep -Fxq "$privileged_group" \
    || die "service account remains in privileged group '$privileged_group'"
done

sudo_list="$(sudo -l -U "$service_user")"
! grep -Eq 'NOPASSWD:[[:space:]]*ALL([[:space:]]|$)' <<<"$sudo_list" \
  || die "service account still has an unrestricted NOPASSWD rule"
grep -Fq '/usr/local/sbin/agent-runner-deploy ""' <<<"$sudo_list" \
  || die "deploy helper is missing from the effective sudo policy"
grep -Fq '/usr/local/sbin/agent-runner-deploy restart-review' <<<"$sudo_list" \
  || die "sanctioned Review replacement is missing from the effective sudo policy"
grep -Fq 'config review RUNNER_MAX_PARALLELISM 6' <<<"$sudo_list" \
  || die "bounded role configuration is missing from the effective sudo policy"
! grep -Fq 'systemctl restart agent-runner-review.service' <<<"$sudo_list" \
  || die "direct Review restart remains in the effective sudo policy"
! grep -Fq 'agent-runner-deploy --force' <<<"$sudo_list" \
  || die "the service account can bypass the Review restart guard"

printf 'agent-runner host hardening: applied\n'
printf '  backup: %s\n' "$backup_root"
printf '  groups: %s\n' "$groups_after"
printf '  sudoers: %s\n' "$installed_sudoers"
printf '  deploy helper: %s\n' "$installed_helper"
printf '  config policy: %s\n' "$installed_policy"
printf '  dependency validator: %s\n' "$installed_deps_validator"
printf '  review restart guard: %s\n' "$review_restart_guard"
printf '%s\n' \
  "Existing login sessions retain supplementary groups. End them, restart Coding, and run 'agent-runner-deploy drain' for Review before acceptance."
