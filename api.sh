#!/usr/bin/env bash
# Robust local .NET API control script - sh equivalent of api.ps1.
#
# Why this exists: PowerShell-based scripts behave unreliably when called
# from agent CLIs (Claude Code, Codex, Copilot CLI on Windows). Long-running
# background launches and PID tracking get mangled, agents wait for prompts
# that never come. This sh version runs cleanly under Git Bash / WSL / any
# POSIX shell available on the dev machine, and is the canonical entrypoint
# for agents.
#
# Restart contract (AGT-2678). Every command below verifies, never assumes:
#   stop     terminates the port listener AND every OrchestratorApi process of
#            THIS checkout (launcher, app child, orphan that outlived its
#            launcher), then proves the port is free and no such process is
#            left. It fails loudly instead of printing "API stopped" over a
#            still-running backend.
#   start    refuses to run when the port is owned by a process it did not
#            start, and only reports success once the process answering
#            /healthz is provably the one this invocation launched. A start
#            can no longer be satisfied by the old build still holding the
#            port.
#   restart  asserts that the PID and the process start time both changed. If
#            the previous process is still serving, the command fails.
# Before this, all three reported success while the previous process kept
# serving: rollouts shipped stale code, config reloads never took effect, and
# the watchdog's restart path was hollow.
# Proof that the contract holds: tools/api-restart-selfcheck.sh
#
# Start exit codes (AGT-2830). `dotnet run` under Windows Git Bash hands the
# PID captured by `$!` to a launcher, not the native process that compiles and
# then owns the port; that launcher can exit minutes before the real work is
# done, which used to be misread as a crash. `start` now only reports a
# confirmed failure (exit 1) when neither the port nor a live process tied to
# this project's build/run exists. If the bounded wait (START_TIMEOUT_SECS)
# runs out while such a process is still active, it exits 2 ("inconclusive,
# still starting") instead, so a caller such as an outer start.sh wrapper can
# tell that apart from a confirmed crash and still bring up the frontend
# rather than aborting under `set -e`. See
# docs/operations/setup/contributor-setup.md and
# docs/operations/common-problems/windows-launcher-exit-during-compile/.
# Proof: scripts/api-start-windows-launcher.test.sh
#
# Usage:
#   ./api.sh start
#   ./api.sh stop
#   ./api.sh restart
#   ./api.sh status

set -u

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")" && pwd)"
# Default port is pinned to the checkout folder name: stable runs on 5031,
# dev on 5030. This matters because a coding-agent session running INSIDE
# one backend may invoke api.sh from a SIBLING checkout (e.g. claude doing
# `bash api.sh restart` from inside the dev folder while it was spawned by
# the stable backend). The child process inherits PORT from its parent;
# without a guard, dev's api.sh would happily start dev's exe on stable's
# port 5031 - exactly what happened during the suchbox-orphan incident on
# 2026-05-07. The guard below refuses any inherited PORT that disagrees
# with this checkout's pinned default unless API_PORT_OVERRIDE=1 is set,
# which is the explicit "I know what I'm doing" escape hatch.
case "$(basename "${SCRIPT_DIR}")" in
  *-stable) DEFAULT_PORT=5031 ;;
  *)        DEFAULT_PORT=5030 ;;
esac

if [[ -n "${PORT:-}" && "${PORT}" != "${DEFAULT_PORT}" ]]; then
  if [[ "${API_PORT_OVERRIDE:-}" != "1" ]]; then
    echo "ERROR: api.sh in ${SCRIPT_DIR}" >&2
    echo "       has DEFAULT_PORT=${DEFAULT_PORT} (pinned by checkout folder name)" >&2
    echo "       but the environment carries PORT=${PORT}." >&2
    echo "       Refusing to bind a sibling checkout's exe onto this port." >&2
    echo "       If this is intentional, re-run with API_PORT_OVERRIDE=1." >&2
    exit 1
  fi
  echo "WARN: API_PORT_OVERRIDE=1; using non-default PORT=${PORT}." >&2
fi
PORT="${PORT:-${DEFAULT_PORT}}"
BASE_URL="http://127.0.0.1:${PORT}"
HEALTH_URL="${BASE_URL}/healthz"
PROJECT_FILE="${SCRIPT_DIR}/backend/OrchestratorApi.csproj"
PID_FILE="${SCRIPT_DIR}/.api.pid"
# The launcher (`dotnet run`) and the process that owns the port are two
# different PIDs. Track both: signalling only one of them is what left either
# a server still serving or a build-output lock nobody could explain.
LAUNCHER_PID_FILE="${SCRIPT_DIR}/.api.launcher.pid"
LOG_OUT="${SCRIPT_DIR}/.api.log.out"
LOG_ERR="${SCRIPT_DIR}/.api.log.err"

# Identifies a process as this checkout's backend. Always matched together
# with the checkout path, so a sibling checkout's backend is never touched.
PROJECT_MARKER="OrchestratorApi"
# Budget for a graceful shutdown before the stop escalates to a forced kill.
STOP_TIMEOUT_SECS="${API_STOP_TIMEOUT_SECS:-20}"
# Budget for start's health-check poll. A cold `dotnet run` compile (no prior
# build cache) can take roughly two minutes; 180s leaves headroom instead of
# failing fast on a boot that is merely slow rather than actually dead.
START_TIMEOUT_SECS="${API_START_TIMEOUT_SECS:-180}"
# Set by cmd_start before it launches; read by the ownership check afterwards.
PRE_LAUNCH_IDS=""

is_windows() {
  case "$(uname -s 2>/dev/null)" in
    MINGW*|MSYS*|CYGWIN*) return 0 ;;
    *) return 1 ;;
  esac
}

# Windows records command lines in native form (C:\dev\...), while this shell
# knows the checkout as /c/dev/... Keep both spellings so path matching works.
if is_windows && command -v cygpath >/dev/null 2>&1; then
  NATIVE_SCRIPT_DIR="$(cygpath -w "${SCRIPT_DIR}" 2>/dev/null || printf '%s' "${SCRIPT_DIR}")"
else
  NATIVE_SCRIPT_DIR="${SCRIPT_DIR}"
fi

# ---------------------------------------------------------------------------
# Process inspection
# ---------------------------------------------------------------------------

# Windows only. Reads one Win32_Process property. wmic is present on the Git
# Bash targets in use; powershell is the fallback for hosts where wmic has
# been removed. Query only, so the "no PowerShell-authored files" rule holds.
win_process_field() {
  local pid="$1" field="$2" out=""
  if command -v wmic >/dev/null 2>&1; then
    out="$(wmic process where "ProcessId=${pid}" get "${field}" /value 2>/dev/null \
      | tr -d '\r' | sed -n "s/^${field}=//p" | head -n1)"
  fi
  if [[ -z "${out}" ]] && command -v powershell >/dev/null 2>&1; then
    out="$(powershell -NoProfile -NonInteractive -Command \
      "(Get-CimInstance Win32_Process -Filter \"ProcessId=${pid}\").${field}" 2>/dev/null \
      | tr -d '\r' | head -n1)"
  fi
  printf '%s' "${out}"
}

# Returns 0 if PID is alive, 1 otherwise. Works on Git Bash & POSIX.
pid_alive() {
  local pid="$1"
  if [[ -z "$pid" ]] || ! [[ "$pid" =~ ^[0-9]+$ ]]; then return 1; fi
  if is_windows; then
    tasklist //FI "PID eq ${pid}" //NH 2>/dev/null \
      | grep -qE "(^|[[:space:]])${pid}([[:space:]]|$)"
  else
    kill -0 "$pid" 2>/dev/null
  fi
}

# A PID on its own is not an identity: the OS reuses PIDs, so "the number
# changed" is a weaker claim than "the process changed". Pair every PID with
# its start time; restart asserts on the pair.
pid_start_time() {
  local pid="$1"
  [[ "${pid}" =~ ^[0-9]+$ ]] || return 0
  if is_windows; then
    win_process_field "${pid}" CreationDate
  elif [[ -r "/proc/${pid}/stat" ]]; then
    # Field 22 is starttime, but field 2 (comm) may itself contain spaces and
    # parentheses. Drop everything through the LAST ')' before counting, which
    # leaves starttime at index 20.
    sed 's/^.*) //' "/proc/${pid}/stat" 2>/dev/null | awk '{ print $20 }'
  else
    ps -o lstart= -p "${pid}" 2>/dev/null | tr -s ' ' | sed 's/^ *//;s/ *$//'
  fi
}

pid_command() {
  local pid="$1"
  [[ "${pid}" =~ ^[0-9]+$ ]] || return 0
  if is_windows; then
    win_process_field "${pid}" CommandLine
  elif [[ -r "/proc/${pid}/cmdline" ]]; then
    tr '\0' ' ' < "/proc/${pid}/cmdline" 2>/dev/null
  else
    ps -o args= -p "${pid}" 2>/dev/null
  fi
}

# One line per process: "PID command line".
process_table() {
  if is_windows; then
    if command -v wmic >/dev/null 2>&1; then
      # wmic emits Node first and then the requested properties in ALPHABETICAL
      # order, not the order they were asked for. Read the header rather than
      # assume a layout. The command line itself may contain commas, so it is
      # rebuilt from every field that is not Node and not ProcessId.
      wmic process get ProcessId,CommandLine /format:csv 2>/dev/null | tr -d '\r' \
        | awk -F',' '
            pid_i == 0 && /ProcessId/ {
              hdr = NF
              for (i = 1; i <= NF; i++) if ($i == "ProcessId") pid_i = i
              next
            }
            pid_i == 0 { next }
            {
              if (pid_i == hdr) { pid = $NF; lo = 2; hi = NF - 1 }
              else              { pid = $pid_i; lo = pid_i + 1; hi = NF }
              if (pid !~ /^[0-9]+$/) next
              cmd = ""
              for (i = lo; i <= hi; i++) cmd = cmd (i > lo ? "," : "") $i
              print pid, cmd
            }'
      return 0
    fi
    if command -v powershell >/dev/null 2>&1; then
      powershell -NoProfile -NonInteractive -Command \
        'Get-CimInstance Win32_Process | ForEach-Object { "$($_.ProcessId) $($_.CommandLine)" }' \
        2>/dev/null | tr -d '\r'
      return 0
    fi
    return 1
  fi
  ps -eww -o pid=,args= 2>/dev/null || ps -eo pid=,args= 2>/dev/null
}

# One line per process: "PID PARENT_PID".
child_table() {
  if is_windows; then
    if command -v wmic >/dev/null 2>&1; then
      # Alphabetical column order puts ParentProcessId BEFORE ProcessId here,
      # which is the opposite of the argument order. Read the header.
      wmic process get ProcessId,ParentProcessId /format:csv 2>/dev/null | tr -d '\r' \
        | awk -F',' '
            pid_i == 0 && /ProcessId/ {
              for (i = 1; i <= NF; i++) {
                if ($i == "ParentProcessId") ppid_i = i
                else if ($i == "ProcessId") pid_i = i
              }
              next
            }
            pid_i > 0 && ppid_i > 0 && $pid_i ~ /^[0-9]+$/ && $ppid_i ~ /^[0-9]+$/ {
              print $pid_i, $ppid_i
            }'
      return 0
    fi
    if command -v powershell >/dev/null 2>&1; then
      powershell -NoProfile -NonInteractive -Command \
        'Get-CimInstance Win32_Process | ForEach-Object { "$($_.ProcessId) $($_.ParentProcessId)" }' \
        2>/dev/null | tr -d '\r'
      return 0
    fi
    return 1
  fi
  ps -eo pid=,ppid= 2>/dev/null
}

parent_pid() {
  local pid="$1"
  [[ "${pid}" =~ ^[0-9]+$ ]] || return 0
  if is_windows; then
    win_process_field "${pid}" ParentProcessId
  elif [[ -r "/proc/${pid}/stat" ]]; then
    sed 's/^.*) //' "/proc/${pid}/stat" 2>/dev/null | awk '{ print $2 }'
  else
    ps -o ppid= -p "${pid}" 2>/dev/null | tr -d ' '
  fi
}

# This shell and everything that spawned it. Never a sweep target: an agent
# CLI invoked from inside the checkout must not be able to kill its own
# session while stopping the backend.
ancestor_pids() {
  local pid="$1" guard=0
  while [[ "${pid}" =~ ^[0-9]+$ ]] && (( pid > 1 )) && (( guard < 32 )); do
    pid="$(parent_pid "${pid}")"
    [[ "${pid}" =~ ^[0-9]+$ ]] && (( pid > 1 )) || break
    printf '%s\n' "${pid}"
    guard=$((guard + 1))
  done
}

# `dotnet run` is a launcher: the process that actually owns the port is its
# child. Signalling only the launcher leaves the server serving; signalling
# only the child leaves the launcher holding the build output. Walk the tree.
descendant_pids() {
  local roots="$1" table frontier next found="" guard=0 p c
  table="$(child_table)" || return 0
  frontier="${roots}"
  while [[ -n "${frontier// /}" ]] && (( guard < 64 )); do
    next=""
    for p in ${frontier}; do
      for c in $(printf '%s\n' "${table}" | awk -v pp="${p}" '$2 == pp { print $1 }'); do
        case " ${roots} ${found} " in *" ${c} "*) continue ;; esac
        found="${found} ${c}"
        next="${next} ${c}"
      done
    done
    frontier="${next}"
    guard=$((guard + 1))
  done
  [[ -n "${found// /}" ]] && printf '%s\n' ${found}
  return 0
}

# ---------------------------------------------------------------------------
# Port ownership
# ---------------------------------------------------------------------------

# One line per listening socket: "PID PORT". Both the port sweep and the
# "does this process serve some OTHER port" check read this single table, so
# the two can never disagree. Returns 1 when the host offers no way to ask,
# which callers turn into a loud failure rather than a silent "nothing is
# running" - that silence is how a stop came to report success over a live
# process.
listen_table() {
  if is_windows; then
    netstat -ano 2>/dev/null | tr -d '\r' | awk '
      $1 ~ /^TCP/ && $4 == "LISTENING" && $5 ~ /^[0-9]+$/ {
        n = split($2, a, ":")
        if (a[n] ~ /^[0-9]+$/) print $5, a[n]
      }'
    return 0
  fi
  if command -v lsof >/dev/null 2>&1; then
    lsof -nP -iTCP -sTCP:LISTEN -F pn 2>/dev/null | awk '
      /^p/ { pid = substr($0, 2); next }
      /^n/ {
        n = split(substr($0, 2), a, ":")
        if (pid != "" && a[n] ~ /^[0-9]+$/) print pid, a[n]
      }'
    return 0
  fi
  if command -v ss >/dev/null 2>&1; then
    ss -lntp 2>/dev/null | awk '
      {
        n = split($4, a, ":")
        if (a[n] !~ /^[0-9]+$/) next
        line = $0
        while (match(line, /pid=[0-9]+/)) {
          print substr(line, RSTART + 4, RLENGTH - 4), a[n]
          line = substr(line, RSTART + RLENGTH)
        }
      }'
    return 0
  fi
  if command -v netstat >/dev/null 2>&1; then
    netstat -lntp 2>/dev/null | awk '
      $1 ~ /^tcp/ && $NF ~ /^[0-9]+\// {
        n = split($4, a, ":")
        split($NF, b, "/")
        if (a[n] ~ /^[0-9]+$/) print b[1], a[n]
      }'
    return 0
  fi
  return 1
}

# Fail loudly when the host cannot answer "who owns this port". Guessing here
# is what produced a hollow stop.
require_port_inspection() {
  if listen_table >/dev/null 2>&1; then
    return 0
  fi
  echo "ERROR: no way to inspect listening sockets on this host." >&2
  echo "       Tried: netstat (Windows), lsof, ss, netstat (POSIX)." >&2
  echo "       Refusing to act on port ${PORT} without knowing who owns it," >&2
  echo "       because an unverified stop reports success over a live process." >&2
  return 1
}

listener_pids() {
  listen_table 2>/dev/null | awk -v p="${PORT}" '$2 == p { print $1 }' | sort -u
}

pid_listen_ports() {
  listen_table 2>/dev/null | awk -v pid="$1" '$1 == pid { print $2 }' | sort -u
}

# True when the process listens somewhere but never on our port. Two backends
# from one checkout on different ports is legitimate (the isolated worktree
# test stack does exactly that), so a port sweep must leave those alone.
serves_foreign_port() {
  local ports
  ports="$(pid_listen_ports "$1")"
  [[ -n "${ports}" ]] || return 1
  printf '%s\n' "${ports}" | grep -qx "${PORT}" && return 1
  return 0
}

# The server is one of exactly two things: the `dotnet run` launcher, which
# names the csproj, or the built app under backend/bin. Matching the checkout
# path plus the marker alone would be too wide: `dotnet test` on
# backend.Tests/OrchestratorApi.Tests.csproj spawns vstest and testhost
# children whose command lines carry both, and sweeping those would kill a
# test run started from an unrelated terminal.
# The csproj name is a parameter because the Windows comparison below runs on
# a lowercased copy of both strings, where a mixed-case literal never matches.
cmd_names_backend() {
  local cmd="$1" dir="$2" csproj="$3"
  case "${cmd}" in
    *"${dir}/backend/${csproj}"*) return 0 ;;
    *"${dir}/backend/bin/"*) return 0 ;;
  esac
  return 1
}

# Matches the exact backend project or its build output for this checkout.
# A backend launched by hand with a relative --project path is not recognised
# here; it is still caught as a port listener, which is the tier that does not
# depend on identity at all.
matches_project_path() {
  local cmd="$1" lc_cmd lc_dir
  cmd_names_backend "${cmd}" "${SCRIPT_DIR}" "OrchestratorApi.csproj" && return 0
  [[ -n "${NATIVE_SCRIPT_DIR}" && "${NATIVE_SCRIPT_DIR}" != "${SCRIPT_DIR}" ]] || return 1
  # Windows command lines differ in case and separator from this shell's view
  # of the checkout path. Normalise both, and compare against lowercase
  # literals to match.
  lc_cmd="$(printf '%s' "${cmd}" | tr 'A-Z\\' 'a-z/')"
  lc_dir="$(printf '%s' "${NATIVE_SCRIPT_DIR}" | tr 'A-Z\\' 'a-z/')"
  cmd_names_backend "${lc_cmd}" "${lc_dir}" "orchestratorapi.csproj"
}

# Does this command line belong to THIS checkout's backend server? Build and
# test workers are excluded because this matcher is also used by stop/kill.
matches_project() {
  local cmd="$1"
  case "${cmd}" in *"${PROJECT_MARKER}"*) ;; *) return 1 ;; esac
  case "${cmd}" in
    *.Tests*|*vstest*|*testhost*|*MSBuild.dll*|*msbuild*|*"/nodemode:"*) return 1 ;;
  esac
  matches_project_path "${cmd}"
}

# Loosened version of matches_project for "is a build or run for this
# checkout still active" only - never for the stop/kill sweep. matches_project
# excludes MSBuild worker command lines so cmd_stop never sweeps an unrelated
# `dotnet test` run (AGT-2678); that same exclusion also hides the exact
# process doing the work while `dotnet run` is still compiling. Matching the
# literal csproj path (not just the OrchestratorApi marker) already keeps this
# from matching OrchestratorApi.Tests.csproj or an unrelated project that
# merely references OrchestratorApi, so it is safe to drop the exclusion here.
matches_project_build() {
  matches_project_path "$1"
}

# PIDs of anything still building or running this launch's project. On
# Windows Git Bash the PID `$!` captures for `dotnet run` is a launcher, not
# the native process that compiles and then owns the port; that launcher can
# exit minutes before the real work is done. `start`'s poll loop uses this to
# tell "the backend crashed" apart from "the launcher exited but the build is
# still going", which is the false-positive this function exists to prevent.
launch_activity_pids() {
  local pid cmd
  process_table 2>/dev/null | while read -r pid cmd; do
    [[ "${pid}" =~ ^[0-9]+$ ]] || continue
    matches_project_build "${cmd}" && printf '%s\n' "${pid}"
  done | sort -u
}

# Keep polling PIDs already found by launch_activity_pids. A new full process
# table scan is only needed after every known build/run process has exited.
live_pids() {
  local pid
  for pid in $1; do
    pid_alive "${pid}" && printf '%s\n' "${pid}"
  done
}

# Every process of this checkout's backend, listening or not. This is what
# catches the orphan that outlived its launcher and kept the build output
# locked so the next rebuild could not copy over it.
project_pids() {
  local self_chain pid cmd
  self_chain=" $$ $(ancestor_pids $$ | tr '\n' ' ') "
  process_table 2>/dev/null | while read -r pid cmd; do
    [[ "${pid}" =~ ^[0-9]+$ ]] || continue
    case "${self_chain}" in *" ${pid} "*) continue ;; esac
    matches_project "${cmd}" || continue
    printf '%s\n' "${pid}"
  done | sort -u
}

pid_summary() {
  local cmd
  cmd="$(pid_command "$1")"
  printf '%.160s' "${cmd:-<command line unavailable>}"
}

read_pid_file() {
  [[ -f "$1" ]] || return 0
  tr -d ' \r\n' < "$1" 2>/dev/null || true
}

# "PID:START_TIME" for everything that could be serving this port right now.
# restart compares the set before and after; any survivor means the restart
# did not replace the process.
identity_set() {
  local pid
  { listener_pids; project_pids; } | sort -u | while read -r pid; do
    [[ "${pid}" =~ ^[0-9]+$ ]] || continue
    printf '%s:%s\n' "${pid}" "$(pid_start_time "${pid}" | tr -d ' ')"
  done | sort -u
}

# ---------------------------------------------------------------------------
# Termination
# ---------------------------------------------------------------------------

signal_pid() {
  local pid="$1" sig="$2"
  [[ "${pid}" =~ ^[0-9]+$ ]] || return 0
  if is_windows; then
    if [[ "${sig}" == "KILL" ]]; then
      taskkill //F //T //PID "${pid}" >/dev/null 2>&1 || true
    else
      taskkill //T //PID "${pid}" >/dev/null 2>&1 || true
    fi
  else
    kill "-${sig}" "${pid}" 2>/dev/null || true
  fi
}

# Returns 0 once every PID is gone, 1 on timeout. Polls in 200 ms steps.
wait_for_exit() {
  local pids="$1" secs="$2" waited=0 p alive
  while :; do
    alive=0
    for p in ${pids}; do
      if pid_alive "${p}"; then alive=1; break; fi
    done
    (( alive == 0 )) && return 0
    (( waited >= secs * 5 )) && return 1
    sleep 0.2
    waited=$((waited + 1))
  done
}

# Graceful first, forced second. A backend killed mid-write can leave the task
# ledger half-updated, so a forced kill is the escalation and never the
# opening move.
terminate_pids() {
  local pids="$1" p
  [[ -n "${pids// /}" ]] || return 0
  for p in ${pids}; do signal_pid "${p}" TERM; done
  wait_for_exit "${pids}" "${STOP_TIMEOUT_SECS}" && return 0
  for p in ${pids}; do
    pid_alive "${p}" || continue
    echo "  PID ${p} ignored the graceful stop for ${STOP_TIMEOUT_SECS}s; forcing."
    signal_pid "${p}" KILL
  done
  wait_for_exit "${pids}" 5
}

# ---------------------------------------------------------------------------
# Status
# ---------------------------------------------------------------------------

# Sets globals: STATUS_RUNNING, STATUS_HEALTHY, STATUS_PID, STATUS_MSG.
get_status() {
  STATUS_RUNNING=0
  STATUS_HEALTHY=0
  STATUS_PID=""
  STATUS_MSG="stopped"

  local owners stored code
  owners="$(listener_pids | tr '\n' ' ')"
  if [[ -n "${owners// /}" ]]; then
    STATUS_RUNNING=1
    STATUS_PID="$(printf '%s' "${owners}" | awk '{ print $1 }')"
  else
    # No listener. A tracked PID that is still alive is a backend that failed
    # to bind, which is a different problem from "stopped" and must not be
    # rounded down to it.
    stored="$(read_pid_file "${PID_FILE}")"
    if pid_alive "${stored}"; then
      STATUS_RUNNING=1
      STATUS_PID="${stored}"
    fi
  fi

  if [[ "${STATUS_RUNNING}" -eq 1 ]]; then
    code="$(curl -s -o /dev/null -w '%{http_code}' --max-time 2 "${HEALTH_URL}" 2>/dev/null || echo 000)"
    if [[ "${code}" == "200" ]]; then
      STATUS_HEALTHY=1
      STATUS_MSG="running and healthy (PID: ${STATUS_PID}, started: $(pid_start_time "${STATUS_PID}"))"
    else
      STATUS_MSG="running but unhealthy (HTTP ${code}, PID: ${STATUS_PID})"
    fi
  fi
}

cmd_status() {
  require_port_inspection || return 1
  get_status
  echo "API STATUS: ${STATUS_MSG}"

  local owners count p
  owners="$(listener_pids | tr '\n' ' ')"
  count="$(printf '%s\n' ${owners} | grep -c . || true)"
  if (( count > 1 )); then
    echo "WARN: port ${PORT} has more than one listener: ${owners}" >&2
    echo "      Run './api.sh stop' before the next start." >&2
  fi

  # An OrchestratorApi process that is not serving the port is the shape the
  # zombie took: invisible to a health check, still holding the build output.
  for p in $(project_pids); do
    printf '%s\n' ${owners} | grep -qx "${p}" && continue
    echo "NOTE: ${PROJECT_MARKER} process from this checkout is not serving :${PORT}:"
    echo "      PID ${p} ($(pid_summary "${p}"))"
  done
  return 0
}

# ---------------------------------------------------------------------------
# Stop
# ---------------------------------------------------------------------------

# project_pids minus the instances that legitimately serve a different port.
sweepable_project_pids() {
  local p
  for p in $(project_pids); do
    if serves_foreign_port "${p}"; then
      echo "  leaving PID ${p} alone: same checkout, but it serves port(s)" \
           "$(pid_listen_ports "${p}" | tr '\n' ' ')" >&2
      continue
    fi
    printf '%s\n' "${p}"
  done
}

# Everything that must die before the port can be called free: the live
# listeners, the PIDs we recorded, this checkout's backend processes, and the
# descendants of all of them.
stop_targets() {
  local pids="" p
  for p in $(listener_pids); do pids="${pids} ${p}"; done
  for p in $(read_pid_file "${PID_FILE}") $(read_pid_file "${LAUNCHER_PID_FILE}"); do
    pid_alive "${p}" && pids="${pids} ${p}"
  done
  for p in $(sweepable_project_pids); do pids="${pids} ${p}"; done
  [[ -n "${pids// /}" ]] || return 0
  pids="${pids} $(descendant_pids "${pids}" | tr '\n' ' ')"
  printf '%s\n' ${pids} | sort -u
}

cmd_stop() {
  require_port_inspection || return 1

  local round=0 targets p
  # Re-collect between rounds: a shutting-down backend can spawn a last child,
  # and an orphan only becomes visible once its launcher is gone.
  while (( round < 3 )); do
    targets="$(stop_targets | tr '\n' ' ')"
    [[ -n "${targets// /}" ]] || break
    if (( round == 0 )); then
      echo "Stopping the backend for ${SCRIPT_DIR} on port ${PORT}:"
    fi
    for p in ${targets}; do
      printf '  PID %s  %s\n' "${p}" "$(pid_summary "${p}")"
    done
    terminate_pids "${targets}"
    round=$((round + 1))
  done

  # Verify instead of assume. The absence of this check is what let
  # "API stopped." print while the previous process kept serving.
  local left_listeners left_project
  left_listeners="$(listener_pids | tr '\n' ' ')"
  left_project="$(sweepable_project_pids 2>/dev/null | tr '\n' ' ')"
  if [[ -n "${left_listeners// /}" || -n "${left_project// /}" ]]; then
    echo "ERROR: stop could not clear the backend on port ${PORT}." >&2
    for p in ${left_listeners} ${left_project}; do
      echo "       surviving PID ${p}: $(pid_summary "${p}")" >&2
    done
    echo "       The port is NOT free. Do not start on top of this." >&2
    return 1
  fi

  rm -f "${PID_FILE}" "${LAUNCHER_PID_FILE}"
  echo "API stopped: port ${PORT} is free and no ${PROJECT_MARKER} process from this checkout remains."
  return 0
}

# ---------------------------------------------------------------------------
# Start
# ---------------------------------------------------------------------------

# Was this PID already there before we launched? Compared as PID:start-time so
# a reused PID number cannot pass as the process we started.
was_present_before_launch() {
  local id
  id="$1:$(pid_start_time "$1" | tr -d ' ')"
  case " ${PRE_LAUNCH_IDS} " in *" ${id} "*) return 0 ;; esac
  return 1
}

# The listener must be the process this invocation started: the launcher, one
# of its descendants, or a backend of this checkout that did not exist before
# the launch (a `dotnet run` child can be reparented when the launcher exits).
# Anything else means the port is served by code we did not just deploy, which
# is the whole failure this contract exists to prevent.
listener_is_ours() {
  local pid="$1" launcher="$2" kin
  [[ "${pid}" == "${launcher}" ]] && return 0
  kin=" $(descendant_pids "${launcher}" | tr '\n' ' ') "
  case "${kin}" in *" ${pid} "*) return 0 ;; esac
  if matches_project "$(pid_command "${pid}")" && ! was_present_before_launch "${pid}"; then
    return 0
  fi
  return 1
}

cmd_start() {
  # ADR-0044: dev backend boot policy gate. The dev checkout is the regression-
  # test target, not a second pickup driver on the shared workspace. The
  # Playwright fixture (`scripts/supervisor/dev-lifecycle.sh`) is the one
  # legitimate caller that brings dev up; it exports
  # ATP_DEV_BACKEND_FROM_FIXTURE=1 to bypass this gate. Direct human
  # invocation needs ATP_ALLOW_DEV_BACKEND=1 as an explicit policy
  # acknowledgement. Anything else refuses to boot so that an interactive
  # session or a stray watchdog cannot silently restart dev's pickup loop
  # on a shared workspace. The stable checkout (folder name ends in -stable)
  # is unaffected by this gate.
  case "$(basename "${SCRIPT_DIR}")" in
    *-stable) ;;
    *)
      if [[ "${ATP_DEV_BACKEND_FROM_FIXTURE:-}" != "1" \
         && "${ATP_ALLOW_DEV_BACKEND:-}" != "1" \
         && "${ATP_WORKTREE_TEST_BACKEND:-}" != "1" ]]; then
        echo "ERROR: refusing to start the dev backend." >&2
        echo "       AGENTS.md 'Dev backend lifecycle: Playwright-only' (ADR-0044) says the dev" >&2
        echo "       checkout is offline by default. The only path that may bring it up is the" >&2
        echo "       Playwright dev-backend fixture, which routes through" >&2
        echo "       scripts/supervisor/dev-lifecycle.sh and sets ATP_DEV_BACKEND_FROM_FIXTURE=1." >&2
        echo "" >&2
        echo "       To boot dev manually for interactive debugging, re-run with" >&2
        echo "       ATP_ALLOW_DEV_BACKEND=1 set in the environment as a policy acknowledgement:" >&2
        echo "         ATP_ALLOW_DEV_BACKEND=1 ./api.sh start" >&2
        echo "" >&2
        echo "       To boot an ISOLATED worktree test backend on a dynamic port, use" >&2
        echo "       scripts/worktree-test-stack.sh (sets ATP_WORKTREE_TEST_BACKEND=1 +" >&2
        echo "       an isolated TaskRepository)." >&2
        exit 1
      fi
      if [[ "${ATP_WORKTREE_TEST_BACKEND:-}" == "1" ]]; then
        # ASS-1715: isolated per-worktree test backend on a dynamic port. The
        # dev-backend gate exists to stop a SECOND pickup driver from booting on
        # the SHARED workspace. A worktree test backend is only safe when it
        # points at an isolated workspace, so refuse unless TaskRepository is set
        # to an isolated path (the lifecycle script always points it at a unique
        # temp dir). This mirrors the in-process xunit isolation guard in
        # backend/Program.cs.
        if [[ -z "${TaskRepository:-}" ]]; then
          echo "ERROR: ATP_WORKTREE_TEST_BACKEND=1 requires an isolated TaskRepository." >&2
          echo "       Refusing to boot a worktree test backend against the shared workspace." >&2
          echo "       Set TaskRepository to a dedicated temp dir (scripts/worktree-test-stack.sh" >&2
          echo "       does this for you)." >&2
          exit 1
        fi
        echo "[api.sh] worktree test backend boot: isolated TaskRepository=${TaskRepository} on :${PORT}."
      elif [[ "${ATP_DEV_BACKEND_FROM_FIXTURE:-}" == "1" ]]; then
        echo "[api.sh] dev backend boot: Playwright fixture (ATP_DEV_BACKEND_FROM_FIXTURE=1)."
      else
        echo "[api.sh] dev backend boot: operator acknowledged (ATP_ALLOW_DEV_BACKEND=1)."
      fi
      ;;
  esac

  require_port_inspection || exit 1

  # Nothing may be launched on top of a port somebody else owns. Reporting
  # "started and healthy" while a stranger answers /healthz is how a rollout
  # silently keeps serving the old build.
  local owners p foreign
  owners="$(listener_pids | tr '\n' ' ')"
  if [[ -n "${owners// /}" ]]; then
    foreign=""
    for p in ${owners}; do
      matches_project "$(pid_command "${p}")" || foreign="${foreign} ${p}"
    done
    if [[ -n "${foreign// /}" ]]; then
      echo "ERROR: port ${PORT} is owned by a process that is not this checkout's backend." >&2
      for p in ${foreign}; do
        echo "       PID ${p}: $(pid_summary "${p}")" >&2
      done
      echo "       Refusing to start: whatever answers on ${BASE_URL} would not be the" >&2
      echo "       process this command launched. Free the port first, or run" >&2
      echo "       './api.sh stop' if you are sure the owner may be terminated." >&2
      exit 1
    fi
    get_status
    if [[ "${STATUS_HEALTHY}" -eq 1 ]]; then
      echo "API is already running and healthy (PID: ${STATUS_PID})."
      return 0
    fi
    echo "API is running but unhealthy. Stopping first..."
    cmd_stop || exit 1
  fi

  # Snapshot before launching so a listener that appears afterwards can be
  # told apart from one that was already there.
  PRE_LAUNCH_IDS="$(identity_set | tr '\n' ' ')"

  echo "Starting API on ${BASE_URL}..."
  # Log rotation, not truncate. The truncate-on-start path lost three crash
  # traces in a row when the backend died between restarts: by the time we
  # ran tail on the log it was already empty. Move the previous run's logs
  # to .api.log.out.<ts>.bak / .api.log.err.<ts>.bak so the final seconds
  # of the dead process remain readable. Keep the last 5 rotations per
  # stream; older ones drop off so the workspace does not grow unbounded.
  if [[ -s "${LOG_OUT}" ]] || [[ -s "${LOG_ERR}" ]]; then
    local rot_ts
    rot_ts="$(date -u +%Y%m%dT%H%M%SZ)"
    [[ -s "${LOG_OUT}" ]] && mv "${LOG_OUT}" "${LOG_OUT}.${rot_ts}.bak"
    [[ -s "${LOG_ERR}" ]] && mv "${LOG_ERR}" "${LOG_ERR}.${rot_ts}.bak"
    # Trim to the 5 most recent .bak files per stream.
    ls -1t "${LOG_OUT}".*.bak 2>/dev/null | tail -n +6 | xargs -r rm -f --
    ls -1t "${LOG_ERR}".*.bak 2>/dev/null | tail -n +6 | xargs -r rm -f --
  fi
  : > "${LOG_OUT}"
  : > "${LOG_ERR}"

  # Background-launch dotnet detached from this shell. nohup keeps it alive
  # after the script exits; the redirects keep stdout/stderr persistent.
  #
  # The caller's environment is passed through unchanged, deliberately. The
  # Update Service restarts Stable with ATP_BUILD_MANIFEST pointing at the
  # run folder's intended-build-manifest.json so the restarted backend
  # reports the release being installed, before that manifest is committed
  # into the checkout root. Never scrub or reset the environment here (no
  # `env -i`, no allowlist): that would put the runtime identity check back
  # into the state where no upgrade could ever verify itself. See
  # docs/operations/stable-release-contract.md, "Identity handoff at restart".
  nohup dotnet run --project "${PROJECT_FILE}" --urls "${BASE_URL}" \
    > "${LOG_OUT}" 2> "${LOG_ERR}" &
  local launched_pid=$!
  disown 2>/dev/null || true

  echo "${launched_pid}" > "${LAUNCHER_PID_FILE}"
  echo "${launched_pid}" > "${PID_FILE}"
  echo "API launcher started with PID: ${launched_pid}. Waiting for health check..."

  # Wait for the health endpoint to come up, bounded by START_TIMEOUT_SECS.
  local max_attempts=$(( $(printf '%.0f' "${START_TIMEOUT_SECS}") * 2 ))
  local attempts=0 lp code impostors listener_pid activity=""
  while (( attempts < max_attempts )); do
    sleep 0.5
    attempts=$((attempts + 1))

    lp="$(listener_pids | tr '\n' ' ')"

    if [[ -z "${lp// /}" ]] && ! pid_alive "${launched_pid}"; then
      # The launcher PID is gone. On Windows Git Bash that PID is `dotnet
      # run`'s own launcher, not the native process that compiles and then
      # owns the port, so it can exit minutes before the real work is done.
      # Only call this a crash when nothing tied to this project is doing
      # anything either; otherwise keep polling within the budget.
      activity="$(live_pids "${activity}" | tr '\n' ' ')"
      if [[ -z "${activity// /}" ]]; then
        activity="$(launch_activity_pids | tr '\n' ' ')"
      fi
      if [[ -z "${activity// /}" ]]; then
        echo "ERROR: the backend exited before it started listening on port ${PORT}." >&2
        echo "       Last lines of ${LOG_ERR}:" >&2
        tail -n 20 "${LOG_ERR}" >&2 2>/dev/null || true
        rm -f "${PID_FILE}" "${LAUNCHER_PID_FILE}"
        exit 1
      fi
    fi

    [[ -n "${lp// /}" ]] || continue

    code="$(curl -s -o /dev/null -w '%{http_code}' --max-time 1 "${HEALTH_URL}" 2>/dev/null || echo 000)"
    [[ "${code}" == "200" ]] || continue

    # Healthy is not enough: prove the responder is the process we launched.
    # `dotnet run` hands the port to a child, so the listener PID is expected
    # to differ from the launcher PID, and that gap is exactly where an old
    # survivor used to slip through.
    impostors=""
    for p in ${lp}; do
      listener_is_ours "${p}" "${launched_pid}" || impostors="${impostors} ${p}"
    done
    if [[ -n "${impostors// /}" ]]; then
      echo "ERROR: ${HEALTH_URL} answers, but the port is owned by a process this" >&2
      echo "       command did not start. It is serving code from another build." >&2
      for p in ${impostors}; do
        echo "       PID ${p}: $(pid_summary "${p}")" >&2
      done
      echo "       Run './api.sh stop' and start again." >&2
      exit 1
    fi

    listener_pid="$(printf '%s' "${lp}" | awk '{ print $1 }')"
    echo "${listener_pid}" > "${PID_FILE}"
    echo "API listener PID: ${listener_pid} (started: $(pid_start_time "${listener_pid}"))"
    echo "API is successfully started and healthy!"
    return 0
  done

  # The budget ran out. Distinguish a confirmed crash (nothing is listening
  # and nothing tied to this project is still running) from a still-active
  # build or slow boot, which is not a crash and must not be reported as one.
  lp="$(listener_pids | tr '\n' ' ')"
  if [[ -n "${lp// /}" ]]; then
    echo "ERROR: API started but did not become healthy within ${START_TIMEOUT_SECS} seconds." >&2
    echo "Check ${LOG_OUT} and ${LOG_ERR} for details." >&2
    exit 1
  fi
  activity="$(launch_activity_pids | tr '\n' ' ')"
  if [[ -n "${activity// /}" ]]; then
    # Exit 2 means "inconclusive, still starting": distinct from exit 1's
    # "confirmed dead" so a caller such as an outer start.sh wrapper can tell
    # them apart and still bring up the frontend instead of aborting under
    # `set -e`. See docs/operations/setup/contributor-setup.md.
    echo "API did not become healthy within ${START_TIMEOUT_SECS} seconds, but a build or" >&2
    echo "run process for this checkout is still active (PID(s):${activity})." >&2
    echo "Not a confirmed failure: run './api.sh status' to check again once it finishes." >&2
    exit 2
  fi
  echo "ERROR: the backend exited before it started listening on port ${PORT}." >&2
  echo "       Last lines of ${LOG_ERR}:" >&2
  tail -n 20 "${LOG_ERR}" >&2 2>/dev/null || true
  rm -f "${PID_FILE}" "${LAUNCHER_PID_FILE}"
  exit 1
}

# ---------------------------------------------------------------------------
# Restart
# ---------------------------------------------------------------------------

cmd_restart() {
  require_port_inspection || return 1

  local before after survivor overlap=""
  before="$(identity_set | tr '\n' ' ')"
  if [[ -n "${before// /}" ]]; then
    echo "Restarting. Process identities before the stop: ${before}"
  fi

  cmd_stop || return 1
  cmd_start || return 1

  after="$(identity_set | tr '\n' ' ')"
  if [[ -z "${after// /}" ]]; then
    echo "ERROR: restart reported a healthy start but no process owns port ${PORT}." >&2
    return 1
  fi

  # The assertion that used to be missing. A restart that hands back the same
  # PID and the same start time did not restart anything, whatever the health
  # check says.
  for survivor in ${after}; do
    case " ${before} " in *" ${survivor} "*) overlap="${overlap} ${survivor}" ;; esac
  done
  if [[ -n "${overlap// /}" ]]; then
    echo "ERROR: restart did NOT replace the backend process." >&2
    echo "       Still alive from before the restart: ${overlap}" >&2
    echo "       The backend is serving the code it was serving before. Treat this" >&2
    echo "       run as a failed rollout, not a successful one." >&2
    return 1
  fi

  echo "Restart verified: process identities after the start: ${after}"
  echo "None of them was serving before the stop, so the process was replaced."
  return 0
}

print_usage() {
  cat <<EOF

  API Control (sh)

  Usage: ./api.sh <command>

  Commands:
    start     Start the API (skips if already healthy, refuses a foreign port owner)
    stop      Stop the API and verify the port is free
    restart   Stop + start, and assert the process was actually replaced
    status    Show current API status and health

  Environment:
    PORT                    override the port pinned by the checkout folder name
    API_PORT_OVERRIDE=1     accept a PORT that disagrees with that default
    API_STOP_TIMEOUT_SECS   graceful shutdown budget before a forced kill (default 20)
    API_START_TIMEOUT_SECS  health-check poll budget before start gives up (default 180)

EOF
}

CMD="${1:-}"
case "${CMD}" in
  start)   cmd_start ;;
  stop)    cmd_stop ;;
  restart) cmd_restart ;;
  status)  cmd_status ;;
  ""|-h|--help|help) print_usage; exit 0 ;;
  *) echo "Unknown command: '${CMD}'"; print_usage; exit 2 ;;
esac
