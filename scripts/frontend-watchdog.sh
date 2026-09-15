#!/usr/bin/env bash
# Generic crash supervisor for a long-running frontend dev server (`ng serve`
# via `npm start`, typically). Not specific to Stable: any checkout's outer
# start wrapper can point this at its frontend command.
#
# Why this exists: the Stable frontend (port 4011) died with no notice and
# stayed dark ("UI answered 000") until an operator happened to restart it by
# hand, because `start-stable.sh` / `start.sh` launched `ng serve` once and
# never watched it again. See docs/operations/setup/contributor-setup.md
# ("Reference: dev + stable side-by-side") for the contract an outer wrapper
# must follow to invoke this script under DETACH=1 (background) and in the
# foreground (interactive) start paths.
#
# This script owns exactly one thing: keep the child command alive, with
# backoff, and stop trying (loudly) when it cannot. It does not touch the
# backend; `api.sh` is unaffected and remains the canonical backend control
# script (AGENTS.md "Backend lifecycle scripts are shell scripts").
#
# Usage:
#   scripts/frontend-watchdog.sh [options] -- <command> [args...]
#
# Options:
#   --cwd PATH                     Working directory for the command (default: current dir)
#   --log-dir PATH                 Directory for watchdog.log / frontend.log (default: --cwd)
#   --pid-file PATH                Where this watchdog's own PID is written
#                                   (default: <log-dir>/.frontend-watchdog.pid)
#   --child-pid-file PATH          Where the current child's PID is written
#                                   (default: <log-dir>/.frontend-watchdog.child.pid)
#   --restart-delay-seconds N      Initial backoff before a restart (default: 2)
#   --max-restart-delay-seconds N  Backoff cap (default: 60)
#   --fast-crash-seconds N         A run shorter than this counts as a fast
#                                   crash (default: 10)
#   --max-fast-crashes N           Consecutive fast crashes before giving up
#                                   (default: 5)
#   --max-cycles N                 Test-only: stop after N restarts; 0 runs
#                                   indefinitely (default: 0)
#
# Stopping: send SIGTERM/SIGINT to the PID in --pid-file (or the process
# group of this script). The watchdog terminates its current child (graceful
# then forced) before exiting, so a stop actually stops the frontend instead
# of leaving it to be respawned on the next tick.
#
# Proof this behaves: scripts/test-frontend-watchdog.sh

set -u

cwd="$(pwd)"
log_dir=""
pid_file=""
child_pid_file=""
restart_delay_seconds=2
max_restart_delay_seconds=60
fast_crash_seconds=10
max_fast_crashes=5
max_cycles=0
cmd=()

usage() {
  sed -n '2,40p' "$0" | sed 's/^# \{0,1\}//'
}

while [ "$#" -gt 0 ]; do
  case "$1" in
    --cwd) cwd=${2-}; shift 2 ;;
    --log-dir) log_dir=${2-}; shift 2 ;;
    --pid-file) pid_file=${2-}; shift 2 ;;
    --child-pid-file) child_pid_file=${2-}; shift 2 ;;
    --restart-delay-seconds) restart_delay_seconds=${2-}; shift 2 ;;
    --max-restart-delay-seconds) max_restart_delay_seconds=${2-}; shift 2 ;;
    --fast-crash-seconds) fast_crash_seconds=${2-}; shift 2 ;;
    --max-fast-crashes) max_fast_crashes=${2-}; shift 2 ;;
    --max-cycles) max_cycles=${2-}; shift 2 ;;
    --help|-h) usage; exit 0 ;;
    --) shift; cmd=("$@"); break ;;
    *) printf 'frontend-watchdog: unknown option: %s\n' "$1" >&2; usage >&2; exit 2 ;;
  esac
done

if [ "${#cmd[@]}" -eq 0 ]; then
  printf 'frontend-watchdog: no command given (expected: ... -- <command> [args...])\n' >&2
  exit 2
fi

case "$restart_delay_seconds:$max_restart_delay_seconds:$fast_crash_seconds:$max_fast_crashes:$max_cycles" in
  *[!0-9:]*)
    printf 'frontend-watchdog: numeric options must be non-negative integers\n' >&2
    exit 2
    ;;
esac
if [ "$restart_delay_seconds" -lt 1 ] || [ "$max_restart_delay_seconds" -lt "$restart_delay_seconds" ] \
   || [ "$fast_crash_seconds" -lt 1 ] || [ "$max_fast_crashes" -lt 1 ]; then
  printf 'frontend-watchdog: restart-delay/max-restart-delay/fast-crash-seconds/max-fast-crashes must be positive, and max-restart-delay-seconds >= restart-delay-seconds\n' >&2
  exit 2
fi

[ -d "$cwd" ] || { printf 'frontend-watchdog: --cwd does not exist: %s\n' "$cwd" >&2; exit 2; }
cwd=$(cd "$cwd" && pwd)
log_dir=${log_dir:-$cwd}
mkdir -p "$log_dir"
pid_file=${pid_file:-"$log_dir/.frontend-watchdog.pid"}
child_pid_file=${child_pid_file:-"$log_dir/.frontend-watchdog.child.pid"}
watchdog_log="$log_dir/frontend-watchdog.log"
frontend_log="$log_dir/frontend.log"

is_windows() {
  case "$(uname -s 2>/dev/null)" in
    MINGW*|MSYS*|CYGWIN*) return 0 ;;
    *) return 1 ;;
  esac
}

iso_now() { date -u +%Y-%m-%dT%H:%M:%SZ; }

journal() {
  printf '%s %s\n' "$(iso_now)" "$*" >> "$watchdog_log"
}

pid_alive() {
  pid="$1"
  [ -n "$pid" ] || return 1
  if is_windows; then
    tasklist //FI "PID eq ${pid}" //NH 2>/dev/null | grep -qE "(^|[[:space:]])${pid}([[:space:]]|$)"
  else
    kill -0 "$pid" 2>/dev/null
  fi
}

# Graceful TERM, then a bounded wait, then a forced KILL. On Windows,
# `taskkill //T` walks the whole process tree (npm -> node -> ng ->
# webpack), which is what actually stops the frontend instead of leaving
# orphaned children behind. On POSIX, the child is launched via `setsid`
# when available so it heads its own process group and `-pid` reaches the
# whole tree; without `setsid` this falls back to signalling the tracked pid
# alone (best effort).
terminate_child() {
  pid="$1"
  [ -n "$pid" ] || return 0
  pid_alive "$pid" || return 0
  if is_windows; then
    taskkill //T //PID "$pid" >/dev/null 2>&1 || true
  elif [ "$used_setsid" = 1 ]; then
    kill -TERM "-$pid" 2>/dev/null || true
  else
    kill -TERM "$pid" 2>/dev/null || true
  fi
  waited=0
  while pid_alive "$pid"; do
    [ "$waited" -ge 20 ] && break
    sleep 0.5
    waited=$((waited + 1))
  done
  if pid_alive "$pid"; then
    if is_windows; then
      taskkill //F //T //PID "$pid" >/dev/null 2>&1 || true
    elif [ "$used_setsid" = 1 ]; then
      kill -KILL "-$pid" 2>/dev/null || true
    else
      kill -KILL "$pid" 2>/dev/null || true
    fi
  fi
}

current_child_pid=""
shutting_down=0

on_signal() {
  shutting_down=1
  journal "event=watchdog_stopping signal=received child_pid=$current_child_pid"
  terminate_child "$current_child_pid"
  rm -f "$pid_file" "$child_pid_file"
  journal "event=watchdog_stopped"
  exit 0
}
trap on_signal INT TERM
trap 'rm -f "$pid_file"' EXIT

printf '%s\n' "$$" > "$pid_file"

cmd_display=$(printf '%s ' "${cmd[@]}")
journal "event=watchdog_started cwd=$cwd cmd=${cmd_display% } restart_delay_seconds=$restart_delay_seconds max_restart_delay_seconds=$max_restart_delay_seconds fast_crash_seconds=$fast_crash_seconds max_fast_crashes=$max_fast_crashes"

delay=$restart_delay_seconds
consecutive_fast_crashes=0
cycles=0

while :; do
  [ "$shutting_down" -eq 1 ] && exit 0

  used_setsid=0
  if ! is_windows && command -v setsid >/dev/null 2>&1; then
    used_setsid=1
  fi
  start_epoch=$(date +%s)
  (
    cd "$cwd" || exit 127
    if [ "$used_setsid" -eq 1 ]; then
      exec setsid "${cmd[@]}"
    else
      exec "${cmd[@]}"
    fi
  ) >> "$frontend_log" 2>&1 &
  current_child_pid=$!
  printf '%s\n' "$current_child_pid" > "$child_pid_file"
  journal "event=child_started pid=$current_child_pid cycle=$((cycles + 1))"

  wait "$current_child_pid"
  exit_code=$?
  end_epoch=$(date +%s)
  age_seconds=$((end_epoch - start_epoch))
  rm -f "$child_pid_file"

  [ "$shutting_down" -eq 1 ] && exit 0

  tail_lines=$(tail -n 20 "$frontend_log" 2>/dev/null | tr '\n' '|')
  journal "event=child_exited pid=$current_child_pid exit_code=$exit_code age_seconds=$age_seconds tail=${tail_lines:-<empty>}"

  if [ "$age_seconds" -lt "$fast_crash_seconds" ]; then
    consecutive_fast_crashes=$((consecutive_fast_crashes + 1))
  else
    consecutive_fast_crashes=0
    delay=$restart_delay_seconds
  fi

  if [ "$consecutive_fast_crashes" -ge "$max_fast_crashes" ]; then
    journal "event=giving_up consecutive_fast_crashes=$consecutive_fast_crashes threshold=$max_fast_crashes"
    printf 'frontend-watchdog: giving up after %s consecutive fast crashes (< %ss each). See %s and %s.\n' \
      "$consecutive_fast_crashes" "$fast_crash_seconds" "$watchdog_log" "$frontend_log" >&2
    rm -f "$pid_file"
    exit 1
  fi

  cycles=$((cycles + 1))
  if [ "$max_cycles" -gt 0 ] && [ "$cycles" -ge "$max_cycles" ]; then
    journal "event=watchdog_stopped reason=max_cycles cycles=$cycles"
    exit 0
  fi

  journal "event=backoff_sleep seconds=$delay"
  sleep "$delay"
  delay=$((delay * 2))
  if [ "$delay" -gt "$max_restart_delay_seconds" ]; then
    delay=$max_restart_delay_seconds
  fi
done
