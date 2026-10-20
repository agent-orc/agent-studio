#!/usr/bin/env sh
# Layer 3 - External restart orchestrator for the stable instance.
#
# Stable is the orchestrator that runs tasks; the source it serves is the
# same source those tasks edit. Stable is never allowed to stop or restart
# itself: a job that touches the running source while the same process is
# running is the recipe for the silent crashes that motivated this layer
# (see ADR-0021 and docs/system/architecture/decisions/adr-archive.md).
#
# This script is the external decision point. It does NOT loop; it makes one
# decision per invocation and exits. The companion `run-stable-restart-watcher.sh`
# wraps this in a sleep-tick loop. Same convention as run-system-review.sh.
#
# Decision rules (single tick):
#   1. In review-batch mode (the default), compare 4-review with the last
#      snapshot and continue only after the configured arrival threshold.
#   2. In main-advance mode, compare the stable checkout HEAD with remote main
#      and continue only when main has moved. This is the deploy-cron handoff
#      for a completed develop -> main promotion.
#   3. Probe stable's /api/runner/status. If unreachable, exit and try later.
#   4. If any project reports a non-null activeJobId, exit because it is busy.
#   5. Otherwise: drain the merge gate, call update-stable.sh, verify the new
#      checkout, append a structured log line, and resume the runner.
#
# Read-only contract with the runner: the runner is the single state-machine
# authority over its own job state (ADR-0017). This script never pokes
# job.json, never writes job state, never calls a state-mutating endpoint.
# It only counts files in 4-review and reads /api/runner/status.
#
# Env overrides:
#   ATP_WORKSPACE            workspace root (default: C:/Projects/agent-taskboard-workspace)
#   ATP_PROJECT              project name to watch (default: agent-taskboard)
#   ATP_STABLE_CHECKOUT      stable repo (default: <devspace>/agent-taskboard-stable)
#   ATP_STABLE_API           stable API base URL (default: http://127.0.0.1:5031)
#   ATP_RESTART_THRESHOLD    new-job count that triggers a restart (default: 3)
#   ATP_RESTART_TRIGGER      review-batch (default) or main-advance
#   ATP_DEPLOY_REMOTE        stable checkout remote watched by main-advance
#                            (default: origin)
#   ATP_UPDATE_SCRIPT        update script path
#                            (default: <dev-checkout>/scripts/update-stable.sh)
#   ATP_GATE_DRAIN_TIMEOUT_SECONDS
#                            max wait for an active merge gate (default: 120)
#   ATP_GATE_DRAIN_POLL_SECONDS
#                            merge-gate poll interval (default: 2)
#
# Exit codes:
#   0  decision tick handled (no-op or successful restart)
#   2  fatal misconfiguration (missing update script, missing stable checkout)

set -eu

# Pin to the C locale for the whole script so `sort` and `comm` agree on
# byte ordering. Without this, MSYS/Git-Bash on Windows runs `sort` under
# LC_ALL=C (set explicitly below) but `comm` under the inherited UTF-8
# locale, and `comm --check-order` then aborts with "input is not in
# sorted order" the moment any new job arrives. Setting it once at the
# top removes the mismatch.
export LC_ALL=C

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
THIS_CHECKOUT="$(cd "$SCRIPT_DIR/../.." && pwd)"
DEVSPACE_DIR="$(cd "$THIS_CHECKOUT/.." && pwd)"

WORKSPACE="${ATP_WORKSPACE:-C:/Projects/agent-taskboard-workspace}"
PROJECT="${ATP_PROJECT:-agent-taskboard}"
STABLE="${ATP_STABLE_CHECKOUT:-$DEVSPACE_DIR/agent-taskboard-stable}"
STABLE_API="${ATP_STABLE_API:-http://127.0.0.1:5031}"
THRESHOLD="${ATP_RESTART_THRESHOLD:-3}"
TRIGGER="${ATP_RESTART_TRIGGER:-review-batch}"
DEPLOY_REMOTE="${ATP_DEPLOY_REMOTE:-origin}"
UPDATE_SCRIPT="${ATP_UPDATE_SCRIPT:-$THIS_CHECKOUT/scripts/update-stable.sh}"
GATE_DRAIN_TIMEOUT="${ATP_GATE_DRAIN_TIMEOUT_SECONDS:-120}"
GATE_DRAIN_POLL="${ATP_GATE_DRAIN_POLL_SECONDS:-2}"

LANE_DIR="$WORKSPACE/projects/$PROJECT/${ATP_RESTART_LANE:-4-auto-review}"
LOG_DIR="$WORKSPACE/logs"
STATE_DIR="$LOG_DIR/stable-restart-watcher"
SNAPSHOT="$STATE_DIR/snapshot.txt"
JSONL="$LOG_DIR/stable-restarts.jsonl"

log() { printf '[restart-watcher] %s\n' "$*" >&2; }

iso_now() { date -u +%Y-%m-%dT%H:%M:%SZ; }

# Build the sorted list of job folder names currently in 4-review.
# Empty / missing lane → empty list, no error.
list_review_now() {
  [ -d "$LANE_DIR" ] || return 0
  ls -1 "$LANE_DIR" 2>/dev/null | while IFS= read -r entry; do
    [ -d "$LANE_DIR/$entry" ] && printf '%s\n' "$entry"
  done | LC_ALL=C sort
}

count_lines() {
  awk 'END { print NR }'
}

# Return 0 if stable reports any project with a non-null activeJobId,
# 1 if all projects are idle, 2 if the API is unreachable.
# Note on the regex: stable's /api/runner/status returns
#   "activeJobId":"<id>"  when busy
#   "activeJobId":null    when idle
# We can't pull in jq portably, but distinguishing those two patterns with
# a quoted-vs-null grep is unambiguous against this shape.
stable_active_state() {
  if [ -n "${AGENT_STUDIO_API_TOKEN:-}" ]; then
    body="$(curl -fsS --max-time 5 -H "Authorization: Bearer $AGENT_STUDIO_API_TOKEN" "$STABLE_API/api/runner/status" 2>/dev/null || true)"
  else
    body="$(curl -fsS --max-time 5 "$STABLE_API/api/runner/status" 2>/dev/null || true)"
  fi
  [ -n "$body" ] || return 2
  if printf '%s' "$body" | grep -qE '"activeJobId"[[:space:]]*:[[:space:]]*"[^"]+"'; then
    return 0
  fi
  return 1
}

# Return 0 while merge/gate/rollback is active, 1 when idle, and 2 when the
# endpoint is unavailable (for example while updating an older stable build).
stable_merge_gate_state() {
  body="$(curl -fsS --max-time 5 "$STABLE_API/healthz/drain" 2>/dev/null || true)"
  body="$(printf '%s' "$body" | tr -d '[:space:]')"
  case "$body" in
    gate-busy) return 0 ;;
    idle) return 1 ;;
    *) return 2 ;;
  esac
}

wait_for_merge_gate() {
  started="$(date -u +%s)"
  while :; do
    if stable_merge_gate_state; then
      now="$(date -u +%s)"
      elapsed=$((now - started))
      if [ "$elapsed" -ge "$GATE_DRAIN_TIMEOUT" ]; then
        log "merge gate remained busy for ${elapsed}s; drain window exhausted, continuing with hard restart"
        return 0
      fi
      log "merge gate busy; waiting up to ${GATE_DRAIN_TIMEOUT}s before hard restart (${elapsed}s elapsed)"
      sleep "$GATE_DRAIN_POLL"
      continue
    else
      gate_state=$?
      case "$gate_state" in
        1)
          log "merge gate idle; restart may proceed"
          return 0
          ;;
        2)
          # Rolling-upgrade compatibility: an older stable process does not have
          # this endpoint yet. The existing runner-idle guard still applies.
          log "merge-gate drain endpoint unavailable; proceeding with runner-idle restart"
          return 0
          ;;
      esac
    fi
  done
}

stable_head() {
  git -C "$STABLE" rev-parse --short HEAD 2>/dev/null || printf 'unknown'
}

stable_full_head() {
  git -C "$STABLE" rev-parse HEAD 2>/dev/null || printf 'unknown'
}

remote_main_head() {
  git -C "$STABLE" ls-remote --exit-code "$DEPLOY_REMOTE" refs/heads/main 2>/dev/null \
    | awk 'NR == 1 { print $1 }'
}

# JSON string escaping for fields that can carry path-like values.
# We only emit ASCII fields so a minimal escape (backslash, quote) suffices.
json_escape() {
  printf '%s' "$1" | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g'
}

# --- main ---------------------------------------------------------------------

if [ ! -x "$UPDATE_SCRIPT" ]; then
  log "ERROR: update script missing or not executable: $UPDATE_SCRIPT"
  exit 2
fi
if [ ! -d "$STABLE" ]; then
  log "ERROR: stable checkout missing: $STABLE"
  exit 2
fi
case "$TRIGGER" in
  review-batch|main-advance) ;;
  *)
    log "ERROR: ATP_RESTART_TRIGGER must be review-batch or main-advance, got: $TRIGGER"
    exit 2
    ;;
esac
case "$GATE_DRAIN_TIMEOUT:$GATE_DRAIN_POLL" in
  *[!0-9:]*|:*|*:)
    log "ERROR: gate drain timeout and poll interval must be non-negative integer seconds"
    exit 2
    ;;
esac
if [ "$GATE_DRAIN_POLL" -eq 0 ]; then
  log "ERROR: gate drain poll interval must be at least one second"
  exit 2
fi

mkdir -p "$STATE_DIR" "$LOG_DIR"

target_main=""
new_count=0
if [ "$TRIGGER" = "review-batch" ]; then
  # Bootstrap the snapshot on first run; do NOT restart on the bootstrap tick.
  if [ ! -r "$SNAPSHOT" ]; then
    list_review_now > "$SNAPSHOT"
    baseline_n="$(count_lines < "$SNAPSHOT")"
    log "bootstrap: snapshot taken at $baseline_n jobs in $LANE_DIR; no restart"
    exit 0
  fi

  # Compute new arrivals since the snapshot.
  NEW_TMP="$(mktemp 2>/dev/null || mktemp -t restart-watcher)"
  trap 'rm -f "$NEW_TMP"' EXIT
  list_review_now | comm -23 - "$SNAPSHOT" > "$NEW_TMP"
  new_count="$(count_lines < "$NEW_TMP")"

  if [ "$new_count" -lt "$THRESHOLD" ]; then
    log "new=$new_count (< $THRESHOLD), no restart"
    exit 0
  fi
  trigger_reason="new=$new_count >= $THRESHOLD"
else
  set +e
  target_main="$(remote_main_head)"
  remote_rc=$?
  set -e
  if [ "$remote_rc" -ne 0 ] || [ -z "$target_main" ]; then
    log "remote main is unavailable from $DEPLOY_REMOTE; skipping this cron tick"
    exit 0
  fi
  deployed_head="$(stable_full_head)"
  if [ "$deployed_head" = "$target_main" ]; then
    log "stable already matches $DEPLOY_REMOTE/main at $target_main; no deploy"
    exit 0
  fi
  trigger_reason="stable=$deployed_head target-main=$target_main"
fi

# A trigger is pending. Now check stable's runner state.
set +e
stable_active_state
state=$?
set -e
case "$state" in
  0) log "$trigger_reason but stable has an active job; skipping"; exit 0 ;;
  2) log "$trigger_reason but stable API is unreachable at $STABLE_API; skipping"; exit 0 ;;
esac

wait_for_merge_gate

log "$trigger_reason and stable is idle; calling $UPDATE_SCRIPT"

ts="$(iso_now)"
before="$(stable_head)"
start_epoch="$(date -u +%s)"

set +e
"$UPDATE_SCRIPT" >&2
update_rc=$?
set -e

end_epoch="$(date -u +%s)"
duration=$((end_epoch - start_epoch))
after="$(stable_head)"

if [ "$update_rc" -eq 0 ]; then
  status="ok"
  # update-stable.sh stops stable, pulls, and restarts. The fresh backend
  # comes back up in whatever mode it was in before, so for the
  # pause-then-update-then-resume recipe we need an explicit verified
  # resume here. Without verification, a transient backend-restart race
  # or a missing X-Client-Id silently leaves the runner paused — that is
  # the regression that motivated this hardening (see in-product
  # MetaCycleHostedService.ResumeWithVerificationAsync for the matching
  # in-process path).
  if [ -x "$SCRIPT_DIR/resume-runner.sh" ]; then
    if ATP_API="$STABLE_API" "$SCRIPT_DIR/resume-runner.sh" "$PROJECT"; then
      status="ok"
    else
      resume_rc=$?
      status="resume-failed-rc-$resume_rc"
      log "resume-runner.sh failed (rc=$resume_rc); stable runner may still be paused"
    fi
  fi
  if [ "$TRIGGER" = "main-advance" ]; then
    set +e
    current_remote_main="$(remote_main_head)"
    current_remote_rc=$?
    set -e
    deployed_after="$(stable_full_head)"
    if [ "$current_remote_rc" -ne 0 ] || [ -z "$current_remote_main" ]; then
      status="remote-verify-failed"
      log "updated stable, but remote main could not be verified"
    elif [ "$deployed_after" != "$current_remote_main" ]; then
      status="behind-main-after-update"
      log "updated stable to $deployed_after, but $DEPLOY_REMOTE/main is $current_remote_main; a later cron tick will retry"
    fi
  fi
else
  status="failed"
  log "update-stable.sh exited with code $update_rc"
fi

# Review-batch mode refreshes its arrival snapshot regardless of outcome. The
# main-advance mode deliberately keeps retrying while stable differs from main.
if [ "$TRIGGER" = "review-batch" ]; then
  list_review_now > "$SNAPSHOT"
fi
review_after="$(list_review_now | count_lines)"

ts_e="$(json_escape "$ts")"
before_e="$(json_escape "$before")"
after_e="$(json_escape "$after")"
status_e="$(json_escape "$status")"
trigger_e="$(json_escape "$TRIGGER")"
target_main_e="$(json_escape "$target_main")"

printf '{"ts":"%s","event":"restart","trigger":"%s","status":"%s","jobsSinceLastRestart":%s,"targetMain":"%s","headBefore":"%s","headAfter":"%s","durationSeconds":%s,"reviewCountAfter":%s}\n' \
  "$ts_e" "$trigger_e" "$status_e" "$new_count" "$target_main_e" "$before_e" "$after_e" "$duration" "$review_after" \
  >> "$JSONL"

log "logged restart: trigger=$TRIGGER status=$status duration=${duration}s before=$before after=$after"
