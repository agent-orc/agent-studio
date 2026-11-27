#!/usr/bin/env bash
# AGT-4239: a workspace shaped like the production installation whose start-up
# hung on 27 November 2026, for scripts/product-start-fixture-check.sh.
#
# usage: startup-fixture.sh <empty-or-missing-directory> [cards]
#
# Ingredients:
#   - one registered project whose repository has an origin and a develop line
#   - <cards> task cards (default 2000) that carry persisted commits; this is
#     the ingredient the hang needs: the unfixed CommitRepositoryBackfill read
#     Git and walked the whole task tree once per card before the listener
#   - an interrupted integration journal (pre-develop build gate, GateRunning)
#   - integration conflict receipts with recoveryQueued on a Ready and a
#     Progress card
#   - an orchestration occurrence in status "applying"
# The last three reproduce the rest of the production state; on their own
# they start in seconds.
set -euo pipefail

workspace="${1:?usage: startup-fixture.sh <directory> [cards]}"
cards="${2:-2000}"
if [[ -e "${workspace}" && -n "$(ls -A "${workspace}" 2>/dev/null)" ]]; then
  echo "startup-fixture: ${workspace} is not empty" >&2
  exit 2
fi
mkdir -p "${workspace}/projects/fixture"
project="${workspace}/projects/fixture"
repo="${workspace}/repo"
origin="${workspace}/origin.git"

g() { git -C "${repo}" -c user.name=startup-fixture -c user.email=startup-fixture@example.invalid "$@"; }
git init -q --bare "${origin}"
git -C "${origin}" symbolic-ref HEAD refs/heads/develop
git init -q -b develop "${repo}"
printf 'base\n' > "${repo}/a.txt"
g add a.txt
g commit -qm 'fixture: base'
g remote add origin "${origin}"
g push -q origin develop
base="$(g rev-parse HEAD)"
shas=()
for i in 1 2 3 4; do
  printf 'change %s\n' "${i}" >> "${repo}/a.txt"
  g commit -qam "fixture: change ${i}"
  shas+=("$(g rev-parse HEAD)")
done
g push -q origin develop
develop="$(g rev-parse HEAD)"
# The interrupted merge that the gate never judged, kept on its own ref.
printf 'interrupted\n' >> "${repo}/a.txt"
g commit -qam 'fixture: interrupted merge'
gated="$(g rev-parse HEAD)"
g update-ref refs/heads/fixture-interrupted "${gated}"
g reset -q --hard "${develop}"

lanes=(2-ready 4-auto-review 5-human-review 6-completed 7-archive)
# Cards carry what a long-running installation has: a key in the project's
# short code (auto-discovered as FIX) and an owner, so the first-boot key and
# owner migrations have nothing to do, as on the production workspace.
next_key=1
write_card() { # lane id order commits-json
  local dir="${project}/$1/$2"
  mkdir -p "${dir}"
  printf '{"id":"%s","key":"FIX-%s","title":"Startup fixture %s","state":"%s","order":%s,"agent":"codex","kind":"task","ownerClientId":"local-default","commits":%s}\n' \
    "$2" "${next_key}" "$2" "$1" "$3" "$4" > "${dir}/task.json"
  next_key=$((next_key + 1))
}
for ((i = 0; i < cards; i++)); do
  lane="${lanes[i % ${#lanes[@]}]}"
  sha="${shas[i % ${#shas[@]}]}"
  write_card "${lane}" "fixture-${i}" "${i}" \
    "[{\"sha\":\"${sha}\",\"shortSha\":\"${sha:0:7}\",\"message\":\"fixture: delivery ${i}\",\"filesChanged\":1,\"files\":[\"a.txt\"]}]"
done

# Interrupted integration journal: the gate died after the merge, before a verdict.
write_card 4-auto-review fixture-interrupted 0 '[]'
mkdir -p "${project}/4-auto-review/fixture-interrupted/post-steps"
printf '{"project":"fixture","jobId":"fixture-interrupted","repoRoot":"%s","integrationBranch":"develop","preMergeTip":"%s","gatedSha":"%s","startedAt":"2026-11-27T12:00:00Z","phase":"GateRunning","owner":"previous-process","step":"pre-develop-build-gate"}\n' \
  "${repo}" "${develop}" "${gated}" > "${project}/4-auto-review/fixture-interrupted/post-steps/integration-gate.inflight.json"

# Conflict recovery receipts on a Ready and a Progress card.
for lane in 2-ready 3-progress; do
  write_card "${lane}" "fixture-conflict-${lane}" 0 '[]'
  printf '{"deliverySha":"%s","target":"develop","originalDeliveredAtUtc":"2026-11-27T11:00:00Z","result":{"outcome":"Conflict","conflictedFiles":["a.txt"]},"recoveryQueued":true,"reservedPaths":["a.txt"]}\n' \
    "${shas[0]}" > "${project}/${lane}/fixture-conflict-${lane}/integration-conflict-recovery.json"
done

# A review decision waiting for the integration lane.
mkdir -p "${workspace}/.orchestration-occurrences"
printf '{"run":{"runId":"orch_startupfixture","projectId":"fixture","taskId":"fixture-0","definitionVersion":1,"status":"applying","currentStage":0,"payloadJson":"{}","reissueAttempts":0,"createdAt":"2026-11-27T12:00:00Z","updatedAt":"2026-11-27T12:00:00Z","completedAt":null},"idempotencyKey":"startup-fixture-applying","expectedSourceState":"4-auto-review","authorityAttemptId":"startup-fixture-attempt","authorityFence":1,"authorityEpoch":1,"creationSequence":1}\n' \
  > "${workspace}/.orchestration-occurrences/orch_startupfixture.json"

echo "startup-fixture: ${workspace} cards=${cards} base=${base:0:7} develop=${develop:0:7} interrupted=${gated:0:7}"
