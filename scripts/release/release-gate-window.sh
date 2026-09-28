#!/usr/bin/env bash
# Reserved-capacity window for the develop -> main promotion gate (AGT-2982).
#
# The promotion train shares its host with the coding and review runner units.
# While the wrapped gate command runs, this helper throttles both units with a
# runtime CPUQuota, waits a bounded time for a hot host to cool down, measures
# the load at gate start and end, and restores the recorded quotas on every
# exit path: success, failure, and INT/TERM/HUP. The gate runs in its own
# process group; on a signal the whole group is stopped (TERM, then KILL after
# a grace period) before the quotas are restored.
#
# Design decision: a runtime CPUQuota window, not a dedicated high-CPUWeight
# slice. See docs/operations/develop-main-promotion.md#gate-capacity-window.

set -Eeuo pipefail

readonly window_units=(agent-runner.service agent-runner-review.service)

usage() {
  cat <<'EOF'
Usage: release-gate-window.sh --record <file> -- <gate command> [args...]

Throttles agent-runner.service and agent-runner-review.service for the duration
of the gate command and restores their recorded CPUQuota afterwards. The exit
code is the gate command's exit code, 75 when RELEASE_GATE_WINDOW=required and
the window cannot be applied, or 128+N when the helper receives signal N.

Environment:
  RELEASE_GATE_WINDOW             auto (default): apply when the units are loaded, warn and
                                  continue unthrottled when the quota cannot be set;
                                  required: refuse to run the gate without the window;
                                  off: never touch the units (load evidence is still recorded).
  RELEASE_GATE_RESERVED_CORES     Cores left to the gate (default: 5).
  RELEASE_GATE_CODING_QUOTA       Explicit window CPUQuota for agent-runner.service, e.g. 200%.
  RELEASE_GATE_REVIEW_QUOTA       Explicit window CPUQuota for agent-runner-review.service.
  RELEASE_GATE_MAX_LOAD_FACTOR    Hot-host threshold as a multiple of the core count (default: 2).
  RELEASE_GATE_SETTLE_SECONDS     Longest wait for a hot host to cool before the gate starts (default: 300).
  RELEASE_GATE_POLL_SECONDS       Load poll interval while waiting (default: 15).
  RELEASE_GATE_STOP_GRACE_SECONDS On a signal, how long the gate process group gets to exit after
                                  SIGTERM before it is sent SIGKILL (default: 30).
  RELEASE_GATE_CPU_COUNT          Core count override (default: nproc).
  RELEASE_GATE_LOADAVG_FILE       Load source (default: /proc/loadavg).
  RELEASE_GATE_SYSTEMCTL          systemctl path (default: /usr/bin/systemctl, the sudoers path).
  RELEASE_GATE_SUDO               Privilege prefix for set-property (default: "sudo -n", empty as root).
EOF
}

gate_window_log() {
  printf '[release-gate-window] %s\n' "$*" >&2 || true
}

# --- Pure policy -------------------------------------------------------------

# Succeeds when the 1-minute load is above factor x cores.
gate_load_is_hot() {
  local load=$1 cores=$2 factor=$3
  awk -v current="$load" -v cores="$cores" -v factor="$factor" \
    'BEGIN { exit !(current + 0 > cores * factor) }'
}

# Prints the hot-host threshold (factor x cores) with two decimals.
gate_load_threshold() {
  awk -v cores="$1" -v factor="$2" 'BEGIN { printf "%.2f", cores * factor }'
}

# Prints "<coding>% <review>%" for the runner budget left after reserving
# cores for the gate. The split mirrors the manual window of 2026-09-27
# (review 5 cores, coding 2 cores on 12 cores with 5 reserved): coding gets
# two sevenths, review the rest, each at least one core.
gate_window_default_quotas() {
  local cores=$1 reserved=$2
  local budget=$((cores - reserved))
  ((budget >= 2)) || budget=2
  local coding=$((budget * 2 / 7))
  ((coding >= 1)) || coding=1
  local review=$((budget - coding))
  ((review >= 1)) || review=1
  printf '%s%% %s%%\n' "$((coding * 100))" "$((review * 100))"
}

# Never raise a unit above its recorded quota: prints the lower of the
# recorded value (empty = infinity) and the window value, both in N% form.
gate_window_effective_quota() {
  local recorded=$1 window=$2
  if [[ -z "$recorded" ]] || ((${window%\%} < ${recorded%\%})); then
    printf '%s\n' "$window"
  else
    printf '%s\n' "$recorded"
  fi
}

# Converts systemd's CPUQuotaPerSecUSec (e.g. "infinity", "12s", "7.200000s",
# "500ms", "1min 12s") to "N%"; infinity prints an empty string.
gate_window_quota_percent() {
  local value=$1
  [[ "$value" != infinity && -n "$value" ]] || { printf '\n'; return 0; }
  awk -v value="$value" 'BEGIN {
    n = split(value, parts, " ")
    total = 0
    for (i = 1; i <= n; i++) {
      part = parts[i]
      if (match(part, /^[0-9.]+/) == 0) exit 2
      number = substr(part, 1, RLENGTH) + 0
      unit = substr(part, RLENGTH + 1)
      if (unit == "us") total += number
      else if (unit == "ms") total += number * 1000
      else if (unit == "s") total += number * 1000000
      else if (unit == "min") total += number * 60000000
      else exit 2
    }
    percent = int(total / 10000 + 0.5)
    if (percent < 1) percent = 1
    printf "%d%%\n", percent
  }'
}

# --- Host effects ------------------------------------------------------------

gate_window_read_load() {
  local first _
  read -r first _ < "$loadavg_file"
  printf '%s\n' "$first"
}

gate_window_set_quota() {
  local unit=$1 quota=$2
  "${sudo_prefix[@]}" "$systemctl" set-property --runtime "$unit" "CPUQuota=$quota"
}

# Stops every process in the gate's process group: SIGTERM first, SIGKILL for
# whatever is still alive after the grace period. Returns only once no process
# of the group remains, so the caller never restores quotas under live gate work.
gate_window_stop_group() {
  local pgid=$1 grace=$2 deadline killed=
  kill -TERM -- "-$pgid" 2>/dev/null || return 0
  deadline=$((SECONDS + grace))
  while kill -0 -- "-$pgid" 2>/dev/null; do
    if [[ -z "$killed" ]] && ((SECONDS >= deadline)); then
      gate_window_log "gate process group $pgid outlived SIGTERM by ${grace}s; sending SIGKILL"
      kill -KILL -- "-$pgid" 2>/dev/null || true
      killed=1
    fi
    # Waiting for this foreground sleep also reaps the exited group leader.
    sleep 0.2
  done
}

record_value() {
  printf '%s=%s\n' "$1" "$2" >> "$record_file"
}

main() {
  record_file=
  while (($#)); do
    case "$1" in
      --record)
        (($# >= 2)) || { usage >&2; exit 2; }
        record_file=$2
        shift 2
        ;;
      --)
        shift
        break
        ;;
      -h|--help)
        usage
        exit 0
        ;;
      *)
        printf 'Unknown argument: %s\n' "$1" >&2
        usage >&2
        exit 2
        ;;
    esac
  done
  (($#)) || { usage >&2; exit 2; }
  [[ -n "$record_file" ]] || { usage >&2; exit 2; }

  local mode=${RELEASE_GATE_WINDOW:-auto}
  [[ "$mode" =~ ^(auto|required|off)$ ]] || {
    printf 'RELEASE_GATE_WINDOW must be auto, required, or off: %s\n' "$mode" >&2
    exit 2
  }
  local reserved=${RELEASE_GATE_RESERVED_CORES:-5}
  local factor=${RELEASE_GATE_MAX_LOAD_FACTOR:-2}
  local settle=${RELEASE_GATE_SETTLE_SECONDS:-300}
  local poll=${RELEASE_GATE_POLL_SECONDS:-15}
  stop_grace=${RELEASE_GATE_STOP_GRACE_SECONDS:-30}
  local cores=${RELEASE_GATE_CPU_COUNT:-$(nproc 2>/dev/null || getconf _NPROCESSORS_ONLN)}
  loadavg_file=${RELEASE_GATE_LOADAVG_FILE:-/proc/loadavg}
  systemctl=${RELEASE_GATE_SYSTEMCTL:-/usr/bin/systemctl}
  if [[ -n ${RELEASE_GATE_SUDO+set} ]]; then
    read -r -a sudo_prefix <<< "$RELEASE_GATE_SUDO"
  elif ((EUID == 0)); then
    sudo_prefix=()
  else
    sudo_prefix=(sudo -n)
  fi
  local number
  for number in "$reserved" "$settle" "$poll" "$cores" "$stop_grace"; do
    [[ "$number" =~ ^[0-9]+$ ]] || {
      printf 'Gate window numbers must be non-negative integers: %s\n' "$number" >&2
      exit 2
    }
  done
  ((cores >= 1 && poll >= 1)) || { printf 'Core count and poll interval must be positive.\n' >&2; exit 2; }
  [[ "$factor" =~ ^[0-9]+([.][0-9]+)?$ ]] || {
    printf 'RELEASE_GATE_MAX_LOAD_FACTOR must be a positive number: %s\n' "$factor" >&2
    exit 2
  }

  local default_quotas coding_window review_window
  default_quotas=$(gate_window_default_quotas "$cores" "$reserved")
  coding_window=${RELEASE_GATE_CODING_QUOTA:-${default_quotas% *}}
  review_window=${RELEASE_GATE_REVIEW_QUOTA:-${default_quotas#* }}
  local quota
  for quota in "$coding_window" "$review_window"; do
    [[ "$quota" =~ ^[1-9][0-9]{0,5}%$ ]] || {
      printf 'Window CPUQuota must look like 200%%: %s\n' "$quota" >&2
      exit 2
    }
  done

  : > "$record_file"
  record_value window-mode-requested "$mode"
  record_value cpu-count "$cores"
  record_value reserved-cores "$reserved"
  record_value load-threshold "$(gate_load_threshold "$cores" "$factor")"

  # Record the current quota of every loaded runner unit before touching any.
  declare -gA recorded_quota=() applied_quota=()
  applied_units=()
  local unit load_state quota_usec window_status=off
  if [[ "$mode" != off ]]; then
    window_status=skipped
    for unit in "${window_units[@]}"; do
      load_state=$("$systemctl" show -p LoadState --value "$unit" 2>/dev/null || true)
      if [[ "$load_state" != loaded ]]; then
        gate_window_log "$unit is not loaded on this host; nothing to throttle"
        continue
      fi
      quota_usec=$("$systemctl" show -p CPUQuotaPerSecUSec --value "$unit")
      recorded_quota[$unit]=$(gate_window_quota_percent "$quota_usec")
      record_value "recorded:$unit" "${recorded_quota[$unit]:-infinity}"
    done
  fi

  restored=0
  restore_status=not-needed
  restore_quotas() {
    ((restored == 0)) || return 0
    restored=1
    ((${#applied_units[@]} > 0)) || return 0
    restore_status=restored
    local unit
    for unit in "${applied_units[@]}"; do
      if gate_window_set_quota "$unit" "${recorded_quota[$unit]}"; then
        gate_window_log "restored $unit CPUQuota=${recorded_quota[$unit]:-infinity}"
      else
        restore_status=failed
        gate_window_log "FAILED to restore $unit CPUQuota=${recorded_quota[$unit]:-infinity}; restore it by hand"
      fi
    done
    record_value quotas-restored "$restore_status"
  }

  child_pid=
  gate_started=
  gate_launching=
  pending_signal=
  on_signal() {
    local signal=$1 code=$2
    if [[ -n "$gate_launching" && -z "$child_pid" ]]; then
      # The gate is being forked and its process group is not known yet;
      # the launch sequence replays this signal once child_pid is set.
      pending_signal="$signal $code"
      return 0
    fi
    trap - INT TERM HUP
    # The train pipes this helper into tee, which dies with the same INT; a
    # log write must not turn into a SIGPIPE death before the quotas are back.
    trap '' PIPE
    gate_window_log "received SIG$signal; stopping the gate process group and restoring runner quotas"
    if [[ -n "$child_pid" ]]; then
      gate_window_stop_group "$child_pid" "$stop_grace"
      wait "$child_pid" 2>/dev/null || true
    fi
    [[ -z "$gate_started" ]] \
      || record_value gate-duration-seconds "$(($(date +%s) - gate_started))"
    record_value load-at-gate-end "$(gate_window_read_load)"
    record_value gate-exit "signal-$signal"
    restore_quotas
    exit "$code"
  }
  trap restore_quotas EXIT
  trap 'on_signal INT 130' INT
  trap 'on_signal TERM 143' TERM
  trap 'on_signal HUP 129' HUP

  local effective
  if ((${#recorded_quota[@]} > 0)); then
    window_status=applied
    for unit in "${window_units[@]}"; do
      [[ -n ${recorded_quota[$unit]+set} ]] || continue
      if [[ "$unit" == agent-runner.service ]]; then
        effective=$(gate_window_effective_quota "${recorded_quota[$unit]}" "$coding_window")
      else
        effective=$(gate_window_effective_quota "${recorded_quota[$unit]}" "$review_window")
      fi
      # Register before applying so a signal between the two still restores.
      applied_units+=("$unit")
      if gate_window_set_quota "$unit" "$effective"; then
        applied_quota[$unit]=$effective
        record_value "applied:$unit" "$effective"
        gate_window_log "throttled $unit CPUQuota=$effective (recorded ${recorded_quota[$unit]:-infinity})"
      else
        unset 'applied_units[-1]'
        window_status=failed
        gate_window_log "could not set $unit CPUQuota=$effective (sudoers rule missing?)"
        break
      fi
    done
    if [[ "$window_status" == failed ]]; then
      restore_quotas
      applied_quota=()
      if [[ "$mode" == required ]]; then
        record_value window-mode "$window_status"
        gate_window_log 'RELEASE_GATE_WINDOW=required and the window could not be applied; the gate did not run'
        exit 75
      fi
      window_status=unavailable
      gate_window_log 'continuing without reserved capacity (RELEASE_GATE_WINDOW=auto)'
    fi
  elif [[ "$mode" == required ]]; then
    record_value window-mode failed
    gate_window_log 'RELEASE_GATE_WINDOW=required but no runner unit is loaded; the gate did not run'
    exit 75
  fi
  record_value window-mode "$window_status"

  # Refuse to start hot: wait a bounded time for the throttle to take effect.
  local load waited=0
  load=$(gate_window_read_load)
  record_value load-before-window-wait "$load"
  while gate_load_is_hot "$load" "$cores" "$factor" && ((waited < settle)); do
    gate_window_log "host load $load is above $(gate_load_threshold "$cores" "$factor") (${factor}x $cores cores); waiting (${waited}s of ${settle}s)"
    sleep "$poll" & wait $!
    waited=$((waited + poll))
    load=$(gate_window_read_load)
  done
  if gate_load_is_hot "$load" "$cores" "$factor"; then
    gate_window_log "host load $load is still hot after ${waited}s; starting the gate anyway"
  fi
  record_value load-wait-seconds "$waited"
  record_value load-at-gate-start "$load"
  gate_window_log "gate starts at host load $load on $cores cores"

  local gate_rc
  gate_started=$(date +%s)
  set +e
  # Job control puts the gate in its own process group (pgid == child_pid) so
  # a signal can stop every descendant, not only the immediate child. Without
  # job control bash would also point the gate's stdin at /dev/null; keep that.
  gate_launching=1
  set -m
  "$@" < /dev/null &
  child_pid=$!
  set +m
  if [[ -n "$pending_signal" ]]; then
    # shellcheck disable=SC2086 # "<name> <code>" splits into two arguments.
    on_signal $pending_signal
  fi
  wait "$child_pid"
  gate_rc=$?
  set -e
  child_pid=
  record_value gate-duration-seconds "$(($(date +%s) - gate_started))"
  load=$(gate_window_read_load)
  record_value load-at-gate-end "$load"
  record_value gate-exit "$gate_rc"
  gate_window_log "gate finished with exit code $gate_rc at host load $load"
  restore_quotas
  exit "$gate_rc"
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
  main "$@"
fi
