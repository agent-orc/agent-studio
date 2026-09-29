#!/usr/bin/env bash
# Contract tests for the promotion gate capacity window (AGT-2982): pure load
# and quota policy, then record/apply/restore against a fake systemctl on
# success, failure, and signal.

set -Eeuo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
helper="$repo_root/scripts/release/release-gate-window.sh"
test_root=$(mktemp -d 2>/dev/null || mktemp -d -t release-gate-window-tests)
cleanup() {
  # A failing signal scenario can leave gate processes behind; never leak them.
  local pids
  pids=$(cat "$test_root"/*/gate-tree.pids 2>/dev/null || true)
  # shellcheck disable=SC2086 # one pid per word.
  [[ -z "$pids" ]] || { pkill -KILL -P "${pids//$'\n'/,}" 2>/dev/null; kill -KILL $pids 2>/dev/null; } || true
  [[ -n ${KEEP_TEST_ROOT:-} ]] || rm -rf -- "$test_root"
}
trap cleanup EXIT HUP INT TERM

fail() {
  printf 'release-gate-window test failed: %s\n' "$*" >&2
  exit 1
}

expect_eq() {
  [[ "$1" == "$2" ]] || fail "$3: expected '$2', got '$1'"
}

# --- Unit: load check and quota policy ---------------------------------------
# shellcheck source=release-gate-window.sh
source "$helper"

gate_load_is_hot 24.01 12 2 || fail 'load 24.01 on 12 cores must be hot at 2x'
! gate_load_is_hot 24.00 12 2 || fail 'load exactly at the threshold is not hot'
! gate_load_is_hot 9.5 12 2 || fail 'quiet host must not be hot'
gate_load_is_hot 114.3 12 2 || fail 'incident load 114 must be hot'
gate_load_is_hot 6.1 4 1.5 || fail 'fractional factor 1.5 on 4 cores'
! gate_load_is_hot 5.9 4 1.5 || fail 'fractional factor below threshold'
expect_eq "$(gate_load_threshold 12 2)" 24.00 'threshold for 12 cores'
expect_eq "$(gate_load_threshold 4 1.5)" 6.00 'threshold for fractional factor'

expect_eq "$(gate_window_default_quotas 12 5)" '200% 500%' '12 cores, 5 reserved matches the manual window'
expect_eq "$(gate_window_default_quotas 32 5)" '700% 2000%' '32 cores'
expect_eq "$(gate_window_default_quotas 4 5)" '100% 100%' 'tiny host keeps one core per unit'
expect_eq "$(gate_window_default_quotas 8 5)" '100% 200%' '8 cores, 3-core runner budget'

expect_eq "$(gate_window_effective_quota '' 200%)" 200% 'unlimited unit is throttled'
expect_eq "$(gate_window_effective_quota 1200% 200%)" 200% 'higher recorded quota is lowered'
expect_eq "$(gate_window_effective_quota 150% 200%)" 150% 'lower recorded quota is never raised'

expect_eq "$(gate_window_quota_percent infinity)" '' 'infinity'
expect_eq "$(gate_window_quota_percent 12s)" 1200% 'whole seconds'
expect_eq "$(gate_window_quota_percent 7.200000s)" 720% 'fractional seconds'
expect_eq "$(gate_window_quota_percent 500ms)" 50% 'milliseconds'
expect_eq "$(gate_window_quota_percent '1min 12s')" 7200% 'compound timespan'
printf '%s\n' 'gate window unit policy passed'

# --- Contract: fake systemctl -------------------------------------------------
fake_bin="$test_root/bin"
mkdir -p "$fake_bin"
cat > "$fake_bin/systemctl" <<'EOF'
#!/usr/bin/env bash
set -eu
state=$FAKE_SYSTEMCTL_STATE
case "$1" in
  show)
    property=$3 unit=$5
    if [[ ! -f "$state/$unit.quota" ]]; then
      [[ "$property" == LoadState ]] && { printf 'not-found\n'; exit 0; }
      exit 1
    fi
    case "$property" in
      LoadState) printf 'loaded\n' ;;
      CPUQuotaPerSecUSec) cat "$state/$unit.quota" ;;
      *) exit 1 ;;
    esac
    ;;
  set-property)
    [[ "$2" == --runtime ]] || exit 64
    unit=$3 assignment=$4
    [[ "$assignment" == CPUQuota=* ]] || exit 64
    printf '%s %s\n' "$unit" "$assignment" >> "$state/calls.log"
    # A restore while any recorded gate process is still alive is a contract
    # violation: the quotas must only come back once the gate tree is gone.
    if [[ -f "$state/gate-tree.pids" && -f "$state/gate-window-applied" ]]; then
      while read -r pid; do
        ! kill -0 "$pid" 2>/dev/null || printf '%s\n' "$pid" >> "$state/alive-at-restore.log"
      done < "$state/gate-tree.pids"
    fi
    touch "$state/gate-window-applied"
    if [[ -f "$state/fail-$unit" ]]; then
      exit 1
    fi
    value=${assignment#CPUQuota=}
    if [[ -z "$value" ]]; then
      printf 'infinity\n' > "$state/$unit.quota"
    else
      printf '%ss\n' "$(awk -v p="${value%\%}" 'BEGIN { printf "%g", p / 100 }')" > "$state/$unit.quota"
    fi
    ;;
  *) exit 64 ;;
esac
EOF
chmod +x "$fake_bin/systemctl"

# Each scenario gets a fresh state directory: coding unlimited, review 600%.
new_state() {
  local name=$1
  local state="$test_root/$name"
  mkdir -p "$state"
  printf 'infinity\n' > "$state/agent-runner.service.quota"
  printf '6s\n' > "$state/agent-runner-review.service.quota"
  printf '3.10 2.00 1.00 1/100 1\n' > "$state/loadavg"
  : > "$state/calls.log"
  printf '%s\n' "$state"
}

# exec so that a backgrounded "(run_helper ...) &" makes $! the helper itself.
run_helper() {
  local state=$1
  shift
  exec_helper "$state" "$@" &
  local pid=$!
  wait "$pid"
}

exec_helper() {
  local state=$1
  shift
  exec env FAKE_SYSTEMCTL_STATE="$state" \
    RELEASE_GATE_SYSTEMCTL="$fake_bin/systemctl" \
    RELEASE_GATE_SUDO= \
    RELEASE_GATE_CPU_COUNT=12 \
    RELEASE_GATE_LOADAVG_FILE="$state/loadavg" \
    RELEASE_GATE_POLL_SECONDS=1 \
    RELEASE_GATE_SETTLE_SECONDS="${SETTLE:-2}" \
    "$helper" --record "$state/record.env" -- "$@"
}

record_has() {
  grep -Fxq "$2" "$1/record.env" || fail "record lacks '$2': $(cat "$1/record.env")"
}

assert_restored() {
  local state=$1
  expect_eq "$(cat "$state/agent-runner.service.quota")" infinity 'coding quota restored'
  expect_eq "$(cat "$state/agent-runner-review.service.quota")" 6s 'review quota restored'
  expect_eq "$(tail -n 2 "$state/calls.log" | tr '\n' '|')" \
    'agent-runner.service CPUQuota=|agent-runner-review.service CPUQuota=600%|' 'restore calls'
}

# Success: record, apply the window while the gate runs, restore afterwards.
state=$(new_state success)
cat > "$test_root/observe-gate.sh" <<'EOF'
#!/usr/bin/env bash
cat "$1/agent-runner.service.quota" "$1/agent-runner-review.service.quota" > "$1/during-gate.txt"
printf '%s\n' PROMOTION_FULL_GATE=passed
EOF
chmod +x "$test_root/observe-gate.sh"
run_helper "$state" "$test_root/observe-gate.sh" "$state" > "$state/out.log" 2>&1
expect_eq "$(tr '\n' '|' < "$state/during-gate.txt")" '2s|5s|' 'quotas during the gate'
grep -Fxq PROMOTION_FULL_GATE=passed "$state/out.log" || fail 'gate output is passed through'
assert_restored "$state"
record_has "$state" 'recorded:agent-runner.service=infinity'
record_has "$state" 'recorded:agent-runner-review.service=600%'
record_has "$state" 'applied:agent-runner.service=200%'
record_has "$state" 'applied:agent-runner-review.service=500%'
record_has "$state" 'window-mode=applied'
record_has "$state" 'load-at-gate-start=3.10'
record_has "$state" 'load-at-gate-end=3.10'
record_has "$state" 'load-wait-seconds=0'
record_has "$state" 'gate-exit=0'
record_has "$state" 'quotas-restored=restored'
grep -q '^gate-duration-seconds=[0-9][0-9]*$' "$state/record.env" || fail 'gate duration recorded'
printf '%s\n' 'gate window success path passed'

# Failure: the gate's exit code is preserved and the quotas are restored.
state=$(new_state failure)
set +e
run_helper "$state" bash -c 'exit 9' > "$state/out.log" 2>&1
rc=$?
set -e
expect_eq "$rc" 9 'failing gate exit code'
assert_restored "$state"
record_has "$state" 'gate-exit=9'
record_has "$state" 'quotas-restored=restored'
printf '%s\n' 'gate window failure path passed'

# Signal: the gate is a shell with non-exec descendants, like the real full
# gate: a background worker, a background shell that ignores SIGTERM, and a
# foreground child the gate shell waits for. A signal to the helper must stop
# the whole tree (SIGKILL after the grace period for the TERM-ignoring one)
# before the quotas are restored.
cat > "$test_root/tree-gate.sh" <<'EOF'
#!/usr/bin/env bash
state=$1
printf '%s\n' "$$" >> "$state/gate-tree.pids"
sleep 300 &
printf '%s\n' "$!" >> "$state/gate-tree.pids"
bash -c 'trap "" TERM; printf "%s\n" "$$" >> "$1/gate-tree.pids"; while :; do sleep 0.1; done' _ "$state" &
bash -c 'printf "%s\n" "$$" >> "$1/gate-tree.pids"; sleep 300; :' _ "$state" &
# Foreground, non-exec child: this shell keeps running while it waits.
bash -c 'printf "%s\n" "$$" >> "$1/gate-tree.pids"; touch "$1/gate-started"; sleep 300; :' _ "$state"
printf '%s\n' 'gate shell continued after its child' >> "$state/gate-continued.log"
EOF
chmod +x "$test_root/tree-gate.sh"

gate_tree_alive() {
  local pid alive=
  while read -r pid; do
    ! kill -0 "$pid" 2>/dev/null || alive+="$pid "
  done < "$1/gate-tree.pids"
  printf '%s' "$alive"
}

signal_scenario() {
  local signal=$1 expected_rc=$2
  local state helper_pid rc
  state=$(new_state "signal-$signal")
  RELEASE_GATE_STOP_GRACE_SECONDS=1 exec_helper "$state" "$test_root/tree-gate.sh" "$state" \
    > "$state/out.log" 2>&1 &
  helper_pid=$!
  for _ in $(seq 1 100); do
    [[ -f "$state/gate-started" ]] && break
    sleep 0.1
  done
  [[ -f "$state/gate-started" ]] || fail "$signal scenario gate never started"
  expect_eq "$(wc -l < "$state/gate-tree.pids" | tr -d ' ')" 5 "$signal scenario gate tree size"
  expect_eq "$(cat "$state/agent-runner-review.service.quota")" 5s "window active before SIG$signal"
  kill "-$signal" "$helper_pid"
  set +e
  wait "$helper_pid"
  rc=$?
  set -e
  expect_eq "$rc" "$expected_rc" "SIG$signal exit code"
  expect_eq "$(gate_tree_alive "$state")" '' "SIG$signal leaves no gate process alive"
  [[ ! -s "$state/alive-at-restore.log" ]] \
    || fail "SIG$signal restored quotas while gate pids were alive: $(tr '\n' ' ' < "$state/alive-at-restore.log")"
  [[ ! -e "$state/gate-continued.log" ]] || fail "SIG$signal let the gate shell continue"
  grep -q 'outlived SIGTERM by 1s; sending SIGKILL' "$state/out.log" \
    || fail "SIG$signal did not escalate to SIGKILL for the TERM-ignoring descendant: $(cat "$state/out.log")"
  assert_restored "$state"
  record_has "$state" "gate-exit=signal-$signal"
  record_has "$state" 'quotas-restored=restored'
}
signal_scenario TERM 143
signal_scenario INT 130
signal_scenario HUP 129
printf '%s\n' 'gate window signal path passed'

# Broken output: the train pipes the helper into tee, and an interactive INT
# kills tee first. The helper's own log writes must not turn into a SIGPIPE
# death that skips the stop and the restore.
state=$(new_state signal-broken-pipe)
mkfifo "$state/output.fifo"
cat "$state/output.fifo" > "$state/out.log" &
reader_pid=$!
RELEASE_GATE_STOP_GRACE_SECONDS=1 exec_helper "$state" "$test_root/tree-gate.sh" "$state" \
  > "$state/output.fifo" 2>&1 &
helper_pid=$!
for _ in $(seq 1 100); do
  [[ -f "$state/gate-started" ]] && break
  sleep 0.1
done
[[ -f "$state/gate-started" ]] || fail 'broken-pipe scenario gate never started'
{ kill -KILL "$reader_pid"; wait "$reader_pid"; } 2>/dev/null || true
kill -INT "$helper_pid"
set +e
wait "$helper_pid"
rc=$?
set -e
expect_eq "$rc" 130 'INT exit code with a dead output reader'
expect_eq "$(gate_tree_alive "$state")" '' 'broken pipe leaves no gate process alive'
assert_restored "$state"
record_has "$state" 'gate-exit=signal-INT'
record_has "$state" 'quotas-restored=restored'
printf '%s\n' 'gate window signal path with a dead output reader passed'

# Late signal: a SIGTERM that arrives after the gate was reaped but before the
# helper exits must still end the helper with the signal exit code, not be
# parked as a "launch in progress" signal and swallowed. The load source is a
# FIFO so the helper blocks on the gate-end load read, a point that is
# provably after child_pid was cleared.
state=$(new_state signal-after-gate)
rm -f "$state/loadavg"
mkfifo "$state/loadavg"
feed_load() {
  timeout 10 bash -c 'printf "3.10 2.00 1.00 1/100 1\n" > "$1"' _ "$state/loadavg"
}
exec_helper "$state" true > "$state/out.log" 2>&1 &
helper_pid=$!
feed_load || fail 'late-signal scenario never read the load before the gate'
# gate-duration-seconds is recorded after the gate is reaped and child_pid is
# cleared, immediately before the gate-end load read that blocks on the FIFO.
for _ in $(seq 1 100); do
  grep -q '^gate-duration-seconds=' "$state/record.env" 2>/dev/null && break
  sleep 0.1
done
grep -q '^gate-duration-seconds=' "$state/record.env" || fail 'late-signal scenario gate never finished'
kill -TERM "$helper_pid"
# Serve every further load read until the helper exits, however many it takes.
# A write can race a reader that is just closing the FIFO; ignore that EPIPE.
(trap '' PIPE; set +e; while :; do printf '3.10 2.00 1.00 1/100 1\n' > "$state/loadavg"; done) 2>/dev/null &
feeder_pid=$!
set +e
wait "$helper_pid"
rc=$?
set -e
{ kill -KILL "$feeder_pid"; wait "$feeder_pid"; } 2>/dev/null || true
expect_eq "$rc" 143 "SIGTERM after the gate finished is not swallowed: $(cat "$state/out.log")"
expect_eq "$(grep '^gate-exit=' "$state/record.env" | tail -n 1)" 'gate-exit=signal-TERM' \
  'late SIGTERM is the recorded gate exit'
assert_restored "$state"
record_has "$state" 'quotas-restored=restored'
printf '%s\n' 'gate window late signal after the gate passed'

# Hot host: the helper waits a bounded time for the window, then proceeds.
state=$(new_state hot)
printf '40.00 30.00 20.00 1/100 1\n' > "$state/loadavg"
SETTLE=2 run_helper "$state" true > "$state/out.log" 2>&1
record_has "$state" 'load-before-window-wait=40.00'
record_has "$state" 'load-wait-seconds=2'
record_has "$state" 'load-at-gate-start=40.00'
record_has "$state" 'gate-exit=0'
grep -q 'still hot after 2s; starting the gate anyway' "$state/out.log" || fail 'hot host proceeds after the bound'
assert_restored "$state"
printf '%s\n' 'gate window hot-host bound passed'

# A window that cannot be applied: auto rolls back and runs the gate
# unthrottled; required refuses to run the gate. Both leave quotas as found.
state=$(new_state auto-unavailable)
touch "$state/fail-agent-runner-review.service"
run_helper "$state" true > "$state/out.log" 2>&1
record_has "$state" 'window-mode=unavailable'
record_has "$state" 'gate-exit=0'
expect_eq "$(cat "$state/agent-runner.service.quota")" infinity 'auto rollback restored coding'
expect_eq "$(cat "$state/agent-runner-review.service.quota")" 6s 'auto rollback left review untouched'

state=$(new_state required-unavailable)
touch "$state/fail-agent-runner-review.service"
set +e
RELEASE_GATE_WINDOW=required run_helper "$state" touch "$state/gate-ran" > "$state/out.log" 2>&1
rc=$?
set -e
expect_eq "$rc" 75 'required window failure exit code'
[[ ! -e "$state/gate-ran" ]] || fail 'required window must not run the gate'
record_has "$state" 'window-mode=failed'
expect_eq "$(cat "$state/agent-runner.service.quota")" infinity 'required rollback restored coding'
printf '%s\n' 'gate window unavailable paths passed'

# Off, or a host without the runner units: nothing is touched.
state=$(new_state off)
RELEASE_GATE_WINDOW=off run_helper "$state" true > "$state/out.log" 2>&1
record_has "$state" 'window-mode=off'
[[ ! -s "$state/calls.log" ]] || fail 'off must not call set-property'

state=$(new_state no-units)
rm -f "$state"/*.quota
run_helper "$state" true > "$state/out.log" 2>&1
record_has "$state" 'window-mode=skipped'
[[ ! -s "$state/calls.log" ]] || fail 'absent units must not be touched'
printf '%s\n' 'gate window off and absent-unit paths passed'

# The installed sudoers rule grants exactly this call shape and nothing wider.
sudoers="$repo_root/deploy/agent-host/sudoers.d/agent-runner"
grep -Fq '/usr/bin/systemctl ^set-property --runtime agent-runner[.]service CPUQuota\=([1-9][0-9]{0\,5}%)?$' "$sudoers" \
  || fail 'sudoers lacks the coding gate-window rule'
grep -Fq '/usr/bin/systemctl ^set-property --runtime agent-runner-review[.]service CPUQuota\=([1-9][0-9]{0\,5}%)?$' "$sudoers" \
  || fail 'sudoers lacks the review gate-window rule'
expect_eq "$(grep -c 'set-property' "$sudoers")" 2 'sudoers set-property rule count'
if command -v visudo >/dev/null 2>&1; then
  visudo -cf "$sudoers" >/dev/null || fail 'sudoers does not parse'
fi

printf '%s\n' 'release gate window tests passed'
