#!/usr/bin/env bash
# Host verification for the runner temp namespace (AGT-2750).
#
# PrivateTmp is bound to the unit lifecycle. Because the runner units also use
# KillMode=process, a restart unmounts the unit's /tmp while detached workers
# keep running on the deleted mount, and every build or test they still have to
# run fails with MSB1025 or mkdtemp ENOENT. This script proves the shipped
# configuration on a real host.
set -euo pipefail

units=(agent-runner.service agent-runner-review.service)
restart_probe=0
probe_unit="agent-runner-review.service"
probe_timeout=900

usage() {
  printf '%s\n' \
    "Usage: verify-runner-tmp-namespace.sh [--restart-probe] [options]" \
    "" \
    "Default (read-only): asserts that every runner unit reports PrivateTmp=no" \
    "and that no live worker of those units holds a deleted /tmp mount." \
    "" \
    "Options:" \
    "  --restart-probe        Also restart a unit while one of its slots is busy" \
    "                         and assert the slot completes with parsed results." \
    "                         Disruptive. Run it on a host you may interrupt." \
    "  --probe-unit <unit>    Unit for the restart probe (default: $probe_unit)" \
    "  --probe-timeout <sec>  Seconds to wait for the slot (default: $probe_timeout)" \
    "  -h, --help             Show this help"
}

die() {
  printf 'runner tmp namespace: %s\n' "$*" >&2
  exit 2
}

note() {
  printf 'runner tmp namespace: %s\n' "$*"
}

while (($#)); do
  case "$1" in
    --restart-probe) restart_probe=1; shift ;;
    --probe-unit) probe_unit="${2:-}"; shift 2 ;;
    --probe-timeout) probe_timeout="${2:-}"; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) usage >&2; exit 2 ;;
  esac
done

command -v systemctl >/dev/null || die "systemctl is required"
[[ -r /proc/self/mountinfo ]] || die "/proc is required"

# 1. The units must no longer own a lifecycle-bound temp namespace.
for unit in "${units[@]}"; do
  systemctl cat "$unit" >/dev/null 2>&1 || { note "skipping absent unit $unit"; continue; }
  private_tmp="$(systemctl show "$unit" --property=PrivateTmp --value)"
  [[ "$private_tmp" == "no" ]] \
    || die "$unit reports PrivateTmp=$private_tmp; re-run harden-agent-runner-host.sh --apply"
  kill_mode="$(systemctl show "$unit" --property=KillMode --value)"
  [[ "$kill_mode" == "process" ]] \
    || die "$unit reports KillMode=$kill_mode; detached workers would not survive a restart"
  note "$unit: PrivateTmp=no KillMode=process"
done

# 2. No live process of these units may hold a deleted /tmp mount. Two kinds
#    show up: a job worker stranded mid-run by a pre-fix restart, and a reusable
#    MSBuild node daemon (/nodemode:1 /nodeReuse:true) left behind by an earlier
#    build. The node daemons are the more common of the two and actively poison
#    later builds, because a build in the fresh namespace still tries to reach
#    them through /tmp/MSBuild<pid>.
stranded=0
for status in /proc/[0-9]*/status; do
  pid="${status%/status}"
  pid="${pid#/proc/}"
  cgroup="$(cat "/proc/$pid/cgroup" 2>/dev/null || true)"
  case "$cgroup" in
    *agent-runner*) ;;
    *) continue ;;
  esac
  # Match inside awk rather than globbing its output: a process can have more
  # than one /tmp mount, and only the last line would reach a shell glob.
  mount_root="$(awk '$5 == "/tmp" && $4 ~ /\/\/deleted$/ { print $4; exit }' \
    "/proc/$pid/mountinfo" 2>/dev/null || true)"
  if [[ -n "$mount_root" ]]; then
    printf 'runner tmp namespace: pid %s (%s) holds a deleted /tmp mount: %s\n' \
      "$pid" \
      "$(tr '\0' ' ' <"/proc/$pid/cmdline" 2>/dev/null | cut -c1-80)" \
      "$mount_root" >&2
    stranded=$((stranded + 1))
  fi
done
((stranded == 0)) || die \
  "$stranded process(es) stranded on a deleted /tmp. They cannot build or test, and
   leftover MSBuild nodes among them break later builds with MSB1025. Terminate
   them once their unit has no busy slot, then re-run this check."
note "no process holds a deleted /tmp mount"

((restart_probe == 1)) || { note "read-only checks passed"; exit 0; }

# 3. Restart the unit while a slot is busy and assert the slot still finishes
#    with parsed results rather than an infrastructure or unparsed-failure grade.
[[ "$EUID" -eq 0 ]] || die "--restart-probe must run as root"
systemctl cat "$probe_unit" >/dev/null 2>&1 || die "unknown probe unit: $probe_unit"

# A live worker process, not a journal line: the point of the probe is that a
# slot is busy right now, and a "worker started" line may already have finished.
busy_before=0
for status in /proc/[0-9]*/status; do
  pid="${status%/status}"
  pid="${pid#/proc/}"
  grep -q "${probe_unit%.service}" "/proc/$pid/cgroup" 2>/dev/null || continue
  grep -qE 'dotnet|node|npm' <(tr '\0' ' ' <"/proc/$pid/cmdline" 2>/dev/null) \
    && busy_before=$((busy_before + 1))
done
((busy_before > 0)) \
  || die "no build or test process is running under $probe_unit; start a review first"
note "$busy_before build or test process(es) running under $probe_unit"

# Epoch form: journalctl parses a bare timestamp as LOCAL time, so a UTC string
# would shift the window by the host's offset and read pre-restart lines.
since="@$(date +%s)"
note "restarting $probe_unit with a busy slot"
systemctl restart "$probe_unit"

deadline=$((SECONDS + probe_timeout))
while ((SECONDS < deadline)); do
  log="$(journalctl -u "$probe_unit" --since "$since")"
  if grep -qE 'review adoption failed|HostTempUnavailable|MSB1025' <<<"$log"; then
    printf '%s\n' "$log" >&2
    die "the slot did not survive the restart"
  fi
  if grep -qE 'review (report accepted|outcome) .*(BaselineCompared|NewTestFailures|Pass)' <<<"$log"; then
    note "slot completed with parsed results after the restart"
    grep -E 'persisted review accepted|adopting persisted review|review (report accepted|outcome)' \
      <<<"$log"
    exit 0
  fi
  sleep 10
done
die "the slot did not report within ${probe_timeout}s"
