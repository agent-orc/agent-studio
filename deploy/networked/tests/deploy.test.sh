#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
scratch="$(mktemp -d)"
trap 'rm -rf "$scratch"' EXIT
export FAKE_LOG="$scratch/actions.log"
export FAKE_BLOCKED=0
export FAKE_BAD_PLAN=0
export FAKE_BUSY_REVIEW=0
export FAKE_RUNNING_GATE=0
export FAKE_LEGACY_SNAPSHOT=0
# AGT-3647: how many host_status reads still report a running review.
export FAKE_REVIEW_RUNNING_READS=0
export FAKE_STATUS_COUNT="$scratch/status-count"
commit="$(printf 'a%.0s' {1..40})"

cat >"$scratch/fake-host.sh" <<'FAKE'
#!/usr/bin/env bash
set -euo pipefail
action="$1"
shift
printf '%s\n' "$action" >>"$FAKE_LOG"
digest="$(printf 'b%.0s' {1..64})"
case "$action" in
  build:api|build:web|build:engine)
    name="${action#build:}"
    printf '{"commit":"%s","ref":"example/%s@sha256:%s","sha256":"%s"}\n' "$1" "$name" "$digest" "$digest" ;;
  build:agent_host|build:gate_worker)
    printf '{"commit":"%s","ref":"/release/%s-%s","sha256":"%s"}\n' "$1" "${action#build:}" "$1" "$digest" ;;
  snapshot:server)
    if [[ "$FAKE_LEGACY_SNAPSHOT" == 1 ]]; then
      printf '{"value":{"apiImage":"old-api:tag","webImage":"old-web:tag","apiDigest":"%s","webDigest":"%s","apiImageId":"sha256:%s","webImageId":"sha256:%s","gateSshHost":null,"workerPath":null}}\n' "$digest" "$digest" "$digest" "$digest"
    else
      printf '{"value":{"apiImage":"old-api:tag","webImage":"old-web:tag","apiDigest":"%s","webDigest":"%s","apiImageId":"sha256:%s","webImageId":"sha256:%s","gateSshHost":"old-gate","workerPath":"old-worker"}}\n' "$digest" "$digest" "$digest" "$digest"
    fi ;;
  snapshot:gate) printf '{"value":{"workerPath":"old-worker"}}\n' ;;
  snapshot:engine) printf '{"value":{"engineVersion":"old-engine"}}\n' ;;
  snapshot:coding) printf '{"value":{"agentHostRelease":"old-agent"}}\n' ;;
  snapshot:review) printf '{"value":{"agentHostRelease":"old-agent","image":"old-review"}}\n' ;;
  preflight)
    if [[ "$FAKE_BLOCKED" == 1 ]]; then
      printf '{"allowed":false,"blockers":[{"code":"pending-push"}]}\n'
      exit 8
    else
      printf '{"allowed":true,"blockers":[]}\n'
    fi ;;
  work_state)
    if [[ "$FAKE_RUNNING_GATE" == 1 ]]; then
      printf '{"formedBatches":[],"runningGates":["active"]}\n'
    else
      printf '{"formedBatches":[],"runningGates":[]}\n'
    fi ;;
  plan)
    expiry="$(date -u -d '+30 minutes' +%Y-%m-%dT%H:%M:%SZ)"
    if [[ "$FAKE_BAD_PLAN" == 1 ]]; then
      printf '{"ok":true,"targetVersion":"%s","expiresAtUtc":"%s","steps":[]}\n' "$1" "$expiry"
    else
      printf '{"ok":true,"targetVersion":"%s","expiresAtUtc":"%s","steps":[{"order":1,"kind":"drain"},{"order":2,"kind":"task-server"},{"order":3,"kind":"readiness"},{"order":4,"kind":"engine"},{"order":5,"kind":"hosts"}]}\n' "$1" "$expiry"
    fi ;;
  gate_publish) printf '{"commit":"%s","artifactRef":"/release/gate_worker-%s","workerPath":"/gate/releases/%s/OrchestratorApi.dll"}\n' "$1" "$1" "$1" ;;
  gate_verify|review_copy) printf '{"sha256":"%s"}\n' "$digest" ;;
  server_switch) [[ "$3" == "/gate/releases/$1/OrchestratorApi.dll" ]]; printf '{"switched":true}\n' ;;
  drain) printf '{"drained":true}\n' ;;
  host_drain) printf '{"ok":true,"clientId":"%s","draining":true}\n' "$3" ;;
  host_resume) printf '{"ok":true,"clientId":"%s","admitting":true}\n' "$3" ;;
  host_status)
    reads="$(cat "$FAKE_STATUS_COUNT" 2>/dev/null || printf 0)"
    printf '%s' "$((reads + 1))" >"$FAKE_STATUS_COUNT"
    if [[ "$3" == *review* && ( "$FAKE_REVIEW_RUNNING_READS" == always || "$reads" -lt "$FAKE_REVIEW_RUNNING_READS" ) ]]; then
      printf '{"ok":true,"clientId":"%s","draining":true,"drained":false,"running":[{"kind":"review","attemptId":"review-1","taskKey":"DEMO-1"}]}\n' "$3"
    else
      printf '{"ok":true,"clientId":"%s","draining":true,"drained":true,"running":[]}\n' "$3"
    fi ;;
  resume) printf '{"admitting":true}\n' ;;
  server_health|engine_health|coding_health) printf '{"healthy":true,"commit":"%s"}\n' "$1" ;;
  model_probe) printf '{"accepted":true}\n' ;;
  review_build) printf '{"commit":"%s","agentHostSha256":"%s","planDigest":"plan-123","imageRef":"example/review@sha256:%s"}\n' "$1" "$digest" "$digest" ;;
  review_idle)
    if [[ "$FAKE_BUSY_REVIEW" == 1 ]]; then printf '{"idle":false}\n'; else printf '{"idle":true}\n'; fi ;;
  review_verify) printf '{"commit":"%s","planDigest":"plan-123","imageRef":"example/review@sha256:%s"}\n' "$1" "$digest" ;;
  rollback:server)
    if [[ "$FAKE_LEGACY_SNAPSHOT" == 1 ]]; then
      [[ "$2" == *'"apiImage":"old-api:tag"'* && "$2" == *'"workerPath":null'* ]]
    else
      [[ "$2" == *'"apiImage":"old-api:tag"'* && "$2" == *'"workerPath":"old-worker"'* ]]
    fi
    printf '{"restored":true}\n' ;;
  rollback:*) printf '{"restored":true}\n' ;;
  *) printf '{}\n' ;;
esac
FAKE
chmod +x "$scratch/fake-host.sh"

export FAKE_SCRIPT="$scratch/fake-host.sh" FAKE_COMMIT="$commit" MANIFEST="$scratch/manifest.json"
python3 - <<'PY'
import json, os
script = os.environ['FAKE_SCRIPT']
def cmd(action, *args): return ['bash', script, action, *args]
commands = {
    'build': {name: cmd('build:' + name, '{commit}') for name in ('api', 'web', 'engine', 'agent_host', 'gate_worker')},
    'snapshot': {role: cmd('snapshot:' + role, '{host}') for role in ('server', 'gate', 'engine', 'coding', 'review')},
    'rollback': {role: cmd('rollback:' + role, '{host}', '{previous}') for role in ('server', 'gate', 'engine', 'coding', 'review')},
}
for action in ('preflight', 'work_state', 'plan', 'drain', 'gate_publish', 'gate_verify', 'server_switch',
               'server_health', 'engine_switch', 'engine_health', 'coding_install', 'coding_health',
               'model_probe', 'review_copy', 'review_build', 'review_idle', 'review_recreate', 'review_verify', 'resume'):
    commands[action] = cmd(action, '{commit}', '{host}' if action not in ('preflight', 'work_state', 'plan') else '{commit}')
commands['server_switch'].append('{gate_worker_path}')
for action in ('host_drain', 'host_status', 'host_resume'):
    commands[action] = cmd(action, '{commit}', '{host}', '{client_id}')
hosts = [{'id': role, 'role': role, 'target': role} for role in ('server', 'gate', 'engine', 'coding', 'review')]
hosts[3]['drainClients'] = ['agent-runner-01']
hosts[4]['drainClients'] = ['agent-runner-01-review', 'kori-review-2']
manifest = {'schema': 1, 'hosts': hosts, 'commands': commands,
            'quietPoint': {'deadlineSeconds': 2, 'pollSeconds': 0.1}}
with open(os.environ['MANIFEST'], 'w') as file: json.dump(manifest, file)
PY

dry="$scratch/dry.json"
bash "$root/deploy.sh" deploy "$commit" --manifest "$MANIFEST" --journal "$dry" --dry-run >"$scratch/plan.txt"
grep -q 'preflight' "$scratch/plan.txt"
grep -q 'review_recreate' "$scratch/plan.txt"
[[ ! -e "$dry" && ! -e "$FAKE_LOG" ]]

export FAKE_BLOCKED=1
if bash "$root/deploy.sh" deploy "$commit" --manifest "$MANIFEST" --journal "$scratch/blocked.json" >"$scratch/blocked.out" 2>"$scratch/blocked.err"; then
  echo 'blocked preflight unexpectedly passed' >&2
  exit 1
fi
grep -q 'pending-push' "$scratch/blocked.err"
grep -q 'no quiet point before the deadline' "$scratch/blocked.err"
! grep -q '^server_switch$' "$FAKE_LOG"
# An aborted deploy gives every drained host back.
[[ "$(grep -c '^host_resume$' "$FAKE_LOG")" == 3 ]]

export FAKE_BLOCKED=0
: >"$FAKE_LOG"
export FAKE_RUNNING_GATE=1
if bash "$root/deploy.sh" deploy "$commit" --manifest "$MANIFEST" --journal "$scratch/running-gate.json" >"$scratch/running-gate.out" 2>"$scratch/running-gate.err"; then
  echo 'running gate unexpectedly passed' >&2
  exit 1
fi
grep -q 'running gates block deployment' "$scratch/running-gate.err"
! grep -q '^server_switch$' "$FAKE_LOG"

export FAKE_RUNNING_GATE=0
: >"$FAKE_LOG"
export FAKE_BAD_PLAN=1
if bash "$root/deploy.sh" deploy "$commit" --manifest "$MANIFEST" --journal "$scratch/bad-plan.json" >"$scratch/bad-plan.out" 2>"$scratch/bad-plan.err"; then
  echo 'bad plan unexpectedly passed' >&2
  exit 1
fi
grep -q 'OP-6 plan must order' "$scratch/bad-plan.err"
! grep -q '^server_switch$' "$FAKE_LOG"

export FAKE_BAD_PLAN=0
: >"$FAKE_LOG"
export FAKE_BUSY_REVIEW=1
if bash "$root/deploy.sh" deploy "$commit" --manifest "$MANIFEST" --journal "$scratch/busy-review.json" >"$scratch/busy-review.out" 2>"$scratch/busy-review.err"; then
  echo 'busy review unexpectedly passed' >&2
  exit 1
fi
grep -q 'expected idle=True' "$scratch/busy-review.err"
! grep -q '^review_recreate$' "$FAKE_LOG"

export FAKE_BUSY_REVIEW=0
: >"$FAKE_LOG"
export FAKE_REVIEW_RUNNING_READS=always
if bash "$root/deploy.sh" deploy "$commit" --manifest "$MANIFEST" --journal "$scratch/running-review.json" >"$scratch/running-review.out" 2>"$scratch/running-review.err"; then
  echo 'running review unexpectedly passed' >&2
  exit 1
fi
grep -q 'review-running review/DEMO-1' "$scratch/running-review.err"
! grep -q '^plan$' "$FAKE_LOG"
[[ "$(grep -c '^host_resume$' "$FAKE_LOG")" == 3 ]]

: >"$FAKE_LOG"
rm -f "$FAKE_STATUS_COUNT"
# The running review ends while the deploy waits; the deploy then proceeds.
export FAKE_REVIEW_RUNNING_READS=4
bash "$root/deploy.sh" deploy "$commit" --manifest "$MANIFEST" --journal "$scratch/deploy.json" >"$scratch/deploy.out"
export FAKE_REVIEW_RUNNING_READS=0
python3 - "$scratch/deploy.json" <<'PY'
import json, sys
record = json.load(open(sys.argv[1]))
assert record['status'] == 'passed'
assert all(step['status'] == 'passed' and step['durationSeconds'] >= 0
           and step['startedAtUtc'] and step['endedAtUtc'] for step in record['steps'])
names = [step['step'] for step in record['steps']]
assert names.index('server_switch') < names.index('coding_install:coding') < names.index('review_recreate:review')
assert names.index('server_health') < names.index('engine_switch') < names.index('coding_install:coding')
assert names.index('review_idle:review') < names.index('review_recreate:review')
# AGT-3647: drain first, wait for the quiet point, switch the server, then
# update and resume each host on its own.
assert names.index('host_drain:coding:agent-runner-01') < names.index('host_drain:review:kori-review-2') \
    < names.index('quiet_point') < names.index('plan') < names.index('server_switch')
assert names.index('model_probe:coding') < names.index('host_resume:coding:agent-runner-01') \
    < names.index('review_copy:review')
assert names.index('review_verify:review') < names.index('host_resume:review:agent-runner-01-review') \
    < names.index('host_resume:review:kori-review-2') < names.index('resume')
assert record['quietPoint']['polls'] >= 2 and record['quietPoint']['blockers'] == []
PY

bash "$root/deploy.sh" rollback "$commit" --manifest "$MANIFEST" --journal "$scratch/deploy.json" --dry-run >"$scratch/rollback-plan.txt"
grep -q 'rollback review review' "$scratch/rollback-plan.txt"
cp "$MANIFEST" "$scratch/changed-manifest.json"
python3 - "$scratch/changed-manifest.json" <<'PY'
import json, sys
path = sys.argv[1]
value = json.load(open(path))
value['hosts'][0]['target'] = 'different-server'
with open(path, 'w') as file: json.dump(value, file)
PY
if bash "$root/deploy.sh" rollback "$commit" --manifest "$scratch/changed-manifest.json" --journal "$scratch/deploy.json" --dry-run >"$scratch/changed.out" 2>"$scratch/changed.err"; then
  echo 'changed rollback manifest unexpectedly passed' >&2
  exit 1
fi
grep -q 'manifest changed' "$scratch/changed.err"
bash "$root/deploy.sh" rollback "$commit" --manifest "$MANIFEST" --journal "$scratch/deploy.json" >"$scratch/rollback.out"
python3 - "$scratch/deploy.json" <<'PY'
import json, sys
record = json.load(open(sys.argv[1]))
assert record['status'] == 'rolled-back'
assert [step['step'] for step in record['steps'] if step['step'].startswith('rollback:')] == [
    'rollback:review', 'rollback:coding', 'rollback:engine', 'rollback:server', 'rollback:gate']
assert [step['step'] for step in record['steps'] if step['step'].startswith('rollback_host_resume:')] == [
    'rollback_host_resume:coding:agent-runner-01', 'rollback_host_resume:review:agent-runner-01-review',
    'rollback_host_resume:review:kori-review-2']
PY
export FAKE_LEGACY_SNAPSHOT=1
[[ ! -e "$scratch/legacy.json" ]]
bash "$root/deploy.sh" deploy "$commit" --manifest "$MANIFEST" --journal "$scratch/legacy.json" >"$scratch/legacy.out"
python3 - "$scratch/legacy.json" <<'PY'
import json, sys
record = json.load(open(sys.argv[1]))
assert record['status'] == 'passed'
assert record['previous']['server']['value']['gateSshHost'] is None
assert record['previous']['server']['value']['workerPath'] is None
PY
bash "$root/deploy.sh" rollback "$commit" --manifest "$MANIFEST" --journal "$scratch/legacy.json" >"$scratch/legacy-rollback.out"
echo 'networked deploy shell tests passed'
