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
printf '#!/bin/sh\nprintf "%%s\\n" "$*" >>"$FAKE_LOG"\n' >"$work/bin/docker"
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
  */healthz) exit 7 ;;
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
export FAKE_LOG="$work/fake.log" FAKE_BOOTSTRAP=true
PATH="$work/bin:$PATH"
export PATH
GIT_AUTHOR_NAME=Test GIT_AUTHOR_EMAIL=test@example.invalid
GIT_COMMITTER_NAME=Test GIT_COMMITTER_EMAIL=test@example.invalid
export GIT_AUTHOR_NAME GIT_AUTHOR_EMAIL GIT_COMMITTER_NAME GIT_COMMITTER_EMAIL
export MOVE_STABLE_HEALTH_URL=http://127.0.0.1:9/healthz

printf 'name: agent-studio-website\n' >"$work/server/stack/compose.yaml"
printf 'STUDIO_VERSION=1.0.0\nSTUDIO_DOMAIN=studio.test\nSTUDIO_DATA_DIR=%s\n' "$work/server/data" >"$work/server/stack/.env"

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
ws="$work/devspace/agent-taskboard-workspace"
git init -q --bare -b main "$work/remotes/workspace.git"
git init -q -b main "$ws"
mkdir -p "$ws/.metadata" "$ws/projects/demo/tasks/AGT-1" "$ws/projects/demo/tasks/AGT-2"
cat >"$ws/.metadata/projects.json" <<EOF
{"Projects":[{"Id":"PROJ-001","DisplayName":"Demo","StorageLocation":"$ws/projects/demo/tasks","RepositoryPath":"$work/code/Demo","Urls":[]}]}
EOF
printf '# AGT-1\n' >"$ws/projects/demo/tasks/AGT-1/task.md"
printf 'open\n' >"$ws/projects/demo/tasks/AGT-1/status.md"
printf '# AGT-2\n' >"$ws/projects/demo/tasks/AGT-2/task.md"
git -C "$ws" add -A
git -C "$ws" commit -qm 'workspace'
git -C "$ws" remote add origin "$work/remotes/workspace.git"
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

run() { sh "$move" "$@" --workspace "$ws" --server test-server --server-root "$work/server" --state-dir "$work/state" >"$work/out" 2>&1 || { cat "$work/out" >&2; fail "move-studio-to-server.sh $* exited non-zero"; }; }

# Dry run: inventory and preflight, nothing written on the server.
run --dry-run
check grep -q 'attempt-authority' "$work/out"
check grep -q 'not copied (credential): .git-credentials' "$work/out"
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
[ ! -e "$work/server/move-rehearsal" ] || fail 'rehearsal copy was not torn down'
check test -f "$staged/.security/access-state.json"

# Apply: Stable stop, final delta, switch, check, rollback hint.
printf '#!/bin/sh\ntouch "%s/stable-stopped"\n' "$work" >"$work/stop-stable.sh"
chmod 0755 "$work/stop-stable.sh"
mkdir -p "$work/server/data/workspace"
printf 'empty install' >"$work/server/data/workspace/README"
: >"$FAKE_LOG"
FAKE_BOOTSTRAP=false MOVE_STABLE_STOP="$work/stop-stable.sh" MOVE_STABLE_START="$work/start-stable.sh" run --apply --yes
check test -f "$work/stable-stopped"
check test "$(git -C "$work/server/data/workspace" rev-parse HEAD)" = "$head"
check test -f "$work/server/data/workspace/logs/bus/day.jsonl"
check test -d "$work/server/data/projects/demo/.git"
ls -d "$work/server/data/workspace.pre-move-"* >/dev/null || fail 'previous server workspace was not kept'
check grep -q 'check: https://studio.test answers locally with the moved accounts' "$work/out"
check grep -q 'Rollback:' "$work/out"
check grep -qF "$work/start-stable.sh" "$work/out"
check grep -q 'compose --project-directory .*/server/stack .*up -d' "$FAKE_LOG"

printf 'move-studio-to-server: read-only dry run, first pass, delta pass, idempotent repeat, rehearsal, and apply verified.\n'
