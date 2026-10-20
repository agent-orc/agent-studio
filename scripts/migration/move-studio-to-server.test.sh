#!/bin/sh
# Proof for move-studio-to-server.sh without real hosts: a fake ssh runs the
# remote command locally, a fake docker accepts every call, a fake curl serves
# canned Studio API answers, and local bare repositories stand in for the
# workspace and project remotes.
# Run: sh scripts/migration/move-studio-to-server.test.sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
move="$script_dir/move-studio-to-server.sh"
work=$(mktemp -d)
trap 'rm -rf -- "$work"' EXIT HUP INT TERM

fail() { printf 'FAIL: %s\n' "$*" >&2; exit 1; }
check() { "$@" || fail "check failed: $*"; }

mkdir -p "$work/bin" "$work/server/stack" "$work/server/data"
cat >"$work/bin/ssh" <<'SSH'
#!/bin/sh
while [ "$#" -gt 0 ]; do
  case "$1" in
    -o|-p|-l|-i) shift 2 ;;
    -*) shift ;;
    *) break ;;
  esac
done
shift
exec sh -c "$*"
SSH
# FAKE_DOCKER_RUNNING names containers that `compose ps --status ... --quiet` still reports.
cat >"$work/bin/docker" <<'DOCKER'
#!/bin/sh
printf '%s\n' "$*" >>"$FAKE_LOG"
case "$*" in *' ps --status '*' --quiet') [ -z "${FAKE_DOCKER_RUNNING:-}" ] || echo "$FAKE_DOCKER_RUNNING" ;; esac
DOCKER
cat >"$work/bin/curl" <<'CURL'
#!/bin/sh
out= url= write= data=
while [ "$#" -gt 0 ]; do
  case "$1" in
    -o) out=$2; shift 2 ;;
    -w) write=$2; shift 2 ;;
    --data-binary) data=${2#@}; shift 2 ;;
    --config) printf 'CONFIG %s\n' "$(cat "$2")" >>"$FAKE_LOG"; shift 2 ;;
    -H) printf 'HEADER %s\n' "$2" >>"$FAKE_LOG"; shift 2 ;;
    -c|-b|--resolve|--max-time) shift 2 ;;
    -*) shift ;;
    *) url=$1; shift ;;
  esac
done
printf 'URL %s\n' "$url" >>"$FAKE_LOG"
[ -z "$data" ] || printf 'BODY %s\n' "$(cat "$data")" >>"$FAKE_LOG"
case "$url" in
  # Stable answers while its fake start script has run and its stop script has not.
  */healthz) [ -e "$FAKE_STABLE_UP" ] || exit 7; body=ok ;;
  */api/auth/status) body="{\"bootstrapRequired\":$FAKE_BOOTSTRAP}" ;;
  */api/auth/bootstrap|*/api/auth/login) body='{"csrfToken":"csrf-1"}' ;;
  */api/projects) body='[{"id":"PROJ-001"}]' ;;
  */api/tasks*) body='[]' ;;
  */api/auth/runner-enrollments) body='{"enrollmentCode":"enrol-1"}' ;;
  */api/auth/runner-enroll) body='{"runnerId":"runner-1","runnerName":"move-rehearsal-runner","secret":"test-secret"}' ;;
  */api/runner/claim) body='{"status":"empty"}' ;;
  *) exit 22 ;;
esac
if [ -n "$out" ]; then printf '%s' "$body" >"$out"; else printf '%s' "$body"; fi
[ -z "$write" ] || printf 200
CURL
chmod 0755 "$work/bin/ssh" "$work/bin/docker" "$work/bin/curl"
export FAKE_LOG="$work/fake.log" FAKE_BOOTSTRAP=true FAKE_STABLE_UP="$work/stable-up"
PATH="$work/bin:$PATH"
export PATH
GIT_AUTHOR_NAME=Test GIT_AUTHOR_EMAIL=test@example.invalid
GIT_COMMITTER_NAME=Test GIT_COMMITTER_EMAIL=test@example.invalid
export GIT_AUTHOR_NAME GIT_AUTHOR_EMAIL GIT_COMMITTER_NAME GIT_COMMITTER_EMAIL
export MOVE_STABLE_HEALTH_URL=http://127.0.0.1:9/healthz

printf 'name: agent-studio-website\n' >"$work/server/stack/compose.yaml"
printf 'STUDIO_VERSION=1.0.0\nSTUDIO_DOMAIN=studio.test\nSTUDIO_DATA_DIR=%s\n' "$work/server/data" >"$work/server/stack/.env"

# The real layout: workspace and devspace are siblings (C:\Projects\agent-taskboard-workspace
# and C:\Projects\agent-taskboard-devspace). The devspace holds the Stable checkout
# with the api.sh under test and the outer start/stop scripts.
projects_root="$work/Projects"
devspace="$projects_root/agent-taskboard-devspace"
stable="$devspace/agent-taskboard-stable"
mkdir -p "$stable"
cp "$script_dir/../../api.sh" "$stable/api.sh"
printf '#!/bin/sh\nrm -f "%s/stable-up"\ntouch "%s/stable-stopped"\n' "$work" "$work" >"$work/stop-stable.sh"
printf '#!/bin/sh\n[ "$DETACH" = 1 ] || exit 9\ntouch "%s/stable-started" "%s/stable-up"\n' "$work" "$work" >"$devspace/start-stable.sh"
chmod 0755 "$work/stop-stable.sh" "$devspace/start-stable.sh"
cp "$work/stop-stable.sh" "$devspace/stop-stable.sh"

# Project checkout and its remote.
git init -q --bare -b main "$work/remotes/demo.git"
git init -q -b main "$work/code/Demo"
printf 'demo\n' >"$work/code/Demo/README.md"
git -C "$work/code/Demo" add README.md
git -C "$work/code/Demo" commit -qm 'demo'
# scp-style URL: a usable remote for the planner, served through the fake ssh.
git -C "$work/code/Demo" remote add origin "git@test-server:$work/remotes/demo.git"
git -C "$work/code/Demo" push -q origin main

# Workspace with pushed history, one unpushed commit, and untracked state.
ws="$projects_root/agent-taskboard-workspace"
git init -q --bare -b main "$work/remotes/workspace.git"
git init -q -b main "$ws"
mkdir -p "$ws/.metadata" "$ws/projects/demo/tasks/AGT-1" "$ws/projects/demo/tasks/AGT-2"
# The registry repo URL carries a password; it must never reach a log, a plan file, or the server.
url_password="pw-$(date +%s)"
cat >"$ws/.metadata/projects.json" <<EOF
{"Projects":[{"Id":"PROJ-001","DisplayName":"Demo","StorageLocation":"$ws/projects/demo/tasks","RepositoryPath":"$work/code/Demo",
 "Urls":[{"Id":"repo","Label":"Repository","Url":"ssh://operator:$url_password@test-server$work/remotes/demo.git"}]}]}
EOF
printf '# AGT-1\n' >"$ws/projects/demo/tasks/AGT-1/task.md"
printf 'open\n' >"$ws/projects/demo/tasks/AGT-1/status.md"
printf '# AGT-2\n' >"$ws/projects/demo/tasks/AGT-2/task.md"
git -C "$ws" add -A
git -C "$ws" commit -qm 'workspace'
# file:// like a network remote: a plain local path would hardlink objects with the source modes.
git -C "$ws" remote add origin "file://$work/remotes/workspace.git"
git -C "$ws" push -q origin main
printf 'note\n' >"$ws/projects/demo/tasks/AGT-1/notes.md"
git -C "$ws" add -A
git -C "$ws" commit -qm 'unpushed evidence'
head=$(git -C "$ws" rev-parse HEAD)
printf 'edited after commit\n' >"$ws/projects/demo/tasks/AGT-1/status.md"
rm "$ws/projects/demo/tasks/AGT-2/task.md"
mkdir -p "$ws/.runtime/cli-output" "$ws/logs/bus" "$ws/projects/demo/tasks/AGT-1/results" "$ws/.orchestration-occurrences"
printf '{"schemaVersion":7}' >"$ws/.metadata/attempt-authority.json"
mkdir -p "$ws/.security"
printf '{"Users":[]}' >"$ws/.security/access-state.json"
printf 'run\n' >"$ws/.runtime/cli-output/AGT-1.log"
printf '{"e":1}\n' >"$ws/logs/bus/day.jsonl"
printf '<p>ok</p>' >"$ws/projects/demo/tasks/AGT-1/results/report.html"
printf '{}' >"$ws/.orchestration-occurrences/o.json"
printf 'https://operator:%s@example.invalid\n' "fake-$(date +%s)" >"$ws/.git-credentials"
printf 'partial' >"$ws/.metadata/write.json.tmp"
printf 'RUNNER_TOKEN=%s\n' "fake-$(date +%s)" >"$ws/runner.env"
mkdir -p "$ws/.docker"
printf '{"auths":{}}' >"$ws/.docker/config.json"
# A CLI log that echoed a GitHub token, built at runtime.
printf 'token %s_%s\n' 'ghp' "$(printf 'x%.0s' $(seq 1 36))" >"$ws/.runtime/cli-output/leak.log"

run() { sh "$move" "$@" --workspace "$ws" --devspace "$devspace" --server test-server --server-root "$work/server" --state-dir "$work/state" >"$work/out" 2>&1 || { cat "$work/out" >&2; fail "move-studio-to-server.sh $* exited non-zero"; }; }

run_fails() {
  if sh "$move" "$@" --workspace "$ws" --devspace "$devspace" --server test-server --server-root "$work/server" --state-dir "$work/state" >"$work/out" 2>&1; then
    cat "$work/out" >&2; fail "move-studio-to-server.sh $* succeeded but should fail"
  fi
}

# The workspace is never guessed.
if sh "$move" --dry-run --server test-server --server-root "$work/server" >"$work/out" 2>&1; then fail 'ran without --workspace'; fi
check grep -q -- '--workspace is required' "$work/out"

# Dry run without --devspace names what --apply will need.
sh "$move" --dry-run --workspace "$ws" --server test-server --server-root "$work/server" --state-dir "$work/state" >"$work/out" 2>&1 \
  || { cat "$work/out" >&2; fail 'dry run without --devspace exited non-zero'; }
check grep -q 'devspace: --devspace is not set' "$work/out"

# Dry run: inventory and preflight, nothing written on the server.
run --dry-run
check grep -qF "devspace $devspace: Stable checkout $stable, guard $devspace/.studio-moved-to-server" "$work/out"
check grep -q 'attempt-authority' "$work/out"
check grep -q 'not copied (credential): .git-credentials' "$work/out"
check grep -q 'not copied (credential): runner.env' "$work/out"
check grep -q 'not copied (credential): .docker/config.json' "$work/out"
check grep -q 'not copied (contains a github-token): .runtime/cli-output/leak.log' "$work/out"
check grep -q 'credentials removed from the URL' "$work/out"
check grep -q 'server preflight passed' "$work/out"
check grep -q 'Git Credential Manager entries' "$work/out"
[ ! -e "$work/server/move-bin" ] || fail 'dry run wrote to the server'
[ ! -e "$work/server/move-staging" ] || fail 'dry run created staging'

# First pass.
run --transfer
staged="$work/server/move-staging/workspace"
check test "$(git -C "$staged" rev-parse HEAD)" = "$head"
check test "$(cat "$staged/projects/demo/tasks/AGT-1/status.md")" = 'edited after commit'
check test -f "$staged/.metadata/attempt-authority.json"
check test -f "$staged/.runtime/cli-output/AGT-1.log"
check test -f "$staged/logs/bus/day.jsonl"
check test -f "$staged/projects/demo/tasks/AGT-1/results/report.html"
check test -f "$staged/.orchestration-occurrences/o.json"
[ ! -e "$staged/.git-credentials" ] || fail 'credential file was transferred'
[ ! -e "$staged/runner.env" ] || fail 'runner.env was transferred'
[ ! -e "$staged/.docker/config.json" ] || fail 'Docker config was transferred'
[ ! -e "$staged/.runtime/cli-output/leak.log" ] || fail 'file with a token pattern was transferred'
check test "$(stat -c %a "$work/server/move-staging")" = 700
check test "$(stat -c %a "$staged/.security")" = 700
check test "$(stat -c %a "$staged/.security/access-state.json")" = 600
for file in "$work/out" "$work/state/checkouts.tsv" "$work/state/inventory.json" "$work/state/remotes.tsv" \
  "$work/server/move-staging/projects/demo/.git/config"; do
  ! grep -q "$url_password" "$file" || fail "repository URL password leaked into $file"
done
[ ! -e "$staged/.metadata/write.json.tmp" ] || fail 'transient file was transferred'
[ ! -e "$staged/projects/demo/tasks/AGT-2/task.md" ] || fail 'tracked file deleted on the source still exists'
check test "$(git -C "$work/server/move-staging/projects/demo" rev-parse HEAD)" = "$(git -C "$work/code/Demo" rev-parse HEAD)"
check grep -q 'Delta: 7 to copy, 1 to remove' "$work/out"

# Second pass sends only the delta: one changed log, one new file, one removal.
sleep 1
printf '{"e":2}\n' >>"$ws/logs/bus/day.jsonl"
printf 'new' >"$ws/.runtime/cli-output/AGT-3.log"
rm "$ws/.runtime/cli-output/AGT-1.log"
run --transfer
check grep -q 'Delta: 3 to copy, 2 to remove' "$work/out"
check test "$(wc -l <"$staged/logs/bus/day.jsonl")" -eq 2
check test -f "$staged/.runtime/cli-output/AGT-3.log"
[ ! -e "$staged/.runtime/cli-output/AGT-1.log" ] || fail 'removed source file still exists in staging'
check test "$(cat "$staged/projects/demo/tasks/AGT-1/status.md")" = 'edited after commit'

# Unchanged third pass: the Git reset restores tracked state, so the modified
# tracked file is sent and the deleted tracked file removed again; nothing else.
run --transfer
check grep -q 'Delta: 1 to copy, 1 to remove' "$work/out"
check test "$(cat "$staged/projects/demo/tasks/AGT-1/status.md")" = 'edited after commit'

# A token appears in a file an earlier pass sent: it leaves staging. A token in
# orchestration state blocks the transfer instead of being dropped silently.
sleep 1
token="$(printf 'ghp')_$(printf 'y%.0s' $(seq 1 36))"
printf 'pushed with %s\n' "$token" >>"$ws/.runtime/cli-output/AGT-3.log"
run --transfer
check grep -q 'not copied (contains a github-token): .runtime/cli-output/AGT-3.log' "$work/out"
[ ! -e "$staged/.runtime/cli-output/AGT-3.log" ] || fail 'a file that now holds a token stayed in staging'
cp "$ws/.orchestration-occurrences/o.json" "$work/o.json"
printf '{"note":"%s"}' "$token" >"$ws/.orchestration-occurrences/o.json"
run_fails --transfer
check grep -q 'token-in-state.*\.orchestration-occurrences/o.json' "$work/out"
check grep -q 'blocking inventory issues' "$work/out"
cp "$work/o.json" "$ws/.orchestration-occurrences/o.json"
rm "$ws/.runtime/cli-output/AGT-3.log"

# Rehearsal: copy, relocation, separate Compose project, smoke checks, teardown.
: >"$FAKE_LOG"
run --rehearse --rehearsal-port 18443
for line in 'check: path relocation ok' 'check: access state parses' 'check: health ok' 'check: sign-in ok (bootstrap' \
  'check: board read ok (PROJ-001)' 'check: runner claim ok (move-rehearsal-runner, status empty)' 'rehearsal: torn down'; do
  check grep -qF "$line" "$work/out"
done
check grep -q -- '-p studio-move-rehearsal' "$FAKE_LOG"
check grep -q 'HEADER X-CSRF-Token: csrf-1' "$FAKE_LOG"
check grep -q 'CONFIG header = "Authorization: Bearer test-secret"' "$FAKE_LOG"
check grep -q '"availableSlots":0' "$FAKE_LOG"
# Owner password and one-time codes reach the payloads through stdin, not node's command line.
check grep -q 'BODY {"username":"move.rehearsal.owner","password":"Rehearsal-[0-9a-f]*","displayName":"Move rehearsal owner","bootstrapCode":"[0-9a-f]*"}' "$FAKE_LOG"
check grep -qx 'BODY {"code":"enrol-1"}' "$FAKE_LOG"
[ ! -e "$work/server/move-rehearsal" ] || fail 'rehearsal copy was not torn down'
check test -f "$staged/.security/access-state.json"

# Apply. Stable scripts record what ran; the guard and the maintenance marker
# live in the devspace, where api.sh and the watchdog look, not beside the workspace.
guard="$devspace/.studio-moved-to-server"
maintenance="$devspace/.stable-maintenance"
data="$work/server/data"
# Registers a checkout without a repository URL while Stable stops: a blocking final inventory.
cat >"$work/stop-stable-blocking.sh" <<STOP
#!/bin/sh
rm -f "$work/stable-up"
touch "$work/stable-stopped"
node -e 'const fs=require("fs");const f=process.argv[1];const r=JSON.parse(fs.readFileSync(f,"utf8"));r.Projects.push({Id:"PROJ-002",DisplayName:"Lost",RepositoryPath:process.argv[2],Urls:[]});fs.writeFileSync(f,JSON.stringify(r));' \
  "$ws/.metadata/projects.json" "$work/code/NoRemote"
STOP
chmod 0755 "$work/stop-stable-blocking.sh"
blocking_stop() { cp "$work/stop-stable-blocking.sh" "$devspace/stop-stable.sh"; }
normal_stop() { cp "$work/stop-stable.sh" "$devspace/stop-stable.sh"; git -C "$ws" checkout -q -- .metadata/projects.json; }
mkdir -p "$data/workspace"
printf 'empty install' >"$data/workspace/README"

# Each case starts with Stable serving.
fresh() { rm -f "$work/stable-stopped" "$work/stable-started"; touch "$work/stable-up"; : >"$FAKE_LOG"; }
pre_moves() { find "$data" -maxdepth 1 -name 'workspace.pre-move-*' | wc -l | tr -d ' '; }
stable_refuses() {
  if API_REQUIRE_PREBUILT=1 bash "$stable/api.sh" start >"$work/api.out" 2>&1; then return 1; fi
  grep -q 'Studio has moved to a server' "$work/api.out"
}
untouched() {
  check test "$(cat "$data/workspace/README")" = 'empty install'
  check test "$(pre_moves)" -eq 0
  [ ! -e "$work/server/moved-to-server" ] || fail 'moved marker written by a failed apply'
}
restarted() {
  check test -f "$work/stable-started"
  [ ! -e "$guard" ] || fail 'guard left behind after Stable restarted'
  [ ! -e "$maintenance" ] || fail 'maintenance marker left behind after Stable restarted'
  check grep -q 'restarting Stable' "$work/out"
}
kept_down() {
  [ ! -e "$work/stable-started" ] || fail 'Stable restarted although it had to stay down'
  check test -f "$guard"
  check test -f "$maintenance"
  check stable_refuses
  check grep -q 'Stable stays stopped behind the guard' "$work/out"
  check grep -q 'Rollback:' "$work/out"
}
rollback() {
  command=$(sed -n '/Rollback:/{n;p;}' "$work/out" | tail -n 1)
  [ -n "$command" ] || fail 'no rollback command printed'
  sh -c "$command"
}
# Nothing may land beside the workspace, where no reader looks.
nothing_beside_workspace() {
  [ ! -e "$projects_root/.studio-moved-to-server" ] || fail 'guard written beside the workspace'
  [ ! -e "$projects_root/.stable-maintenance" ] || fail 'maintenance marker written beside the workspace'
}

# A Stable whose api.sh predates the guard: refused before Stable stops.
printf '#!/bin/sh\n' >"$stable/api.sh"
fresh
run_fails --apply --yes
check grep -q 'does not honour the moved-to-server guard' "$work/out"
[ ! -e "$work/stable-stopped" ] || fail 'Stable stopped although its api.sh has no guard'
cp "$script_dir/../../api.sh" "$stable/api.sh"

# Apply needs the devspace that holds the Stable checkout and its scripts.
fresh
if sh "$move" --apply --yes --workspace "$ws" --server test-server --server-root "$work/server" --state-dir "$work/state" >"$work/out" 2>&1; then
  fail 'apply ran without --devspace'
fi
check grep -q -- '--apply needs --devspace' "$work/out"
if sh "$move" --apply --yes --workspace "$ws" --devspace "$projects_root" --server test-server --server-root "$work/server" --state-dir "$work/state" >"$work/out" 2>&1; then
  fail 'apply ran with the folder beside the workspace as devspace'
fi
check grep -q 'holds no agent-taskboard-stable/api.sh and start-stable.sh' "$work/out"
[ ! -e "$work/stable-stopped" ] || fail 'Stable stopped without a valid devspace'
nothing_beside_workspace

# A blocking issue in the final inventory restarts Stable and leaves the server alone.
fresh
blocking_stop
run_fails --apply --yes
check test -f "$work/stable-stopped"
check grep -q 'missing-repository-url' "$work/out"
restarted
untouched
normal_stop

# A server stack that keeps running after stop: refused before live data moves, Stable restarts.
fresh
FAKE_DOCKER_RUNNING=website-api-1 run_fails --apply --yes
check grep -q 'the server stack still runs after stop (website-api-1)' "$work/out"
check grep -q 'ps --status running --status restarting --status paused --quiet' "$FAKE_LOG"
restarted
untouched

# Stable did not answer before the stop: a pre-switch failure keeps it down.
fresh
rm -f "$work/stable-up"
blocking_stop
run_fails --apply --yes
kept_down
check grep -qx 'status=switching' "$guard"
untouched
normal_stop
rollback
[ ! -e "$guard" ] || fail 'rollback did not lift the guard'

# Failure after the switch began: the server stack is stopped again, Stable
# stays stopped behind the guard, and the printed rollback command restores it.
fresh
FAKE_BOOTSTRAP=true run_fails --apply --yes
check grep -q 'asks for owner bootstrap' "$work/out"
check grep -q 'switch failed: server stack stopped' "$work/out"
up_line=$(grep -n 'server/stack .* up -d' "$FAKE_LOG" | tail -n 1 | cut -d: -f1)
stop_line=$(grep -n 'server/stack .* stop$' "$FAKE_LOG" | tail -n 1 | cut -d: -f1)
[ -n "$up_line" ] && [ -n "$stop_line" ] && [ "$stop_line" -gt "$up_line" ] || fail 'a failed switch did not stop the server stack'
[ ! -e "$work/stable-started" ] || fail 'Stable restarted although the server data was switched'
check grep -qx 'status=switching' "$guard"
check test -f "$maintenance"
check stable_refuses
nothing_beside_workspace
check test "$(pre_moves)" -eq 1
cp "$work/out" "$work/failed-switch.out"

# A re-run after a failed switch is refused without --force, before Stable is touched.
fresh
rm -f "$work/stable-up"
run_fails --apply --yes
check grep -q 'an earlier apply did not finish' "$work/out"
check grep -q 'workstation guard: status=switching' "$work/out"
[ ! -e "$work/stable-stopped" ] || fail 'a re-run after a failed switch ran the Stable stop'
check test "$(pre_moves)" -eq 1

cp "$work/failed-switch.out" "$work/out"
rollback
check test -f "$work/stable-started"
[ ! -e "$guard" ] || fail 'rollback did not lift the guard'
[ ! -e "$maintenance" ] || fail 'rollback did not lift the maintenance marker'
check grep -q 'compose --project-directory .*/server/stack stop' "$FAKE_LOG"
API_REQUIRE_PREBUILT=1 bash "$stable/api.sh" start >"$work/api.out" 2>&1 || true
check grep -q 'api-prebuilt-required' "$work/api.out"
! grep -q 'Studio has moved' "$work/api.out" || fail 'api.sh still refuses after the rollback'

# Apply: Stable stop, final delta, switch, check, moved marker, guard, rollback hint.
sleep 1
fresh
FAKE_BOOTSTRAP=false run --apply --yes
check test -f "$work/stable-stopped"
[ ! -e "$work/stable-started" ] || fail 'Stable restarted after a successful apply'
check test "$(git -C "$data/workspace" rev-parse HEAD)" = "$head"
check test -f "$data/workspace/logs/bus/day.jsonl"
check test -d "$data/projects/demo/.git"
check test "$(pre_moves)" -eq 2
check test "$(stat -c %a "$data/workspace/.security/access-state.json")" = 600
[ -z "$(find "$data/workspace" "$data/projects" ! -type l -perm /077 | head -n 1)" ] || fail 'moved data is readable by other accounts'
check grep -q 'check: staging relocates with 0 unresolved paths' "$work/out"
check grep -q 'check: server stack stopped' "$work/out"
check grep -q 'check: https://studio.test answers locally with the moved accounts' "$work/out"
check grep -q '^moved_at=' "$work/server/moved-to-server"
[ ! -e "$work/server/move-staging" ] || fail 'staging was not removed after the apply'
check grep -qx 'status=moved' "$guard"
check grep -q '^move-studio-to-server apply ' "$maintenance"
check stable_refuses
nothing_beside_workspace
check grep -q 'Rollback:' "$work/out"
check grep -qF "DETACH=1 '$devspace/start-stable.sh'" "$work/out"
check grep -q 'compose --project-directory .*/server/stack .*up -d' "$FAKE_LOG"

# A second apply would roll the live server back to the workstation snapshot: refused before Stable stops.
fresh
rm -f "$work/stable-up"
FAKE_BOOTSTRAP=false run_fails --apply --yes
check grep -q 'Studio already moved' "$work/out"
[ ! -e "$work/stable-stopped" ] || fail 'second apply stopped Stable'
check test "$(pre_moves)" -eq 2
check grep -qx 'status=moved' "$guard"

# --force with a blocking final inventory: the server is the live Studio, so
# Stable stays stopped and the moved guard stays in place.
fresh
blocking_stop
FAKE_BOOTSTRAP=false run_fails --apply --yes --force
check grep -q 'missing-repository-url' "$work/out"
check grep -q 'an earlier apply left a moved marker or guard' "$work/out"
kept_down
check grep -qx 'status=moved' "$guard"
check test "$(pre_moves)" -eq 2
normal_stop

# --force replaces the server data on purpose; the existing maintenance marker stays.
sleep 1
fresh
FAKE_BOOTSTRAP=false run --apply --yes --force
check grep -q 'warning: --force' "$work/out"
check test "$(pre_moves)" -eq 3
check test "$(git -C "$data/workspace" rev-parse HEAD)" = "$head"
check test -f "$maintenance"
check grep -qx 'status=moved' "$guard"

printf 'move-studio-to-server: required workspace and devspace, read-only dry run, first pass, delta pass, idempotent repeat, rehearsal, apply guards, restart only when safe, failed-switch stop and re-run refusal, rollback, re-apply refusal, and forced apply verified.\n'
